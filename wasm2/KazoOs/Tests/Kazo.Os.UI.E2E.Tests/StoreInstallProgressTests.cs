using System.Diagnostics;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Kazo.Os.UI.E2E.Tests;

/// <summary>
/// ADR 0266 §7, end to end: while the Store installs an app, its page and its card show the step
/// (here the download, held by the test) instead of « Working… »; only that app's buttons are
/// disabled, so another app's page still offers its install. When the download ends with a package
/// that does not match the catalog's SHA-256, the progress goes away and the buttons come back. The
/// catalog is a test catalog served in place of the official <c>catalog.json</c>.
/// </summary>
public sealed class StoreInstallProgressTests(ITestOutputHelper output)
{
    // Both channels of the official catalog (ADR 0271 §6): a prerelease OS reads catalog-beta.json.
    private const string OfficialCatalogAddress = "https://raw.githubusercontent.com/Kazo-ca/Os-AppMarketPlace/packages/catalog{.json,-beta.json}";
    private const string PackagesRoot = "https://raw.githubusercontent.com/Kazo-ca/Os-AppMarketPlace/packages/packages/";
    private const string SlowAppId = "org.e2e.slow";
    private const string OtherAppId = "org.e2e.other";

    private static string AppJson(string appId, string name) => $$$"""
        {"appId":"{{{appId}}}","kind":"app","name":"{{{name}}}","category":"utilities","tags":[],
         "shortDescriptions":{"en":"{{{name}}} for the end-to-end tests"},
         "license":"MIT","repository":"https://github.com/Kazo-ca/Os-App-E2E","latestVersion":"1.0.0",
         "versions":[{"version":"1.0.0","commit":"abc","package":"packages/{{{appId}}}/1.0.0/{{{appId}}}-1.0.0.kazoapp",
           "sha256":"{{{new string('a', 64)}}}","size":4096,"capabilities":[]}]}
        """;

    private static readonly string Catalog = $$$"""
        {"schemaVersion":1,"categories":[{"id":"utilities","labels":{"en":"Utilities"}}],
         "apps":[{{{AppJson(SlowAppId, "E2E Slow")}}},{{{AppJson(OtherAppId, "E2E Other")}}}]}
        """;

    [Fact]
    public async Task An_install_shows_its_step_and_disables_only_its_own_app()
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
            TaskCompletionSource downloadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource releaseDownload = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await context.RouteAsync(OfficialCatalogAddress, async route =>
            {
                await route.FulfillAsync(new() { Status = 200, ContentType = "application/json", Body = Catalog });
                catalogServed.TrySetResult();
            });
            await context.RouteAsync($"{PackagesRoot}**", async route =>
            {
                if (route.Request.Url.EndsWith(".kazoapp", StringComparison.Ordinal))
                {
                    // The download stays pending until the test has looked at the Store.
                    downloadStarted.TrySetResult();
                    await releaseDownload.Task;
                    await route.FulfillAsync(new() { Status = 200, ContentType = "application/octet-stream", BodyBytes = new byte[4096] });
                    return;
                }

                await route.FulfillAsync(new() { Status = 404 });
            });

            IPage page = await context.NewPageAsync();
            _ = E2ESupport.AttachDiagnostics(page, output);

            await E2ESupport.NavigateWhenReadyAsync(page, address, output);
            await E2ESupport.CreateLocalProfileAndReachDesktopAsync(page, output);
            await catalogServed.Task.WaitAsync(TimeSpan.FromSeconds(60));

            await E2ESupport.OpenAppAsync(page, "Kazo OS Store", output);
            ILocator slowCard = page.Locator(".store-card", new() { HasText = "E2E Slow" });
            await slowCard.ClickAsync(new() { Timeout = 30000 });
            ILocator detail = page.Locator(".store-detail");
            await Assertions.Expect(detail.Locator("h2")).ToHaveTextAsync("E2E Slow", new() { Timeout = 20000 });

            ILocator installForMe = detail.GetByRole(AriaRole.Button, new() { Name = "Install for me" });
            await installForMe.ClickAsync();
            await downloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

            // The page names the step, with a progress bar, and the app's own buttons wait.
            ILocator status = detail.Locator(".store-operation[role='status']");
            await Assertions.Expect(status).ToContainTextAsync("Downloading", new() { Timeout = 15000 });
            await Assertions.Expect(status).ToHaveAttributeAsync("data-stage", "Downloading");
            await Assertions.Expect(status.Locator("progress")).ToHaveCountAsync(1);
            await Assertions.Expect(installForMe).ToBeDisabledAsync();
            await Assertions.Expect(detail.GetByRole(AriaRole.Button, new() { Name = "Install for everyone" })).ToBeDisabledAsync();
            Assert.Equal(0, await page.GetByText("Working…").CountAsync());
            output.WriteLine("[test] the app's page shows the download and its buttons are disabled.");

            // Home: the card shows the step too; another app's page still offers its install.
            await detail.GetByRole(AriaRole.Button, new() { Name = "Back" }).ClickAsync();
            await Assertions.Expect(slowCard.Locator(".store-operation.is-compact")).ToContainTextAsync("Downloading", new() { Timeout = 15000 });
            await page.Locator(".store-card", new() { HasText = "E2E Other" }).ClickAsync();
            await Assertions.Expect(detail.Locator("h2")).ToHaveTextAsync("E2E Other", new() { Timeout = 20000 });
            await Assertions.Expect(detail.GetByRole(AriaRole.Button, new() { Name = "Install for me" })).ToBeEnabledAsync();
            await Assertions.Expect(detail.Locator(".store-operation")).ToHaveCountAsync(0);
            output.WriteLine("[test] another app's buttons stay enabled during the install.");

            // The package does not match the catalog: the progress goes away and the buttons come back.
            releaseDownload.SetResult();
            await Assertions.Expect(page.Locator(".store-operation")).ToHaveCountAsync(0, new() { Timeout = 30000 });
            await detail.GetByRole(AriaRole.Button, new() { Name = "Back" }).ClickAsync();
            await slowCard.ClickAsync();
            await Assertions.Expect(detail.Locator("h2")).ToHaveTextAsync("E2E Slow", new() { Timeout = 20000 });
            await Assertions.Expect(detail.GetByRole(AriaRole.Button, new() { Name = "Install for me" })).ToBeEnabledAsync();
            output.WriteLine("[test] the refused package frees the app's buttons.");
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }
}
