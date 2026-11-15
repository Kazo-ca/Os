using System.Diagnostics;
using System.Text;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Kazo.Os.UI.E2E.Tests;

/// <summary>
/// store-ux work item 3 (ADR 0266 §4 and §6), end to end: an app's page writes its capabilities in
/// plain language (the sensitive one first, the identifiers folded), lists the file types the app
/// opens, links to its source code, keeps a bar with the app and its main button at the top once
/// the header has scrolled away, keeps the facts panel in view, and copies the « Try it » link in
/// a message that closes by itself. The catalog is a test catalog served in place of the official one.
/// </summary>
public sealed class StoreAppPageTests(ITestOutputHelper output)
{
    // Both channels of the official catalog (ADR 0271 §6): a prerelease OS reads catalog-beta.json.
    private const string OfficialCatalogAddress = "https://raw.githubusercontent.com/Kazo-ca/Os-AppMarketPlace/packages/catalog{.json,-beta.json}";
    private const string PackagesRoot = "https://raw.githubusercontent.com/Kazo-ca/Os-AppMarketPlace/packages/packages/";
    private const string AppId = "org.e2e.notebook";

    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private static readonly string Catalog = $$$"""
        {"schemaVersion":1,"categories":[{"id":"productivity","labels":{"en":"Productivity","fr":"Productivité"}}],
         "apps":[{"appId":"{{{AppId}}}","kind":"app","name":"E2E Notebook","category":"productivity","tags":[],
                  "shortDescriptions":{"en":"Writes long notes","fr":"Écrit de longues notes"},
                  "license":"MIT","repository":"https://github.com/Kazo-ca/Os-App-E2E","latestVersion":"1.0.0",
                  "versions":[{"version":"1.0.0","commit":"abc","package":"packages/{{{AppId}}}/1.0.0/{{{AppId}}}-1.0.0.kazoapp",
                    "sha256":"{{{new string('a', 64)}}}","size":1024,
                    "capabilities":["filesystem.user-home.read","network.real.access","apps.launch"],
                    "fileHandlers":[{"extensions":[".note",".txt"],"actions":["open"]}],
                    "icon":"packages/{{{AppId}}}/1.0.0/icon.png",
                    "descriptions":{"en":"packages/{{{AppId}}}/1.0.0/description.en.md"}}]}]}
        """;

