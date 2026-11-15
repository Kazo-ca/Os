using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Kazo.Os.Platform.Core.CloudAccounts;

/// <summary>The tokens of one account, as kept sealed on the device.</summary>
/// <param name="AccessToken">Bearer token for the provider's API.</param>
/// <param name="RefreshToken">Token that renews <see cref="AccessToken"/>, or <see langword="null"/> when the provider gave none.</param>
/// <param name="ExpiresAtUtc">When <see cref="AccessToken"/> stops working, or <see langword="null"/> when the provider did not say.</param>
public sealed record OAuthTokenSet(string AccessToken, string? RefreshToken, DateTimeOffset? ExpiresAtUtc);

/// <summary>Who signed in, as the provider says.</summary>
/// <param name="AccountId">Provider's id of the account, or <see langword="null"/>.</param>
/// <param name="DisplayName">E-mail or name, or <see langword="null"/>.</param>
public sealed record CloudIdentity(string? AccountId, string? DisplayName);

/// <summary>
/// The token requests of OAuth 2.0 for a public client (RFC 6749 section 4.1.3 and 6, RFC 7636),
/// over <see cref="HttpClient"/>, which is <c>fetch</c> in the browser: every provider of ADR 0267
/// answers them with CORS headers, so no server is involved and no secret is sent.
/// </summary>
/// <param name="http">Client of the requests; its handler is a fake server in the tests.</param>
/// <param name="timeProvider">Clock of the expiry dates.</param>
public sealed class CloudOAuthTokenClient(HttpClient http, TimeProvider? timeProvider = null)
{
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>Exchanges an authorization code for tokens, with the PKCE verifier.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="scope">The access scope the code was asked for (selects the app id).</param>
    /// <param name="code">The code the callback page received.</param>
    /// <param name="verifier">The PKCE verifier of that sign-in.</param>
    /// <param name="redirectUri">The redirect URI sent in the authorization request (must match exactly).</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="CloudAccountException"><see cref="CloudAccountError.ProviderUnavailable"/> or <see cref="CloudAccountError.InvalidResponse"/>.</exception>
    public ValueTask<OAuthTokenSet> ExchangeCodeAsync(
        CloudOAuthProvider provider,
        CloudAccessScope scope,
        string code,
        string verifier,
        Uri redirectUri,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrEmpty(code);
        ArgumentException.ThrowIfNullOrEmpty(verifier);
        ArgumentNullException.ThrowIfNull(redirectUri);
        return RequestTokensAsync(
            provider,
            [
                new("grant_type", "authorization_code"),
                new("code", code),
                new("redirect_uri", redirectUri.AbsoluteUri),
                new("client_id", provider.GetClient(scope).ClientId),
                new("code_verifier", verifier)
            ],
            previousRefreshToken: null,
            refreshing: false,
            cancellationToken);
    }

    /// <summary>Renews the access token. A provider that rotates refresh tokens (Microsoft) gives a new one, kept in the result.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="scope">The account's access scope (selects the app id and, for Microsoft, the scope string).</param>
    /// <param name="refreshToken">The current refresh token.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="CloudAccountException">
    /// <see cref="CloudAccountError.ReconnectRequired"/> when the provider refuses the refresh token
    /// (<c>invalid_grant</c>, expired, revoked), <see cref="CloudAccountError.ProviderUnavailable"/>
    /// when it cannot be reached.
    /// </exception>
    public ValueTask<OAuthTokenSet> RefreshAsync(
        CloudOAuthProvider provider,
        CloudAccessScope scope,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrEmpty(refreshToken);
        CloudOAuthClient client = provider.GetClient(scope);
        List<KeyValuePair<string, string>> form =
        [
            new("grant_type", "refresh_token"),
            new("refresh_token", refreshToken),
            new("client_id", client.ClientId)
        ];
        if (client.Scope is { Length: > 0 } scopeText)
        {
            form.Add(new("scope", scopeText));
        }

        return RequestTokensAsync(provider, form, refreshToken, refreshing: true, cancellationToken);
    }

