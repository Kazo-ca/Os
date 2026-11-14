using System.Diagnostics;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Kazo.Os.UI.E2E.Tests;

/// <summary>
/// cloud-drives work item 5 (ADR 0267 §11, ADR 0270), end to end on the real shell: File Explorer's
/// "Connect a network drive" dialog on the desktop and on the mobile shell, and the Settings page of
/// the cloud accounts. No provider is configured by the test host, so the dialog offers the folder of
/// the computer; the dialog takes the focus, so Escape closes it; each surface is scanned by axe (serious and critical findings fail) and leaves a
/// screenshot in <c>Tests/TestResults/cloud-drives/</c>. No real provider is ever reached.
/// </summary>
public sealed class CloudDrivesUiTests(ITestOutputHelper output)
{
    [Fact]
    public async Task The_connect_dialog_and_the_cloud_accounts_page_are_accessible_on_desktop_and_phone()
    {
        string solutionDirectory = E2ESupport.FindSolutionDirectory();
        string shots = Path.Combine(solutionDirectory, "Tests", "TestResults", "cloud-drives");
        Directory.CreateDirectory(shots);
        int port = E2ESupport.ReservePort();
        string address = $"http://127.0.0.1:{port}";
        using Process server = E2ESupport.StartHarness(solutionDirectory, address, testDemo: false, output);
        List<string> findings = [];

        try
        {
            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, new BrowserTypeLaunchOptions { Headless = true });
            await using IBrowserContext context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1280, Height = 800 } });
            IPage page = await context.NewPageAsync();
            E2ESupport.AttachDiagnostics(page, output);
            try
            {
                await RunStepsAsync(page, address, shots, findings);
            }
            catch
            {
                // Keep what the page showed when a step failed, next to the surfaces' screenshots.
                await page.ScreenshotAsync(new() { Path = Path.Combine(shots, "failure.png") });
                throw;
            }

            Assert.True(findings.Count == 0, string.Join('\n', findings));
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }

    private async Task RunStepsAsync(IPage page, string address, string shots, List<string> findings)
    {
        await E2ESupport.NavigateWhenReadyAsync(page, address, output);
        await E2ESupport.CreateLocalProfileAndReachDesktopAsync(page, output);

        // --- Desktop: File Explorer's dialog ---
        await E2ESupport.OpenAppAsync(page, "File Explorer", output);
        await page.GetByRole(AriaRole.Button, new() { Name = "Connect a network drive…" }).ClickAsync(new() { Timeout = 30000 });
        ILocator dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "Connect a network drive" });
        await Assertions.Expect(dialog).ToBeVisibleAsync(new() { Timeout = 15000 });
        await Assertions.Expect(dialog.GetByText("Folder of this computer")).ToBeVisibleAsync(new() { Timeout = 15000 });
        await ScanAsync(page, dialog, "desktop-connect-dialog", shots, findings);
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(dialog).ToBeHiddenAsync();

        // --- Desktop: the Settings page of the cloud accounts ---
        await E2ESupport.OpenAppAsync(page, "Settings", output);
        await page.GetByRole(AriaRole.Tab, new() { Name = "Cloud accounts" }).ClickAsync();
        ILocator panel = page.Locator("#settings-page-panel");
        await Assertions.Expect(panel.GetByRole(AriaRole.Heading, new() { Name = "Cloud accounts" })).ToBeVisibleAsync(new() { Timeout = 15000 });
        await Assertions.Expect(panel.GetByRole(AriaRole.Status).First).ToBeVisibleAsync();
        await ScanAsync(page, panel, "desktop-settings-cloud-accounts", shots, findings);

        // --- Phone: the mobile explorer offers the dialog as a chip, full screen ---
        await page.GetByRole(AriaRole.Button, new() { Name = "System clock, notifications, and calendar" }).ClickAsync();
        await page.GetByRole(AriaRole.Dialog, new() { Name = "Notifications and calendar" })
            .GetByRole(AriaRole.Radio, new() { Name = "Mobile", Exact = true }).ClickAsync();
        await page.SetViewportSizeAsync(390, 844);
        // Settings stays in front on the phone: the system Home button shows the home screen.
        await page.GetByRole(AriaRole.Button, new() { Name = "Home", Exact = true }).ClickAsync();
        ILocator home = page.Locator(".mobile-home-screen");
        await Assertions.Expect(home).ToBeVisibleAsync(new() { Timeout = 15000 });
        await home.GetByRole(AriaRole.Button, new() { Name = "All applications", Exact = true }).ClickAsync();
        ILocator drawer = page.GetByRole(AriaRole.Dialog, new() { Name = "All applications", Exact = true });
        await drawer.GetByRole(AriaRole.Searchbox, new() { Name = "Search applications" }).FillAsync("File Explorer");
        await drawer.GetByRole(AriaRole.Button, new() { Name = "File Explorer", Exact = true }).ClickAsync();

        await page.GetByRole(AriaRole.Button, new() { Name = "Connect a network drive…" }).Last.ClickAsync(new() { Timeout = 30000 });
        await Assertions.Expect(dialog).ToBeVisibleAsync(new() { Timeout = 15000 });
        LocatorBoundingBoxResult? box = await page.Locator(".cnd-dialog").BoundingBoxAsync();
        Assert.NotNull(box);
        Assert.True(box.Width >= 380, $"The dialog is {box.Width} px wide on a 390 px phone: it should fill the screen.");
        if (await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > document.documentElement.clientWidth"))
        {
            findings.Add("mobile-connect-dialog: horizontal overflow at 390 px");
        }

        await ScanAsync(page, dialog, "mobile-connect-dialog", shots, findings);
    }

    /// <summary>
    /// Runs axe on the surface only (serious and critical findings count) and saves a screenshot of
    /// the page. The taskbar's own findings are known and tracked apart (project memory).
    /// </summary>
    private async Task ScanAsync(IPage page, ILocator surfaceRoot, string surface, string shots, List<string> findings)
    {
        await page.WaitForTimeoutAsync(400); // let the open animation finish before the scan and the screenshot
        var result = await surfaceRoot.RunAxe();
        var blocking = result.Violations?
            .Where(static item => item.Impact is "serious" or "critical")
            .ToArray() ?? [];
        foreach (var violation in blocking)
        {
            foreach (var node in violation.Nodes ?? [])
            {
                output.WriteLine($"[axe] {surface} :: {violation.Id} :: {node.Target?.Selector} :: {node.Html}");
            }
        }

        if (blocking.Length > 0)
        {
            findings.Add($"{surface}: {string.Join(", ", blocking.Select(static item => item.Id))}");
        }

        await page.ScreenshotAsync(new() { Path = Path.Combine(shots, $"{surface}.png") });
    }
}
