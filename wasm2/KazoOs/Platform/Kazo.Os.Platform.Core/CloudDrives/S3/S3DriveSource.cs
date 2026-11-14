using Kazo.Os.Platform.Core.CloudAccounts;
using Kazo.Os.Simulation.Abstractions.Events;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Cryptography;

namespace Kazo.Os.Platform.Core.CloudDrives.S3;

/// <summary>
/// The <c>s3</c> drive source (ADR 0267 §3 and §6): a bucket of any S3 compatible service
/// (Cloudflare R2, Backblaze B2, MinIO, AWS), or a prefix of it, mounted as a network drive.
/// </summary>
/// <remarks>
/// <para>
/// What the user types comes in <see cref="NetworkDriveRequest.Options"/> (<c>endpoint</c>,
/// <c>region</c>, <c>bucket</c>, <c>prefix</c>, <c>addressing</c>) and
/// <see cref="NetworkDriveRequest.Secrets"/> (<c>access-key-id</c>, <c>secret-access-key</c>). The
/// source checks the bucket with one listing, then keeps the service and the keys as a cloud account
/// of provider <c>s3</c> (<see cref="CloudAccountService"/>), sealed on the device under a key of the
/// keyring (ADR 0269). The drive list keeps only the account reference and <c>bucket/prefix</c>
/// as its root. A request with <see cref="NetworkDriveRequest.Account"/> and no secrets mounts
/// another bucket with the keys of that account.
/// </para>
/// <para>
/// No app registration: the user puts a CORS rule on the bucket for the address of Kazo OS
/// (<c>doc/en/wasm/dev/s3-cors.md</c>).
/// </para>
/// </remarks>
public sealed class S3DriveSource : INetworkDriveSource, IDisposable
{
    /// <summary>How often a drive that lost the network asks the service again.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    private readonly CloudAccountService _accounts;
    private readonly S3Client _client;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly IHostActivitySignal? _activity;
    private readonly IDisposable[] _subscriptions;
    private readonly Dictionary<string, S3Connection> _connections = new(StringComparer.Ordinal);
    private readonly List<WeakReference<S3Folder>> _folders = [];
    private readonly Lock _lock = new();
    private ITimer? _retry;

    /// <summary>Initializes the source.</summary>
    /// <param name="accounts">The cloud accounts, where the keys are sealed.</param>
    /// <param name="http">The HTTP client of the S3 requests (the browser's <c>fetch</c>).</param>
    /// <param name="eventBus">Logout events: the keys held in memory are dropped; <see langword="null"/> in tests.</param>
    /// <param name="activity">Page shown again: an unreachable drive tries at once; <see langword="null"/> when the host has none.</param>
    /// <param name="timeProvider">The real clock, for the signatures and the retry timer.</param>
    /// <param name="logger">Logs drives that cannot be opened (never a key).</param>
    public S3DriveSource(
        CloudAccountService accounts,
        HttpClient http,
        IEventBus? eventBus = null,
        IHostActivitySignal? activity = null,
        TimeProvider? timeProvider = null,
        ILogger<S3DriveSource>? logger = null)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _time = timeProvider ?? TimeProvider.System;
        _client = new S3Client(http ?? throw new ArgumentNullException(nameof(http)), _time);
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _activity = activity;
        _accounts.Changed += OnAccountsChanged;
        if (_activity is not null)
        {
            _activity.Resumed += OnResumed;
        }