    /// <summary>Asks the provider who signed in. Never fails: an unreadable answer gives empty values.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="accessToken">A fresh access token.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async ValueTask<CloudIdentity> ReadIdentityAsync(CloudOAuthProvider provider, string accessToken, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrEmpty(accessToken);
        try
        {
            using HttpRequestMessage request = new(provider.Identity.UsePost ? HttpMethod.Post : HttpMethod.Get, provider.Identity.Address);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new CloudIdentity(null, null);
            }

            using JsonDocument document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            string? name = provider.Identity.NamePaths
                .Select(path => ReadPath(document.RootElement, path))
                .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
            return new CloudIdentity(ReadPath(document.RootElement, provider.Identity.IdPath), name);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new CloudIdentity(null, null);
        }
    }

    /// <summary>Revokes a token when the provider offers it. Best effort: returns whether the provider confirmed.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="accessToken">The access token to revoke (Dropbox revokes the whole grant with it).</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async ValueTask<bool> RevokeAsync(CloudOAuthProvider provider, string accessToken, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (provider.RevocationEndpoint is not { } endpoint || string.IsNullOrEmpty(accessToken))
        {
            return false;
        }

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async ValueTask<OAuthTokenSet> RequestTokensAsync(
        CloudOAuthProvider provider,
        IEnumerable<KeyValuePair<string, string>> form,
        string? previousRefreshToken,
        bool refreshing,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, provider.TokenEndpoint) { Content = new FormUrlEncodedContent(form) };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new CloudAccountException(CloudAccountError.ProviderUnavailable, $"{provider.DisplayName} cannot be reached.", exception);
        }

        using (response)
        {
            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new CloudAccountException(
                    CloudAccountError.ProviderUnavailable,
                    $"{provider.DisplayName} answered {(int)response.StatusCode}; try again later.");
            }

            JsonDocument document;
            try
            {
                document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException exception)
            {
                throw new CloudAccountException(CloudAccountError.InvalidResponse, $"{provider.DisplayName} sent an answer that is not JSON.", exception);
            }

            using (document)
            {
                JsonElement root = document.RootElement;
                if (!response.IsSuccessStatusCode)
                {
                    string error = ReadPath(root, "error") ?? ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    // RFC 6749 section 5.2: invalid_grant means the refresh token is expired or revoked.
                    CloudAccountError kind = refreshing && error is "invalid_grant" or "interaction_required" or "invalid_client" or "unauthorized_client"
                        ? CloudAccountError.ReconnectRequired
                        : CloudAccountError.Denied;
                    throw new CloudAccountException(kind, $"{provider.DisplayName} refused the token request ({error}).");
                }

                string? accessToken = ReadPath(root, "access_token");
                if (string.IsNullOrEmpty(accessToken))
                {
                    throw new CloudAccountException(CloudAccountError.InvalidResponse, $"{provider.DisplayName} sent no access token.");
                }

                DateTimeOffset? expiresAt = root.TryGetProperty("expires_in", out JsonElement expiresIn) && TryReadSeconds(expiresIn, out long seconds)
                    ? _time.GetUtcNow().AddSeconds(seconds)
                    : null;
                return new OAuthTokenSet(accessToken, ReadPath(root, "refresh_token") ?? previousRefreshToken, expiresAt);
            }
        }
    }

    private static async ValueTask<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using Stream content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static bool TryReadSeconds(JsonElement value, out long seconds)
    {
        seconds = 0;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt64(out seconds) && seconds > 0,
            // Some servers send expires_in as a string.
            JsonValueKind.String => long.TryParse(value.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out seconds) && seconds > 0,
            _ => false
        };
    }

    /// <summary>Reads a dotted path (<c>name.display_name</c>) as a string; numbers are returned as their text.</summary>
    private static string? ReadPath(JsonElement root, string path)
    {
        JsonElement current = root;
        foreach (string segment in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.GetRawText(),
            _ => null
        };
    }
}
