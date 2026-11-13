using System.Diagnostics;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Kazo.Os.UI.E2E.Tests;

/// <summary>
/// store-ux work item 6 (ADR 0266 §8), end to end on the mobile shell at 390 × 844 with a test
/// catalog: the Store's header holds on two lines, its tabs sit in a bar at the bottom of the
/// window, every touch target of the home page, the app page and the screenshot viewer is at least
/// 44 × 44 px, the portrait screenshot comes before the landscape one, and a message floats above
/// the bar of tabs. Each step leaves a screenshot in <c>Tests/TestResults/store-mobile/</c>.
/// </summary>
public sealed class StoreMobileTests(ITestOutputHelper output)
{
    private const string OfficialCatalogAddress = "https://raw.githubusercontent.com/Kazo-ca/Os-AppMarketPlace/packages/catalog.json";
    private const string PackagesRoot = "https://raw.githubusercontent.com/Kazo-ca/Os-AppMarketPlace/packages/packages/";
    private const string AppId = "org.e2e.phone";
    private const double MinimumTarget = 44;

    // A 16 × 10 (landscape) and a 9 × 19 (portrait) PNG.
    private static readonly byte[] LandscapePng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAABAAAAAKCAIAAAAy3EnLAAAAFElEQVR4nGNwWNBAEmIY1TA0NQAAcjjcARK4LTQAAAAASUVORK5CYII=");

    private static readonly byte[] PortraitPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAkAAAATCAIAAADNvrC6AAAAE0lEQVR4nGNwWNCACzGMyo1IOQA04OshL6PL/gAAAABJRU5ErkJggg==");

    private static string App(string id, string name, string publishedAt, string screenshots) => $$$"""
        {"appId":"{{{id}}}","kind":"app","name":"{{{name}}}","category":"productivity","tags":[],
         "shortDescriptions":{"en":"{{{name}}} does useful things"},"license":"MIT","repository":"https://github.com/Kazo-ca/Os-App-E2E",
         "latestVersion":"1.0.0","versions":[{"version":"1.0.0","commit":"abc","package":"packages/{{{id}}}/1.0.0/{{{id}}}-1.0.0.kazoapp",
           "sha256":"{{{new string('a', 64)}}}","size":1024,"capabilities":["notifications"],"publishedAt":"{{{publishedAt}}}",
           "icon":"packages/{{{id}}}/1.0.0/icon.png","screenshots":[{{{screenshots}}}],
           "descriptions":{"en":"packages/{{{id}}}/1.0.0/description.en.md"}}]}
        """;

    // The catalog lists the landscape screenshot first: on a phone the portrait one must come first.
    private static readonly string Catalog = $$$"""
        {"schemaVersion":1,
         "categories":[{"id":"productivity","labels":{"en":"Productivity"}},{"id":"graphics","labels":{"en":"Graphics"}}],
         "apps":[{{{App(AppId, "E2E Phone", "2026-11-01T00:00:00Z", $"\"packages/{AppId}/1.0.0/wide.png\",\"packages/{AppId}/1.0.0/tall.png\"")}}},
                 {{{App("org.e2e.other", "E2E Other", "2026-06-01T00:00:00Z", string.Empty)}}}]}
        """;

