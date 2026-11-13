using Kazo.Os.Platform.Core.CloudAccounts;
using Kazo.Os.Platform.Core.Tests.Settings;

namespace Kazo.Os.Platform.Core.Tests.CloudAccounts;

/// <summary>
/// ADR 0267 sections 5, 6 and 8: PKCE S256, the authorization requests of Dropbox and Microsoft,
/// the scopes offered per configured app id, and the app ids an administrator sets in
/// <c>/etc/kazoos/cloud-drives.config</c>.
/// </summary>
public sealed class CloudOAuthProviderTests
{
    private static readonly Uri Callback = new("https://os.kazo.ca/_content/Kazo.Os.Infrastructure.Browser/oauth-callback.html");

    [Fact]
    public void The_challenge_is_the_rfc_7636_s256_of_the_verifier()
    {
        // RFC 7636, appendix B.
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", OAuthPkce.ComputeChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    [Fact]
    public void A_new_pair_has_a_43_character_verifier_and_state_that_never_repeat()
    {
        OAuthPkce first = OAuthPkce.Create();
        OAuthPkce second = OAuthPkce.Create();

        Assert.Equal(43, first.Verifier.Length);
        Assert.Equal(43, first.State.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", first.Verifier + first.State + first.Challenge);
        Assert.Equal(OAuthPkce.ComputeChallenge(first.Verifier), first.Challenge);
        Assert.NotEqual(first.Verifier, second.Verifier);
        Assert.NotEqual(first.State, second.State);
    }

    [Fact]
    public void Dropbox_asks_for_an_offline_refresh_token_with_the_app_key_of_the_chosen_scope()
    {
        CloudOAuthProvider dropbox = CloudOAuthProvider.Dropbox("full-key", "folder-key")!;
        OAuthPkce pkce = new("verifier", "challenge", "state-1");

        Dictionary<string, string> full = Query(dropbox.BuildAuthorizationUri(CloudAccessScope.Full, pkce, Callback));
        Dictionary<string, string> folder = Query(dropbox.BuildAuthorizationUri(CloudAccessScope.AppFolder, pkce, Callback));

        Assert.Equal("https://www.dropbox.com/oauth2/authorize", dropbox.BuildAuthorizationUri(CloudAccessScope.Full, pkce, Callback).GetLeftPart(UriPartial.Path));
        Assert.Equal("full-key", full["client_id"]);
        Assert.Equal("folder-key", folder["client_id"]);
        Assert.Equal("code", full["response_type"]);
        Assert.Equal("challenge", full["code_challenge"]);
        Assert.Equal("S256", full["code_challenge_method"]);
        Assert.Equal("state-1", full["state"]);
        Assert.Equal(Callback.AbsoluteUri, full["redirect_uri"]);
        Assert.Equal("offline", full["token_access_type"]);
        Assert.False(full.ContainsKey("scope"));
    }

    [Fact]
    public void OneDrive_asks_for_the_graph_permission_of_the_chosen_scope()
    {
        CloudOAuthProvider oneDrive = CloudOAuthProvider.OneDrive("client-guid", tenant: null)!;
        OAuthPkce pkce = new("verifier", "challenge", "state-2");

        Uri full = oneDrive.BuildAuthorizationUri(CloudAccessScope.Full, pkce, Callback);
        Dictionary<string, string> folder = Query(oneDrive.BuildAuthorizationUri(CloudAccessScope.AppFolder, pkce, Callback));

        Assert.Equal("https://login.microsoftonline.com/common/oauth2/v2.0/authorize", full.GetLeftPart(UriPartial.Path));
        Assert.Equal("Files.ReadWrite offline_access User.Read", Query(full)["scope"]);
        Assert.Equal("Files.ReadWrite.AppFolder offline_access User.Read", folder["scope"]);
        Assert.Equal("query", folder["response_mode"]);
        Assert.Null(oneDrive.RevocationEndpoint);
        Assert.Equal(new Uri("https://login.microsoftonline.com/common/oauth2/v2.0/token"), oneDrive.TokenEndpoint);
    }

    [Fact]
    public void A_provider_without_app_id_is_not_offered_and_a_scope_without_app_id_neither()
    {
        Assert.Empty(CloudProviderSettings.None.BuildProviders());
        Assert.Null(CloudOAuthProvider.Dropbox(" ", null));
        Assert.Null(CloudOAuthProvider.OneDrive("", "common"));

        CloudOAuthProvider folderOnly = CloudOAuthProvider.Dropbox(null, "folder-key")!;
        Assert.Equal([CloudAccessScope.AppFolder], folderOnly.Scopes);
        CloudAccountException refused = Assert.Throws<CloudAccountException>(
            () => folderOnly.BuildAuthorizationUri(CloudAccessScope.Full, OAuthPkce.Create(), Callback));
        Assert.Equal(CloudAccountError.ProviderNotConfigured, refused.Error);
    }

    [Fact]
    public async Task The_system_file_replaces_the_host_app_ids_it_sets_and_keeps_the_others()
    {
        SettingsFilesHarness harness = new();
        await harness.WriteAsRootAsync(
            "/etc/kazoos/cloud-drives.config",
            "[dropbox]\nfull-access-client-id = mine\n\n[onedrive]\ntenant = consumers\n");
        CloudProviderCatalog catalog = new(new CloudProviderSettings("host-full", "host-folder", "host-client", null), harness.Settings);

        IReadOnlyList<CloudOAuthProvider> providers = await catalog.GetProvidersAsync();

        Assert.Equal(["dropbox", "onedrive"], providers.Select(static provider => provider.Id));
        Assert.Equal("mine", providers[0].GetClient(CloudAccessScope.Full).ClientId);
        Assert.Equal("host-folder", providers[0].GetClient(CloudAccessScope.AppFolder).ClientId);
        Assert.Equal("host-client", providers[1].GetClient(CloudAccessScope.Full).ClientId);
        Assert.Contains("/consumers/", providers[1].AuthorizationEndpoint.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_any_app_id_no_provider_is_offered()
    {
        SettingsFilesHarness harness = new();
        CloudProviderCatalog catalog = new(CloudProviderSettings.None, harness.Settings);

        Assert.Empty(await catalog.GetProvidersAsync());
    }

    private static Dictionary<string, string> Query(Uri uri) =>
        uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(static pair => pair.Split('=', 2))
            .ToDictionary(static parts => Uri.UnescapeDataString(parts[0]), static parts => Uri.UnescapeDataString(parts[1]), StringComparer.Ordinal);
}
