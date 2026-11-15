using System.Diagnostics;
using System.Text;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Kazo.Os.UI.E2E.Tests;

/// <summary>
/// ADR 0257 §4, §5, end to end: a <c>.docx</c> no installed app opens, double-clicked in the File
/// Explorer, shows the "Open with" box with the app of the official catalog that declares
/// <c>.docx</c> (its name, short description and icon), and « View in the Store » opens the Store on
/// that app's page with the file to open after the install. The catalog is a test catalog served in
/// place of the official <c>catalog.json</c>.
/// </summary>
public sealed class OpenWithStoreTests(ITestOutputHelper output)
{
    // Both channels of the official catalog (ADR 0271 §6): a prerelease OS reads catalog-beta.json.
    private const string OfficialCatalogAddress = "https://raw.githubusercontent.com/Kazo-ca/Os-AppMarketPlace/packages/catalog{.json,-beta.json}";
    private const string PackagesRoot = "https://raw.githubusercontent.com/Kazo-ca/Os-AppMarketPlace/packages/packages/";
    private const string AppId = "org.e2e.documents";

    // A 1×1 transparent PNG, the app's icon.
    private static readonly byte[] IconPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private static readonly string Catalog = $$$"""
        {"schemaVersion":1,"categories":[{"id":"productivity","labels":{"en":"Productivity","fr":"Productivité"}}],
         "apps":[{"appId":"{{{AppId}}}","kind":"app","name":"E2E Documents","category":"productivity","tags":[],
                  "shortDescriptions":{"en":"Opens word processing documents","fr":"Ouvre les documents de traitement de texte"},
                  "license":"MIT","repository":"https://github.com/Kazo-ca/Os-App-E2E","latestVersion":"1.0.0",
                  "versions":[{"version":"1.0.0","commit":"abc","package":"packages/{{{AppId}}}/1.0.0/{{{AppId}}}-1.0.0.kazoapp",
                    "sha256":"{{{new string('a', 64)}}}","size":1024,"capabilities":[],
                    "icon":"packages/{{{AppId}}}/1.0.0/icon.png",
                    "descriptions":{"en":"packages/{{{AppId}}}/1.0.0/description.en.md"},
                    "fileHandlers":[{"mediaType":"application/vnd.openxmlformats-officedocument.wordprocessingml.document","extensions":[".docx"],"actions":["open","edit"]}]}]}]}
        """;

    [Fact]
    public async Task A_docx_no_app_opens_proposes_the_store_app_and_opens_its_page()
    {
        string solutionDirectory = E2ESupport.FindSolutionDirectory();
        int port = E2ESupport.ReservePort();
        string address = $"http://127.0.0.1:{port}";
        using Process server = E2ESupport.StartHarness(solutionDirectory, address, testDemo: false, output);

        string fileName = $"e2e-report-{Guid.NewGuid():N}"[..19] + ".docx";
        string filePath = Path.Combine(Path.GetTempPath(), fileName);
        await File.WriteAllBytesAsync(filePath, Encoding.UTF8.GetBytes("not really a document"));

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
                ? route.FulfillAsync(new() { Status = 200, ContentType = "image/png", BodyBytes = IconPng })
                : route.FulfillAsync(new() { Status = 200, ContentType = "text/markdown", Body = "# E2E Documents" }));

            IPage page = await context.NewPageAsync();
            List<string> consoleErrors = E2ESupport.AttachDiagnostics(page, output);

            await E2ESupport.NavigateWhenReadyAsync(page, address, output);
            await E2ESupport.CreateLocalProfileAndReachDesktopAsync(page, output);

            // The update check downloads the catalogs at sign-in (ADR 0257 §2): only then can the
            // open propose anything, since it never waits for the network.
            await catalogServed.Task.WaitAsync(TimeSpan.FromSeconds(60));
            output.WriteLine("[test] the test catalog was downloaded at sign-in.");

            await E2ESupport.OpenAppAsync(page, "File Explorer", output);
            ILocator addressInput = page.GetByLabel("Current path address");
            await Assertions.Expect(addressInput).ToHaveValueAsync(E2ESupport.HomePath, new() { Timeout = 15000 });
            await page.Locator("tr.entry-row", new() { HasText = "Documents" }).WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15000 });
            await page.Locator("#fileInput").SetInputFilesAsync(filePath);

            ILocator fileRow = page.Locator("tr.entry-row", new() { HasText = fileName });
            await fileRow.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15000 });
            await fileRow.DblClickAsync();

            // The "Open with" box: no installed app opens it, and the Store app is proposed.
            ILocator box = page.Locator(".explorer-file-dialog.open-with");
            await Assertions.Expect(box.GetByText("No installed app opens this type of file.")).ToBeVisibleAsync(new() { Timeout = 15000 });
            ILocator storeApp = box.Locator($".store-app[data-app-id='{AppId}']");
            await Assertions.Expect(storeApp.Locator(".store-app-name")).ToHaveTextAsync("E2E Documents");
            await Assertions.Expect(storeApp.Locator(".store-app-description")).ToHaveTextAsync("Opens word processing documents");
            await Assertions.Expect(storeApp.Locator("img")).ToHaveAttributeAsync("src", $"{PackagesRoot}{AppId}/1.0.0/icon.png");
            output.WriteLine("[test] the Open with box proposes the Store app.");

            await storeApp.GetByRole(AriaRole.Button, new() { Name = "View E2E Documents in the Kazo OS Store" }).ClickAsync();

            // The Store opens on the app's page and says the file opens once the app is installed.
            await Assertions.Expect(box).ToHaveCountAsync(0, new() { Timeout = 15000 });
            ILocator detail = page.Locator(".store-detail");
            await Assertions.Expect(detail.Locator("h2")).ToHaveTextAsync("E2E Documents", new() { Timeout = 20000 });
            await Assertions.Expect(detail.Locator(".store-open-after")).ToContainTextAsync($"{E2ESupport.HomePath}/{fileName} will open once the app is installed.");
            output.WriteLine("[test] the Store shows the app's page with the file to open after the install.");

            Assert.Empty(consoleErrors);
        }
        finally
        {
            E2ESupport.StopProcess(server);
            File.Delete(filePath);
        }
    }
}
