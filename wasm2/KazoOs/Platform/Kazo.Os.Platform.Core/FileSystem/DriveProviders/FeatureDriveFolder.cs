using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Drives;
using Kazo.Os.Platform.Core.CloudAccounts;
using Kazo.Os.Platform.Core.CloudDrives;
using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.Platform.Core.FileSystem.DriveProviders;

/// <summary>
/// One drive of a type a feature provides, seen by the file system as an <see cref="IHostFolder"/>
/// (ADR 0282 §2, §3): the bridge from the platform's internal contract to the provider's
/// <see cref="KazoDriveFolder"/>, which the SDK exposes instead.
/// </summary>
/// <remarks>
/// <para>
/// <b>Never waits at login.</b> Creating the folder calls nothing. The provider is loaded and its
/// <see cref="KazoDriveProvider.OpenAsync"/> called in the background at the first question; until
/// they answer, <see cref="QueryPermissionAsync"/> says <see cref="HostFolderPermission.Unavailable"/>
/// (the drive is mounted disconnected) and the source raises its availability event once the folder
/// is open, so the drive reconnects by itself (ADR 0267 §9). Loading and opening are bounded by
/// <see cref="FeatureDriveSource.OpenTimeout"/>, the state question by
/// <see cref="FeatureDriveSource.StateTimeout"/>.
/// </para>
/// <para>
/// <b>Errors.</b> A <see cref="KazoDriveException"/> becomes the matching
/// <see cref="HostFolderException"/>; anything else the provider throws is a
/// <see cref="HostFolderErrorKind.Failure"/> written to the feature's log. When the provider says
/// <see cref="KazoDriveErrorKind.SignInRequired"/> after it was given an access token, the OS renews
/// the token once and repeats the operation, as for the shipped connectors (ADR 0267 §4).
/// </para>
/// <para>
/// <b>Permission</b> follows the account, like a shipped connector: removed or of another type,
/// <see cref="HostFolderPermission.Denied"/>; to sign in again, <see cref="HostFolderPermission.Prompt"/>
/// and <see cref="RequestPermissionAsync"/> opens the sign-in window; otherwise the provider's
/// <see cref="KazoDriveFolder.GetStateAsync"/>.
/// </para>
/// </remarks>
internal sealed class FeatureDriveFolder : IHostFolder, IHostFolderQuota, IConditionalHostFolder
{
    private readonly FeatureDriveSource _source;
    private readonly NetworkDriveReference _reference;
    private readonly string _accountId;
    private readonly Lock _gate = new();
    private Task<OpenedFolder?>? _opening;
    private bool _disposed;

    /// <summary>Creates the folder of one drive; nothing is loaded or called yet.</summary>
    /// <param name="source">The source of the drive's type.</param>
    /// <param name="reference">What the drive list keeps of the drive; its account is required.</param>
    public FeatureDriveFolder(FeatureDriveSource source, NetworkDriveReference reference)
    {
        _source = source;
        _reference = reference;
        _accountId = reference.Account ?? throw new ArgumentException("A drive of a provided type belongs to an account.", nameof(reference));
    }

    /// <inheritdoc />
    public string Name => _reference.Name;

    /// <summary>Gets whether the provider's folder is open (for tests and diagnostics).</summary>
    internal bool IsOpen => _opening is { IsCompletedSuccessfully: true, Result: not null };

