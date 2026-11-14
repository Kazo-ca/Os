using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.AppSdk.Keyring;
using Kazo.Os.Platform.Core.CloudAccounts;
using Kazo.Os.Platform.Core.Keyring;
using Kazo.Os.Platform.Core.ServerConnection;
using Kazo.Os.Platform.Core.Sessions;
using Kazo.Os.Platform.Core.Tests.Settings;
using Kazo.Os.Simulation.Abstractions.CloudAccounts;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Tests.CloudAccounts;

/// <summary>
/// ADR 0267 section 5: cloud accounts sign in with OAuth and PKCE through the sign-in window, keep
/// their tokens sealed on the device under a key of the user's keyring, renew them before expiry,
/// need a reconnection when the provider stops renewing, and are removed with their drives.
/// </summary>
public sealed class CloudAccountServiceTests
{
    private const string Table = "/home/alice/.local/state/kazoos/cloud-accounts.json";
    private const string KeyringFile = "/home/alice/.local/share/keyrings/login.keyring";

    private readonly SettingsFilesHarness _harness = new(withKeyring: true);
    private readonly FakeOAuthServer _server = new();
    private readonly MutableTimeProvider _time = new(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
    private readonly RecordingDrives _drives = new();

    [Fact]
    public async Task Connecting_signs_in_with_pkce_and_keeps_the_tokens_sealed_on_the_device_only()
    {
        await _harness.LoginAsync(_harness.Alice);
        CloudAccountService accounts = CreateService();

        CloudAccount account = await accounts.ConnectAsync("dropbox", CloudAccessScope.Full);

        Assert.Equal("alice@example.test", account.DisplayName);
        Assert.Equal("dbid:alice", account.ProviderAccountId);
        Assert.Equal(CloudAccountState.Connected, account.State);
        Dictionary<string, string> exchange = Assert.Single(_server.TokenRequests);
        Assert.Equal("authorization_code", exchange["grant_type"]);
        Assert.Equal(FakeOAuthServer.ClientId, exchange["client_id"]);
        Assert.False(exchange.ContainsKey("client_secret"));
        Assert.Contains("token_access_type=offline", _server.AuthorizationRequests[0].Query, StringComparison.Ordinal);

        // The list is owner-only and device-local, and holds no token in clear.
        string table = (await _harness.ReadAsync(Table))!;
        Assert.DoesNotContain("access-", table, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-", table, StringComparison.Ordinal);
        Assert.Equal(0b110_000_000, (await _harness.StatAsync(Table))!.Permissions.Mode);
        Assert.True(FileSystemSyncService.IsDeviceLocal(CloudAccountStore.TableRelativePath));
        Assert.False(FileSystemSyncService.IsDeviceLocal($"{UserKeyringService.KeyringDirectory}/{UserKeyringService.LoginKeyringName}"));

        // The keyring, which is synchronized, holds only the sealing key: never a token.
        IReadOnlyList<KeyringEntryRecord> entries = await _harness.Keyring!.ListAsync(_harness.Alice.Id, CloudAccountStore.KeyringOwner);
        KeyringEntryRecord key = Assert.Single(entries);
        Assert.Equal(account.Id, key.Id);
        Assert.Equal(44, key.Secret.Length);
        Assert.DoesNotContain("access-", key.Secret, StringComparison.Ordinal);
        Assert.DoesNotContain("access-", (await _harness.ReadAsync(KeyringFile))!, StringComparison.Ordinal);

        Assert.Equal("access-2", await accounts.GetAccessTokenAsync(account.Id));
    }

    [Fact]
    public async Task The_chosen_scope_selects_its_own_app_id()
    {
        await _harness.LoginAsync(_harness.Alice);
        CloudAccountService accounts = CreateService();

        CloudAccount account = await accounts.ConnectAsync("dropbox", CloudAccessScope.AppFolder);

        Assert.Equal(CloudAccessScope.AppFolder, account.Scope);
        Assert.Contains($"client_id={FakeOAuthServer.AppFolderClientId}", _server.AuthorizationRequests[0].Query, StringComparison.Ordinal);
        Assert.Equal(FakeOAuthServer.AppFolderClientId, _server.TokenRequests[0]["client_id"]);
    }

    [Fact]
    public async Task An_access_token_is_renewed_before_it_expires_and_a_rotated_refresh_token_is_kept()
    {
        _server.RotatesRefreshTokens = true;
        await _harness.LoginAsync(_harness.Alice);
        CloudAccountService accounts = CreateService();
        CloudAccount account = await accounts.ConnectAsync("dropbox", CloudAccessScope.Full);

        _time.Advance(TimeSpan.FromHours(4) - TimeSpan.FromMinutes(1));
        string renewed = await accounts.GetAccessTokenAsync(account.Id);
        _time.Advance(TimeSpan.FromHours(4));
        string renewedAgain = await accounts.GetAccessTokenAsync(account.Id);

        Assert.NotEqual("access-2", renewed);
        Assert.NotEqual(renewed, renewedAgain);
        Assert.Equal(["authorization_code", "refresh_token", "refresh_token"], _server.TokenRequests.Select(static form => form["grant_type"]));
        Assert.Equal("refresh-2", _server.TokenRequests[1]["refresh_token"]);
        Assert.NotEqual("refresh-2", _server.TokenRequests[2]["refresh_token"]);
        Assert.Equal("files.all offline", _server.TokenRequests[1]["scope"]);
    }

    [Fact]
    public async Task A_token_the_provider_refused_is_renewed_once_even_before_its_expiry()
    {
        await _harness.LoginAsync(_harness.Alice);
        CloudAccountService accounts = CreateService();
        CloudAccount account = await accounts.ConnectAsync("dropbox", CloudAccessScope.Full);
        string refused = await accounts.GetAccessTokenAsync(account.Id);

        string renewed = await accounts.RenewAccessTokenAsync(account.Id, refused);
        // A second request that saw the same refused token gets the renewed one, without a new renewal.
        string again = await accounts.RenewAccessTokenAsync(account.Id, refused);

        Assert.NotEqual(refused, renewed);
        Assert.Equal(renewed, again);
        Assert.Equal(renewed, await accounts.GetAccessTokenAsync(account.Id));
        Assert.Equal(["authorization_code", "refresh_token"], _server.TokenRequests.Select(static form => form["grant_type"]));
        Assert.Equal(account, await accounts.FindAccountAsync(account.Id));
        Assert.Null(await accounts.FindAccountAsync("nobody"));
    }

    [Fact]
    public async Task A_refused_renewal_asks_for_a_reconnection_and_keeps_the_drives()
    {
        await _harness.LoginAsync(_harness.Alice);
        CloudAccountService accounts = CreateService();
        CloudAccount account = await accounts.ConnectAsync("dropbox", CloudAccessScope.Full);
        int changes = 0;
        accounts.Changed += () => changes++;

        _server.RefuseRefresh = true;
        _time.Advance(TimeSpan.FromDays(1));
        CloudAccountException refused = await Assert.ThrowsAsync<CloudAccountException>(() => accounts.GetAccessTokenAsync(account.Id).AsTask());

        Assert.Equal(CloudAccountError.ReconnectRequired, refused.Error);
        Assert.Equal(CloudAccountState.NeedsReconnect, Assert.Single(await accounts.GetAccountsAsync()).State);
        Assert.Equal(1, changes);
        Assert.Empty(_drives.Released);

        // Reconnecting the same provider account brings it back, with the same id.
        _server.RefuseRefresh = false;
        CloudAccount reconnected = await accounts.ReconnectAsync(account.Id);
        Assert.Equal(account.Id, reconnected.Id);
        Assert.Equal(CloudAccountState.Connected, reconnected.State);
        Assert.StartsWith("access-", await accounts.GetAccessTokenAsync(account.Id), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Offline_renewal_is_a_temporary_failure_that_keeps_the_account_connected()
    {
        await _harness.LoginAsync(_harness.Alice);
        CloudAccountService accounts = CreateService();
        CloudAccount account = await accounts.ConnectAsync("dropbox", CloudAccessScope.Full);

        _server.Offline = true;
        _time.Advance(TimeSpan.FromHours(5));
        CloudAccountException offline = await Assert.ThrowsAsync<CloudAccountException>(() => accounts.GetAccessTokenAsync(account.Id).AsTask());

        Assert.Equal(CloudAccountError.ProviderUnavailable, offline.Error);
        Assert.Equal(CloudAccountState.Connected, Assert.Single(await accounts.GetAccountsAsync()).State);
    }

    [Fact]
    public async Task Reconnecting_to_another_provider_account_is_refused_and_changes_nothing()
    {
        await _harness.LoginAsync(_harness.Alice);
        CloudAccountService accounts = CreateService();
        CloudAccount account = await accounts.ConnectAsync("dropbox", CloudAccessScope.Full);

        _server.SignedInAccountId = "dbid:mallory";
        CloudAccountException mismatch = await Assert.ThrowsAsync<CloudAccountException>(() => accounts.ReconnectAsync(account.Id).AsTask());

        Assert.Equal(CloudAccountError.AccountMismatch, mismatch.Error);
        Assert.Single(_server.RevokedTokens);
        Assert.Equal("access-2", await accounts.GetAccessTokenAsync(account.Id));
    }

    [Fact]
    public async Task Signing_in_again_to_a_connected_account_renews_it_instead_of_adding_a_second_one()
    {
        await _harness.LoginAsync(_harness.Alice);
        CloudAccountService accounts = CreateService();
        CloudAccount first = await accounts.ConnectAsync("dropbox", CloudAccessScope.Full);

        CloudAccount second = await accounts.ConnectAsync("dropbox", CloudAccessScope.Full);

        Assert.Equal(first.Id, second.Id);
        Assert.Single(await accounts.GetAccountsAsync());
    }

    [Fact]
    public async Task Removing_an_account_releases_its_drives_revokes_its_token_and_forgets_its_key()
    {
        await _harness.LoginAsync(_harness.Alice);
        CloudAccountService accounts = CreateService();
        CloudAccount account = await accounts.ConnectAsync("dropbox", CloudAccessScope.Full);

        await accounts.RemoveAsync(account.Id);

        Assert.Equal([account.Id], _drives.Released);
        Assert.Equal(["access-2"], _server.RevokedTokens);
        Assert.Empty(await _harness.Keyring!.ListAsync(_harness.Alice.Id, CloudAccountStore.KeyringOwner));
        Assert.Empty(await accounts.GetAccountsAsync());
        CloudAccountException gone = await Assert.ThrowsAsync<CloudAccountException>(() => accounts.GetAccessTokenAsync(account.Id).AsTask());
        Assert.Equal(CloudAccountError.AccountNotFound, gone.Error);
    }

    [Fact]
    public async Task A_provider_without_revocation_is_removed_all_the_same()
    {
        FakeOAuthServer microsoft = new("onedrive", withRevocation: false);
        await _harness.LoginAsync(_harness.Alice);
        CloudAccountService accounts = CreateService(microsoft);
        CloudAccount account = await accounts.ConnectAsync("onedrive", CloudAccessScope.Full);

        await accounts.RemoveAsync(account.Id);

        Assert.Empty(microsoft.RevokedTokens);
        Assert.Empty(await accounts.GetAccountsAsync());
    }

    [Theory]
    [InlineData(OAuthAuthorizationStatus.Blocked, CloudAccountError.WindowBlocked)]
    [InlineData(OAuthAuthorizationStatus.Failed, CloudAccountError.Denied)]
    [InlineData(OAuthAuthorizationStatus.TimedOut, CloudAccountError.Cancelled)]
    [InlineData(OAuthAuthorizationStatus.Cancelled, CloudAccountError.Cancelled)]
    public async Task A_sign_in_that_does_not_finish_adds_no_account(OAuthAuthorizationStatus outcome, CloudAccountError expected)
    {
        await _harness.LoginAsync(_harness.Alice);
        CloudAccountService accounts = CreateService();
        _server.WindowOutcome = outcome;

        CloudAccountException failed = await Assert.ThrowsAsync<CloudAccountException>(
            () => accounts.ConnectAsync("dropbox", CloudAccessScope.Full).AsTask());

        Assert.Equal(expected, failed.Error);
        Assert.Empty(_server.TokenRequests);
        Assert.Empty(await accounts.GetAccountsAsync());
    }

    [Fact]
    public async Task A_user_without_an_open_keyring_cannot_connect_an_account()
    {
        SettingsFilesHarness harness = new(withAccounts: false, withKeyring: true);
        GuestAccountService guests = new(harness.Users, harness.Groups, harness.Audit, fileSystem: harness.FileSystem, session: harness.Session, iterations: 10);
        LocalUser guest = await guests.CreateAsync("Guest");
        await harness.Session.LoginAsync(guest.LoginName, password: null);
        CloudAccountService accounts = CreateService(_server, harness);

        Assert.False(accounts.CanConnect);
        CloudAccountException locked = await Assert.ThrowsAsync<CloudAccountException>(
            () => accounts.ConnectAsync("dropbox", CloudAccessScope.Full).AsTask());

        Assert.Equal(CloudAccountError.KeyringLocked, locked.Error);
        Assert.Contains("passwd", locked.Message, StringComparison.Ordinal);
        Assert.Empty(_server.AuthorizationRequests);
    }

    [Fact]
    public async Task Tokens_whose_key_left_the_keyring_need_a_reconnection()
    {
        AuthenticatedPrincipal alice = await _harness.LoginAsync(_harness.Alice);
        CloudAccountService accounts = CreateService();
        CloudAccount account = await accounts.ConnectAsync("dropbox", CloudAccessScope.Full);

        // As after an administrator reset Alice's password: the old keyring is set aside.
        await _harness.Keyring!.UnlockAsync(alice, "a brand new password");

        CloudAccountException lost = await Assert.ThrowsAsync<CloudAccountException>(() => accounts.GetAccessTokenAsync(account.Id).AsTask());
        Assert.Equal(CloudAccountError.ReconnectRequired, lost.Error);
        Assert.Equal(CloudAccountState.NeedsReconnect, Assert.Single(await accounts.GetAccountsAsync()).State);
    }

    [Fact]
    public async Task Another_user_never_sees_the_accounts()
    {
        await _harness.LoginAsync(_harness.Alice);
        CloudAccountService accounts = CreateService();
        await accounts.ConnectAsync("dropbox", CloudAccessScope.Full);
        await _harness.Session.LogoutAsync();

        await _harness.LoginAsync(_harness.Bob);

        Assert.Empty(await accounts.GetAccountsAsync());
        Assert.Null(await _harness.ReadAsync("/home/bob/.local/state/kazoos/cloud-accounts.json"));
    }

    [Fact]
    public async Task Apps_cannot_read_the_keys_even_with_keyring_read_all()
    {
        AuthenticatedPrincipal alice = await _harness.LoginAsync(_harness.Alice);
        CloudAccountService accounts = CreateService();
        CloudAccount account = await accounts.ConnectAsync("dropbox", CloudAccessScope.Full);
        AppKeyringGateway passwords = new(
            _harness.Keyring!, alice.UserId, "org.example.passwords", new AllowAll());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => passwords.GetAsync(CloudAccountStore.KeyringOwner, account.Id).AsTask());
    }

    [Fact]
    public async Task Prepare_loads_the_window_and_lists_the_configured_providers()
    {
        await _harness.LoginAsync(_harness.Alice);
        CloudAccountService accounts = CreateService();

        await accounts.PrepareAsync();
        CloudProviderInfo provider = Assert.Single(await accounts.GetProvidersAsync());

        Assert.Equal(1, _server.PrepareCalls);
        Assert.Equal("dropbox", provider.Id);
        Assert.Equal([CloudAccessScope.Full, CloudAccessScope.AppFolder], provider.Scopes);
        Assert.Equal(_server.RedirectUri, accounts.RedirectUri);
    }

    [Fact]
    public async Task An_unknown_provider_is_refused_before_any_window_opens()
    {
        await _harness.LoginAsync(_harness.Alice);
        CloudAccountService accounts = CreateService();

        CloudAccountException refused = await Assert.ThrowsAsync<CloudAccountException>(
            () => accounts.ConnectAsync("box", CloudAccessScope.Full).AsTask());

        Assert.Equal(CloudAccountError.ProviderNotConfigured, refused.Error);
        Assert.Empty(_server.AuthorizationRequests);
    }

    private CloudAccountService CreateService(FakeOAuthServer? server = null, SettingsFilesHarness? harness = null)
    {
        FakeOAuthServer provider = server ?? _server;
        SettingsFilesHarness h = harness ?? _harness;
        return new CloudAccountService(
            h.FileSystem,
            h.Clock,
            h.Keyring!,
            new ManagedKeyringCipher(),
            new FixedCatalog(provider.Provider),
            provider,
            new CloudOAuthTokenClient(new HttpClient(provider), _time),
            () => h.Session.CurrentPrincipal,
            () => _drives,
            timeProvider: _time);
    }

    private sealed class FixedCatalog(CloudOAuthProvider provider) : ICloudProviderCatalog
    {
        public ValueTask<IReadOnlyList<CloudOAuthProvider>> GetProvidersAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<CloudOAuthProvider>>([provider]);
    }

    private sealed class RecordingDrives : ICloudAccountDrives
    {
        public List<string> Released { get; } = [];

        public ValueTask ReleaseAsync(string accountId, CancellationToken cancellationToken = default)
        {
            Released.Add(accountId);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class AllowAll : ICapabilityChecker
    {
        public CapabilityPolicyEvaluation Evaluate(
            string capability, AppAuthority requiredAuthority = AppAuthority.User, CapabilityResourceCandidate? resourceCandidate = null) =>
            throw new NotSupportedException();

        public void Require(
            string capability, AppAuthority requiredAuthority = AppAuthority.User, CapabilityResourceCandidate? resourceCandidate = null)
        {
        }
    }
}

/// <summary>A clock the test moves by hand.</summary>
internal sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public void Advance(TimeSpan by) => _now += by;

    public override DateTimeOffset GetUtcNow() => _now;
}
