using System.Diagnostics;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Kazo.Os.UI.E2E.Tests;

/// <summary>
/// ADR 0268, end to end: a new profile finds the Kazo OS Store in the Start menu's Quick access
/// panel, between File Explorer and Settings, and no longer among its pinned apps. The link opens
/// the Store. The official catalog is answered with an empty test catalog.
/// </summary>
public sealed class StartMenuQuickAccessTests(ITestOutputHelper output)
{
    // Both channels of the official catalog (ADR 0271 §6): a prerelease OS reads catalog-beta.json.
    private const string OfficialCatalogAddress = "https://raw.githubusercontent.com/Kazo-ca/Os-AppMarketPlace/packages/catalog{.json,-beta.json}";
    private const string EmptyCatalog = """{"schemaVersion":1,"categories":[],"apps":[]}""";

    [Fact]
    public async Task The_store_sits_in_quick_access_and_is_not_pinned()
    {
        string solutionDirectory = E2ESupport.FindSolutionDirectory();
        int port = E2ESupport.ReservePort();
        string address = $"http://127.0.0.1:{port}";
        using Process server = E2ESupport.StartHarness(solutionDirectory, address, testDemo: false, output);

        try
        {
            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, new BrowserTypeLaunchOptions { Headless = true });
            await using IBrowserContext context = await browser.NewContextAsync();
            await context.RouteAsync(OfficialCatalogAddress, route =>
                route.FulfillAsync(new() { Status = 200, ContentType = "application/json", Body = EmptyCatalog }));

            IPage page = await context.NewPageAsync();
            E2ESupport.AttachDiagnostics(page, output);

            await E2ESupport.NavigateWhenReadyAsync(page, address, output);
            await E2ESupport.CreateLocalProfileAndReachDesktopAsync(page, output);

            await page.GetByRole(AriaRole.Button, new() { Name = "App launcher" }).ClickAsync();
            ILocator quickPanel = page.Locator("aside.quick-panel");
            await Assertions.Expect(quickPanel.Locator(".quick-link strong"))
                .ToHaveTextAsync(["File Explorer", "Store", "Settings"], new() { Timeout = 20000 });
            await Assertions.Expect(page.Locator(".pinned-section", new() { HasText = "Kazo OS Store" })).ToHaveCountAsync(0);
            output.WriteLine("[test] The Store is in Quick access, between File Explorer and Settings, and not pinned.");

            ILocator storeLink = quickPanel.GetByRole(AriaRole.Button, new() { Name = "Open Kazo OS Store", Exact = true });
            await Assertions.Expect(storeLink).ToBeEnabledAsync();
            await storeLink.ClickAsync();
            await Assertions.Expect(page.Locator(".store-title")).ToHaveTextAsync("Kazo OS Store", new() { Timeout = 20000 });
            output.WriteLine("[test] The Quick access link opened the Store.");
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }
}