    [Fact]
    public async Task The_apps_page_explains_its_capabilities_keeps_its_bar_and_copies_the_try_link()
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
                : route.FulfillAsync(new() { Status = 200, ContentType = "text/markdown", Body = LongDescription() }));

            IPage page = await context.NewPageAsync();
            List<string> consoleErrors = E2ESupport.AttachDiagnostics(page, output);

            await E2ESupport.NavigateWhenReadyAsync(page, address, output);
            await E2ESupport.CreateLocalProfileAndReachDesktopAsync(page, output);
            await catalogServed.Task.WaitAsync(TimeSpan.FromSeconds(60));

            await E2ESupport.OpenAppAsync(page, "Kazo OS Store", output);
            await page.Locator(".store-card", new() { HasText = "E2E Notebook" }).ClickAsync(new() { Timeout = 20000 });
            ILocator detail = page.Locator(".store-detail");
            await Assertions.Expect(detail.Locator("h2")).ToHaveTextAsync("E2E Notebook", new() { Timeout = 20000 });
            await Assertions.Expect(detail.Locator(".store-markdown p").First).ToBeVisibleAsync(new() { Timeout = 15000 });

            // Capabilities in plain language, the sensitive one first; the identifiers are folded.
            ILocator facts = detail.Locator(".store-facts");
            ILocator titles = facts.Locator(".store-capability-title");
            await Assertions.Expect(titles).ToHaveTextAsync(["Access the real network", "Read your files, including your network drives", "Launch other applications"]);
            await Assertions.Expect(facts.Locator(".store-capability-group").First).ToHaveTextAsync("Sensitive access");
            await Assertions.Expect(facts.Locator(".store-capability-text code")).ToHaveCountAsync(0);
            ILocator toggle = facts.GetByRole(AriaRole.Button, new() { Name = "See the 3 technical identifiers" });
            await toggle.ClickAsync();
            await Assertions.Expect(facts.Locator(".store-capability-text code").First).ToHaveTextAsync("network.real.access");
            await Assertions.Expect(facts.GetByRole(AriaRole.Button, new() { Name = "Hide the technical identifiers" })).ToHaveAttributeAsync("aria-expanded", "true");
            output.WriteLine("[test] Capabilities are written in plain language, identifiers on demand.");

            // File types and the source link.
            await Assertions.Expect(facts.Locator(".store-file-types li")).ToHaveTextAsync([".note", ".txt"]);
            ILocator source = facts.GetByRole(AriaRole.Link, new() { Name = "Source code on GitHub" });
            await Assertions.Expect(source).ToHaveAttributeAsync("href", "https://github.com/Kazo-ca/Os-App-E2E");

            // Scrolling: the bar shows the app and its button; the facts panel stays in view.
            // Playwright scrolled the facts into view to click them: start from the top again.
            ILocator bar = detail.Locator(".store-page-bar");
            ILocator body = page.Locator(".store-body");
            await body.EvaluateAsync("body => body.scrollTo(0, 0)");
            await Assertions.Expect(bar.Locator(".store-page-bar-name")).ToHaveCountAsync(0, new() { Timeout = 10000 });
            await body.EvaluateAsync("body => body.scrollTo(0, body.scrollHeight)");
            await Assertions.Expect(bar).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("is-compact"), new() { Timeout = 10000 });
            await Assertions.Expect(bar.Locator(".store-page-bar-name")).ToHaveTextAsync("E2E Notebook");
            await Assertions.Expect(bar.GetByRole(AriaRole.Button, new() { Name = "Install for me" })).ToBeVisibleAsync();
            LocatorBoundingBoxResult bodyBox = await body.BoundingBoxAsync() ?? throw new Xunit.Sdk.XunitException("The page has no box.");
            LocatorBoundingBoxResult barBox = await bar.BoundingBoxAsync() ?? throw new Xunit.Sdk.XunitException("The bar has no box.");
            LocatorBoundingBoxResult factsBox = await facts.BoundingBoxAsync() ?? throw new Xunit.Sdk.XunitException("The facts have no box.");
            Assert.Equal(bodyBox.Y, barBox.Y, 1.0);
            Assert.InRange(factsBox.Y, bodyBox.Y, bodyBox.Y + bodyBox.Height - 40);
            output.WriteLine("[test] The bar stays at the top with the app's button; the facts panel stays in view.");

            await body.EvaluateAsync("body => body.scrollTo(0, 0)");
            await Assertions.Expect(bar.Locator(".store-page-bar-name")).ToHaveCountAsync(0, new() { Timeout = 10000 });

            // « Try it »: the link of this OS's own address, in a message that closes by itself.
            await detail.GetByRole(AriaRole.Button, new() { Name = "Copy the « Try it » link" }).ClickAsync();
            await page.Locator(".store-toast").WaitForAsync(new() { Timeout = 10000 });
            output.WriteLine("[test] message: " + await page.Locator(".store-toast").InnerTextAsync());
            ILocator toast = page.Locator(".store-toast.is-info");
            await Assertions.Expect(toast).ToContainTextAsync($"{address}/?autolaunch={AppId}", new() { Timeout = 10000 });
            await Assertions.Expect(toast).ToHaveCountAsync(0, new() { Timeout = 15000 });
            output.WriteLine("[test] The « Try it » link was copied and the message closed by itself.");

            Assert.Empty(consoleErrors);
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }

    /// <summary>A description long enough to scroll the page well past its header.</summary>
    private static string LongDescription()
    {
        StringBuilder text = new();
        for (int i = 1; i <= 40; i++)
        {
            text.Append("Paragraph ").Append(i).AppendLine(" of a long description that makes the page scroll.").AppendLine();
        }

        return text.ToString();
    }
}
