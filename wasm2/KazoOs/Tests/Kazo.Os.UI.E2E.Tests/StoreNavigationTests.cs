using System.Diagnostics;
using System.Text;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Kazo.Os.UI.E2E.Tests;

/// <summary>
/// The Kazo OS Store's quick fixes (series <c>store-ux</c>), end to end with a test catalog served in
/// place of the official <c>catalog.json</c>: an app's page opens at the top even from a scrolled
/// list, the list comes back where it was left, the description does not repeat the app's name, a
/// list item written on two lines stays one item, the arrow keys move between the tabs, and Enter in
/// the search box opens the first result.
/// </summary>
public sealed class StoreNavigationTests(ITestOutputHelper output)
{
    private const string OfficialCatalogAddress = "https://raw.githubusercontent.com/Kazo-ca/Os-AppMarketPlace/packages/catalog.json";
    private const string PackagesRoot = "https://raw.githubusercontent.com/Kazo-ca/Os-AppMarketPlace/packages/packages/";
    private static readonly string[] Categories = ["utilities", "graphics", "productivity", "network"];
    private const int AppsPerCategory = 3;

    private static string AppId(int index) => $"org.e2e.app{index:D2}";

    private static string AppName(int index) => $"E2E App {index:D2}";

    private static string Description(int index) =>
        $"# {AppName(index)}\n\nIntro of app {index}.\n\n- Headers,\n  footers and page numbers\n- Tables\n\n"
        + string.Join("\n\n", Enumerable.Range(1, 30).Select(line => $"Paragraph {line} of a long description."));

    private static string Catalog()
    {
        StringBuilder apps = new();
        for (int index = 0; index < Categories.Length * AppsPerCategory; index++)
        {
            string id = AppId(index);
            apps.Append(index == 0 ? string.Empty : ",").Append($$$"""
                {"appId":"{{{id}}}","kind":"app","name":"{{{AppName(index)}}}","category":"{{{Categories[index / AppsPerCategory]}}}","tags":[],
                 "shortDescriptions":{"en":"Test app number {{{index}}}"},"license":"MIT","repository":"https://github.com/Kazo-ca/Os-App-E2E",
                 "latestVersion":"1.0.0","versions":[{"version":"1.0.0","commit":"abc","package":"packages/{{{id}}}/1.0.0/{{{id}}}-1.0.0.kazoapp",
                   "sha256":"{{{new string('a', 64)}}}","size":1024,"capabilities":[],
                   "descriptions":{"en":"packages/{{{id}}}/1.0.0/description.en.md"}}]}
                """);
        }

        string categories = string.Join(",", Categories.Select(category => $$$"""{"id":"{{{category}}}","labels":{"en":"{{{category}}}"}}"""));
        return $$$"""{"schemaVersion":1,"categories":[{{{categories}}}],"apps":[{{{apps}}}]}""";
    }

    [Fact]
    public async Task An_apps_page_opens_at_the_top_and_the_list_comes_back_where_it_was()
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
            string catalog = Catalog();
            TaskCompletionSource catalogServed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await context.RouteAsync(OfficialCatalogAddress, async route =>
            {
                await route.FulfillAsync(new() { Status = 200, ContentType = "application/json", Body = catalog });
                catalogServed.TrySetResult();
            });
            await context.RouteAsync($"{PackagesRoot}**", route =>
            {
                string url = route.Request.Url;
                int index = Enumerable.Range(0, Categories.Length * AppsPerCategory).FirstOrDefault(i => url.Contains(AppId(i) + "/", StringComparison.Ordinal));
                return route.FulfillAsync(new() { Status = 200, ContentType = "text/markdown", Body = Description(index) });
            });

            IPage page = await context.NewPageAsync();
            List<string> consoleErrors = E2ESupport.AttachDiagnostics(page, output);

            await E2ESupport.NavigateWhenReadyAsync(page, address, output);
            await E2ESupport.CreateLocalProfileAndReachDesktopAsync(page, output);
            await catalogServed.Task.WaitAsync(TimeSpan.FromSeconds(60));

            await E2ESupport.OpenAppAsync(page, "Kazo OS Store", output);
            ILocator body = page.Locator(".store-body");
            ILocator lastCard = page.Locator(".store-card", new() { HasText = AppName(Categories.Length * AppsPerCategory - 1) });
            await lastCard.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 20000 });

            // B1: scroll the home page down, open the last app: its page opens at the top.
            await body.EvaluateAsync("element => { element.scrollTop = element.scrollHeight; }");
            await page.WaitForTimeoutAsync(300);
            double homeScroll = await body.EvaluateAsync<double>("element => element.scrollTop");
            Assert.True(homeScroll > 50, $"the home page should scroll in the test window (scrollTop {homeScroll}).");
            await lastCard.ClickAsync();
            ILocator detail = page.Locator(".store-detail");
            await Assertions.Expect(detail.Locator("h2")).ToHaveTextAsync(AppName(Categories.Length * AppsPerCategory - 1), new() { Timeout = 15000 });
            await Assertions.Expect(detail.Locator(".store-markdown li").First).ToHaveTextAsync("Headers, footers and page numbers", new() { Timeout = 15000 });
            Assert.Equal(0, await body.EvaluateAsync<double>("element => element.scrollTop"));
            output.WriteLine($"[test] the app's page opened at the top (home was at {homeScroll}).");

            // B4: the description's first heading repeated the name: it is not drawn again.
            await Assertions.Expect(detail.Locator(".store-markdown h3")).ToHaveCountAsync(0);

            // B1: Back returns to the home page where it was left.
            await detail.Locator(".store-back").ClickAsync();
            await lastCard.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 });
            double restored = await body.EvaluateAsync<double>("element => element.scrollTop");
            Assert.InRange(restored, homeScroll - 2, homeScroll + 2);
            output.WriteLine($"[test] back on the home page at {restored}.");

            // B7: the arrow keys move between the tabs.
            ILocator homeTab = page.GetByRole(AriaRole.Tab, new() { Name = "Home" });
            await homeTab.FocusAsync();
            await page.Keyboard.PressAsync("ArrowRight");
            ILocator themesTab = page.GetByRole(AriaRole.Tab, new() { Name = "Themes" });
            await Assertions.Expect(themesTab).ToHaveAttributeAsync("aria-selected", "true");
            await Assertions.Expect(themesTab).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("ArrowLeft");
            await Assertions.Expect(homeTab).ToHaveAttributeAsync("aria-selected", "true");

            // B6: Enter in the search box opens the first result.
            ILocator search = page.GetByRole(AriaRole.Searchbox, new() { Name = "Search the Kazo OS Store" });
            await search.FillAsync(AppName(4));
            await search.PressAsync("Enter");
            await Assertions.Expect(detail.Locator("h2")).ToHaveTextAsync(AppName(4), new() { Timeout = 15000 });
            output.WriteLine("[test] arrow keys and Enter work.");

            Assert.Empty(consoleErrors);
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }
}