    [Fact]
    public async Task On_a_phone_the_tabs_are_at_the_bottom_targets_are_44_px_and_portrait_screenshots_come_first()
    {
        string solutionDirectory = E2ESupport.FindSolutionDirectory();
        string shots = Path.Combine(solutionDirectory, "Tests", "TestResults", "store-mobile");
        Directory.CreateDirectory(shots);
        int port = E2ESupport.ReservePort();
        string address = $"http://127.0.0.1:{port}";
        using Process server = E2ESupport.StartHarness(solutionDirectory, address, testDemo: false, output);

        try
        {
            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, new BrowserTypeLaunchOptions { Headless = true });
            await using IBrowserContext context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1400, Height = 900 } });
            await context.RouteAsync(OfficialCatalogAddress, route =>
                route.FulfillAsync(new() { Status = 200, ContentType = "application/json", Body = Catalog }));
            await context.RouteAsync($"{PackagesRoot}**", route =>
            {
                string url = route.Request.Url;
                if (url.EndsWith(".kazoapp", StringComparison.Ordinal))
                {
                    // An install that fails: its message must float above the bar of tabs.
                    return route.FulfillAsync(new() { Status = 404, Body = "missing" });
                }

                return url.EndsWith(".png", StringComparison.Ordinal)
                    ? route.FulfillAsync(new() { Status = 200, ContentType = "image/png", BodyBytes = url.EndsWith("tall.png", StringComparison.Ordinal) ? PortraitPng : LandscapePng })
                    : route.FulfillAsync(new() { Status = 200, ContentType = "text/markdown", Body = "A phone-friendly app. See [the site](https://example.test) for more." });
            });

            IPage page = await context.NewPageAsync();
            List<string> consoleErrors = E2ESupport.AttachDiagnostics(page, output);
            await E2ESupport.NavigateWhenReadyAsync(page, address, output);
            await E2ESupport.CreateLocalProfileAndReachDesktopAsync(page, output);

            await page.GetByRole(AriaRole.Button, new() { Name = "System clock, notifications, and calendar" }).ClickAsync();
            await page.GetByRole(AriaRole.Dialog, new() { Name = "Notifications and calendar" })
                .GetByRole(AriaRole.Radio, new() { Name = "Mobile", Exact = true }).ClickAsync();
            ILocator home = page.Locator(".mobile-home-screen");
            await Assertions.Expect(home).ToBeVisibleAsync();
            await page.SetViewportSizeAsync(390, 844);

            await home.GetByRole(AriaRole.Button, new() { Name = "All applications", Exact = true }).ClickAsync();
            ILocator drawer = page.GetByRole(AriaRole.Dialog, new() { Name = "All applications", Exact = true });
            await drawer.GetByRole(AriaRole.Searchbox, new() { Name = "Search applications" }).FillAsync("Store");
            await drawer.GetByRole(AriaRole.Button, new() { Name = "Kazo OS Store", Exact = true }).ClickAsync();

            ILocator store = page.Locator(".store-root");
            await Assertions.Expect(store.Locator(".store-card", new() { HasText = "E2E Other" })).ToBeVisibleAsync(new() { Timeout = 30000 });
            await WaitForCalmAsync(page);
            await page.ScreenshotAsync(new() { Path = Path.Combine(shots, "50-mobile-home.png") });

            // The header holds on two lines (brand and Refresh, then the search box).
            LocatorBoundingBoxResult header = await BoxAsync(store.Locator(".store-header"));
            Assert.True(header.Height <= 120, $"The header is {header.Height} px tall: more than two lines.");

            // The tabs are a bar at the bottom of the window, below the scrolling body.
            LocatorBoundingBoxResult root = await BoxAsync(store);
            LocatorBoundingBoxResult tabs = await BoxAsync(store.GetByRole(AriaRole.Tablist));
            LocatorBoundingBoxResult body = await BoxAsync(store.Locator(".store-body"));
            Assert.Equal(root.Y + root.Height, tabs.Y + tabs.Height, 1.0);
            Assert.Equal(root.Width, tabs.Width, 1.0);
            Assert.True(body.Y + body.Height <= tabs.Y + 0.5, "The scrolling body runs under the bar of tabs.");
            await Assertions.Expect(store.GetByRole(AriaRole.Tab, new() { Name = "Home" })).ToHaveAttributeAsync("aria-selected", "true");
            output.WriteLine($"[test] header {header.Height} px, tabs at the bottom ({tabs.Y} to {tabs.Y + tabs.Height}).");

            await AssertTouchTargetsAsync(store, "home");

            // A failed install: the message floats above the bar of tabs.
            await store.Locator(".store-card", new() { HasText = "E2E Other" }).GetByRole(AriaRole.Button, new() { Name = "Install", Exact = true }).ClickAsync();
            ILocator toast = store.Locator(".store-toast");
            await Assertions.Expect(toast).ToBeVisibleAsync(new() { Timeout = 30000 });
            await WaitForCalmAsync(page);
            LocatorBoundingBoxResult toastBox = await BoxAsync(toast);
            Assert.True(toastBox.Y + toastBox.Height <= tabs.Y, "The message covers the bar of tabs.");
            await page.ScreenshotAsync(new() { Path = Path.Combine(shots, "51-mobile-message.png") });
            await toast.GetByRole(AriaRole.Button).ClickAsync();

            // The app page: the portrait screenshot comes first, the targets stay 44 px.
            await store.Locator(".store-card", new() { HasText = "E2E Phone" }).GetByRole(AriaRole.Button, new() { Name = "E2E Phone" }).First.ClickAsync();
            ILocator detail = store.Locator(".store-detail");
            await Assertions.Expect(detail.Locator("h2")).ToHaveTextAsync("E2E Phone", new() { Timeout = 20000 });
            ILocator thumbnails = detail.Locator(".store-screenshot-thumb img");
            await Assertions.Expect(thumbnails.First).ToHaveAttributeAsync("src", $"{PackagesRoot}{AppId}/1.0.0/tall.png", new() { Timeout = 20000 });
            await Assertions.Expect(detail.Locator(".store-screenshots li").First).ToHaveClassAsync("is-portrait");
            LocatorBoundingBoxResult tall = await BoxAsync(thumbnails.Nth(0));
            Assert.True(tall.Height > tall.Width, "The portrait screenshot is not shown tall.");
            await WaitForCalmAsync(page);
            await page.ScreenshotAsync(new() { Path = Path.Combine(shots, "52-mobile-app-page.png") });
            await AssertTouchTargetsAsync(store, "app page");

            await detail.Locator(".store-screenshots").ScrollIntoViewIfNeededAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(shots, "53-mobile-app-page-screenshots.png") });

            // The viewer moves in the same order as the strip.
            await detail.Locator(".store-screenshot-thumb").First.ClickAsync();
            ILocator viewer = page.GetByRole(AriaRole.Dialog, new() { Name = "Screenshots of E2E Phone" });
            await Assertions.Expect(viewer.Locator(".store-viewer-image")).ToHaveAttributeAsync("src", $"{PackagesRoot}{AppId}/1.0.0/tall.png");
            await Assertions.Expect(viewer.Locator(".store-viewer-position")).ToHaveTextAsync("1 / 2");
            await WaitForCalmAsync(page);
            await page.ScreenshotAsync(new() { Path = Path.Combine(shots, "54-mobile-viewer.png") });
            await AssertTouchTargetsAsync(viewer, "viewer");
            await viewer.GetByRole(AriaRole.Button, new() { Name = "Close" }).ClickAsync();
            await Assertions.Expect(viewer).ToHaveCountAsync(0);

            // The Installed tab, from the bar of tabs.
            await store.GetByRole(AriaRole.Tab, new() { Name = "Installed" }).ClickAsync();
            await Assertions.Expect(store.GetByRole(AriaRole.Tab, new() { Name = "Installed" })).ToHaveAttributeAsync("aria-selected", "true");
            await WaitForCalmAsync(page);
            await page.ScreenshotAsync(new() { Path = Path.Combine(shots, "55-mobile-installed.png") });
            await AssertTouchTargetsAsync(store, "installed");

            // A search from the header.
            await store.GetByRole(AriaRole.Tab, new() { Name = "Home" }).ClickAsync();
            await store.GetByRole(AriaRole.Searchbox).FillAsync("Phone");
            await Assertions.Expect(store.Locator(".store-card")).ToHaveCountAsync(1);
            await page.ScreenshotAsync(new() { Path = Path.Combine(shots, "56-mobile-search.png") });

            // The failed download of the test package is the only expected console error.
            Assert.DoesNotContain(consoleErrors, error => !error.Contains("404", StringComparison.Ordinal));
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }

    /// <summary>
    /// Every visible control is at least 44 × 44 px (ADR 0266 §8). A link inside running text is
    /// exempt, as WCAG 2.5.8 exempts inline targets.
    /// </summary>
    private async Task AssertTouchTargetsAsync(ILocator scope, string where)
    {
        string[] small = await scope.EvaluateAsync<string[]>(
            """
            (root, minimum) => [...root.querySelectorAll("button, a[href], input, select, [role=tab]")]
                .filter(element => element.getClientRects().length > 0 && getComputedStyle(element).visibility !== "hidden")
                .filter(element => !element.closest(".store-reading"))
                .map(element => ({ element, box: element.getBoundingClientRect() }))
                .filter(({ box }) => box.width < minimum - 0.5 || box.height < minimum - 0.5)
                .map(({ element, box }) => `${element.tagName.toLowerCase()}.${[...element.classList].join(".")} "${(element.getAttribute("aria-label") || element.textContent || "").trim().slice(0, 30)}" ${Math.round(box.width)}x${Math.round(box.height)}`)
            """,
            MinimumTarget);
        output.WriteLine($"[test] {where}: {small.Length} target(s) under {MinimumTarget} px.");
        Assert.True(small.Length == 0, $"Touch targets under {MinimumTarget} px on the {where}:\n{string.Join('\n', small)}");
    }

    private static async Task<LocatorBoundingBoxResult> BoxAsync(ILocator locator) =>
        await locator.BoundingBoxAsync() ?? throw new Xunit.Sdk.XunitException("The element has no box.");

    /// <summary>Lets the animations (the window opening, a page) end before a screenshot.</summary>
    private static Task WaitForCalmAsync(IPage page) =>
        page.EvaluateAsync("async () => Promise.all(document.getAnimations().map(animation => animation.finished.catch(() => { })))");
}
