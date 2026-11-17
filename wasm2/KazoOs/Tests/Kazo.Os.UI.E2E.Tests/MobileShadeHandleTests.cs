using System.Diagnostics;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Kazo.Os.UI.E2E.Tests;

/// <summary>
/// Issue #408: on the mobile shell, the notification shade's handle sat on top of the active app,
/// so the buttons an app puts at the very top of its window never got a touch. The handle now
/// lives in a status-bar strip of its own, above the app.
/// </summary>
public sealed class MobileShadeHandleTests(ITestOutputHelper output)
{
    [Fact]
    public async Task The_top_edge_of_a_mobile_app_receives_touches_instead_of_the_shade_handle()
    {
        string solutionDirectory = E2ESupport.FindSolutionDirectory();
        int port = E2ESupport.ReservePort();
        string address = $"http://127.0.0.1:{port}";
        using Process server = E2ESupport.StartHarness(solutionDirectory, address, testDemo: false, output);

        try
        {
            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright,
                new BrowserTypeLaunchOptions { Headless = true });

            await using IBrowserContext context = await browser.NewContextAsync();
            IPage page = await context.NewPageAsync();
            List<string> consoleErrors = E2ESupport.AttachDiagnostics(page, output);

            await E2ESupport.NavigateWhenReadyAsync(page, address, output);
            await E2ESupport.CreateLocalProfileAndReachDesktopAsync(page, output);

            await page.GetByRole(AriaRole.Button, new() { Name = "System clock, notifications, and calendar" }).ClickAsync();
            ILocator clockDialog = page.GetByRole(AriaRole.Dialog, new() { Name = "Notifications and calendar" });
            await clockDialog.GetByRole(AriaRole.Radio, new() { Name = "Mobile" }).ClickAsync();

            ILocator shadeHandle = page.GetByRole(AriaRole.Button, new() { Name = "Notifications, calendar, and platform mode" });
            await shadeHandle.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });

            await page.Locator(".mobile-home-screen").GetByRole(AriaRole.Button, new() { Name = "Settings" }).First.ClickAsync();
            ILocator appLayer = page.Locator(".mobile-app-layer:not([hidden])");
            await appLayer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15000 });
            output.WriteLine("[test] Settings open in the mobile shell.");

            // What a touch on the top row of the app hits, at its left edge, middle and right edge.
            string[] hits = await page.EvaluateAsync<string[]>(
                """
                () => {
                    const layer = document.querySelector('.mobile-app-layer:not([hidden])');
                    const rect = layer.getBoundingClientRect();
                    const y = rect.top + 2;
                    return [rect.left + 4, rect.left + rect.width / 2, rect.right - 4].map(x => {
                        const hit = document.elementFromPoint(x, y);
                        if (!hit) return 'nothing';
                        if (hit.closest('.mobile-shade-handle')) return 'shade-handle';
                        return layer.contains(hit) ? 'app' : (hit.className || hit.tagName).toString();
                    });
                }
                """);

            Assert.All(hits, hit => Assert.Equal("app", hit));

            // The handle still opens the shade, from its own strip.
            await shadeHandle.ClickAsync();
            await Assertions.Expect(page.Locator(".mobile-shade-panel")).ToBeVisibleAsync(new() { Timeout = 5000 });

            Assert.Empty(consoleErrors);
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }
}
