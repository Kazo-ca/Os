using System.Security.Cryptography;
using Kazo.Os.Platform.Core.Keyring;
using Kazo.Os.Simulation.Abstractions.CloudAccounts;
using Kazo.Os.Simulation.Abstractions.Events;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Simulation.Abstractions.Time;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kazo.Os.Platform.Core.CloudAccounts;

/// <summary>
/// The cloud accounts of the signed-in user (ADR 0267 section 5): sign-in by OAuth 2.0 with PKCE in
/// a browser window that returns to a static page of Kazo OS, tokens sealed on the device under a
/// key of the user's keyring, renewal before expiry, reconnection and removal. Connectors
/// (Dropbox, OneDrive) ask it for an access token and never see a refresh token; apps never see
/// any token.
/// </summary>
/// <remarks>
/// <para>
/// Everything is per user and per device. An account whose provider no longer renews its token
/// becomes <see cref="CloudAccountState.NeedsReconnect"/> and keeps its drives: reconnecting it
/// (same provider account) brings them back.
/// </para>
/// <para>
/// A user whose keyring is locked (the guest, who has no password, or a sign-in by a passkey
/// without PRF) cannot connect an account: <see cref="CloudAccountError.KeyringLocked"/>.
/// </para>
/// </remarks>
public sealed class CloudAccountService : IDisposable, CloudDrives.ICloudAccountTokens
{
    /// <summary>How long the OS waits for the sign-in window's answer.</summary>
    public static readonly TimeSpan AuthorizationTimeout = TimeSpan.FromMinutes(10);

    /// <summary>An access token is renewed when it expires within this margin, so a request never starts with a dying token.</summary>
    public static readonly TimeSpan RenewalMargin = TimeSpan.FromMinutes(2);

    private readonly CloudAccountStore _store;
    private readonly ICloudProviderCatalog _catalog;
    private readonly IOAuthAuthorizationWindow _window;
    private readonly CloudOAuthTokenClient _tokens;
    private readonly Func<AuthenticatedPrincipal?> _principal;
    private readonly Func<ICloudAccountDrives?> _drives;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly IDisposable[] _subscriptions;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<CloudOAuthProvider>? _providers;

    /// <summary>Initializes the service.</summary>
    /// <param name="fileSystem">File system of the account list.</param>
    /// <param name="clock">Clock of the file operations.</param>
    /// <param name="keyring">The session keyring: holds one sealing key per account.</param>
    /// <param name="cipher">AES-256-GCM (Web Crypto in the browser).</param>
    /// <param name="catalog">The configured providers.</param>
    /// <param name="window">The sign-in window and its callback page.</param>
    /// <param name="tokens">The token requests.</param>
    /// <param name="principal">The signed-in user, resolved lazily.</param>
    /// <param name="drives">Releases an account's drives at removal; <see langword="null"/> while no drive can belong to an account.</param>
    /// <param name="eventBus">Logout events (forget the session's providers); <see langword="null"/> in tests.</param>
    /// <param name="timeProvider">Clock of the token expiry.</param>
    /// <param name="logger">Logs failed revocations and unreadable tokens (never a token).</param>
    public CloudAccountService(
        IFileSystemService fileSystem,
        ISimulationClock clock,
        UserKeyringService keyring,
        IKeyringCipher cipher,
        ICloudProviderCatalog catalog,
        IOAuthAuthorizationWindow window,
        CloudOAuthTokenClient tokens,
        Func<AuthenticatedPrincipal?> principal,
        Func<ICloudAccountDrives?>? drives = null,
        IEventBus? eventBus = null,
        TimeProvider? timeProvider = null,
        ILogger<CloudAccountService>? logger = null)
    {
        _store = new CloudAccountStore(fileSystem, clock, keyring, cipher);
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _principal = principal ?? throw new ArgumentNullException(nameof(principal));
        _drives = drives ?? (static () => null);
        _time = timeProvider ?? TimeProvider.System;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _subscriptions = eventBus is null
            ? []
            : [eventBus.Subscribe<SessionLoggedOutEvent>(_ => _providers = null)];
    }

