using Kazo.Os.Platform.Core.CloudAccounts;
using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.Platform.Core.CloudDrives;

/// <summary>
/// Base of the drive sources of cloud accounts (ADR 0267 §3 and §5): Dropbox, OneDrive. The user
/// connects the account first (the sign-in window opens in the click), then mounts a drive of it:
/// <see cref="CreateAsync"/> opens no window and needs <see cref="NetworkDriveRequest.Account"/>.
/// </summary>
/// <remarks>
/// <para>
/// The drive list keeps the account id and the root; never a token. <see cref="OpenAsync"/>
/// reaches no service. Forgetting a drive leaves its account connected: removing the account is
/// the Settings page's job, and it releases the drives (<see cref="ICloudAccountDrives"/>).
/// </para>
/// <para>
/// <see cref="INetworkDriveSource.AvailabilityChanged"/> is raised when an account changes (it was
/// reconnected) and, through <see cref="IHostActivitySignal.Resumed"/>, when the page comes back
/// or the network returns, so a disconnected drive tries again by itself (§9).
/// </para>
/// </remarks>
public abstract class CloudDriveSource : INetworkDriveSource, IDisposable
{
    private readonly ICloudProviderCatalog _catalog;
    private readonly IHostActivitySignal? _activity;
    private bool _disposed;

    /// <summary>Initializes the source.</summary>
    /// <param name="tokens">The cloud accounts.</param>
    /// <param name="catalog">The configured providers: the source is available when its provider is.</param>
    /// <param name="http">The HTTP client of the connector.</param>
    /// <param name="activity">The page's activity; <see langword="null"/> in tests.</param>
    protected CloudDriveSource(ICloudAccountTokens tokens, ICloudProviderCatalog catalog, HttpClient http, IHostActivitySignal? activity = null)
    {
        Tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        Http = http ?? throw new ArgumentNullException(nameof(http));
        _activity = activity;
        Tokens.Changed += OnAvailable;
        if (_activity is not null)
        {
            _activity.Resumed += OnAvailable;
        }
    }

    /// <inheritdoc />
    public abstract string Type { get; }

    /// <summary>Gets the provider id of the accounts this source mounts (<see cref="CloudAccount.ProviderId"/>).</summary>
    public abstract string ProviderId { get; }

    /// <summary>Gets the default mount point name under <c>/mnt</c>, e.g. <c>Dropbox</c>.</summary>
    public abstract string DefaultMountName { get; }

    /// <summary>Gets the cloud accounts.</summary>
    protected ICloudAccountTokens Tokens { get; }

    /// <summary>Gets the HTTP client of the connector.</summary>
    protected HttpClient Http { get; }

    /// <inheritdoc />
    public event Action? AvailabilityChanged;

    /// <summary>Available when this OS has an app id for the provider.</summary>
    public async ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CloudOAuthProvider> providers = await _catalog.GetProvidersAsync(cancellationToken).ConfigureAwait(false);
        return providers.Any(provider => string.Equals(provider.Id, ProviderId, StringComparison.Ordinal));
    }

    /// <summary>Builds a drive of a connected account; opens no window.</summary>
    /// <exception cref="ArgumentException">No account, an unknown one, an account of another provider, or a root the provider refuses.</exception>
    public async ValueTask<NetworkDriveCreation?> CreateAsync(NetworkDriveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(request.Account))
        {
            throw new ArgumentException($"{Type}: choose a connected account first.", nameof(request));
        }

        CloudAccount account = await Tokens.FindAccountAsync(request.Account, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException($"{request.Account}: no such cloud account.", nameof(request));
        if (!string.Equals(account.ProviderId, ProviderId, StringComparison.Ordinal))
        {
            throw new ArgumentException($"{account.DisplayName} is not a {DefaultMountName} account.", nameof(request));
        }

        string? root = NormalizeRoot(request.Root);
        NetworkDriveReference reference = new(account.DisplayName, Account: account.Id, Root: root);
        return new NetworkDriveCreation(CreateFolder(new CloudDriveConnection(Tokens, account.Id, Http), account, root), reference, DefaultMountName);
    }

    /// <summary>Opens a drive of the list again without reaching the service; <see langword="null"/> when its account was removed.</summary>
    public async ValueTask<IHostFolder?> OpenAsync(NetworkDriveReference reference, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.Account is not { Length: > 0 } accountId
            || await Tokens.FindAccountAsync(accountId, cancellationToken).ConfigureAwait(false) is not { } account
            || !string.Equals(account.ProviderId, ProviderId, StringComparison.Ordinal))
        {
            return null;
        }

        string? root;
        try
        {
            root = NormalizeRoot(reference.Root);
        }
        catch (ArgumentException)
        {
            // The list is the user's own file: a root the provider cannot use drops the drive.
            return null;
        }

        return CreateFolder(new CloudDriveConnection(Tokens, account.Id, Http), account, root);
    }

    /// <summary>Nothing to forget: the account stays connected.</summary>
    public ValueTask ForgetAsync(NetworkDriveReference reference, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Builds the folder of a drive.</summary>
    /// <param name="connection">The drive's link to its account.</param>
    /// <param name="account">The account.</param>
    /// <param name="root">The normalized root, or <see langword="null"/> for the whole storage the account reaches.</param>
    protected abstract IHostFolder CreateFolder(CloudDriveConnection connection, CloudAccount account, string? root);

    /// <summary>Normalizes the root the user chose; <see langword="null"/> for the whole storage.</summary>
    /// <param name="root">The root as typed, or <see langword="null"/>.</param>
    /// <exception cref="ArgumentException">The provider cannot use it.</exception>
    protected virtual string? NormalizeRoot(string? root) => root;

    /// <summary>Stops listening to the accounts and the page.</summary>
    /// <param name="disposing">Whether managed state is released.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed || !disposing)
        {
            return;
        }

        _disposed = true;
        Tokens.Changed -= OnAvailable;
        if (_activity is not null)
        {
            _activity.Resumed -= OnAvailable;
        }
    }

    private void OnAvailable() => AvailabilityChanged?.Invoke();
}
