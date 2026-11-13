using System.Text.Json;
using Microsoft.Playwright;

namespace Kazo.Os.Pwa.E2E.Tests;

/// <summary>
/// ADR 0267 section 5, end to end on the published PWA: the sign-in window opened from an isolated
/// Kazo OS page goes to a provider on another origin, comes back to the static callback page
/// (served by the published service worker, query included, never a second Kazo OS), which hands
/// the code to the OS over BroadcastChannel; the page then exchanges the code with its PKCE
/// verifier and renews the token by CORS requests, as the cloud account service does.
/// </summary>
[Collection(PwaPublishCollection.Name)]
public sealed class CloudSignInWindowTests(PwaPublishFixture fixture)
{
    private const string CallbackPath = "_content/Kazo.Os.Infrastructure.Browser/oauth-callback.html";

    [Fact]
    public async Task The_callback_page_hands_the_code_to_the_os_which_exchanges_and_renews_it()
    {
        int port = PublishedAppHost.ReservePort();
        await using PublishedAppHost host = new();
        await host.StartAsync(fixture.V1WwwrootPath, port);
        await using FakeOAuthProviderHost provider = new();
        await provider.StartAsync();

        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright,
            new BrowserTypeLaunchOptions { Channel = "chrome", Headless = true });
        IBrowserContext context = await browser.NewContextAsync();
        IPage page = await context.NewPageAsync();
        await PwaTestSupport.NavigateWhenReadyAsync(page, host.Address);
        await PwaTestSupport.WaitForServiceWorkerControllerAsync(page);
        Assert.True(await page.EvaluateAsync<bool>("() => window.crossOriginIsolated"));

        // The page loads the module ahead of the click, as the dialog does (PrepareAsync).
        await page.EvaluateAsync(
            @"async ([callback, authorize]) => {
                const module = await import('./_content/Kazo.Os.Infrastructure.Browser/oauthWindow.js');
                module.prepare();
                const verifier = 'dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk';
                const state = 'state-' + Math.random().toString(36).slice(2);
                const redirect = new URL(callback, document.baseURI).href;
                const url = authorize + '/authorize?response_type=code&client_id=fake&code_challenge_method=S256'
                    + '&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM'
                    + '&redirect_uri=' + encodeURIComponent(redirect) + '&state=' + state;
                const button = document.createElement('button');
                button.id = 'cloud-sign-in-test';
                button.textContent = 'Sign in';
                button.style.cssText = 'position:fixed;top:0;left:0;z-index:2147483647';
                button.onclick = () => {
                    window.__signIn = module.authorize(url, state, 20000).then(answer => ({ answer, state, redirect, verifier }));
                };
                document.body.appendChild(button);
            }",
            new object[] { CallbackPath, provider.Address });

        Task<IPage> popupOpened = context.WaitForPageAsync();
        await page.ClickAsync("#cloud-sign-in-test");
        IPage popup = await popupOpened;
        string result = await page.EvaluateAsync<string>("async () => JSON.stringify(await window.__signIn)");
        using JsonDocument signIn = JsonDocument.Parse(result);
        JsonElement answer = signIn.RootElement.GetProperty("answer");

        Assert.Equal("authorized", answer.GetProperty("status").GetString());
        string code = answer.GetProperty("code").GetString()!;
        Assert.StartsWith("code-", code, StringComparison.Ordinal);
        // The callback page closes itself.
        for (int wait = 0; wait < 100 && !popup.IsClosed; wait++)
        {
            await Task.Delay(100);
        }

        Assert.True(popup.IsClosed);

        // Exchange and renewal from the isolated page: CORS, no secret, PKCE verifier.
        string tokens = await page.EvaluateAsync<string>(
            @"async ([tokenUrl, code, redirect, verifier]) => {
                const post = async body => (await fetch(tokenUrl, { method: 'POST', body: new URLSearchParams(body) })).json();
                const first = await post({ grant_type: 'authorization_code', code, redirect_uri: redirect, client_id: 'fake', code_verifier: verifier });
                const renewed = await post({ grant_type: 'refresh_token', refresh_token: first.refresh_token, client_id: 'fake' });
                return JSON.stringify({ first, renewed });
            }",
            new object[]
            {
                provider.Address + "/token",
                code,
                signIn.RootElement.GetProperty("redirect").GetString()!,
                signIn.RootElement.GetProperty("verifier").GetString()!
            });
        using JsonDocument exchanged = JsonDocument.Parse(tokens);
        Assert.StartsWith("access-", exchanged.RootElement.GetProperty("first").GetProperty("access_token").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("access-", exchanged.RootElement.GetProperty("renewed").GetProperty("access_token").GetString(), StringComparison.Ordinal);
        Assert.Equal(["authorization_code", "refresh_token"], provider.TokenGrants.ToArray());
    }

    [Fact]
    public async Task The_published_worker_serves_the_callback_page_with_its_query_never_a_second_os()
    {
        int port = PublishedAppHost.ReservePort();
        await using PublishedAppHost host = new();
        await host.StartAsync(fixture.V1WwwrootPath, port);

        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright,
            new BrowserTypeLaunchOptions { Channel = "chrome", Headless = true });
        IBrowserContext context = await browser.NewContextAsync();
        IPage page = await context.NewPageAsync();
        await PwaTestSupport.NavigateWhenReadyAsync(page, host.Address);
        await PwaTestSupport.WaitForServiceWorkerControllerAsync(page);

        // The release cache keeps the callback page for offline use like every other asset.
        string version = await PwaTestSupport.GetAssetsManifestVersionAsync(page);
        Assert.True(await page.EvaluateAsync<bool>(
            "async ([cache, url]) => !!(await (await caches.open(cache)).match(new URL(url, document.baseURI).href))",
            new object[] { $"kazoos-cache-{version}", CallbackPath }));

        IPage callback = await context.NewPageAsync();
        IResponse? response = await callback.GotoAsync($"{host.Address}/{CallbackPath}?code=abc&state=");

        Assert.NotNull(response);
        Assert.True(response.FromServiceWorker);
        Assert.Equal("same-origin", await response.HeaderValueAsync("cross-origin-opener-policy"));
        Assert.Equal("Kazo OS sign-in", await callback.TitleAsync());
        // Without a state the page only explains itself, and the code left the address bar.
        Assert.Contains("only used by Kazo OS", await callback.InnerTextAsync("#status"), StringComparison.Ordinal);
        Assert.DoesNotContain("code=", callback.Url, StringComparison.Ordinal);
    }
}
