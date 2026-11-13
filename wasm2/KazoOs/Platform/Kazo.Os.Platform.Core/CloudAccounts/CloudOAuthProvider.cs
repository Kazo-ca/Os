namespace Kazo.Os.Platform.Core.CloudAccounts;

/// <summary>The public app id and the OAuth scope string Kazo OS sends for one access scope of a provider.</summary>
/// <param name="ClientId">The app id registered with the provider (public; no secret is ever used).</param>
/// <param name="Scope">The <c>scope</c> parameter, or <see langword="null"/> when the app's registration fixes it (Dropbox).</param>
public sealed record CloudOAuthClient(string ClientId, string? Scope);

/// <summary>Where Kazo OS asks a provider who the signed-in account is.</summary>
/// <param name="Address">The endpoint, called with the access token.</param>
/// <param name="UsePost"><see langword="true"/> for an RPC endpoint called by POST without a body (Dropbox), otherwise GET.</param>
/// <param name="IdPath">Dotted JSON path of the account id in the answer.</param>
/// <param name="NamePaths">Dotted JSON paths of a display name, tried in order.</param>
public sealed record CloudIdentityEndpoint(Uri Address, bool UsePost, string IdPath, IReadOnlyList<string> NamePaths);

/// <summary>
/// One OAuth provider of ADR 0267 (section 6): its endpoints, the app ids per access scope it is
/// configured with, and the extra parameters of its authorization request.
/// </summary>
/// <param name="Id">Provider id: <c>dropbox</c> or <c>onedrive</c>.</param>
/// <param name="DisplayName">Name shown to the user.</param>
/// <param name="AuthorizationEndpoint">Page opened in the sign-in window.</param>
/// <param name="TokenEndpoint">Where the code and the refresh token are exchanged (CORS, no secret).</param>
/// <param name="RevocationEndpoint">Where a token is revoked when the account is removed, or <see langword="null"/> when the provider has none for public clients.</param>
/// <param name="Clients">App id and scope string per access scope; a scope without an app id is not offered.</param>
/// <param name="ExtraAuthorizationParameters">Provider-specific parameters of the authorization request.</param>
/// <param name="Identity">Where to read the account's id and name.</param>
public sealed record CloudOAuthProvider(
    string Id,
    string DisplayName,
    Uri AuthorizationEndpoint,
    Uri TokenEndpoint,
    Uri? RevocationEndpoint,
    IReadOnlyDictionary<CloudAccessScope, CloudOAuthClient> Clients,
    IReadOnlyDictionary<string, string> ExtraAuthorizationParameters,
    CloudIdentityEndpoint Identity)
{
    /// <summary>Id of the Dropbox provider.</summary>
    public const string DropboxId = "dropbox";

    /// <summary>Id of the OneDrive provider (Microsoft Graph).</summary>
    public const string OneDriveId = "onedrive";

    /// <summary>Microsoft tenant used when none is configured: work, school and personal accounts.</summary>
    public const string DefaultMicrosoftTenant = "common";

    /// <summary>Gets the access scopes this provider has an app id for, <see cref="CloudAccessScope.Full"/> first.</summary>
    public IReadOnlyList<CloudAccessScope> Scopes =>
        [.. Clients.Keys.Order()];

    /// <summary>
    /// Builds the authorization address of one sign-in: response type <c>code</c>, PKCE <c>S256</c>,
    /// the redirect URI of the callback page and the provider's extra parameters.
    /// </summary>
    /// <param name="scope">The access scope chosen by the user.</param>
    /// <param name="pkce">The verifier's challenge and the state of this sign-in.</param>
    /// <param name="redirectUri">Address of the callback page.</param>
    /// <exception cref="CloudAccountException">The provider has no app id for <paramref name="scope"/>.</exception>
    public Uri BuildAuthorizationUri(CloudAccessScope scope, OAuthPkce pkce, Uri redirectUri)
    {
        ArgumentNullException.ThrowIfNull(pkce);
        ArgumentNullException.ThrowIfNull(redirectUri);
        CloudOAuthClient client = GetClient(scope);
        List<KeyValuePair<string, string>> query =
        [
            new("client_id", client.ClientId),
            new("response_type", "code"),
            new("redirect_uri", redirectUri.AbsoluteUri),
            new("code_challenge", pkce.Challenge),
            new("code_challenge_method", OAuthPkce.ChallengeMethod),
            new("state", pkce.State)
        ];
        if (client.Scope is { Length: > 0 } scopeText)
        {
            query.Add(new("scope", scopeText));
        }

        query.AddRange(ExtraAuthorizationParameters);
        UriBuilder builder = new(AuthorizationEndpoint)
        {
            Query = string.Join('&', query.Select(static pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"))
        };
        return builder.Uri;
    }

    /// <summary>Gets the app id and scope string of <paramref name="scope"/>.</summary>
    /// <param name="scope">The access scope.</param>
    /// <exception cref="CloudAccountException">The provider has no app id for it.</exception>
    public CloudOAuthClient GetClient(CloudAccessScope scope) =>
        Clients.TryGetValue(scope, out CloudOAuthClient? client)
            ? client
            : throw new CloudAccountException(
                CloudAccountError.ProviderNotConfigured,
                $"{DisplayName} has no app id for the '{scope}' access in this Kazo OS.");

    /// <summary>
    /// The Dropbox provider (API v2): one app per access scope, because Dropbox fixes the scope per
    /// app (ADR 0267 section 8), and <c>token_access_type=offline</c> for a refresh token.
    /// </summary>
    /// <param name="fullAccessClientId">App key of the "Full Dropbox" app, or empty.</param>
    /// <param name="appFolderClientId">App key of the "App folder" app, or empty.</param>
    /// <returns>The provider, or <see langword="null"/> when neither app key is set.</returns>
    public static CloudOAuthProvider? Dropbox(string? fullAccessClientId, string? appFolderClientId)
    {
        Dictionary<CloudAccessScope, CloudOAuthClient> clients = [];
        if (!string.IsNullOrWhiteSpace(fullAccessClientId))
        {
            clients[CloudAccessScope.Full] = new CloudOAuthClient(fullAccessClientId.Trim(), Scope: null);
        }

        if (!string.IsNullOrWhiteSpace(appFolderClientId))
        {
            clients[CloudAccessScope.AppFolder] = new CloudOAuthClient(appFolderClientId.Trim(), Scope: null);
        }

        return clients.Count == 0
            ? null
            : new CloudOAuthProvider(
                DropboxId,
                "Dropbox",
                new Uri("https://www.dropbox.com/oauth2/authorize"),
                new Uri("https://api.dropboxapi.com/oauth2/token"),
                new Uri("https://api.dropboxapi.com/2/auth/token/revoke"),
                clients,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["token_access_type"] = "offline" },
                new CloudIdentityEndpoint(
                    new Uri("https://api.dropboxapi.com/2/users/get_current_account"),
                    UsePost: true,
                    IdPath: "account_id",
                    NamePaths: ["email", "name.display_name"]));
    }

    /// <summary>
    /// The OneDrive provider (Microsoft identity platform and Graph): one registration of type
    /// "single-page application"; the access scope is the Graph permission asked for
    /// (ADR 0267 section 8). Microsoft has no revocation endpoint for a public client.
    /// </summary>
    /// <param name="clientId">Application (client) id, or empty.</param>
    /// <param name="tenant">Tenant segment; empty means <see cref="DefaultMicrosoftTenant"/>.</param>
    /// <returns>The provider, or <see langword="null"/> when no client id is set.</returns>
    public static CloudOAuthProvider? OneDrive(string? clientId, string? tenant)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return null;
        }

        string id = clientId.Trim();
        string tenantSegment = Uri.EscapeDataString(string.IsNullOrWhiteSpace(tenant) ? DefaultMicrosoftTenant : tenant.Trim());
        return new CloudOAuthProvider(
            OneDriveId,
            "OneDrive",
            new Uri($"https://login.microsoftonline.com/{tenantSegment}/oauth2/v2.0/authorize"),
            new Uri($"https://login.microsoftonline.com/{tenantSegment}/oauth2/v2.0/token"),
            RevocationEndpoint: null,
            new Dictionary<CloudAccessScope, CloudOAuthClient>
            {
                [CloudAccessScope.Full] = new(id, "Files.ReadWrite offline_access User.Read"),
                [CloudAccessScope.AppFolder] = new(id, "Files.ReadWrite.AppFolder offline_access User.Read")
            },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["response_mode"] = "query",
                ["prompt"] = "select_account"
            },
            new CloudIdentityEndpoint(
                new Uri("https://graph.microsoft.com/v1.0/me"),
                UsePost: false,
                IdPath: "id",
                NamePaths: ["mail", "userPrincipalName", "displayName"]));
    }
}
