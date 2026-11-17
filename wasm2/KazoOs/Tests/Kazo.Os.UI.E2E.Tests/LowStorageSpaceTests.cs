using System.Diagnostics;
using System.Text.Json;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Kazo.Os.UI.E2E.Tests;

/// <summary>
/// Low storage space against Chromium's real quota (ADR 0099 §2, ADR 0279): the origin gets a small
/// quota with <c>Storage.overrideQuotaForOrigin</c>, the test fills it like another program filling the
/// disk, and the OS sees the full disk when the browser refuses its next write (Chromium rounds
/// <c>navigator.storage.estimate()</c>, so the estimate alone never shows it).
/// </summary>
/// <remarks>
/// Chromium enforces the quota an origin had when its storage was first opened: an override sent
/// after the OS opened IndexedDB is reported by DevTools but never enforced, even after a reload.
/// The override is therefore sent before the first navigation.
/// </remarks>
public sealed class LowStorageSpaceTests(ITestOutputHelper output)
{
    /// <summary>
    /// Quota of the origin: well above what the OS uses to boot and create a profile (under 1 MiB),
    /// small enough for the test to fill it in a few seconds.
    /// </summary>
    private const double QuotaBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Writes random (incompressible) 64 KiB blocks to a database of the test until the browser
    /// refuses one with <c>QuotaExceededError</c>; returns how many were written.
    /// </summary>
    private const string FillScript = """
        async () => {
            const db = await new Promise((resolve, reject) => {
                const open = indexedDB.open('e2e-fill', 1);
                open.onupgradeneeded = () => open.result.createObjectStore('blocks');
                open.onsuccess = () => resolve(open.result);
                open.onerror = () => reject(open.error);
            });
            let written = 0;
            try {
                for (; written < 1000; written++) {
                    await new Promise((resolve, reject) => {
                        const transaction = db.transaction('blocks', 'readwrite');
                        transaction.objectStore('blocks').put(crypto.getRandomValues(new Uint8Array(65536)), written);
                        transaction.oncomplete = resolve;
                        transaction.onabort = () => reject(transaction.error);
                    });
                }
            } catch {
                // QuotaExceededError: the disk is full.
            }
            db.close();
            return written;
        }
        """;

    /// <summary>
    /// The session user gets one "Low storage space" notification whose action opens Diagnostic
    /// once the browser refuses a write, and the administrator still signs in after a reload with
    /// the storage full.
    /// </summary>
    [Fact]
    public async Task A_full_quota_warns_once_and_the_administrator_still_signs_in()
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
            E2ESupport.AttachDiagnostics(page, output);

            // Before the first navigation: Chromium keeps the quota the origin had when its
            // storage was first opened (see the remarks).
            ICDPSession cdp = await context.NewCDPSessionAsync(page);
            await cdp.SendAsync("Storage.overrideQuotaForOrigin", new Dictionary<string, object>
            {
                ["origin"] = address,
                ["quotaSize"] = QuotaBytes
            });

            await E2ESupport.NavigateWhenReadyAsync(page, address, output);
            await E2ESupport.CreateLocalProfileAndReachDesktopAsync(page, output);

            // Fill what is left with random bytes in a database of the test's own, like another
            // program filling the disk.
            int filled = await page.EvaluateAsync<int>(FillScript);
            JsonElement full = (await cdp.SendAsync("Storage.getUsageAndQuota", new Dictionary<string, object> { ["origin"] = address }))!.Value;
            output.WriteLine($"[test] {filled} random blocks written until the browser refused; usage {full.GetProperty("usage").GetDouble()} B of {QuotaBytes} B.");
            Assert.True(filled < 1000, "The browser never refused the fill: the quota override is not enforced.");

            // Chromium only refuses writes that do not fit what is left: the user writes a file
            // of a few hundred KiB from the terminal and the browser refuses it.
            await E2ESupport.OpenAppAsync(page, "Terminal", output);
            ILocator screen = page.Locator("[data-terminal-panel] .xterm-screen").First;
            ILocator rows = page.Locator("[data-terminal-panel] .xterm-rows").First;
            await Assertions.Expect(rows).ToContainTextAsync("$", new() { Timeout = 30000 });
            await screen.ClickAsync();
            await page.Keyboard.TypeAsync("seq 1 100000 > ~/fill.txt");
            await page.Keyboard.PressAsync("Enter");
            await Assertions.Expect(rows).ToContainTextAsync("No space left on device", new() { Timeout = 60000 });
            output.WriteLine("[test] the browser refused the write: No space left on device.");

            ILocator clockButton = page.GetByRole(AriaRole.Button, new() { Name = "System clock, notifications, and calendar" });
            ILocator panel = page.GetByRole(AriaRole.Dialog, new() { Name = "Notifications and calendar" });
            ILocator warning = panel.GetByRole(AriaRole.Heading, new() { Name = "Low storage space" });
            Stopwatch waited = Stopwatch.StartNew();
            while (true)
            {
                await clockButton.ClickAsync();
                await Assertions.Expect(panel).ToBeVisibleAsync();
                if (await warning.CountAsync() > 0)
                {
                    break;
                }

                Assert.True(waited.Elapsed < TimeSpan.FromSeconds(60), "No low-space notification within 60 s.");
                await page.Keyboard.PressAsync("Escape");
                await page.WaitForTimeoutAsync(3000);
            }

            output.WriteLine($"[test] low-space notification after {waited.Elapsed.TotalSeconds:0.#} s.");
            await Assertions.Expect(warning).ToHaveCountAsync(1);
            await Assertions.Expect(panel.GetByRole(AriaRole.Button, new() { Name = "Open Diagnostic" })).ToBeVisibleAsync();

            // With the storage full, the session ends cleanly and the administrator still signs in
            // after a reload (the writes that sign-in tries are refused, not fatal).
            await page.Keyboard.PressAsync("Escape");
            await page.ReloadAsync();
            ILocator passwordField = page.GetByLabel("Password", new() { Exact = true });
            await passwordField.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30000 });
            await passwordField.FillAsync(E2ESupport.Password);
            await page.GetByRole(AriaRole.Button, new() { Name = "Start session" }).ClickAsync();
            await E2ESupport.PassWelcomeAsync(page);
            await clockButton.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30000 });
            output.WriteLine("[test] signed in again with the storage full.");
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }
}
