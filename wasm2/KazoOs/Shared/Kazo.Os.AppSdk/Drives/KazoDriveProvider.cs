namespace Kazo.Os.AppSdk.Drives;

/// <summary>
/// The class a feature writes to provide a type of network drive (ADR 0282 §2). The feature
/// declares it in <c>FeatureDefinition.DriveProviders</c>; the OS creates one instance per page,
/// on the first drive of that type it opens, in the feature's context, with the public constructor
/// that takes an <c>IFeatureServiceContext</c> or none.
/// </summary>
/// <remarks>
/// <para>
/// The OS keeps everything that is the same for every type: the mount under <c>/mnt</c>, its modes,
/// the drive list, the connect dialog, Settings, <c>mount -t</c>, the sign-in and the sealing of
/// tokens and secrets. The provider only talks to its service.
/// </para>
/// <para>
/// This is an abstract class, not an interface, so a later SDK can add a virtual member with a
/// default without breaking a provider already compiled.
/// </para>
/// </remarks>
public abstract class KazoDriveProvider
{
    /// <summary>
    /// Opens one drive of the user. Never reach the service here: the OS calls it at login, and a
    /// login never waits on the network. Build the folder from <paramref name="request"/> and let
    /// <see cref="KazoDriveFolder.GetStateAsync"/> find out whether the service answers.
    /// </summary>
    /// <param name="request">The drive's name, root, options and the context of its account.</param>
    /// <param name="cancellationToken">Cancels the opening.</param>
    /// <returns>The drive's folder; the OS disposes it when the drive is unmounted.</returns>
    /// <exception cref="KazoDriveException">The request cannot describe a drive of this service (a missing option, say).</exception>
    public abstract ValueTask<KazoDriveFolder> OpenAsync(KazoDriveOpenRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// One drive a <see cref="KazoDriveProvider"/> opened (ADR 0282 §2): the operations the OS calls
/// when an app or a command reads and writes under its mount point. Paths are relative to the
/// drive's root. Every failure the user may meet is a <see cref="KazoDriveException"/>.
/// </summary>
/// <remarks>
/// The OS calls these members from the page, one request at a time per path, with the limits of
/// every network drive: a file is read and written whole, up to <see cref="KazoDriveLimits.MaxFileBytes"/>.
/// A member that throws <see cref="KazoDriveErrorKind.Unavailable"/> or
/// <see cref="KazoDriveErrorKind.SignInRequired"/> disconnects the drive.
/// </remarks>
public abstract class KazoDriveFolder : IAsyncDisposable
{
    /// <summary>Asks whether the service answers and accepts the account, as cheaply as the service allows.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The state of the drive.</returns>
    public abstract ValueTask<KazoDriveState> GetStateAsync(CancellationToken cancellationToken = default);

    /// <summary>Describes the entry at <paramref name="path"/>; for <see cref="KazoDrivePath.Root"/>, a folder.</summary>
    /// <param name="path">The entry.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The entry.</returns>
    /// <exception cref="KazoDriveException"><see cref="KazoDriveErrorKind.NotFound"/> when nothing is there.</exception>
    public abstract ValueTask<KazoDriveEntry> StatAsync(KazoDrivePath path, CancellationToken cancellationToken = default);

    /// <summary>Lists the entries of the folder at <paramref name="path"/>, in any order, every page of the service included.</summary>
    /// <param name="path">The folder.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The entries.</returns>
    /// <exception cref="KazoDriveException"><see cref="KazoDriveErrorKind.NotFound"/> or <see cref="KazoDriveErrorKind.TypeMismatch"/> for a file.</exception>
    public abstract ValueTask<IReadOnlyList<KazoDriveEntry>> ListAsync(KazoDrivePath path, CancellationToken cancellationToken = default);

    /// <summary>Reads the whole content of the file at <paramref name="path"/>.</summary>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>The content.</returns>
    /// <exception cref="KazoDriveException"><see cref="KazoDriveErrorKind.FileTooLarge"/> above <see cref="KazoDriveLimits.MaxFileBytes"/>, without downloading it.</exception>
    public abstract ValueTask<byte[]> ReadAsync(KazoDrivePath path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the whole content of the file at <paramref name="path"/>, creating it when it does
    /// not exist. With <paramref name="ifRevision"/>, the service replaces the file only while its
    /// <see cref="KazoDriveEntry.Revision"/> is still that value.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="content">The new content.</param>
    /// <param name="ifRevision">The revision the OS read, or <see langword="null"/> to write without condition.</param>
    /// <param name="cancellationToken">Cancels the upload.</param>
    /// <returns>A task that completes when the service stored the content.</returns>
    /// <exception cref="KazoDriveException">
    /// <see cref="KazoDriveErrorKind.RevisionConflict"/> when the file changed in between;
    /// <see cref="KazoDriveErrorKind.QuotaExceeded"/> when the storage is full.
    /// </exception>
    public abstract ValueTask WriteAsync(KazoDrivePath path, ReadOnlyMemory<byte> content, string? ifRevision, CancellationToken cancellationToken = default);

    /// <summary>Creates an empty folder.</summary>
    /// <param name="path">The new folder.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes when the folder exists.</returns>
    /// <exception cref="KazoDriveException"><see cref="KazoDriveErrorKind.Exists"/> when the name is taken.</exception>
    public abstract ValueTask CreateFolderAsync(KazoDrivePath path, CancellationToken cancellationToken = default);

    /// <summary>Deletes the entry at <paramref name="path"/>, with its content when <paramref name="recursive"/>.</summary>
    /// <param name="path">The entry.</param>
    /// <param name="recursive">Whether a folder that has entries is deleted with them.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes when the entry is gone.</returns>
    /// <exception cref="KazoDriveException">
    /// <see cref="KazoDriveErrorKind.NotFound"/>; <see cref="KazoDriveErrorKind.NotEmpty"/> for a
    /// folder that has entries when <paramref name="recursive"/> is <see langword="false"/>.
    /// </exception>
    public abstract ValueTask DeleteAsync(KazoDrivePath path, bool recursive, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an empty file, failing when the name is taken (what <c>touch</c> and
    /// <c>O_CREAT|O_EXCL</c> need). By default it asks <see cref="StatAsync"/> then writes an empty
    /// content; a service that can create on condition in one call does better by overriding it.
    /// </summary>
    /// <param name="path">The new file.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes when the file exists.</returns>
    /// <exception cref="KazoDriveException"><see cref="KazoDriveErrorKind.Exists"/> when the name is taken.</exception>
    public virtual async ValueTask CreateFileAsync(KazoDrivePath path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            await StatAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (KazoDriveException exception) when (exception.Kind == KazoDriveErrorKind.NotFound)
        {
            await WriteAsync(path, ReadOnlyMemory<byte>.Empty, ifRevision: null, cancellationToken).ConfigureAwait(false);
            return;
        }

        throw new KazoDriveException(KazoDriveErrorKind.Exists, $"An entry already has the name '{path.Name}'.");
    }

    /// <summary>
    /// Moves or renames an entry in one call of the service. By default, <see langword="false"/>:
    /// the OS then copies and deletes.
    /// </summary>
    /// <param name="from">The entry.</param>
    /// <param name="to">Its new path; nothing is there yet.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns><see langword="true"/> when the service moved it; <see langword="false"/> when it cannot.</returns>
    public virtual ValueTask<bool> TryMoveAsync(KazoDrivePath from, KazoDrivePath to, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(false);

    /// <summary>
    /// Asks the service for the size and free space of the storage, for <c>df</c> and the Explorer.
    /// By default, <see langword="null"/>: <c>df</c> shows <c>-</c>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The quota, or <see langword="null"/> when the service does not say.</returns>
    public virtual ValueTask<KazoDriveQuota?> GetQuotaAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<KazoDriveQuota?>(null);

    /// <summary>Releases what the drive holds (a cache, a pending request). By default, nothing.</summary>
    /// <returns>A task that completes when the drive is released.</returns>
    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
