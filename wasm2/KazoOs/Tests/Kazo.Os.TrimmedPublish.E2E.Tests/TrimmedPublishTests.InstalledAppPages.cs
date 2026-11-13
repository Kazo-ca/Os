using Microsoft.Playwright;

namespace Kazo.Os.TrimmedPublish.E2E.Tests;

/// <summary>
/// ADR 0243: an iframe pointed at an installed app's page (<c>_content/&lt;assembly&gt;/…html</c>)
/// shows that page, with its query on the frame's address, and not a second Kazo OS. Before this
/// ADR the published service worker answered every page load outside its release with
/// <c>index.html</c>.
/// </summary>
public sealed partial class TrimmedPublishTests
{
    /// <summary>The page the tests copy into the installed apps' cache, as the OS copies an app's files.</summary>
    private const string InstalledPageHtml =
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Installed app page</title></head>"
        + "<body><p id=\"installed-page\">installed page</p></body></html>";

    /// <summary>
    /// Copies an HTML page into the installed apps' cache, opens it in an iframe with a query, and
    /// returns what the frame shows: its paragraph, its query and whether it is cross-origin isolated,
    /// or why it failed. The page is removed from the cache afterwards.
    /// </summary>
    private const string FrameInstalledPageScript =
        """
        async ({ content, html }) => {
            const assets = await import('./_content/Kazo.Os.Infrastructure.Browser/installedAppAssets.js');
            if (!await assets.prepare(10000)) return { error: 'no service worker' };
            const address = content + 'host/index.html';
            await assets.put(address, Array.from(new TextEncoder().encode(html)), 'text/html');
            const frame = document.createElement('iframe');
            try {
                await new Promise((resolve, reject) => {
                    const timer = setTimeout(() => reject(new Error('the frame did not load')), 20000);
                    frame.onload = () => { clearTimeout(timer); resolve(); };
                    frame.src = address + '?author=kazo&webgl';
                    document.body.appendChild(frame);
                });
                const inner = frame.contentDocument;
                const paragraph = inner && inner.getElementById('installed-page');
                return {
                    text: paragraph ? paragraph.textContent : '',
                    title: inner ? inner.title : '',
                    search: frame.contentWindow.location.search,
                    isolated: !!frame.contentWindow.crossOriginIsolated
                };
            } catch (error) {
                return { error: String(error) };
            } finally {
                frame.remove();
                await assets.remove(address);
            }
        }
        """;

    /// <summary>
    /// The published service worker (on the address where index.html registers it) answers the page
    /// load of an installed app's file from the installed apps' cache, online and offline, with the
    /// cross-origin isolation headers of ADR 0131 when the OS page has them.
    /// </summary>
    [TrimmedPublishFact]
    public async Task The_published_service_worker_serves_an_installed_app_page_to_an_iframe()
    {
        TrimmedPublishSettings settings = TrimmedPublishSettings.FromEnvironment()!;
        if (settings.PwaUrl is null)
        {
            Log($"{TrimmedPublishSettings.PwaUrlVariable} is not set: no service worker, installed app page check skipped.");
            return;
        }

        await using Session session = await Session.StartAsync(output);
        IPage page = session.Page;
        try
        {
            await NavigateWhenReadyAsync(page, settings.PwaUrl);
            await WaitForServiceWorkerControllerAsync(page);
            await SignInAsync(page);
            bool pageIsolated = await page.EvaluateAsync<bool>("() => crossOriginIsolated");

            FramedPage online = await FrameInstalledPageAsync(page);
            AssertInstalledPage(online, pageIsolated);
            Log($"online: the iframe showed the installed app's page with {online.Search} (crossOriginIsolated={online.Isolated}).");

            // Offline the page comes from the installed apps' cache all the same.
            await session.Context.SetOfflineAsync(true);
            try
            {
                FramedPage offline = await FrameInstalledPageAsync(page);
                AssertInstalledPage(offline, pageIsolated);
                Log("offline: the iframe showed the installed app's page from the cache.");
            }
            finally
            {
                await session.Context.SetOfflineAsync(false);
            }

            session.AssertNoErrors("installed app page in an iframe");
        }
        catch
        {
            await SaveScreenshotAsync(page, settings, "installed-app-page");
            throw;
        }
    }

    /// <summary>
    /// On the development address, the "installed apps only" worker the OS registers (ADR 0206)
    /// answers the same page load: an app shows its page the same way on both hosts.
    /// </summary>
    [TrimmedPublishFact]
    public async Task The_installed_apps_worker_serves_an_installed_app_page_to_an_iframe()
    {
        TrimmedPublishSettings settings = TrimmedPublishSettings.FromEnvironment()!;
        await using Session session = await Session.StartAsync(output);
        IPage page = session.Page;
        try
        {
            await NavigateWhenReadyAsync(page, settings.Url);
            await SignInAsync(page);

            FramedPage framed = await FrameInstalledPageAsync(page);
            AssertInstalledPage(framed, pageIsolated: false);
            Log($"development address: the iframe showed the installed app's page with {framed.Search}.");
            session.AssertNoErrors("installed app page in an iframe (development address)");
        }
        catch
        {
            await SaveScreenshotAsync(page, settings, "installed-app-page-dev");
            throw;
        }
    }

    private static async Task<FramedPage> FrameInstalledPageAsync(IPage page)
    {
        FramedPage framed = await page.EvaluateAsync<FramedPage>(FrameInstalledPageScript, new { content = ProbeContent, html = InstalledPageHtml });
        Assert.True(string.IsNullOrEmpty(framed.Error), $"the installed app's page could not be framed: {framed.Error}");
        return framed;
    }

    /// <summary>
    /// The frame shows the copied page (not the OS's index.html), keeps its query, and is isolated
    /// whenever the OS page is: the worker added COOP/COEP to the page it served (ADR 0131).
    /// </summary>
    private static void AssertInstalledPage(FramedPage framed, bool pageIsolated)
    {
        Assert.Equal("Installed app page", framed.Title);
        Assert.Equal("installed page", framed.Text);
        Assert.Equal("?author=kazo&webgl", framed.Search);
        if (pageIsolated)
        {
            Assert.True(framed.Isolated, "the OS page is cross-origin isolated but the installed app's page is not");
        }
    }

    /// <summary>
    /// What <see cref="FrameInstalledPageScript"/> read from the frame. Settable properties and a
    /// parameterless constructor: Playwright fills the result that way.
    /// </summary>
    private sealed class FramedPage
    {
        public string? Text { get; set; }

        public string? Title { get; set; }

        public string? Search { get; set; }

        public bool Isolated { get; set; }

        public string? Error { get; set; }
    }
}
