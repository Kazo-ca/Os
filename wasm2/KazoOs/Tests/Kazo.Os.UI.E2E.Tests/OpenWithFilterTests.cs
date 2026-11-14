using System.Diagnostics;
using System.Text;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Kazo.Os.UI.E2E.Tests;

/// <summary>
/// The File Explorer's "Open with" box, on a desktop and on a phone: each installed app's row holds
/// its name and its id inside its border, the search box and the category selector narrow the list,
/// and the box has no serious or critical axe finding. Screenshots go to
/// <c>Tests/TestResults/open-with/</c>.
/// </summary>
public sealed class OpenWithFilterTests(ITestOutputHelper output)
{
    [Fact]
    public async Task The_open_with_box_keeps_ids_inside_their_rows_and_filters_by_search_and_category()
    {
        string solutionDirectory = E2ESupport.FindSolutionDirectory();
        string shots = Path.Combine(solutionDirectory, "Tests", "TestResults", "open-with");
        Directory.CreateDirectory(shots);
        int port = E2ESupport.ReservePort();
        string address = $"http://127.0.0.1:{port}";
        using Process server = E2ESupport.StartHarness(solutionDirectory, address, testDemo: false, output);

        // An extension no app declares: "Choose another app…" lists every installed window app.
        string fileName = $"e2e-open-{Guid.NewGuid():N}"[..17] + ".e2edata";
        string filePath = Path.Combine(Path.GetTempPath(), fileName);
        await File.WriteAllBytesAsync(filePath, Encoding.UTF8.GetBytes("plain bytes"));

        try
        {
            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, new BrowserTypeLaunchOptions { Headless = true });
            await using IBrowserContext context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1280, Height = 800 } });
            IPage page = await context.NewPageAsync();
            List<string> consoleErrors = E2ESupport.AttachDiagnostics(page, output);

            await E2ESupport.NavigateWhenReadyAsync(page, address, output);
            await E2ESupport.CreateLocalProfileAndReachDesktopAsync(page, output);
            await E2ESupport.OpenAppAsync(page, "File Explorer", output);

            ILocator addressInput = page.GetByLabel("Current path address");
            await Assertions.Expect(addressInput).ToHaveValueAsync(E2ESupport.HomePath, new() { Timeout = 15000 });
            await page.Locator("tr.entry-row", new() { HasText = "Documents" }).WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15000 });
            await page.Locator("#fileInput").SetInputFilesAsync(filePath);
            ILocator fileRow = page.Locator("tr.entry-row", new() { HasText = fileName });
            await fileRow.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15000 });

            // --- Desktop ---
            ILocator box = await OpenChooserAsync(page, fileRow);
            await page.ScreenshotAsync(new() { Path = Path.Combine(shots, "desktop-open-with.png") });
            await AssertRowsHoldTheirIdsAsync(box, "desktop");
            await AssertSearchAndCategoryFilterAsync(box);
            await page.ScreenshotAsync(new() { Path = Path.Combine(shots, "desktop-open-with-filtered.png") });
            await AssertNoBlockingAxeFindingAsync(box, "desktop");
            await box.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
            await Assertions.Expect(box).ToHaveCountAsync(0, new() { Timeout = 15000 });

            // --- Phone ---
            await page.GetByRole(AriaRole.Button, new() { Name = "System clock, notifications, and calendar" }).ClickAsync();
            await page.GetByRole(AriaRole.Dialog, new() { Name = "Notifications and calendar" })
                .GetByRole(AriaRole.Radio, new() { Name = "Mobile", Exact = true }).ClickAsync();
            ILocator home = page.Locator(".mobile-home-screen");
            await Assertions.Expect(home).ToBeVisibleAsync(new() { Timeout = 15000 });
            await page.SetViewportSizeAsync(390, 844);

            await home.GetByRole(AriaRole.Button, new() { Name = "All applications", Exact = true }).ClickAsync();
            ILocator drawer = page.GetByRole(AriaRole.Dialog, new() { Name = "All applications", Exact = true });
            await drawer.GetByRole(AriaRole.Searchbox, new() { Name = "Search applications" }).FillAsync("File Explorer");
            await drawer.GetByRole(AriaRole.Button, new() { Name = "File Explorer", Exact = true }).ClickAsync();
            ILocator mobileItem = page.Locator(".mobile-files").GetByText(fileName).First;
            await mobileItem.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 20000 });

            ILocator mobileBox = await OpenChooserAsync(page, mobileItem);
            await page.ScreenshotAsync(new() { Path = Path.Combine(shots, "mobile-open-with.png") });
            await AssertRowsHoldTheirIdsAsync(mobileBox, "phone");
            LocatorBoundingBoxResult filters = await BoxAsync(mobileBox.Locator(".app-filters"));
            LocatorBoundingBoxResult boxBounds = await BoxAsync(mobileBox);
            Assert.True(filters.X + filters.Width <= boxBounds.X + boxBounds.Width + 0.5, "The filters overflow the box on a phone.");
            await mobileBox.GetByRole(AriaRole.Searchbox, new() { Name = "Search the installed apps" }).FillAsync("calc");
            await Assertions.Expect(mobileBox.Locator("button.app-option")).ToHaveCountAsync(1);
            await page.ScreenshotAsync(new() { Path = Path.Combine(shots, "mobile-open-with-filtered.png") });
            await AssertNoBlockingAxeFindingAsync(mobileBox, "phone");

            Assert.Empty(consoleErrors);
        }
        finally
        {
            E2ESupport.StopProcess(server);
            File.Delete(filePath);
        }
    }

    /// <summary>Right-click ▸ Open with ▸ Choose another app…, then waits for the box to list its apps.</summary>
    private static async Task<ILocator> OpenChooserAsync(IPage page, ILocator item)
    {
        await item.ClickAsync(new() { Button = MouseButton.Right });
        ILocator items = page.Locator(".kazo-context-menu-panel [role=menuitem]");
        await items.Filter(new() { HasText = "Open with" }).First.ClickAsync();
        await items.Filter(new() { HasText = "Choose another app…" }).ClickAsync();
        ILocator box = page.Locator(".explorer-file-dialog.open-with");
        await Assertions.Expect(box.Locator("button.app-option").First).ToBeVisibleAsync(new() { Timeout = 15000 });
        await page.WaitForTimeoutAsync(400); // let the modal window finish its opening animation
        return box;
    }

    /// <summary>Every visible row holds its name and its id inside its border (the bug of the screenshot).</summary>
    private async Task AssertRowsHoldTheirIdsAsync(ILocator box, string surface)
    {
        ILocator rows = box.Locator("button.app-option");
        int count = await rows.CountAsync();
        Assert.True(count > 1, $"{surface}: the box lists {count} app(s).");
        for (int index = 0; index < Math.Min(count, 4); index++)
        {
            ILocator row = rows.Nth(index);
            LocatorBoundingBoxResult rowBox = await BoxAsync(row);
            LocatorBoundingBoxResult idBox = await BoxAsync(row.Locator(".app-id"));
            LocatorBoundingBoxResult nameBox = await BoxAsync(row.Locator(".app-name"));
            Assert.True(idBox.Y + idBox.Height <= rowBox.Y + rowBox.Height + 0.5,
                $"{surface}: the id of row {index} ends at {idBox.Y + idBox.Height}, below its row ({rowBox.Y + rowBox.Height}).");
            Assert.True(nameBox.Y >= rowBox.Y - 0.5, $"{surface}: the name of row {index} starts above its row.");
            Assert.True(idBox.X + idBox.Width <= rowBox.X + rowBox.Width + 0.5, $"{surface}: the id of row {index} overflows on the right.");
        }

        output.WriteLine($"[test] {surface}: {count} rows, ids inside their rows.");
    }

    /// <summary>The search narrows by name or id, the category narrows by category, and Open follows the visible selection.</summary>
    private async Task AssertSearchAndCategoryFilterAsync(ILocator box)
    {
        ILocator rows = box.Locator("button.app-option");
        int all = await rows.CountAsync();
        ILocator search = box.GetByRole(AriaRole.Searchbox, new() { Name = "Search the installed apps" });

        await search.FillAsync("org.kazoos.calculator");
        await Assertions.Expect(rows).ToHaveCountAsync(1);
        await Assertions.Expect(rows.First.Locator(".app-name")).ToHaveTextAsync("Calculator");
        await Assertions.Expect(rows.First).ToHaveAttributeAsync("aria-checked", "true");

        await search.FillAsync("no app has this name");
        await Assertions.Expect(rows).ToHaveCountAsync(0);
        await Assertions.Expect(box.GetByText("No installed app matches this search.")).ToBeVisibleAsync();
        await Assertions.Expect(box.GetByRole(AriaRole.Button, new() { Name = "Open", Exact = true })).ToBeDisabledAsync();

        await search.FillAsync(string.Empty);
        await Assertions.Expect(rows).ToHaveCountAsync(all);

        ILocator category = box.GetByRole(AriaRole.Combobox, new() { Name = "Category" });
        await category.SelectOptionAsync(new SelectOptionValue { Label = "Utilities" });
        await Assertions.Expect(rows.Filter(new() { HasText = "org.kazoos.browser" })).ToHaveCountAsync(0);
        int utilities = await rows.CountAsync();
        Assert.InRange(utilities, 1, all - 1);
        output.WriteLine($"[test] search and category: {all} apps, {utilities} in Utilities.");
        await category.SelectOptionAsync(new SelectOptionValue { Label = "All categories" });
        await Assertions.Expect(rows).ToHaveCountAsync(all);
    }

    private async Task AssertNoBlockingAxeFindingAsync(ILocator box, string surface)
    {
        var result = await box.RunAxe();
        string[] blocking = [.. (result.Violations ?? [])
            .Where(item => item.Impact is "serious" or "critical")
            .Select(item => $"{item.Id}: {string.Join(" | ", (item.Nodes ?? []).Select(node => node.Html))}")];
        Assert.True(blocking.Length == 0, $"{surface}: {string.Join(Environment.NewLine, blocking)}");
        output.WriteLine($"[test] {surface}: no serious or critical axe finding in the box.");
    }

    private static async Task<LocatorBoundingBoxResult> BoxAsync(ILocator locator) =>
        await locator.BoundingBoxAsync() ?? throw new Xunit.Sdk.XunitException("The element has no box.");
}
