using System.Diagnostics;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Kazo.Os.UI.E2E.Tests;

/// <summary>
/// store-ux work item 5 (ADR 0266 §2, §3 and §5), end to end with a test catalog served in place of
/// the official one: empty cards while the catalogs load, the newest app not installed in the
/// « Featured » banner with its first screenshot, one grid newest first, category chips that
/// filter it, an « Install » button on each card with the « Install for everyone » menu for an
/// administrator, no trust badge on a verified app, and no « Themes » tab while no pack exists.
/// </summary>
public sealed class StoreHomeTests(ITestOutputHelper output)
{
    // Both channels of the official catalog (ADR 0271 §6): a prerelease OS reads catalog-beta.json.
    private const string OfficialCatalogAddress = "https://raw.githubusercontent.com/Kazo-ca/Os-AppMarketPlace/packages/catalog{.json,-beta.json}";
    private const string PackagesRoot = "https://raw.githubusercontent.com/Kazo-ca/Os-AppMarketPlace/packages/packages/";

    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private static string App(string id, string name, string category, string publishedAt, bool screenshot = false) => $$$"""
        {"appId":"{{{id}}}","kind":"app","name":"{{{name}}}","category":"{{{category}}}","tags":[],
         "shortDescriptions":{"en":"{{{name}}} does useful things"},"license":"MIT","repository":"https://github.com/Kazo-ca/Os-App-E2E",
         "latestVersion":"1.0.0","versions":[{"version":"1.0.0","commit":"abc","package":"packages/{{{id}}}/1.0.0/{{{id}}}-1.0.0.kazoapp",
           "sha256":"{{{new string('a', 64)}}}","size":1024,"capabilities":[],"publishedAt":"{{{publishedAt}}}",
           "screenshots":[{{{(screenshot ? $"\"packages/{id}/1.0.0/shot.png\"" : string.Empty)}}}],
           "descriptions":{"en":"packages/{{{id}}}/1.0.0/description.en.md"}}]}
        """;

    private static readonly string Catalog = $$$"""
        {"schemaVersion":1,
         "categories":[{"id":"productivity","labels":{"en":"Productivity"}},{"id":"graphics","labels":{"en":"Graphics"}}],
         "apps":[{{{App("org.e2e.older", "E2E Older", "productivity", "2026-01-05T00:00:00Z")}}},
                 {{{App("org.e2e.newest", "E2E Newest", "graphics", "2026-11-01T00:00:00Z", screenshot: true)}}},
                 {{{App("org.e2e.middle", "E2E Middle", "productivity", "2026-06-01T00:00:00Z")}}}]}
        """;

