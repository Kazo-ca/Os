using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Kazo.Os.UI.E2E.Tests;

/// <summary>
/// ADR 0244: windows keep their place in the page (opening order) and only their <c>z-index</c>
/// follows the stacking order, so an iframe inside a window never reloads when the window comes to
/// the front or goes behind; and a background window that receives the keyboard focus comes to the
/// front.
/// </summary>
public sealed class WindowStableOrderTests(ITestOutputHelper output)
{
    private static readonly Regex Focused = new(@"\bis-focused\b");

    /// <summary>
    /// Puts an iframe in a window and marks its document: a reloaded (moved) iframe gets a fresh
    /// document without the mark, and its load counter grows.
    /// </summary>
    private const string PlantIframe = """
        (element, mark) => {
            const frame = document.createElement('iframe');
            frame.className = 'stable-order-probe';
            frame.title = 'probe';
            frame.srcdoc = '<button>inside</button>';
            const loaded = new Promise(done => frame.addEventListener('load', () => {
                frame.contentWindow.__stableOrderMark = mark;
                frame.__probeLoads = 1;
                frame.addEventListener('load', () => frame.__probeLoads++);
                done();
            }, { once: true }));
            const button = document.createElement('button');
            button.className = 'stable-order-probe';
            button.textContent = 'probe';
            element.querySelector('.window-content').append(button, frame);
            return loaded;
        }
        """;

    private const string ReadIframe = """
        element => {
            const frame = element.querySelector('iframe.stable-order-probe');
            return frame ? `${frame.contentWindow.__stableOrderMark}:${frame.__probeLoads}` : 'missing';
        }
        """;

    private const string PageOrder = """
        () => [...document.querySelectorAll('article.window-host')].map(host => host.dataset.windowId).join(',')
        """;

    [Fact]
    public async Task An_iframe_does_not_reload_when_its_window_comes_to_the_front()
    {
        string solution = E2ESupport.FindSolutionDirectory();
        string address = $"http://127.0.0.1:{E2ESupport.ReservePort()}";
        using Process server = E2ESupport.StartHarness(solution, address, testDemo: false, output);
        try
        {
            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, new() { Headless = true });
            IPage page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 1400, Height = 900 } });
            List<string> errors = E2ESupport.AttachDiagnostics(page, output);
            await E2ESupport.NavigateWhenReadyAsync(page, address, output);
            await E2ESupport.CreateLocalProfileAndReachDesktopAsync(page, output);

            // Arrange: three windows, each with a marked iframe.
            string[] apps = ["Text Editor", "File Explorer", "Calculator"];
            List<string> ids = [];
            foreach (string app in apps)
            {
                await E2ESupport.OpenAppAsync(page, app, output);
                await Assertions.Expect(page.Locator("article.window-host")).ToHaveCountAsync(ids.Count + 1, new() { Timeout = 15000 });
                string notYetSeen = string.Concat(ids.Select(seen => $":not([data-window-id='{seen}'])"));
                ILocator opened = page.Locator($"article.window-host{notYetSeen}");
                await Assertions.Expect(opened).ToHaveClassAsync(Focused, new() { Timeout = 15000 });
                string id = (await opened.GetAttributeAsync("data-window-id"))!;
                ids.Add(id);
                await opened.EvaluateAsync(PlantIframe, app);
            }

            string openingOrder = await page.EvaluateAsync<string>(PageOrder);
            Assert.Equal(string.Join(',', ids), openingOrder);

            // Act: bring each window to the front from the taskbar, back-most first, twice around.
            foreach (string id in ids.Concat(ids))
            {
                await page.Locator($".taskbar-item[data-taskbar-window-id='{id}']").ClickAsync();
                await Assertions.Expect(Window(page, id)).ToHaveClassAsync(Focused);

                // Assert: the page order never changes, the front window has the highest z-index,
                // and no iframe reloaded.
                Assert.Equal(openingOrder, await page.EvaluateAsync<string>(PageOrder));
                int front = await ZOrderAsync(page, id);
                foreach (string other in ids.Where(other => other != id))
                {
                    Assert.True(await ZOrderAsync(page, other) < front, $"{other} is above the front window {id}");
                }

                for (int index = 0; index < ids.Count; index++)
                {
                    Assert.Equal($"{apps[index]}:1", await Window(page, ids[index]).EvaluateAsync<string>(ReadIframe));
                }
            }

            // ADR 0244 §4: the keyboard focus entering a background window brings it to the front,
            // also when it goes into an iframe (the page then only sees its own blur).
            foreach ((string id, string how) in new[] { (ids[0], "iframe"), (ids[1], "page") })
            {
                Assert.DoesNotMatch(Focused, (await Window(page, id).GetAttributeAsync("class"))!);
                await Window(page, id).EvaluateAsync(
                    "(element, how) => how === 'iframe' ? element.querySelector('iframe.stable-order-probe').contentDocument.querySelector('button').focus() : element.querySelector('button.stable-order-probe').focus()",
                    how);
                await Assertions.Expect(Window(page, id)).ToHaveClassAsync(Focused);
                Assert.Equal(openingOrder, await page.EvaluateAsync<string>(PageOrder));
            }

            for (int index = 0; index < ids.Count; index++)
            {
                Assert.Equal($"{apps[index]}:1", await Window(page, ids[index]).EvaluateAsync<string>(ReadIframe));
            }

            Assert.Empty(errors);
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }

    private static ILocator Window(IPage page, string id) => page.Locator($"article.window-host[data-window-id='{id}']");

    private static async Task<int> ZOrderAsync(IPage page, string id) =>
        int.Parse((await Window(page, id).GetAttributeAsync("data-window-z"))!, System.Globalization.CultureInfo.InvariantCulture);
}