    /// <summary>Raised after an account is added, reconnected, removed or needs a reconnection.</summary>
    public event Action? Changed;

    /// <summary>Gets the address the providers must know as redirect URI (the callback page).</summary>
    public Uri RedirectUri => _window.RedirectUri;

    /// <summary>
    /// Gets whether the signed-in user can connect an account: their keyring is open. The guest
    /// and a passkey sign-in without PRF cannot.
    /// </summary>
    public bool CanConnect => _principal() is { } principal && _store.IsKeyringUnlocked(principal);

    /// <summary>
    /// Loads the sign-in window's script and the providers ahead of the user's click. The dialog
    /// that offers the providers calls it when it opens, so the click opens the window at once.
    /// </summary>
    /// <param name="cancellationToken">Cancels the preparation.</param>
    public async ValueTask PrepareAsync(CancellationToken cancellationToken = default)
    {
        await _window.PrepareAsync(cancellationToken).ConfigureAwait(false);
        await GetProvidersAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gets the providers this OS has app ids for, with the scopes offered.</summary>
    /// <param name="cancellationToken">Cancels the read of <c>/etc/kazoos/cloud-drives.config</c>.</param>
    public async ValueTask<IReadOnlyList<CloudProviderInfo>> GetProvidersAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CloudOAuthProvider> providers = await _catalog.GetProvidersAsync(cancellationToken).ConfigureAwait(false);
        _providers = providers;
        return [.. providers.Select(static provider => new CloudProviderInfo(provider.Id, provider.DisplayName, provider.Scopes))];
    }

    /// <summary>Gets the signed-in user's accounts; none when nobody is signed in.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async ValueTask<IReadOnlyList<CloudAccount>> GetAccountsAsync(CancellationToken cancellationToken = default)
    {
        if (_principal() is not { } principal)
        {
            return [];
        }

        List<CloudAccountRecord> records = await _store.LoadAsync(principal, cancellationToken).ConfigureAwait(false);
        return [.. records.Select(static record => record.Describe())];
    }