    /// <inheritdoc />
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The provider's assembly is checked by AppPackageApiCheck at install time.")]
    public async ValueTask<HostFolderPermission> QueryPermissionAsync(CancellationToken cancellationToken = default)
    {
        HostFolderPermission account = await AccountPermissionAsync(cancellationToken).ConfigureAwait(false);
        if (account != HostFolderPermission.Granted)
        {
            return account;
        }

        Task<OpenedFolder?> opening = StartOpening();
        if (!opening.IsCompleted || opening.Result is not { } opened)
        {
            // Never waits for a provider: the drive is disconnected until it answers.
            return HostFolderPermission.Unavailable;
        }

        try
        {
            KazoDriveState state;
            using (opened.Provider.Enter())
            {
                state = await opened.Folder.GetStateAsync(cancellationToken).AsTask()
                    .WaitAsync(_source.StateTimeout, _source.Time, cancellationToken).ConfigureAwait(false);
            }

            return state switch
            {
                KazoDriveState.Ready => HostFolderPermission.Granted,
                KazoDriveState.SignInRequired => HostFolderPermission.Prompt,
                _ => HostFolderPermission.Unavailable
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _source.LogProviderFailure($"the state of {Name} could not be read: {exception.GetType().Name}: {exception.Message}");
            return HostFolderPermission.Unavailable;
        }
    }

    /// <inheritdoc />
    public async ValueTask<HostFolderPermission> RequestPermissionAsync(CancellationToken cancellationToken = default)
    {
        HostFolderPermission permission = await QueryPermissionAsync(cancellationToken).ConfigureAwait(false);
        if (permission != HostFolderPermission.Prompt)
        {
            return permission;
        }

        try
        {
            await _source.Accounts.ReconnectAsync(_accountId, cancellationToken).ConfigureAwait(false);
        }
        catch (CloudAccountException exception) when (exception.Error == CloudAccountError.WindowBlocked)
        {
            throw new InvalidOperationException(exception.Message, exception);
        }
        catch (CloudAccountException exception) when (exception.Error is CloudAccountError.Cancelled or CloudAccountError.Denied or CloudAccountError.AccountMismatch)
        {
            return HostFolderPermission.Prompt;
        }
        catch (CloudAccountException)
        {
            return HostFolderPermission.Unavailable;
        }

        return await QueryPermissionAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<HostFolderEntry> StatAsync(IReadOnlyList<string> path, CancellationToken cancellationToken = default) =>
        RunAsync(path, async (folder, drivePath, ct) => ToEntry(await folder.StatAsync(drivePath, ct).ConfigureAwait(false)), cancellationToken);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<HostFolderEntry>> ListAsync(IReadOnlyList<string> path, CancellationToken cancellationToken = default) =>
        RunAsync<IReadOnlyList<HostFolderEntry>>(path, async (folder, drivePath, ct) =>
        {
            IReadOnlyList<KazoDriveEntry> entries = await folder.ListAsync(drivePath, ct).ConfigureAwait(false) ?? [];
            // A provider's answer is not trusted: a name that could leave the folder is not shown.
            return [.. entries.Where(static entry => entry is not null && KazoDrivePath.IsValidName(entry.Name)).Select(ToEntry)];
        }, cancellationToken);

    /// <inheritdoc />
    public ValueTask<byte[]> ReadAsync(IReadOnlyList<string> path, CancellationToken cancellationToken = default) =>
        RunAsync(path, async (folder, drivePath, ct) =>
        {
            byte[] content = await folder.ReadAsync(drivePath, ct).ConfigureAwait(false) ?? [];
            return content.LongLength > KazoDriveLimits.MaxFileBytes
                ? throw new HostFolderException(HostFolderErrorKind.FileTooLarge, $"{drivePath}: larger than {KazoDriveLimits.MaxFileBytes} bytes")
                : content;
        }, cancellationToken);

    /// <inheritdoc />
    public ValueTask CreateAsync(IReadOnlyList<string> path, HostFolderEntryKind kind, CancellationToken cancellationToken = default) =>
        RunAsync(path, (folder, drivePath, ct) => kind == HostFolderEntryKind.Directory
            ? folder.CreateFolderAsync(drivePath, ct)
            : folder.CreateFileAsync(drivePath, ct), cancellationToken);

    /// <inheritdoc />
    public ValueTask WriteAsync(IReadOnlyList<string> path, byte[] content, CancellationToken cancellationToken = default) =>
        WriteCoreAsync(path, content, null, cancellationToken);

    /// <inheritdoc />
    public ValueTask WriteIfMatchAsync(IReadOnlyList<string> path, byte[] content, string expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(expectedRevision);
        return WriteCoreAsync(path, content, expectedRevision, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask DeleteAsync(IReadOnlyList<string> path, bool recursive, CancellationToken cancellationToken = default) =>
        RunAsync(path, (folder, drivePath, ct) => folder.DeleteAsync(drivePath, recursive, ct), cancellationToken);

    /// <inheritdoc />
    public ValueTask<bool> TryMoveAsync(IReadOnlyList<string> source, IReadOnlyList<string> destination, CancellationToken cancellationToken = default)
    {
        KazoDrivePath to = ToPath(destination);
        return RunAsync(source, (folder, from, ct) => folder.TryMoveAsync(from, to, ct), cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<HostFolderQuota?> GetQuotaAsync(CancellationToken cancellationToken = default)
    {
        KazoDriveQuota? quota = await RunAsync(null, (folder, _, ct) => folder.GetQuotaAsync(ct), cancellationToken).ConfigureAwait(false);
        return quota is null ? null : new HostFolderQuota(Math.Max(0, quota.TotalBytes), Math.Max(0, quota.FreeBytes));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Task<OpenedFolder?>? opening;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            opening = _opening;
        }

        if (opening is not { IsCompletedSuccessfully: true, Result: { } opened })
        {
            // Still opening: the folder is closed when it arrives (see StartOpening).
            return;
        }

        await CloseAsync(opened).ConfigureAwait(false);
    }

    private ValueTask WriteCoreAsync(IReadOnlyList<string> path, byte[] content, string? ifRevision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.LongLength > KazoDriveLimits.MaxFileBytes)
        {
            throw new HostFolderException(HostFolderErrorKind.FileTooLarge, $"{string.Join('/', path)}: larger than {KazoDriveLimits.MaxFileBytes} bytes");
        }

        return RunAsync(path, (folder, drivePath, ct) => folder.WriteAsync(drivePath, content, ifRevision, ct), cancellationToken);
    }

    /// <summary>Gets the permission the account gives, without the provider.</summary>
    private async ValueTask<HostFolderPermission> AccountPermissionAsync(CancellationToken cancellationToken)
    {
        CloudAccount? account;
        try
        {
            account = await _source.Accounts.FindAccountAsync(_accountId, cancellationToken).ConfigureAwait(false);
        }
        catch (CloudAccountException exception)
        {
            return exception.Error is CloudAccountError.NotSignedIn or CloudAccountError.AccountNotFound
                ? HostFolderPermission.Denied
                : HostFolderPermission.Unavailable;
        }

        if (account is null || !string.Equals(account.ProviderId, _source.Type, StringComparison.Ordinal))
        {
            // ADR 0282 §3: a provider only ever gets the account of its own type.
            return HostFolderPermission.Denied;
        }

        return account.State == CloudAccountState.NeedsReconnect ? HostFolderPermission.Prompt : HostFolderPermission.Granted;
    }

    /// <summary>
    /// Opens the provider's folder now and waits for it (bounded by <see cref="FeatureDriveSource.OpenTimeout"/>),
    /// for a drive the user is connecting; a failure is already written to the feature's log.
    /// </summary>
    internal Task OpenNowAsync() => StartOpening();

    /// <summary>Starts loading the provider and opening the folder, once.</summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The provider's assembly is checked by AppPackageApiCheck at install time.")]
    private Task<OpenedFolder?> StartOpening()
    {
        lock (_gate)
        {
            if (_opening is { } current && !(current.IsCompleted && current.Result is null))
            {
                return current;
            }

            if (_disposed)
            {
                return Task.FromResult<OpenedFolder?>(null);
            }

            Task<OpenedFolder?> opening = OpenAsync();
            _opening = opening;
            // ADR 0267 §9: once the folder is open (the task completed, so the drive's question now
            // gets an answer), the disconnected drive asks again and reconnects with no gesture.
            _ = opening.ContinueWith(
                static (opened, source) =>
                {
                    if (opened.Result is not null)
                    {
                        ((FeatureDriveSource)source!).RaiseAvailabilityChanged();
                    }
                },
                _source,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return opening;
        }
    }

    [RequiresUnreferencedCode("A drive provider is created by reflection from its feature's loaded assembly.")]
    private async Task<OpenedFolder?> OpenAsync()
    {
        // Off the caller's path: a login or a file operation never runs the provider's code inline.
        await Task.Yield();
        OpenedFolder? opened = null;
        try
        {
            opened = await OpenCoreAsync().WaitAsync(_source.OpenTimeout, _source.Time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _source.LogProviderFailure($"{Name} did not open within {_source.OpenTimeout.TotalSeconds:0} s");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _source.LogProviderFailure($"{Name} could not be opened: {exception.GetType().Name}: {exception.Message}");
        }

        if (opened is null)
        {
            return null;
        }

        bool closeNow;
        lock (_gate)
        {
            closeNow = _disposed;
        }

        if (closeNow)
        {
            await CloseAsync(opened).ConfigureAwait(false);
            return null;
        }

        return opened;
    }

    [RequiresUnreferencedCode("A drive provider is created by reflection from its feature's loaded assembly.")]
    private async Task<OpenedFolder?> OpenCoreAsync()
    {
        if (await _source.LoadProviderAsync().ConfigureAwait(false) is not { } provider)
        {
            return null;
        }

        FeatureDriveConnection connection = await _source.ConnectAsync(_accountId).ConfigureAwait(false);
        TokenTrackingContext context = new(connection.Context);
        KazoDriveOpenRequest request = new(_reference.Name, context)
        {
            Root = _reference.Root,
            Options = connection.Options
        };
        IDisposable? serverLease = AllowTypedServer(connection.Options);
        try
        {
            KazoDriveFolder? folder;
            using (provider.Enter())
            {
                folder = await provider.Provider.OpenAsync(request).ConfigureAwait(false);
            }

            if (folder is null)
            {
                _source.LogProviderFailure($"{Name}: OpenAsync returned no folder");
                serverLease?.Dispose();
                return null;
            }

            return new OpenedFolder(provider, folder, context, serverLease);
        }
        catch
        {
            serverLease?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// ADR 0284 §2: when the declaration marks a server address field, the host the user typed there
    /// is allowed to the feature until the drive closes. An address the OS may not allow (http, an IP,
    /// <c>localhost</c>) takes no lease: the provider's requests to it are refused by the gateway.
    /// </summary>
    private IDisposable? AllowTypedServer(IReadOnlyDictionary<string, string> options)
    {
        if (_source.ServerHosts is not { } hosts
            || AppDriveProviders.ServerAddressField(_source.Registration.Provider.Connection) is not { } field
            || !options.TryGetValue(field.Name, out string? address)
            || !AppDriveProviders.TryGetServerHost(address, out string host))
        {
            return null;
        }

        return hosts.Allow(_source.FeatureId, host);
    }

    private async ValueTask CloseAsync(OpenedFolder opened)
    {
        try
        {
            using (opened.Provider.Enter())
            {
                await opened.Folder.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _source.LogProviderFailure($"{Name} could not be closed: {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            opened.ServerLease?.Dispose();
        }
    }

    /// <summary>Waits for the folder (bounded), or fails as unavailable.</summary>
    private async ValueTask<OpenedFolder> RequireOpenAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        OpenedFolder? opened = await StartOpening().WaitAsync(cancellationToken).ConfigureAwait(false);
        return opened ?? throw new HostFolderException(HostFolderErrorKind.Unavailable, $"{Name}: the provider of '{_source.Type}' is not available");
    }

    private async ValueTask RunAsync(
        IReadOnlyList<string>? path,
        Func<KazoDriveFolder, KazoDrivePath, CancellationToken, ValueTask> operation,
        CancellationToken cancellationToken) =>
        await RunAsync<bool>(path, async (folder, drivePath, ct) =>
        {
            await operation(folder, drivePath, ct).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Runs one operation of the provider in the feature's context: the error contract, and one
    /// renewal of a refused access token.
    /// </summary>
    private async ValueTask<T> RunAsync<T>(
        IReadOnlyList<string>? path,
        Func<KazoDriveFolder, KazoDrivePath, CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken)
    {
        KazoDrivePath drivePath = path is null ? KazoDrivePath.Root : ToPath(path);
        OpenedFolder opened = await RequireOpenAsync(cancellationToken).ConfigureAwait(false);
        for (int attempt = 0; ; attempt++)
        {
            StrongBox<string?> given = new();
            try
            {
                using (opened.Provider.Enter())
                using (TokenTrackingContext.Track(given))
                {
                    return await operation(opened.Folder, drivePath, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (KazoDriveException exception) when (exception.Kind == KazoDriveErrorKind.SignInRequired && attempt == 0 && given.Value is { } token)
            {
                // The provider was given a token and the service refused it: renewed once by the OS.
                if (!await RenewAsync(token, cancellationToken).ConfigureAwait(false))
                {
                    throw await TranslateAsync(exception, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (KazoDriveException exception)
            {
                throw await TranslateAsync(exception, cancellationToken).ConfigureAwait(false);
            }
            catch (HostFolderException)
            {
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Third-party code: an unexpected exception is a failure of this operation, never a crash.
                _source.LogProviderFailure($"{Name}: {exception.GetType().Name}: {exception.Message}");
                throw new HostFolderException(HostFolderErrorKind.Failure, $"{Name}: the provider of '{_source.Type}' failed", exception);
            }
        }
    }

    /// <summary>Renews a refused access token; <see langword="false"/> when it cannot be renewed now.</summary>
    private async ValueTask<bool> RenewAsync(string rejected, CancellationToken cancellationToken)
    {
        try
        {
            await _source.Accounts.RenewAccessTokenAsync(_accountId, rejected, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (CloudAccountException)
        {
            return false;
        }
    }

    /// <summary>The provider's error as the file system's (ADR 0282 §2).</summary>
    private async ValueTask<HostFolderException> TranslateAsync(KazoDriveException exception, CancellationToken cancellationToken)
    {
        HostFolderErrorKind kind = exception.Kind switch
        {
            KazoDriveErrorKind.NotFound => HostFolderErrorKind.NotFound,
            KazoDriveErrorKind.Exists => HostFolderErrorKind.AlreadyExists,
            KazoDriveErrorKind.Unavailable => HostFolderErrorKind.Unavailable,
            KazoDriveErrorKind.RevisionConflict => HostFolderErrorKind.RevisionConflict,
            KazoDriveErrorKind.FileTooLarge => HostFolderErrorKind.FileTooLarge,
            KazoDriveErrorKind.QuotaExceeded => HostFolderErrorKind.NoSpace,
            KazoDriveErrorKind.TypeMismatch => HostFolderErrorKind.TypeMismatch,
            KazoDriveErrorKind.NotEmpty => HostFolderErrorKind.NotEmpty,
            KazoDriveErrorKind.InvalidName => HostFolderErrorKind.InvalidName,
            KazoDriveErrorKind.AccessDenied => HostFolderErrorKind.NotAllowed,
            // A removed account denies the drive; one to sign in again disconnects it (ADR 0267 §9).
            KazoDriveErrorKind.SignInRequired => await AccountPermissionAsync(cancellationToken).ConfigureAwait(false) == HostFolderPermission.Denied
                ? HostFolderErrorKind.NotAllowed
                : HostFolderErrorKind.Unavailable,
            _ => HostFolderErrorKind.Failure
        };
        return new HostFolderException(kind, exception.Message, exception);
    }

    private static KazoDrivePath ToPath(IReadOnlyList<string> path)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            return KazoDrivePath.FromSegments(path);
        }
        catch (ArgumentException exception)
        {
            throw new HostFolderException(HostFolderErrorKind.InvalidName, exception.Message, exception);
        }
    }

    private static HostFolderEntry ToEntry(KazoDriveEntry entry) =>
        new(entry.Name,
            entry.Kind == KazoDriveEntryKind.Folder ? HostFolderEntryKind.Directory : HostFolderEntryKind.File,
            Math.Max(0, entry.Length),
            entry.LastModifiedUtc)
        {
            Revision = entry.Revision
        };

    /// <summary>The loaded provider, its open folder and the context lent to it.</summary>
    private sealed record OpenedFolder(LoadedDriveProvider Provider, KazoDriveFolder Folder, TokenTrackingContext Context, IDisposable? ServerLease);

    /// <summary>
    /// The context lent to the provider, which remembers the last access token it gave, so a token
    /// the service refused can be renewed by the OS (ADR 0269: the refresh token never leaves it).
    /// </summary>
    private sealed class TokenTrackingContext(IKazoDriveProviderContext inner) : IKazoDriveProviderContext
    {
        // The operation in progress on this async flow, so two operations at once never mix their tokens.
        private static readonly AsyncLocal<StrongBox<string?>?> s_operation = new();

        public string Type => inner.Type;

        public KazoDriveAccount? Account => inner.Account;

        /// <summary>Records in <paramref name="given"/> the token handed out during one operation.</summary>
        public static IDisposable Track(StrongBox<string?> given)
        {
            StrongBox<string?>? previous = s_operation.Value;
            s_operation.Value = given;
            return new Restore(previous);
        }

        public async ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            string token = await inner.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            if (s_operation.Value is { } operation)
            {
                operation.Value = token;
            }

            return token;
        }

        public ValueTask<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default) =>
            inner.GetSecretAsync(name, cancellationToken);

        public void NotifyAvailabilityChanged() => inner.NotifyAvailabilityChanged();

        private sealed class Restore(StrongBox<string?>? previous) : IDisposable
        {
            public void Dispose() => s_operation.Value = previous;
        }
    }
}