    [Fact]
    public async Task The_home_page_features_the_newest_app_and_filters_the_grid_by_category()
    {
        string solutionDirectory = E2ESupport.FindSolutionDirectory();
        int port = E2ESupport.ReservePort();
        string address = $"http://127.0.0.1:{port}";
        using Process server = E2ESupport.StartHarness(solutionDirectory, address, testDemo: false, output);

        try
        {
            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, new BrowserTypeLaunchOptions { Headless = true });
            await using IBrowserContext context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1366, Height = 900 } });
            // The catalog waits until the Store is open, so its first draw is the empty cards.
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await context.RouteAsync(OfficialCatalogAddress, async route =>
            {
                await release.Task;
                await route.FulfillAsync(new() { Status = 200, ContentType = "application/json", Body = Catalog });
            });
            await context.RouteAsync($"{PackagesRoot}**", route => route.Request.Url.EndsWith(".png", StringComparison.Ordinal)
                ? route.FulfillAsync(new() { Status = 200, ContentType = "image/png", BodyBytes = Png })
                : route.FulfillAsync(new() { Status = 200, ContentType = "text/markdown", Body = "Description." }));

            IPage page = await context.NewPageAsync();
            List<string> consoleErrors = E2ESupport.AttachDiagnostics(page, output);

            await E2ESupport.NavigateWhenReadyAsync(page, address, output);
            await E2ESupport.CreateLocalProfileAndReachDesktopAsync(page, output);
            await E2ESupport.OpenAppAsync(page, "Kazo OS Store", output);

            // ADR 0266 §2: empty cards in place of the word « Loading ».
            await Assertions.Expect(page.Locator(".store-card.is-skeleton")).ToHaveCountAsync(6, new() { Timeout = 20000 });
            output.WriteLine("[test] empty cards while the catalog loads.");
            release.TrySetResult();

            // ADR 0266 §2: the newest app not installed, with its first screenshot.
            ILocator featured = page.Locator(".store-featured");
            await Assertions.Expect(featured.Locator("h2")).ToHaveTextAsync("E2E Newest", new() { Timeout = 30000 });
            await Assertions.Expect(featured.Locator(".store-featured-shot")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".store-card.is-skeleton")).ToHaveCountAsync(0);

            // One grid, newest first, and no trust badge on the official (verified) catalog.
            ILocator names = page.Locator(".store-grid .store-card-name");
            await Assertions.Expect(names).ToHaveTextAsync(["E2E Newest", "E2E Middle", "E2E Older"]);
            await Assertions.Expect(page.Locator(".store-card .store-trust")).ToHaveCountAsync(0);

            // ADR 0266 §5: no pack in any catalog, so no « Themes » tab.
            await Assertions.Expect(page.GetByRole(AriaRole.Tab, new() { Name = "Themes" })).ToHaveCountAsync(0);
            await Assertions.Expect(page.GetByRole(AriaRole.Tab)).ToHaveCountAsync(2);

            // The window opens large (about 1100 × 720) in a large enough screen.
            LocatorBoundingBoxResult? root = await page.Locator(".store-root").BoundingBoxAsync();
            Assert.NotNull(root);
            Assert.True(root.Width >= 1000, $"the Store should open about 1100 px wide (got {root.Width}).");
            output.WriteLine($"[test] featured, grid and tabs ok; window content {root.Width} × {root.Height}.");

            // Category chips filter the grid; the banner leaves while a category is chosen.
            ILocator filters = page.GetByRole(AriaRole.Group, new() { Name = "Filter by category" });
            await Assertions.Expect(filters.GetByRole(AriaRole.Button)).ToHaveTextAsync(["All · 3", "Productivity · 2", "Graphics · 1"]);
            ILocator productivity = filters.GetByRole(AriaRole.Button, new() { Name = "Productivity · 2" });
            await productivity.ClickAsync();
            await Assertions.Expect(productivity).ToHaveAttributeAsync("aria-pressed", "true");
            await Assertions.Expect(names).ToHaveTextAsync(["E2E Middle", "E2E Older"]);
            await Assertions.Expect(featured).ToHaveCountAsync(0);
            await filters.GetByRole(AriaRole.Button, new() { Name = "All · 3" }).ClickAsync();
            await Assertions.Expect(names).ToHaveCountAsync(3);
            output.WriteLine("[test] the chips filter the grid.");

            // ADR 0266 §3: « Install » for me, and the menu « Install for everyone » for an administrator.
            ILocator card = page.Locator(".store-card", new() { HasText = "E2E Older" });
            await Assertions.Expect(card.GetByRole(AriaRole.Button, new() { Name = "Install", Exact = true })).ToBeVisibleAsync();
            ILocator toggle = card.GetByRole(AriaRole.Button, new() { Name = "More ways to install E2E Older" });
            await Assertions.Expect(toggle).ToHaveAttributeAsync("aria-expanded", "false");
            await toggle.ClickAsync();
            ILocator item = page.GetByRole(AriaRole.Menuitem, new() { Name = "Install for everyone" });
            await Assertions.Expect(item).ToBeFocusedAsync(new() { Timeout = 5000 });
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(item).ToHaveCountAsync(0);
            await Assertions.Expect(toggle).ToBeFocusedAsync();
            await Assertions.Expect(toggle).ToHaveAttributeAsync("aria-expanded", "false");
            output.WriteLine("[test] the install menu opens, focuses its item and closes on Escape.");

            // A click anywhere on the card, not only on its name, opens the app's page.
            await card.ClickAsync(new() { Position = new() { X = 8, Y = 8 } });
            await Assertions.Expect(page.Locator(".store-detail h2")).ToHaveTextAsync("E2E Older", new() { Timeout = 15000 });

            Assert.Empty(consoleErrors);
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }
}
