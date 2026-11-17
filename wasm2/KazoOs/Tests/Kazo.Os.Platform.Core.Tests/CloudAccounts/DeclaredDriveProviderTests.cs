using System.Reflection;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Drives;
using Kazo.Os.Platform.Core.CloudAccounts;
using Kazo.Os.Platform.Core.Keyring;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Platform.Core.Tests.Settings;

namespace Kazo.Os.Platform.Core.Tests.CloudAccounts;

/// <summary>
/// ADR 0282 §3 (work item 5): the OS runs the connection of a drive type a feature declares. An
/// OAuth type is signed in by the OS from its manifest, like Dropbox and OneDrive; a form type's
/// secret values are sealed by the OS; and the provider receives, for the drive it opens, only the
/// access token or the secrets of that drive's account, never the refresh token.
/// </summary>
public sealed class DeclaredDriveProviderTests
{
    private const string Table = "/home/alice/.local/state/kazoos/cloud-accounts.json";
    private const string FeatureId = "ca.example.pcloud";

    private readonly SettingsFilesHarness _harness = new(withKeyring: true);
    private readonly FakeOAuthServer _server = new("pcloud");
    private readonly MutableTimeProvider _time = new(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));
    private readonly Enablement _enablement = new();

    [Fact]
    public void The_catalog_lists_the_drive_types_of_its_features_by_feature_then_type()
    {
        AppCatalog catalog = Catalog(
            Feature("ca.example.zeta", OAuthType("zcloud")),
            Feature(FeatureId, OAuthType("pcloud"), FormType("webdav")));

        Assert.Equal(["pcloud", "webdav", "zcloud"], catalog.GetDriveProviders().Select(static item => item.Provider.Type));
        Assert.Equal(FeatureId, catalog.FindDriveProvider("webdav")!.Feature.Id);
        Assert.Null(catalog.FindDriveProvider("dropbox"));
    }

    [Fact]
    public async Task An_oauth_type_of_an_enabled_feature_is_offered_after_the_shipped_providers()
    {
        AppCatalog catalog = Catalog(Feature(FeatureId, OAuthType("pcloud"), FormType("webdav")));
        CloudProviderCatalog providers = new(new CloudProviderSettings("dropbox-key"), _harness.Settings)
        {
            DeclaredProviders = () => DeclaredCloudProviders.GetOAuthRegistrations(catalog, _enablement)
        };

        IReadOnlyList<CloudOAuthProvider> offered = await providers.GetProvidersAsync();

        // The form type is not an OAuth provider: its accounts come from SaveFormAccountAsync.
        Assert.Equal(["dropbox", "pcloud"], offered.Select(static provider => provider.Id));
        CloudOAuthProvider pcloud = offered[1];
        Assert.Equal("pCloud", pcloud.DisplayName);
        Assert.Equal(FeatureId, pcloud.FeatureId);
        Assert.Null(offered[0].FeatureId);
        Assert.Equal(new Uri("https://auth.fake.test/authorize"), pcloud.AuthorizationEndpoint);
        Assert.Equal([CloudAccessScope.Full, CloudAccessScope.AppFolder], pcloud.Scopes);
        Assert.Equal("Apps/Kazo OS", pcloud.AppFolderPath);
        Assert.Equal("offline", pcloud.ExtraAuthorizationParameters["token_access_type"]);

        // Disabled (uninstalled, ADR 0048): withdrawn at the next read, without a restart.
        _enablement.Disabled.Add(FeatureId);
        Assert.Equal(["dropbox"], (await providers.GetProvidersAsync()).Select(static provider => provider.Id));
    }

    [Fact]
    public async Task The_system_file_replaces_the_app_ids_of_a_declared_type()
    {
        await _harness.WriteAsRootAsync(
            "/etc/kazoos/cloud-drives.config",
            "[pcloud]\nclient-id = site-full\napp-folder-client-id = site-folder\n\n[foldercloud]\nclient-id = site-only\n");
        AppCatalog catalog = Catalog(Feature(FeatureId, OAuthType("pcloud"), OAuthType("foldercloud", fullAccess: false), OAuthType("plaincloud")));
        CloudProviderCatalog providers = new(CloudProviderSettings.None, _harness.Settings)
        {
            DeclaredProviders = () => DeclaredCloudProviders.GetOAuthRegistrations(catalog, _enablement)
        };

        IReadOnlyList<CloudOAuthProvider> offered = await providers.GetProvidersAsync();

        Assert.Equal(["foldercloud", "pcloud", "plaincloud"], offered.Select(static provider => provider.Id));
        // A type with one access: client-id names that access.
        Assert.Equal("site-only", offered[0].GetClient(CloudAccessScope.AppFolder).ClientId);
        Assert.Equal([CloudAccessScope.AppFolder], offered[0].Scopes);
        Assert.Equal("site-full", offered[1].GetClient(CloudAccessScope.Full).ClientId);
        Assert.Equal("site-folder", offered[1].GetClient(CloudAccessScope.AppFolder).ClientId);
        // No section: the manifest's app ids, and the declared scope string.
        Assert.Equal(FakeOAuthServer.ClientId, offered[2].GetClient(CloudAccessScope.Full).ClientId);
        Assert.Equal("files.all offline", offered[2].GetClient(CloudAccessScope.Full).Scope);
    }

    [Fact]
    public void A_declaration_with_an_address_that_is_not_https_builds_no_provider()
    {
        AppDriveProviderManifest declared = OAuthType("pcloud");
        AppDriveProviderManifest plainHttp = declared with
        {
            Connection = AppDriveConnectionManifest.WithOAuth(declared.Connection.OAuth! with { TokenEndpoint = "http://api.fake.test/token" })
        };
        AppManifest feature = Feature(FeatureId);

        Assert.Null(DeclaredCloudProviders.Build(new AppDriveProviderRegistration(feature, plainHttp)));
        Assert.Null(DeclaredCloudProviders.Build(new AppDriveProviderRegistration(feature, FormType("webdav"))));
        Assert.NotNull(DeclaredCloudProviders.Build(new AppDriveProviderRegistration(feature, declared)));
    }

    [Fact]
    public async Task The_os_signs_in_to_a_declared_type_and_keeps_its_tokens_sealed()
    {
        await _harness.LoginAsync(_harness.Alice);
        CloudAccountService accounts = CreateService(Catalog(Feature(FeatureId, OAuthType("pcloud"))));

        CloudAccount account = await accounts.ConnectAsync("pcloud", CloudAccessScope.Full);

        Assert.Equal("pcloud", account.ProviderId);
        Assert.Equal("alice@example.test", account.DisplayName);
        Assert.Equal("auth.fake.test", _server.AuthorizationRequests[0].Host);
        Assert.Contains($"client_id={FakeOAuthServer.ClientId}", _server.AuthorizationRequests[0].Query, StringComparison.Ordinal);
        Assert.False(Assert.Single(_server.TokenRequests).ContainsKey("client_secret"));
        string table = (await _harness.ReadAsync(Table))!;
        Assert.DoesNotContain("access-", table, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-", table, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_declared_type_without_an_identity_address_names_the_account_after_the_service()
    {
        await _harness.LoginAsync(_harness.Alice);
        AppDriveProviderManifest declared = OAuthType("pcloud");
        AppDriveProviderManifest anonymous = declared with
        {
            Connection = AppDriveConnectionManifest.WithOAuth(declared.Connection.OAuth! with { Identity = null })
        };
        CloudAccountService accounts = CreateService(Catalog(Feature(FeatureId, anonymous)));

        CloudAccount account = await accounts.ConnectAsync("pcloud", CloudAccessScope.Full);

        Assert.Equal("pCloud", account.DisplayName);
        Assert.Null(account.ProviderAccountId);
    }

    [Fact]
    public async Task The_context_gives_only_the_access_token_of_the_open_drives_account()
    {
        await _harness.LoginAsync(_harness.Alice);
        CloudAccountService accounts = CreateService(Catalog(Feature(FeatureId, OAuthType("pcloud"))));
        CloudAccount first = await accounts.ConnectAsync("pcloud", CloudAccessScope.Full);
        _server.SignedInAccountId = "dbid:bob";
        _server.SignedInEmail = "bob@example.test";
        CloudAccount second = await accounts.ConnectAsync("pcloud", CloudAccessScope.Full);
        int calls = 0;

        IKazoDriveProviderContext context = await accounts.CreateDriveProviderContextAsync("pcloud", first.Id, () => calls++);
        string token = await context.GetAccessTokenAsync();

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal("pcloud", context.Type);
        Assert.Equal(new KazoDriveAccount(first.Id, "alice@example.test"), context.Account);
        Assert.Equal(await accounts.GetAccessTokenAsync(first.Id), token);
        Assert.NotEqual(await accounts.GetAccessTokenAsync(second.Id), token);
        Assert.StartsWith("access-", token, StringComparison.Ordinal);
        Assert.Null(await context.GetSecretAsync("password"));
        context.NotifyAvailabilityChanged();
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task The_refresh_token_never_reaches_the_provider_even_when_the_os_renews()
    {
        _server.RotatesRefreshTokens = true;
        await _harness.LoginAsync(_harness.Alice);
        CloudAccountService accounts = CreateService(Catalog(Feature(FeatureId, OAuthType("pcloud"))));
        CloudAccount account = await accounts.ConnectAsync("pcloud", CloudAccessScope.Full);
        IKazoDriveProviderContext context = await accounts.CreateDriveProviderContextAsync("pcloud", account.Id);
        string before = await context.GetAccessTokenAsync();

        _time.Advance(TimeSpan.FromHours(5));
        string after = await context.GetAccessTokenAsync();

        Assert.NotEqual(before, after);
        Assert.Equal("refresh_token", _server.TokenRequests[^1]["grant_type"]);
        Assert.All([before, after], static token => Assert.DoesNotContain("refresh", token, StringComparison.Ordinal));
        // The contract has no way to ask for it, and the OS's context keeps no token at all.
        Assert.DoesNotContain(
            typeof(IKazoDriveProviderContext).GetMethods(),
            static method => method.Name.Contains("Refresh", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            context.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public),
            field => field.GetValue(context) is string text && (text == before || text == after || text.StartsWith("refresh-", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_context_is_refused_for_an_account_of_another_type()
    {
        await _harness.LoginAsync(_harness.Alice);
        CloudAccountService dropboxAccounts = CreateService(catalog: null, new FakeOAuthServer());
        CloudAccount foreign = await dropboxAccounts.ConnectAsync("dropbox", CloudAccessScope.Full);
        CloudAccountService accounts = CreateService(Catalog(Feature(FeatureId, OAuthType("pcloud"))));

        CloudAccountException refused = await Assert.ThrowsAsync<CloudAccountException>(
            () => accounts.CreateDriveProviderContextAsync("pcloud", foreign.Id).AsTask());

        Assert.Equal(CloudAccountError.AccountNotFound, refused.Error);
    }

    [Fact]
    public async Task A_context_gives_nothing_once_its_account_is_removed()
    {
        await _harness.LoginAsync(_harness.Alice);
        CloudAccountService accounts = CreateService(Catalog(Feature(FeatureId, OAuthType("pcloud"))));
        CloudAccount account = await accounts.ConnectAsync("pcloud", CloudAccessScope.Full);
        IKazoDriveProviderContext context = await accounts.CreateDriveProviderContextAsync("pcloud", account.Id);

        await accounts.RemoveAsync(account.Id);
        KazoDriveException refused = await Assert.ThrowsAsync<KazoDriveException>(() => context.GetAccessTokenAsync().AsTask());

        Assert.Equal(KazoDriveErrorKind.SignInRequired, refused.Kind);
    }

    [Fact]
    public async Task An_unreachable_service_is_unavailable_and_a_refused_renewal_needs_a_sign_in()
    {
        await _harness.LoginAsync(_harness.Alice);
        CloudAccountService accounts = CreateService(Catalog(Feature(FeatureId, OAuthType("pcloud"))));
        CloudAccount account = await accounts.ConnectAsync("pcloud", CloudAccessScope.Full);
        IKazoDriveProviderContext context = await accounts.CreateDriveProviderContextAsync("pcloud", account.Id);
        _time.Advance(TimeSpan.FromHours(5));

        _server.Offline = true;
        KazoDriveException offline = await Assert.ThrowsAsync<KazoDriveException>(() => context.GetAccessTokenAsync().AsTask());
        _server.Offline = false;
        _server.RefuseRefresh = true;
        KazoDriveException expired = await Assert.ThrowsAsync<KazoDriveException>(() => context.GetAccessTokenAsync().AsTask());

        Assert.Equal(KazoDriveErrorKind.Unavailable, offline.Kind);
        Assert.Equal(KazoDriveErrorKind.SignInRequired, expired.Kind);
        Assert.Equal(CloudAccountState.NeedsReconnect, (await accounts.FindAccountAsync(account.Id))!.State);
    }

    [Fact]
    public async Task A_form_account_keeps_its_plain_values_and_seals_its_secrets()
    {
        await _harness.LoginAsync(_harness.Alice);
        CloudAccountService accounts = CreateService(catalog: null);
        Dictionary<string, string> options = new() { ["server"] = "https://dav.example.test", ["user"] = "alice" };

        CloudAccount account = await accounts.SaveFormAccountAsync("webdav", "dav.example.test", options, new Dictionary<string, string> { ["password"] = "s3cr3t-one" });
        CloudAccount again = await accounts.SaveFormAccountAsync("webdav", "dav.example.test", options, new Dictionary<string, string> { ["password"] = "s3cr3t-two" });
        CloudAccount other = await accounts.SaveFormAccountAsync(
            "webdav", "dav.example.test", new Dictionary<string, string>(options) { ["user"] = "bob" }, new Dictionary<string, string> { ["password"] = "x" });

        // Same plain values: the same account, which takes the new secret (a new password).
        Assert.Equal(account.Id, again.Id);
        Assert.NotEqual(account.Id, other.Id);
        Assert.Equal("alice", account.Options!["user"]);
        Assert.StartsWith(CloudAccountService.FormAccountIdPrefix, account.ProviderAccountId, StringComparison.Ordinal);
        string table = (await _harness.ReadAsync(Table))!;
        Assert.Contains("https://dav.example.test", table, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t", table, StringComparison.Ordinal);
        Assert.Equal(account.Options, (await accounts.FindAccountAsync(account.Id))!.Options);

        IKazoDriveProviderContext context = await accounts.CreateDriveProviderContextAsync("webdav", account.Id);
        Assert.Null(context.Account);
        Assert.Equal("s3cr3t-two", await context.GetSecretAsync("password"));
        Assert.Null(await context.GetSecretAsync("user"));
        KazoDriveException noToken = await Assert.ThrowsAsync<KazoDriveException>(() => context.GetAccessTokenAsync().AsTask());
        Assert.Equal(KazoDriveErrorKind.SignInRequired, noToken.Kind);
    }

    [Fact]
    public async Task The_secrets_of_a_form_account_never_reach_a_provider_of_another_type()
    {
        await _harness.LoginAsync(_harness.Alice);
        CloudAccountService accounts = CreateService(catalog: null);
        CloudAccount account = await accounts.SaveFormAccountAsync(
            "webdav", "dav", new Dictionary<string, string> { ["user"] = "alice" }, new Dictionary<string, string> { ["password"] = "s3cr3t" });

        CloudAccountException refused = await Assert.ThrowsAsync<CloudAccountException>(
            () => accounts.CreateDriveProviderContextAsync("ftpcloud", account.Id).AsTask());

        Assert.Equal(CloudAccountError.AccountNotFound, refused.Error);
    }

    [Fact]
    public void The_form_account_id_is_a_hash_of_the_plain_values_only()
    {
        string id = CloudAccountService.FormAccountId(new Dictionary<string, string> { ["a"] = "bc" });

        Assert.Equal(id, CloudAccountService.FormAccountId(new Dictionary<string, string> { ["a"] = "bc" }));
        Assert.NotEqual(id, CloudAccountService.FormAccountId(new Dictionary<string, string> { ["ab"] = "c" }));
        Assert.DoesNotContain("bc", id, StringComparison.Ordinal);
    }

    /// <summary>
    /// Creates the account service: with <paramref name="catalog"/>, the providers its features
    /// declare; without it, the fixed provider of <paramref name="server"/>.
    /// </summary>
    private CloudAccountService CreateService(AppCatalog? catalog, FakeOAuthServer? server = null)
    {
        FakeOAuthServer fake = server ?? _server;
        ICloudProviderCatalog providers = catalog is null
            ? new FixedProviders(fake.Provider)
            : new CloudProviderCatalog(CloudProviderSettings.None, _harness.Settings)
            {
                DeclaredProviders = () => DeclaredCloudProviders.GetOAuthRegistrations(catalog, _enablement)
            };
        return new CloudAccountService(
            _harness.FileSystem,
            _harness.Clock,
            _harness.Keyring!,
            new ManagedKeyringCipher(),
            providers,
            fake,
            new CloudOAuthTokenClient(new HttpClient(fake), _time),
            () => _harness.Session.CurrentPrincipal,
            timeProvider: _time);
    }

    private static AppCatalog Catalog(params AppManifest[] features)
    {
        AppCatalogBuildResult result = AppCatalog.Build(features);
        Assert.True(result.IsSuccess, string.Join(", ", result.Errors.Select(static error => error.Message)));
        Assert.Empty(result.Catalog!.Rejected);
        return result.Catalog!;
    }

    private static AppManifest Feature(string id, params AppDriveProviderManifest[] providers) => new()
    {
        Id = id,
        Name = id,
        Version = "1.0.0",
        PublisherId = "ca.example",
        Description = $"Test feature {id}.",
        Kind = AppKind.Feature,
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None,
        Dependencies = [],
        Capabilities = [AppCapabilities.DrivesProvide],
        DriveProviders = providers
    };

    /// <summary>An OAuth declaration whose addresses are those of <see cref="FakeOAuthServer"/>.</summary>
    private static AppDriveProviderManifest OAuthType(string type, bool fullAccess = true) => new(
        type,
        type == "pcloud" ? "pCloud" : type,
        $"Example.{type}.Provider, Example",
        AppDriveConnectionManifest.WithOAuth(new AppDriveOAuthManifest("https://auth.fake.test/authorize", "https://api.fake.test/token")
        {
            RevocationEndpoint = "https://api.fake.test/revoke",
            FullAccess = fullAccess ? new AppDriveOAuthClientManifest(FakeOAuthServer.ClientId) { Scope = "files.all offline" } : null,
            AppFolder = new AppDriveOAuthClientManifest(FakeOAuthServer.AppFolderClientId),
            AppFolderPath = "/Apps/Kazo OS/",
            ExtraAuthorizationParameters = new Dictionary<string, string> { ["token_access_type"] = "offline" },
            Identity = new AppDriveIdentityManifest("https://api.fake.test/me", "account_id") { UsePost = true, NamePaths = ["email"] }
        }))
    {
        Hosts = ["api.fake.test"]
    };

    private static AppDriveProviderManifest FormType(string type) => new(
        type,
        type,
        $"Example.{type}.Provider, Example",
        AppDriveConnectionManifest.WithForm(
            new AppDriveFormFieldManifest("server", "Server", AppDriveFormFieldKind.Url),
            new AppDriveFormFieldManifest("password", "Password", AppDriveFormFieldKind.Text) { Secret = true }));

    private sealed class FixedProviders(CloudOAuthProvider provider) : ICloudProviderCatalog
    {
        public ValueTask<IReadOnlyList<CloudOAuthProvider>> GetProvidersAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<CloudOAuthProvider>>([provider]);
    }

    private sealed class Enablement : IAppEnablementRegistry
    {
        public HashSet<string> Disabled { get; } = new(StringComparer.Ordinal);

        public IReadOnlyCollection<string> DisabledAppIds => Disabled;

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public bool IsEnabled(string appId) => !Disabled.Contains(appId);
    }
}
