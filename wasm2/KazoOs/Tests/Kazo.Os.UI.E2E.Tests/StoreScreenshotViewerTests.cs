using System.Diagnostics;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Kazo.Os.UI.E2E.Tests;

/// <summary>
/// store-ux work item 2, end to end: on an app's page in the Kazo OS Store, a screenshot is a
/// button; Enter opens it at the size of the Store window, → shows the next one, Escape closes the
/// viewer and gives the focus back to the screenshot last shown, and the mouse's Back closes the
/// viewer before the app's page. The catalog is a test catalog served in place of the official one.
/// </summary>
public sealed class StoreScreenshotViewerTests(ITestOutputHelper output)
{
    // Both channels of the official catalog (ADR 0271 §6): a prerelease OS reads catalog-beta.json.
    private const string OfficialCatalogAddress = "https://raw.githubusercontent.com/Kazo-ca/Os-AppMarketPlace/packages/catalog{.json,-beta.json}";
    private const string PackagesRoot = "https://raw.githubusercontent.com/Kazo-ca/Os-AppMarketPlace/packages/packages/";
    private const string AppId = "org.e2e.gallery";

    // A 1×1 transparent PNG: the icon and the three screenshots.
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private static readonly string Catalog = $$$"""
        {"schemaVersion":1,"categories":[{"id":"productivity","labels":{"en":"Productivity","fr":"Productivité"}}],
         "apps":[{"appId":"{{{AppId}}}","kind":"app","name":"E2E Gallery","category":"productivity","tags":[],
                  "shortDescriptions":{"en":"Shows three screenshots","fr":"Montre trois captures"},
                  "license":"MIT","repository":"https://github.com/Kazo-ca/Os-App-E2E","latestVersion":"1.0.0",
                  "versions":[{"version":"1.0.0","commit":"abc","package":"packages/{{{AppId}}}/1.0.0/{{{AppId}}}-1.0.0.kazoapp",
                    "sha256":"{{{new string('a', 64)}}}","size":1024,"capabilities":[],
                    "icon":"packages/{{{AppId}}}/1.0.0/icon.png",
                    "screenshots":["packages/{{{AppId}}}/1.0.0/one.png","packages/{{{AppId}}}/1.0.0/two.png","packages/{{{AppId}}}/1.0.0/three.png"],
                    "descriptions":{"en":"packages/{{{AppId}}}/1.0.0/description.en.md"}}]}]}
        """;

    [Fact]
    public async Task A_screenshot_opens_in_the_viewer_moves_with_the_keys_and_closes_with_escape_and_back()
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
            TaskCompletionSource catalogServed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await context.RouteAsync(OfficialCatalogAddress, async route =>
            {
                await route.FulfillAsync(new() { Status = 200, ContentType = "application/json", Body = Catalog });
                catalogServed.TrySetResult();
            });
            await context.RouteAsync($"{PackagesRoot}{AppId}/**", route => route.Request.Url.EndsWith(".png", StringComparison.Ordinal)
                ? route.FulfillAsync(new() { Status = 200, ContentType = "image/png", BodyBytes = Png })
                : route.FulfillAsync(new() { Status = 200, ContentType = "text/markdown", Body = "Three screenshots to look at." }));

            IPage page = await context.NewPageAsync();
            List<string> consoleErrors = E2ESupport.AttachDiagnostics(page, output);

            await E2ESupport.NavigateWhenReadyAsync(page, address, output);
            await E2ESupport.CreateLocalProfileAndReachDesktopAsync(page, output);
            await catalogServed.Task.WaitAsync(TimeSpan.FromSeconds(60));

            await E2ESupport.OpenAppAsync(page, "Kazo OS Store", output);
            await page.Locator(".store-card", new() { HasText = "E2E Gallery" }).ClickAsync(new() { Timeout = 20000 });
            ILocator detail = page.Locator(".store-detail");
            await Assertions.Expect(detail.Locator("h2")).ToHaveTextAsync("E2E Gallery", new() { Timeout = 20000 });

            // The screenshots are buttons; Enter on the first one opens the viewer.
            ILocator thumbnails = page.Locator(".store-screenshot-thumb");
            await Assertions.Expect(thumbnails).ToHaveCountAsync(3);
            await thumbnails.Nth(0).FocusAsync();
            await page.Keyboard.PressAsync("Enter");

            ILocator viewer = page.GetByRole(AriaRole.Dialog, new() { Name = "Screenshots of E2E Gallery" });
            await Assertions.Expect(viewer).ToBeVisibleAsync(new() { Timeout = 10000 });
            ILocator image = viewer.Locator(".store-viewer-image");
            await Assertions.Expect(image).ToHaveAttributeAsync("src", $"{PackagesRoot}{AppId}/1.0.0/one.png");
            await Assertions.Expect(viewer.GetByRole(AriaRole.Button, new() { Name = "Previous screenshot" })).ToBeDisabledAsync();

            // The viewer covers the Store window, not more.
            var viewerBox = await viewer.BoundingBoxAsync() ?? throw new Xunit.Sdk.XunitException("The viewer has no box.");
            var storeBox = await page.Locator(".store-root").BoundingBoxAsync() ?? throw new Xunit.Sdk.XunitException("The Store has no box.");
            Assert.Equal(storeBox.Width, viewerBox.Width, 1.0);
            Assert.Equal(storeBox.Height, viewerBox.Height, 1.0);
            output.WriteLine("[test] Enter opened the viewer at the size of the Store window.");

            await page.Keyboard.PressAsync("ArrowRight");
            await Assertions.Expect(image).ToHaveAttributeAsync("src", $"{PackagesRoot}{AppId}/1.0.0/two.png");
            await Assertions.Expect(viewer.Locator(".store-viewer-position")).ToHaveTextAsync("2 / 3");

            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(viewer).ToHaveCountAsync(0);
            await Assertions.Expect(thumbnails.Nth(1)).ToBeFocusedAsync();
            output.WriteLine("[test] → moved to the second screenshot and Escape gave the focus back to it.");

            // A click opens it again; the mouse's Back (Alt+←) closes the viewer, not the app's page.
            await thumbnails.Nth(2).ClickAsync();
            await Assertions.Expect(viewer).ToBeVisibleAsync();
            await Assertions.Expect(image).ToHaveAttributeAsync("src", $"{PackagesRoot}{AppId}/1.0.0/three.png");
            await page.Keyboard.PressAsync("Alt+ArrowLeft");
            await Assertions.Expect(viewer).ToHaveCountAsync(0);
            await Assertions.Expect(detail.Locator("h2")).ToHaveTextAsync("E2E Gallery");
            output.WriteLine("[test] Back closed the viewer and left the app's page open.");

            Assert.Empty(consoleErrors);
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }
}