    /// <summary>
    /// Signs in to a provider and adds the account. Call it from a user gesture (a click): the
    /// sign-in window opens first, before any other wait. Signing in again to an account already
    /// connected with the same scope renews that account instead of adding a second one.
    /// </summary>
    /// <param name="providerId"><c>dropbox</c> or <c>onedrive</c>.</param>
    /// <param name="scope">The part of the storage the user chose.</param>
    /// <param name="cancellationToken">Stops waiting for the window.</param>
    /// <returns>The account.</returns>
    /// <exception cref="CloudAccountException">The sign-in failed; <see cref="CloudAccountException.Error"/> says why.</exception>
    public async ValueTask<CloudAccount> ConnectAsync(string providerId, CloudAccessScope scope, CancellationToken cancellationToken = default)
    {
        AuthenticatedPrincipal principal = RequireKeyring();
        CloudOAuthProvider provider = await FindProviderAsync(providerId, cancellationToken).ConfigureAwait(false);
        (OAuthTokenSet tokens, CloudIdentity identity) = await SignInAsync(provider, scope, cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        CloudAccountRecord saved;
        try
        {
            List<CloudAccountRecord> records = await _store.LoadAsync(principal, cancellationToken).ConfigureAwait(false);
            CloudAccountRecord? existing = identity.AccountId is { } accountId
                ? records.FirstOrDefault(record => record.Provider == provider.Id && record.Scope == scope && record.ProviderAccountId == accountId)
                : null;
            string id = existing?.Id ?? NewAccountId();
            string displayName = identity.DisplayName ?? existing?.DisplayName ?? provider.DisplayName;
            SealedTokens sealedTokens = await _store
                .SealAsync(principal, id, Label(provider, displayName), tokens, cancellationToken)
                .ConfigureAwait(false);
            saved = new CloudAccountRecord(
                id, provider.Id, scope, displayName, identity.AccountId, CloudAccountState.Connected, _time.GetUtcNow(), sealedTokens);
            records.RemoveAll(record => record.Id == id);
            records.Add(saved);
            await _store.SaveAsync(principal, records, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
        return saved.Describe();
    }

    /// <summary>
    /// Signs in again to an existing account (after <see cref="CloudAccountState.NeedsReconnect"/>,
    /// or to change nothing but the tokens). The provider account must be the same one, so that the
    /// account's drives keep pointing at the same storage. Call it from a user gesture.
    /// </summary>
    /// <param name="accountId">The account.</param>
    /// <param name="cancellationToken">Stops waiting for the window.</param>
    /// <returns>The account, connected again.</returns>
    /// <exception cref="CloudAccountException">
    /// The sign-in failed, or <see cref="CloudAccountError.AccountMismatch"/> when the user signed in
    /// to another account (the account stays as it was).
    /// </exception>
    public async ValueTask<CloudAccount> ReconnectAsync(string accountId, CancellationToken cancellationToken = default)
    {
        AuthenticatedPrincipal principal = RequireKeyring();
        // The account list is read before the window opens; it is a local read, which completes
        // within the click's activation.
        CloudAccountRecord current = await FindAccountAsync(principal, accountId, cancellationToken).ConfigureAwait(false);
        if (current.HasKeys)
        {
            throw KeysAccount(current);
        }

        CloudOAuthProvider provider = await FindProviderAsync(current.Provider, cancellationToken).ConfigureAwait(false);
        (OAuthTokenSet tokens, CloudIdentity identity) = await SignInAsync(provider, current.Scope, cancellationToken).ConfigureAwait(false);
        if (current.ProviderAccountId is { } expected && identity.AccountId is { } actual && expected != actual)
        {
            await _tokens.RevokeAsync(provider, tokens.AccessToken, cancellationToken).ConfigureAwait(false);
            throw new CloudAccountException(
                CloudAccountError.AccountMismatch,
                $"This {provider.DisplayName} account is not the one '{current.DisplayName}' was connected with.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        CloudAccountRecord saved;
        try
        {
            List<CloudAccountRecord> records = await _store.LoadAsync(principal, cancellationToken).ConfigureAwait(false);
            CloudAccountRecord latest = records.FirstOrDefault(record => record.Id == accountId) ?? current;
            string displayName = identity.DisplayName ?? latest.DisplayName;
            SealedTokens sealedTokens = await _store
                .SealAsync(principal, accountId, Label(provider, displayName), tokens, cancellationToken)
                .ConfigureAwait(false);
            saved = latest with
            {
                DisplayName = displayName,
                ProviderAccountId = latest.ProviderAccountId ?? identity.AccountId,
                State = CloudAccountState.Connected,
                ConnectedAtUtc = _time.GetUtcNow(),
                Tokens = sealedTokens
            };
            records.RemoveAll(record => record.Id == accountId);
            records.Add(saved);
            await _store.SaveAsync(principal, records, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
        return saved.Describe();
    }

    /// <summary>
    /// Gets a valid access token of an account for a connector, renewed first when it expires
    /// within <see cref="RenewalMargin"/>.
    /// </summary>
    /// <param name="accountId">The account.</param>
    /// <param name="cancellationToken">Cancels the renewal.</param>
    /// <returns>The access token.</returns>
    /// <exception cref="CloudAccountException">
    /// <see cref="CloudAccountError.ReconnectRequired"/> (the account is now
    /// <see cref="CloudAccountState.NeedsReconnect"/>), <see cref="CloudAccountError.ProviderUnavailable"/>
    /// (offline: try again later), <see cref="CloudAccountError.KeyringLocked"/> or
    /// <see cref="CloudAccountError.AccountNotFound"/>.
    /// </exception>
    public ValueTask<string> GetAccessTokenAsync(string accountId, CancellationToken cancellationToken = default) =>
        GetAccessTokenCoreAsync(accountId, rejectedAccessToken: null, cancellationToken);

    /// <summary>
    /// Renews the access token of an account the provider just refused (an HTTP 401 before the
    /// expiry the OS knows: the provider revoked or shortened it). When another request already
    /// renewed it, the newer token is returned without asking the provider again.
    /// </summary>
    /// <param name="accountId">The account.</param>
    /// <param name="rejectedAccessToken">The token the provider refused.</param>
    /// <param name="cancellationToken">Cancels the renewal.</param>
    /// <returns>A new access token.</returns>
    /// <exception cref="CloudAccountException">As <see cref="GetAccessTokenAsync"/>.</exception>
    public ValueTask<string> RenewAccessTokenAsync(string accountId, string rejectedAccessToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(rejectedAccessToken);
        return GetAccessTokenCoreAsync(accountId, rejectedAccessToken, cancellationToken);
    }

    /// <summary>Finds an account of the signed-in user; <see langword="null"/> when it is not in the list (or nobody is signed in).</summary>
    /// <param name="accountId">The account.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async ValueTask<CloudAccount?> FindAccountAsync(string accountId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CloudAccount> accounts = await GetAccountsAsync(cancellationToken).ConfigureAwait(false);
        return accounts.FirstOrDefault(account => string.Equals(account.Id, accountId, StringComparison.Ordinal));
    }

    private async ValueTask<string> GetAccessTokenCoreAsync(string accountId, string? rejectedAccessToken, CancellationToken cancellationToken)
    {
        AuthenticatedPrincipal principal = RequireKeyring();
        bool changed = false;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<CloudAccountRecord> records = await _store.LoadAsync(principal, cancellationToken).ConfigureAwait(false);
            CloudAccountRecord account = records.FirstOrDefault(record => record.Id == accountId)
                ?? throw NotFound(accountId);
            if (account.HasKeys)
            {
                throw KeysAccount(account);
            }

            if (account.State == CloudAccountState.NeedsReconnect)
            {
                throw ReconnectRequired(account);
            }

            OAuthTokenSet? tokens = await _store.UnsealAsync(principal, account, cancellationToken).ConfigureAwait(false);
            if (tokens is null)
            {
                _logger.LogWarning("The tokens of cloud account {AccountId} cannot be opened; it needs a reconnection.", accountId);
                changed = await MarkNeedsReconnectAsync(principal, records, account, cancellationToken).ConfigureAwait(false);
                throw ReconnectRequired(account);
            }

            bool rejected = rejectedAccessToken is not null
                && string.Equals(tokens.AccessToken, rejectedAccessToken, StringComparison.Ordinal);
            if (!rejected && (tokens.ExpiresAtUtc is not { } expiresAt || expiresAt - _time.GetUtcNow() > RenewalMargin))
            {
                return tokens.AccessToken;
            }

            CloudOAuthProvider provider = await FindProviderAsync(account.Provider, cancellationToken).ConfigureAwait(false);
            if (tokens.RefreshToken is not { } refreshToken)
            {
                changed = await MarkNeedsReconnectAsync(principal, records, account, cancellationToken).ConfigureAwait(false);
                throw ReconnectRequired(account);
            }

            OAuthTokenSet renewed;
            try
            {
                renewed = await _tokens.RefreshAsync(provider, account.Scope, refreshToken, cancellationToken).ConfigureAwait(false);
            }
            catch (CloudAccountException exception) when (exception.Error is CloudAccountError.ReconnectRequired or CloudAccountError.Denied)
            {
                changed = await MarkNeedsReconnectAsync(principal, records, account, cancellationToken).ConfigureAwait(false);
                throw ReconnectRequired(account, exception);
            }

            SealedTokens sealedTokens = await _store
                .SealAsync(principal, account.Id, Label(provider, account.DisplayName), renewed, cancellationToken)
                .ConfigureAwait(false);
            records[records.IndexOf(account)] = account with { Tokens = sealedTokens };
            await _store.SaveAsync(principal, records, cancellationToken).ConfigureAwait(false);
            return renewed.AccessToken;
        }
        finally
        {
            _gate.Release();
            if (changed)
            {
                Changed?.Invoke();
            }
        }
    }

    /// <summary>
    /// Adds an account that signs its own requests with keys the user typed (S3, ADR 0267 section 6),
    /// or gives new keys to the account of the same provider with the same
    /// <paramref name="providerAccountId"/> (the access key and the address), which connects it again.
    /// The keys are sealed like tokens (ADR 0269): on the device, under a key of the keyring.
    /// </summary>
    /// <param name="providerId">The provider, e.g. <c>s3</c>.</param>
    /// <param name="displayName">What the account list shows, e.g. the address of the service.</param>
    /// <param name="providerAccountId">The provider's id of the account; never a secret.</param>
    /// <param name="secret">The keys, as the connector serializes them; cleared by the caller.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The account.</returns>
    /// <exception cref="CloudAccountException"><see cref="CloudAccountError.KeyringLocked"/> or <see cref="CloudAccountError.NotSignedIn"/>.</exception>
    internal async ValueTask<CloudAccount> SaveKeysAccountAsync(
        string providerId,
        string displayName,
        string providerAccountId,
        byte[] secret,
        CancellationToken cancellationToken = default)
    {
        AuthenticatedPrincipal principal = RequireKeyring();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        CloudAccountRecord saved;
        try
        {
            List<CloudAccountRecord> records = await _store.LoadAsync(principal, cancellationToken).ConfigureAwait(false);
            CloudAccountRecord? existing = records.FirstOrDefault(record =>
                record.HasKeys && record.Provider == providerId && record.ProviderAccountId == providerAccountId);
            string id = existing?.Id ?? NewAccountId();
            SealedTokens sealedKeys = await _store
                .SealBytesAsync(principal, id, $"{displayName} (cloud drive)", secret, cancellationToken)
                .ConfigureAwait(false);
            saved = new CloudAccountRecord(
                id, providerId, CloudAccessScope.Full, displayName, providerAccountId, CloudAccountState.Connected, _time.GetUtcNow(), sealedKeys)
            {
                Credential = CloudAccountRecord.KeysCredential
            };
            records.RemoveAll(record => record.Id == id);
            records.Add(saved);
            await _store.SaveAsync(principal, records, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
        return saved.Describe();
    }

    /// <summary>
    /// Opens the keys of an account added by <see cref="SaveKeysAccountAsync"/>, for its connector.
    /// The caller clears the returned bytes.
    /// </summary>
    /// <param name="accountId">The account.</param>
    /// <param name="providerId">The provider the connector expects; another provider reads as not found.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The account and its keys.</returns>
    /// <exception cref="CloudAccountException">
    /// <see cref="CloudAccountError.ReconnectRequired"/> when the keys cannot be opened any more (the
    /// account is now <see cref="CloudAccountState.NeedsReconnect"/> until the user types them again),
    /// <see cref="CloudAccountError.KeyringLocked"/> or <see cref="CloudAccountError.AccountNotFound"/>.
    /// </exception>
    internal async ValueTask<(CloudAccount Account, byte[] Secret)> GetKeysAsync(
        string accountId,
        string providerId,
        CancellationToken cancellationToken = default)
    {
        AuthenticatedPrincipal principal = RequireKeyring();
        bool changed = false;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<CloudAccountRecord> records = await _store.LoadAsync(principal, cancellationToken).ConfigureAwait(false);
            CloudAccountRecord account = records.FirstOrDefault(record =>
                    record.Id == accountId && record.HasKeys && record.Provider == providerId)
                ?? throw NotFound(accountId);
            if (account.State == CloudAccountState.NeedsReconnect)
            {
                throw ReconnectRequired(account);
            }

            byte[]? secret = await _store.UnsealBytesAsync(principal, account, cancellationToken).ConfigureAwait(false);
            if (secret is null)
            {
                _logger.LogWarning("The keys of cloud account {AccountId} cannot be opened; they must be typed again.", accountId);
                changed = await MarkNeedsReconnectAsync(principal, records, account, cancellationToken).ConfigureAwait(false);
                throw ReconnectRequired(account);
            }

            return (account.Describe(), secret);
        }
        finally
        {
            _gate.Release();
            if (changed)
            {
                Changed?.Invoke();
            }
        }
    }

    /// <summary>
    /// Removes an account (ADR 0267 section 5): unmounts its drives, revokes its token when the
    /// provider allows it, deletes its key from the keyring and forgets it.
    /// </summary>
    /// <param name="accountId">The account.</param>
    /// <param name="cancellationToken">Cancels the removal.</param>
    /// <exception cref="CloudAccountException"><see cref="CloudAccountError.AccountNotFound"/>, or <see cref="CloudAccountError.NotSignedIn"/>.</exception>
    public async ValueTask RemoveAsync(string accountId, CancellationToken cancellationToken = default)
    {
        AuthenticatedPrincipal principal = RequirePrincipal();
        CloudAccountRecord account = await FindAccountAsync(principal, accountId, cancellationToken).ConfigureAwait(false);
        if (_drives() is { } drives)
        {
            await drives.ReleaseAsync(accountId, cancellationToken).ConfigureAwait(false);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_store.IsKeyringUnlocked(principal))
            {
                await RevokeAsync(principal, account, cancellationToken).ConfigureAwait(false);
                await _store.DeleteKeyAsync(principal, accountId, cancellationToken).ConfigureAwait(false);
            }

            List<CloudAccountRecord> records = await _store.LoadAsync(principal, cancellationToken).ConfigureAwait(false);
            records.RemoveAll(record => record.Id == accountId);
            await _store.SaveAsync(principal, records, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (IDisposable subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _gate.Dispose();
    }

    private async ValueTask<(OAuthTokenSet Tokens, CloudIdentity Identity)> SignInAsync(
        CloudOAuthProvider provider,
        CloudAccessScope scope,
        CancellationToken cancellationToken)
    {
        OAuthPkce pkce = OAuthPkce.Create();
        Uri redirectUri = _window.RedirectUri;
        Uri authorizationUri = provider.BuildAuthorizationUri(scope, pkce, redirectUri);
        OAuthAuthorizationResult result = await _window
            .AuthorizeAsync(authorizationUri, pkce.State, AuthorizationTimeout, cancellationToken)
            .ConfigureAwait(false);
        string code = result switch
        {
            { Status: OAuthAuthorizationStatus.Authorized, Code: { Length: > 0 } authorized } => authorized,
            { Status: OAuthAuthorizationStatus.Blocked } => throw new CloudAccountException(
                CloudAccountError.WindowBlocked, "The browser blocked the sign-in window; allow pop-ups for Kazo OS and try again."),
            { Status: OAuthAuthorizationStatus.Failed } => throw new CloudAccountException(
                CloudAccountError.Denied,
                $"{provider.DisplayName} refused the sign-in ({result.Error}{(result.ErrorDescription is { } description ? $": {description}" : string.Empty)})."),
            _ => throw new CloudAccountException(CloudAccountError.Cancelled, $"The {provider.DisplayName} sign-in was not completed.")
        };

        OAuthTokenSet tokens = await _tokens
            .ExchangeCodeAsync(provider, scope, code, pkce.Verifier, redirectUri, cancellationToken)
            .ConfigureAwait(false);
        CloudIdentity identity = await _tokens.ReadIdentityAsync(provider, tokens.AccessToken, cancellationToken).ConfigureAwait(false);
        return (tokens, identity);
    }

    private async ValueTask RevokeAsync(AuthenticatedPrincipal principal, CloudAccountRecord account, CancellationToken cancellationToken)
    {
        IReadOnlyList<CloudOAuthProvider> providers = _providers ?? await _catalog.GetProvidersAsync(cancellationToken).ConfigureAwait(false);
        if (providers.FirstOrDefault(provider => provider.Id == account.Provider) is not { } provider
            || await _store.UnsealAsync(principal, account, cancellationToken).ConfigureAwait(false) is not { } tokens)
        {
            return;
        }

        if (!await _tokens.RevokeAsync(provider, tokens.AccessToken, cancellationToken).ConfigureAwait(false) && provider.RevocationEndpoint is not null)
        {
            _logger.LogInformation("{Provider} did not confirm the revocation of cloud account {AccountId}.", provider.DisplayName, account.Id);
        }
    }

    private async ValueTask<bool> MarkNeedsReconnectAsync(
        AuthenticatedPrincipal principal,
        List<CloudAccountRecord> records,
        CloudAccountRecord account,
        CancellationToken cancellationToken)
    {
        int index = records.IndexOf(account);
        if (index < 0 || account.State == CloudAccountState.NeedsReconnect)
        {
            return false;
        }

        records[index] = account with { State = CloudAccountState.NeedsReconnect };
        await _store.SaveAsync(principal, records, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async ValueTask<CloudOAuthProvider> FindProviderAsync(string providerId, CancellationToken cancellationToken)
    {
        // The providers listed by the dialog are used as they are: a read here would be one more
        // wait between the click and the window.
        IReadOnlyList<CloudOAuthProvider> providers = _providers ?? await _catalog.GetProvidersAsync(cancellationToken).ConfigureAwait(false);
        _providers = providers;
        return providers.FirstOrDefault(provider => string.Equals(provider.Id, providerId, StringComparison.Ordinal))
            ?? throw new CloudAccountException(
                CloudAccountError.ProviderNotConfigured,
                $"'{providerId}' is not a cloud provider this Kazo OS can connect.");
    }

    private async ValueTask<CloudAccountRecord> FindAccountAsync(AuthenticatedPrincipal principal, string accountId, CancellationToken cancellationToken)
    {
        List<CloudAccountRecord> records = await _store.LoadAsync(principal, cancellationToken).ConfigureAwait(false);
        return records.FirstOrDefault(record => record.Id == accountId) ?? throw NotFound(accountId);
    }

    private AuthenticatedPrincipal RequirePrincipal() =>
        _principal() ?? throw new CloudAccountException(CloudAccountError.NotSignedIn, "Nobody is signed in.");

    private AuthenticatedPrincipal RequireKeyring()
    {
        AuthenticatedPrincipal principal = RequirePrincipal();
        return _store.IsKeyringUnlocked(principal)
            ? principal
            : throw new CloudAccountException(
                CloudAccountError.KeyringLocked,
                "Cloud accounts need a keyring to keep their tokens safe, and this session's keyring is locked. "
                + "An account without a password (such as the guest) has none: set a password with passwd, sign out and sign in again.");
    }

    private static CloudAccountException NotFound(string accountId) =>
        new(CloudAccountError.AccountNotFound, $"No cloud account '{accountId}'.");

    private static CloudAccountException KeysAccount(CloudAccountRecord account) =>
        new(CloudAccountError.ProviderNotConfigured,
            $"'{account.DisplayName}' signs its requests with keys and has no token; type its keys again to reconnect it.");

    private static CloudAccountException ReconnectRequired(CloudAccountRecord account, Exception? inner = null) =>
        new(CloudAccountError.ReconnectRequired, $"'{account.DisplayName}' must be signed in again.", inner);

    private static string Label(CloudOAuthProvider provider, string displayName) =>
        string.Equals(provider.DisplayName, displayName, StringComparison.Ordinal)
            ? $"{provider.DisplayName} (cloud drive)"
            : $"{provider.DisplayName} — {displayName} (cloud drive)";

    private static string NewAccountId() => OAuthPkce.Base64Url(RandomNumberGenerator.GetBytes(12));
}
