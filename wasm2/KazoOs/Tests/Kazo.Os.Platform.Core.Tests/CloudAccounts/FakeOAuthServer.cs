using System.Net;
using System.Text;
using System.Text.Json;
using Kazo.Os.Platform.Core.CloudAccounts;
using Kazo.Os.Simulation.Abstractions.CloudAccounts;

namespace Kazo.Os.Platform.Core.Tests.CloudAccounts;

/// <summary>
/// A fake OAuth provider for the cloud account tests (ADR 0267 section 5): it is the sign-in window
/// (it reads the authorization request and "signs the user in") and the provider's HTTP endpoints
/// (token, identity, revocation), checking PKCE <c>S256</c> as a real provider does. No test ever
/// reaches a real provider.
/// </summary>
internal sealed class FakeOAuthServer : HttpMessageHandler, IOAuthAuthorizationWindow
{
    public const string ClientId = "fake-client";
    public const string AppFolderClientId = "fake-app-folder-client";

    private readonly Dictionary<string, (string Challenge, string RedirectUri, string ClientId)> _codes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _refreshTokens = new(StringComparer.Ordinal);
    private int _sequence;

    public FakeOAuthServer(string providerId = "dropbox", bool withRevocation = true)
    {
        Provider = new CloudOAuthProvider(
            providerId,
            "FakeDrive",
            new Uri("https://auth.fake.test/authorize"),
            new Uri("https://api.fake.test/token"),
            withRevocation ? new Uri("https://api.fake.test/revoke") : null,
            new Dictionary<CloudAccessScope, CloudOAuthClient>
            {
                [CloudAccessScope.Full] = new(ClientId, "files.all offline"),
                [CloudAccessScope.AppFolder] = new(AppFolderClientId, null)
            },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["token_access_type"] = "offline" },
            new CloudIdentityEndpoint(new Uri("https://api.fake.test/me"), UsePost: true, "account_id", ["email", "name.display_name"]));
    }

    public CloudOAuthProvider Provider { get; }

    public Uri RedirectUri { get; } = new("https://os.example.test/_content/Kazo.Os.Infrastructure.Browser/oauth-callback.html");

    /// <summary>What the "user" does in the window.</summary>
    public OAuthAuthorizationStatus WindowOutcome { get; set; } = OAuthAuthorizationStatus.Authorized;

    /// <summary>The account the user signs in to.</summary>
    public string SignedInAccountId { get; set; } = "dbid:alice";

    public string SignedInEmail { get; set; } = "alice@example.test";

    /// <summary>Lifetime of the access tokens issued.</summary>
    public int ExpiresInSeconds { get; set; } = 14_400;

    /// <summary>Whether a refresh issues a new refresh token (Microsoft does, Dropbox does not).</summary>
    public bool RotatesRefreshTokens { get; set; }

    /// <summary>Whether the token endpoint refuses refresh tokens (<c>invalid_grant</c>), as after 24 hours on OneDrive.</summary>
    public bool RefuseRefresh { get; set; }

    /// <summary>Whether every request fails as if offline.</summary>
    public bool Offline { get; set; }

    public List<Uri> AuthorizationRequests { get; } = [];

    public List<Dictionary<string, string>> TokenRequests { get; } = [];

    public List<string> RevokedTokens { get; } = [];

    public int PrepareCalls { get; private set; }

    public ValueTask PrepareAsync(CancellationToken cancellationToken = default)
    {
        PrepareCalls++;
        return ValueTask.CompletedTask;
    }

    public ValueTask<OAuthAuthorizationResult> AuthorizeAsync(Uri authorizationUrl, string state, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        AuthorizationRequests.Add(authorizationUrl);
        Dictionary<string, string> query = ParseForm(authorizationUrl.Query.TrimStart('?'));
        Assert.Equal(state, query["state"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(RedirectUri.AbsoluteUri, query["redirect_uri"]);
        return ValueTask.FromResult(WindowOutcome switch
        {
            OAuthAuthorizationStatus.Authorized => new OAuthAuthorizationResult(
                OAuthAuthorizationStatus.Authorized,
                IssueCode(query["code_challenge"], query["redirect_uri"], query["client_id"])),
            OAuthAuthorizationStatus.Failed => new OAuthAuthorizationResult(
                OAuthAuthorizationStatus.Failed, Error: "access_denied", ErrorDescription: "The user said no."),
            OAuthAuthorizationStatus status => new OAuthAuthorizationResult(status)
        });
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Offline)
        {
            throw new HttpRequestException("Network is unreachable (fake).");
        }

        string path = request.RequestUri!.AbsolutePath;
        string bearer = request.Headers.Authorization?.Parameter ?? string.Empty;
        switch (path)
        {
            case "/token":
                Dictionary<string, string> form = ParseForm(await request.Content!.ReadAsStringAsync(cancellationToken));
                TokenRequests.Add(form);
                return form["grant_type"] switch
                {
                    "authorization_code" => ExchangeCode(form),
                    "refresh_token" => Refresh(form),
                    _ => Json(HttpStatusCode.BadRequest, new { error = "unsupported_grant_type" })
                };
            case "/me":
                return bearer.StartsWith("access-", StringComparison.Ordinal)
                    ? Json(HttpStatusCode.OK, new { account_id = SignedInAccountId, email = SignedInEmail, name = new { display_name = "Alice" } })
                    : Json(HttpStatusCode.Unauthorized, new { error = "invalid_access_token" });
            case "/revoke":
                RevokedTokens.Add(bearer);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("null") };
            default:
                return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private string IssueCode(string challenge, string redirectUri, string clientId)
    {
        string code = $"code-{++_sequence}";
        _codes[code] = (challenge, redirectUri, clientId);
        return code;
    }

    private HttpResponseMessage ExchangeCode(Dictionary<string, string> form)
    {
        if (!_codes.Remove(form["code"], out (string Challenge, string RedirectUri, string ClientId) issued)
            || issued.RedirectUri != form["redirect_uri"]
            || issued.ClientId != form["client_id"]
            || OAuthPkce.ComputeChallenge(form["code_verifier"]) != issued.Challenge)
        {
            return Json(HttpStatusCode.BadRequest, new { error = "invalid_grant" });
        }

        return IssueTokens(includeRefreshToken: true);
    }

    private HttpResponseMessage Refresh(Dictionary<string, string> form)
    {
        if (RefuseRefresh || !_refreshTokens.Contains(form["refresh_token"]))
        {
            return Json(HttpStatusCode.BadRequest, new { error = "invalid_grant", error_description = "expired" });
        }

        if (RotatesRefreshTokens)
        {
            _refreshTokens.Remove(form["refresh_token"]);
        }

        return IssueTokens(includeRefreshToken: RotatesRefreshTokens);
    }

    private HttpResponseMessage IssueTokens(bool includeRefreshToken)
    {
        int id = ++_sequence;
        string? refresh = includeRefreshToken ? $"refresh-{id}" : null;
        if (refresh is not null)
        {
            _refreshTokens.Add(refresh);
        }

        return Json(HttpStatusCode.OK, refresh is null
            ? new Dictionary<string, object> { ["access_token"] = $"access-{id}", ["token_type"] = "bearer", ["expires_in"] = ExpiresInSeconds }
            : new Dictionary<string, object> { ["access_token"] = $"access-{id}", ["token_type"] = "bearer", ["expires_in"] = ExpiresInSeconds, ["refresh_token"] = refresh });
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private static Dictionary<string, string> ParseForm(string text) =>
        text.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(static pair => pair.Split('=', 2))
            .ToDictionary(
                static parts => Uri.UnescapeDataString(parts[0].Replace('+', ' ')),
                static parts => parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : string.Empty,
                StringComparer.Ordinal);
}