        _subscriptions = eventBus is null
            ? []
            : [eventBus.Subscribe<SessionLoggedOutEvent>(_ => ClearConnections())];
    }

    /// <inheritdoc />
    public string Type => S3Connection.ProviderId;

    /// <inheritdoc />
    public event Action? AvailabilityChanged;

    /// <summary>S3 needs nothing from the browser but <c>fetch</c>: always available.</summary>
    public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(true);

    /// <inheritdoc />
    /// <exception cref="ArgumentException">
    /// A value is missing or wrong, or the service refused: the message says what to fix (the
    /// address, the keys, the bucket, the CORS rule, a locked keyring).
    /// </exception>
    public async ValueTask<NetworkDriveCreation?> CreateAsync(NetworkDriveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyDictionary<string, string> options = request.Options ?? new Dictionary<string, string>();
        IReadOnlyDictionary<string, string> secrets = request.Secrets ?? new Dictionary<string, string>();
        (string bucket, string prefix) = Location(options, request.Root);

        S3Connection connection;
        string? accountId = null;
        if (request.Account is { Length: > 0 } existing && !secrets.ContainsKey("secret-access-key"))
        {
            connection = await OpenKeysAsync(existing, cancellationToken).ConfigureAwait(false)
                ?? throw new ArgumentException($"s3: the keys of account '{existing}' cannot be opened; type them again.", nameof(request));
            accountId = existing;
        }
        else
        {
            connection = S3Connection.Parse(
                Get(options, "endpoint"),
                Get(options, "region"),
                Get(options, "addressing"),
                Get(secrets, "access-key-id"),
                Get(secrets, "secret-access-key"));
        }

        connection.CheckBucket(bucket);
        S3Folder probe = new(_client, _ => ValueTask.FromResult<S3Connection?>(connection), bucket, prefix, DriveName(bucket, prefix));
        await CheckAsync(probe, connection, bucket, cancellationToken).ConfigureAwait(false);

        if (accountId is null)
        {
            byte[] secret = connection.Serialize();
            try
            {
                CloudAccount account = await _accounts
                    .SaveKeysAccountAsync(S3Connection.ProviderId, connection.DisplayName, connection.AccountKey, secret, cancellationToken)
                    .ConfigureAwait(false);
                accountId = account.Id;
            }
            catch (CloudAccountException exception)
            {
                throw new ArgumentException($"s3: {exception.Message}", nameof(request), exception);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secret);
            }
        }

        lock (_lock)
        {
            _connections[accountId] = connection;
        }

        string root = prefix.Length == 0 ? bucket : $"{bucket}/{prefix}";
        NetworkDriveReference reference = new(DriveName(bucket, prefix), Account: accountId, Root: root);
        return new NetworkDriveCreation(Track(new S3Folder(_client, token => OpenKeysAsync(accountId, token), bucket, prefix, reference.Name)), reference, bucket);
    }

    /// <inheritdoc />
    public ValueTask<IHostFolder?> OpenAsync(NetworkDriveReference reference, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.Account is not { Length: > 0 } accountId || reference.Root is not { Length: > 0 } root)
        {
            return ValueTask.FromResult<IHostFolder?>(null);
        }

        (string bucket, string prefix) = SplitRoot(root);
        if (bucket.Length == 0)
        {
            return ValueTask.FromResult<IHostFolder?>(null);
        }

        // The keys are opened at the first request, not here: the login never waits for them.
        return ValueTask.FromResult<IHostFolder?>(Track(new S3Folder(_client, token => OpenKeysAsync(accountId, token), bucket, prefix, reference.Name)));
    }

    /// <summary>Nothing to forget: the keys belong to the account, which the user removes in Settings.</summary>
    public ValueTask ForgetAsync(NetworkDriveReference reference, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public void Dispose()
    {
        _accounts.Changed -= OnAccountsChanged;
        if (_activity is not null)
        {
            _activity.Resumed -= OnResumed;
        }

        foreach (IDisposable subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _retry?.Dispose();
        ClearConnections();
    }

    /// <summary>Raises <see cref="AvailabilityChanged"/> when a drive lost the network; the retry timer and the page coming back call it.</summary>
    internal void RetryUnreachable()
    {
        bool any;
        lock (_lock)
        {
            _folders.RemoveAll(static reference => !reference.TryGetTarget(out _));
            any = _folders.Any(static reference => reference.TryGetTarget(out S3Folder? folder) && folder.IsUnreachable);
        }

        if (any)
        {
            AvailabilityChanged?.Invoke();
        }
    }

    private async ValueTask CheckAsync(S3Folder probe, S3Connection connection, string bucket, CancellationToken cancellationToken)
    {
        try
        {
            await probe.CheckAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HostFolderException exception)
        {
            string host = connection.Endpoint.Host;
            string message = exception switch
            {
                { InnerException: S3ServiceException { KeysRefused: true } } or { InnerException.InnerException: S3ServiceException { KeysRefused: true } } =>
                    $"s3: {host} refused the keys (unknown access key or wrong secret, or a wrong region: '{connection.Region}').",
                { InnerException: S3ServiceException { Code: "NoSuchBucket" } } or { Kind: HostFolderErrorKind.NotFound } =>
                    $"s3: {host} has no bucket '{bucket}'.",
                { Kind: HostFolderErrorKind.NotAllowed } =>
                    $"s3: the keys may not list the bucket '{bucket}' on {host}.",
                { Kind: HostFolderErrorKind.Unavailable } =>
                    $"s3: {host} cannot be reached. Check the address and the network, and that the bucket has a CORS rule for the address of Kazo OS (see the S3 CORS guide).",
                _ => $"s3: {host} refused to list the bucket '{bucket}' ({exception.Message})."
            };
            throw new ArgumentException(message, nameof(bucket), exception);
        }
    }

    private async ValueTask<S3Connection?> OpenKeysAsync(string accountId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_connections.TryGetValue(accountId, out S3Connection? cached))
            {
                return cached;
            }
        }

        byte[]? secret = null;
        try
        {
            (_, secret) = await _accounts.GetKeysAsync(accountId, S3Connection.ProviderId, cancellationToken).ConfigureAwait(false);
            S3Connection? connection = S3Connection.Deserialize(secret);
            if (connection is not null)
            {
                lock (_lock)
                {
                    _connections[accountId] = connection;
                }
            }

            return connection;
        }
        catch (CloudAccountException exception)
        {
            _logger.LogInformation("The keys of S3 account {AccountId} cannot be opened: {Error}.", accountId, exception.Error);
            return null;
        }
        finally
        {
            if (secret is not null)
            {
                CryptographicOperations.ZeroMemory(secret);
            }
        }
    }

    private S3Folder Track(S3Folder folder)
    {
        lock (_lock)
        {
            _folders.Add(new WeakReference<S3Folder>(folder));
            _retry ??= _time.CreateTimer(_ => RetryUnreachable(), null, RetryInterval, RetryInterval);
        }

        return folder;
    }

    private void OnAccountsChanged()
    {
        // New keys typed for an account, or an account removed: the next request opens them again,
        // and a drive waiting for them may work now.
        ClearConnections();
        AvailabilityChanged?.Invoke();
    }

    private void OnResumed() => RetryUnreachable();

    private void ClearConnections()
    {
        lock (_lock)
        {
            _connections.Clear();
        }
    }

    /// <summary>The bucket and the prefix: the <c>bucket</c> and <c>prefix</c> options, or the root <c>bucket/prefix</c>.</summary>
    private static (string Bucket, string Prefix) Location(IReadOnlyDictionary<string, string> options, string? root)
    {
        (string rootBucket, string rootPrefix) = root is { Length: > 0 } ? SplitRoot(root) : (string.Empty, string.Empty);
        string bucket = Get(options, "bucket")?.Trim() is { Length: > 0 } named ? named : rootBucket;
        if (bucket.Length == 0)
        {
            throw new ArgumentException("s3: the bucket is missing.", nameof(options));
        }

        string prefix = Get(options, "prefix") is { } typed ? NormalizePrefix(typed) : rootPrefix;
        return (bucket, prefix);
    }

    private static (string Bucket, string Prefix) SplitRoot(string root)
    {
        string trimmed = root.Trim().Trim('/');
        int slash = trimmed.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? (trimmed, string.Empty) : (trimmed[..slash], NormalizePrefix(trimmed[(slash + 1)..]));
    }

    /// <summary>A prefix as segments joined by <c>/</c>, with no empty, <c>.</c> or <c>..</c> segment.</summary>
    private static string NormalizePrefix(string prefix)
    {
        string[] segments = prefix.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Any(static segment => segment is "." or ".."))
        {
            throw new ArgumentException($"s3: '{prefix}' is not a prefix ('.' and '..' are not folders in S3).", nameof(prefix));
        }

        return string.Join('/', segments);
    }

    private static string DriveName(string bucket, string prefix) => prefix.Length == 0 ? bucket : $"{bucket}/{prefix}";

    private static string? Get(IReadOnlyDictionary<string, string> values, string name) =>
        values.TryGetValue(name, out string? value) ? value : null;
}
