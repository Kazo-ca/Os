using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Kazo.Os.Pwa.E2E.Tests;

/// <summary>
/// A fake OAuth provider on another origin (ADR 0267 section 5), for the sign-in window test: its
/// authorization page sends the browser back to the redirect URI with a code, and its token
/// endpoint answers with CORS headers like Dropbox and Microsoft, checking PKCE S256 and issuing
/// a refresh token. No test ever reaches a real provider.
/// </summary>
internal sealed class FakeOAuthProviderHost : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, (string Challenge, string RedirectUri)> _codes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _refreshTokens = new(StringComparer.Ordinal);
    private WebApplication? _app;
    private int _sequence;

    public string Address { get; private set; } = string.Empty;

    public ConcurrentQueue<string> TokenGrants { get; } = new();

    public async Task StartAsync()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        Address = $"http://127.0.0.1:{port}";
        builder.WebHost.UseUrls(Address);
        builder.Logging.ClearProviders();
        WebApplication app = builder.Build();

        app.MapGet("/authorize", (HttpContext context) =>
        {
            IQueryCollection query = context.Request.Query;
            if (query["response_type"] != "code" || query["code_challenge_method"] != "S256")
            {
                return Results.BadRequest();
            }

            string code = $"code-{Interlocked.Increment(ref _sequence)}";
            _codes[code] = (query["code_challenge"].ToString(), query["redirect_uri"].ToString());
            return Results.Redirect($"{query["redirect_uri"]}?code={code}&state={Uri.EscapeDataString(query["state"].ToString())}");
        });

        app.MapMethods("/token", ["OPTIONS"], (HttpContext context) =>
        {
            AllowCors(context);
            return Results.NoContent();
        });
        app.MapPost("/token", async (HttpContext context) =>
        {
            AllowCors(context);
            IFormCollection form = await context.Request.ReadFormAsync();
            string grant = form["grant_type"].ToString();
            TokenGrants.Enqueue(grant);
            if (grant == "authorization_code")
            {
                if (!_codes.TryRemove(form["code"].ToString(), out (string Challenge, string RedirectUri) issued)
                    || issued.RedirectUri != form["redirect_uri"]
                    || Challenge(form["code_verifier"].ToString()) != issued.Challenge)
                {
                    return Results.Json(new { error = "invalid_grant" }, statusCode: 400);
                }
            }
            else if (grant != "refresh_token" || !_refreshTokens.ContainsKey(form["refresh_token"].ToString()))
            {
                return Results.Json(new { error = "invalid_grant" }, statusCode: 400);
            }

            int id = Interlocked.Increment(ref _sequence);
            _refreshTokens[$"refresh-{id}"] = true;
            return Results.Json(new Dictionary<string, object>
            {
                ["access_token"] = $"access-{id}",
                ["token_type"] = "bearer",
                ["expires_in"] = 14400,
                ["refresh_token"] = $"refresh-{id}"
            });
        });

        await app.StartAsync();
        _app = app;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private static void AllowCors(HttpContext context)
    {
        context.Response.Headers.AccessControlAllowOrigin = "*";
        context.Response.Headers.AccessControlAllowHeaders = "Content-Type, Accept";
        context.Response.Headers.AccessControlAllowMethods = "POST";
    }

    private static string Challenge(string verifier) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
