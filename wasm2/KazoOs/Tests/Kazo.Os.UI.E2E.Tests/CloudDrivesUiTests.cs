using System.Diagnostics;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Kazo.Os.UI.E2E.Tests;

/// <summary>
/// cloud-drives work item 5 (ADR 0267 §11, ADR 0270), end to end on the real shell: File Explorer's
/// "Connect a network drive" dialog on the desktop and on the mobile shell, and the Settings page of
/// the cloud accounts. The test gives the OS fake Dropbox and OneDrive app keys and answers a fake S3
/// address itself: the dialog offers the folder of the computer, the providers with the choice of the
/// scope, and the S3 form, whose refusal shows in the dialog; the dialog takes the focus, so Escape closes it; each surface is scanned by axe (serious and critical findings fail) and leaves a
/// screenshot in <c>Tests/TestResults/cloud-drives/</c>. No real provider is ever reached.
/// </summary>
public sealed class CloudDrivesUiTests(ITestOutputHelper output)
{
    /// <summary>An S3 address the test answers itself: nothing leaves the machine.</summary>
    private const string FakeS3Endpoint = "https://s3.e2e.test";

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
            await RouteFakeServicesAsync(context);
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

    /// <summary>
    /// ADR 0282 §5: a drive whose provider is missing stays in the Explorer, greyed, with the reason:
    /// a type the administrator switched off (<c>KazoOs:CloudDrives:Disabled</c>), and a type whose
    /// Feature is not installed, which offers « Install the provider ». The drive list is written from
    /// the terminal, then the session signs in again, as after a reboot.
    /// </summary>
    [Fact]
    public async Task A_drive_without_its_provider_is_greyed_with_the_reason_on_desktop_and_phone()
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
            await context.RouteAsync("**/appsettings.json", async route =>
            {
                IAPIResponse original = await route.FetchAsync();
                string json = (await original.TextAsync())
                    .Replace("\"CloudDrives\": {", "\"CloudDrives\": { \"Disabled\": \"s3\",", StringComparison.Ordinal);
                await route.FulfillAsync(new() { Response = original, Body = json });
            });
            IPage page = await context.NewPageAsync();
            E2ESupport.AttachDiagnostics(page, output);
            try
            {
                await RunMissingProviderStepsAsync(page, address, shots, findings);
            }
            catch
            {
                await page.ScreenshotAsync(new() { Path = Path.Combine(shots, "failure-missing-provider.png") });
                throw;
            }

            Assert.True(findings.Count == 0, string.Join('\n', findings));
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }

    private async Task RunMissingProviderStepsAsync(IPage page, string address, string shots, List<string> findings)
    {
        await E2ESupport.NavigateWhenReadyAsync(page, address, output);
        await E2ESupport.CreateLocalProfileAndReachDesktopAsync(page, output);

        // The user's drive list, as a session that mounted both drives earlier would have left it.
        await E2ESupport.OpenAppAsync(page, "Terminal", output);
        ILocator rows = page.Locator("[data-terminal-panel] .xterm-rows").First;
        await WaitForTerminalAsync(rows, static text => text.Contains('$', StringComparison.Ordinal), "the shell prompt");
        await page.Locator("[data-terminal-panel] .xterm-screen").First.ClickAsync();
        const string table =
            "{\"schemaVersion\":1,\"drives\":["
            + "{\"mountPoint\":\"/mnt/Bucket\",\"name\":\"Bucket\",\"createdMountPoint\":true,\"type\":\"s3\",\"account\":\"s3-e2e\"},"
            + "{\"mountPoint\":\"/mnt/Pictures\",\"name\":\"Pictures\",\"createdMountPoint\":true,\"type\":\"e2ecloud\",\"account\":\"e2e-1\",\"provider\":\"org.example.e2ecloud\"}]}";
        await RunInTerminalAsync(page, rows, "mkdir -p ~/.local/state/kazoos");
        await RunInTerminalAsync(page, rows, "echo '" + table + "' > ~/.local/state/kazoos/network-drives.json");
        await RunInTerminalAsync(page, rows, "clear");
        await page.Keyboard.TypeAsync("ls ~/.local/state/kazoos");
        await page.Keyboard.PressAsync("Enter");
        await WaitForTerminalAsync(rows, static text => text.Contains("network-drives.json", StringComparison.Ordinal), "the drive list written");

        // Sign in again: the drives are read at login.
        await page.ReloadAsync();
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(E2ESupport.Password, new() { Timeout = 60000 });
        await page.GetByRole(AriaRole.Button, new() { Name = "Start session", Exact = true }).ClickAsync();
        await E2ESupport.PassWelcomeAsync(page);

        // The login kept both lines without mounting them: nothing new under /mnt, the list unchanged.
        await E2ESupport.OpenAppAsync(page, "Terminal", output);
        ILocator after = page.Locator("[data-terminal-panel] .xterm-rows").First;
        await WaitForTerminalAsync(after, static text => text.Contains('$', StringComparison.Ordinal), "the shell prompt");
        await page.Locator("[data-terminal-panel] .xterm-screen").First.ClickAsync();
        await RunInTerminalAsync(page, after, "mount");
        await RunInTerminalAsync(page, after, "cat ~/.local/state/kazoos/network-drives.json");
        string listed = (await after.InnerTextAsync()).Replace("\n", string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("/mnt/Bucket type", listed, StringComparison.Ordinal);
        Assert.Contains("org.example.e2ecloud", listed, StringComparison.Ordinal);

        await E2ESupport.OpenAppAsync(page, "File Explorer", output);
        ILocator disabled = page.Locator(".sidebar-entry.missing", new() { HasText = "Bucket" });
        ILocator removed = page.Locator(".sidebar-entry.missing", new() { HasText = "Pictures" });
        await Assertions.Expect(disabled).ToBeVisibleAsync(new() { Timeout = 30000 });
        await Assertions.Expect(removed).ToBeVisibleAsync();
        await Assertions.Expect(disabled).ToHaveAttributeAsync("aria-disabled", "true");
        await Assertions.Expect(removed).ToHaveAttributeAsync("aria-disabled", "false");
        await Assertions.Expect(disabled).ToContainTextAsync("Disabled by the administrator");
        await Assertions.Expect(removed).ToContainTextAsync("Provider not installed");
        await Assertions.Expect(removed).ToContainTextAsync("Install the provider");
        await Assertions.Expect(disabled).Not.ToContainTextAsync("Install the provider");
        await ScanAsync(page, page.Locator(".network-drive").First.Locator(".."), "desktop-missing-provider", shots, findings);

        // Phone: the same drives are chips of the places bar.
        await page.GetByRole(AriaRole.Button, new() { Name = "System clock, notifications, and calendar" }).ClickAsync();
        await page.GetByRole(AriaRole.Dialog, new() { Name = "Notifications and calendar" })
            .GetByRole(AriaRole.Radio, new() { Name = "Mobile", Exact = true }).ClickAsync();
        await page.SetViewportSizeAsync(390, 844);
        await page.GetByRole(AriaRole.Button, new() { Name = "Home", Exact = true }).ClickAsync();
        ILocator home = page.Locator(".mobile-home-screen");
        await Assertions.Expect(home).ToBeVisibleAsync(new() { Timeout = 15000 });
        await home.GetByRole(AriaRole.Button, new() { Name = "All applications", Exact = true }).ClickAsync();
        ILocator drawer = page.GetByRole(AriaRole.Dialog, new() { Name = "All applications", Exact = true });
        await drawer.GetByRole(AriaRole.Searchbox, new() { Name = "Search applications" }).FillAsync("File Explorer");
        await drawer.GetByRole(AriaRole.Button, new() { Name = "File Explorer", Exact = true }).ClickAsync();
        ILocator chip = page.Locator(".network-chip.missing");
        await Assertions.Expect(chip).ToHaveCountAsync(2, new() { Timeout = 30000 });
        await ScanAsync(page, chip.First.Locator(".."), "mobile-missing-provider", shots, findings);
    }

    /// <summary>Types a command and waits for the next prompt: the shell runs one line at a time.</summary>
    private async Task RunInTerminalAsync(IPage page, ILocator rows, string command)
    {
        static int Prompts(string text) => text.Split("@kazoos:").Length - 1;
        int before = Prompts(await rows.InnerTextAsync());
        await page.Keyboard.TypeAsync(command);
        await page.Keyboard.PressAsync("Enter");
        await WaitForTerminalAsync(
            rows, text => command == "clear" ? Prompts(text) >= 1 : Prompts(text) > before, $"the prompt after '{command.Split(' ')[0]}'");
    }

    /// <summary>Polls the terminal's rendered rows until <paramref name="done"/> holds.</summary>
    private async Task WaitForTerminalAsync(ILocator rows, Func<string, bool> done, string what)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        string text = string.Empty;
        while (elapsed.Elapsed < TimeSpan.FromSeconds(30))
        {
            text = (await rows.InnerTextAsync()).Replace(' ', ' ').Replace("\n", string.Empty, StringComparison.Ordinal);
            if (done(text))
            {
                return;
            }

            await Task.Delay(200);
        }

        output.WriteLine(text);
        throw new TimeoutException($"The terminal never showed {what}.");
    }

    private async Task RunStepsAsync(IPage page, string address, string shots, List<string> findings)
    {
        await E2ESupport.NavigateWhenReadyAsync(page, address, output);
        await E2ESupport.CreateLocalProfileAndReachDesktopAsync(page, output);

        // --- Desktop: File Explorer's dialog ---
        await E2ESupport.OpenAppAsync(page, "File Explorer", output);
        await page.GetByRole(AriaRole.Button, new() { Name = "Connect a network drive…" }).ClickAsync(new() { Timeout = 30000 });
        // The dialog's name is its title, which follows the step ("Connect Dropbox"…).
        ILocator dialog = page.Locator(".cnd-dialog[role='dialog']");
        await Assertions.Expect(page.GetByRole(AriaRole.Dialog, new() { Name = "Connect a network drive" })).ToBeVisibleAsync(new() { Timeout = 15000 });
        await Assertions.Expect(dialog.GetByText("Folder of this computer")).ToBeVisibleAsync(new() { Timeout = 15000 });
        await ScanAsync(page, dialog, "desktop-connect-dialog", shots, findings);

        // Dropbox, configured by the test's app keys: the scope is the user's choice (ADR 0267 §8).
        // Nothing signs in: the provider page is routed to an error page below.
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Dropbox" }).First.ClickAsync();
        ILocator scopes = dialog.GetByRole(AriaRole.Group, new() { Name = "What Kazo OS can reach" });
        await Assertions.Expect(page.GetByRole(AriaRole.Dialog, new() { Name = "Connect Dropbox" })).ToBeVisibleAsync(new() { Timeout = 15000 });
        await Assertions.Expect(scopes.GetByRole(AriaRole.Radio)).ToHaveCountAsync(2);
        // The reserved folder: where Dropbox puts it in the storage shows before the starting folder.
        await scopes.GetByRole(AriaRole.Radio, new() { Name = "A folder reserved for Kazo OS" }).CheckAsync();
        await Assertions.Expect(dialog.GetByText("/Apps/Kazo OS Folder/", new() { Exact = true })).ToBeVisibleAsync(new() { Timeout = 15000 });
        await Assertions.Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Sign in and mount" })).ToBeVisibleAsync();
        await ScanAsync(page, dialog, "desktop-dropbox-step", shots, findings);

        // A provider that shows its own error page never comes back to Kazo OS, and the OS cannot see
        // its window close (COOP, ADR 0131): Back must still work and stop the wait, with no error.
        Task<IPage> signInWindow = page.Context.WaitForPageAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Sign in and mount" }).ClickAsync();
        IPage providerPage = await signInWindow;
        await Assertions.Expect(dialog.GetByText("The Dropbox sign-in window is open.", new() { Exact = false })).ToBeVisibleAsync(new() { Timeout = 15000 });
        await providerPage.CloseAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Back" }).Last.ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Dialog, new() { Name = "Connect a network drive" })).ToBeVisibleAsync(new() { Timeout = 15000 });
        await Assertions.Expect(dialog.GetByRole(AriaRole.Alert)).ToHaveCountAsync(0);

        // S3: a refusal of the (routed, fake) service is shown in the dialog, which stays open.
        await dialog.GetByRole(AriaRole.Button, new() { Name = "S3 compatible storage" }).ClickAsync();
        await dialog.GetByLabel("Address of the service", new() { Exact = true }).FillAsync(FakeS3Endpoint);
        await dialog.GetByLabel("Bucket", new() { Exact = true }).FillAsync("photos");
        await dialog.Locator("select").SelectOptionAsync("path"); // its label also holds the chosen option's text
        await dialog.GetByLabel("Access key ID", new() { Exact = true }).FillAsync("AKIDE2EEXAMPLE");
        await dialog.GetByLabel("Secret access key", new() { Exact = true }).FillAsync("e2e-secret");
        await ScanAsync(page, dialog, "desktop-s3-form", shots, findings);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Mount", Exact = true }).ClickAsync();
        await Assertions.Expect(dialog.GetByRole(AriaRole.Alert)).ToBeVisibleAsync(new() { Timeout = 30000 });
        await ScanAsync(page, dialog, "desktop-s3-refused", shots, findings);
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
    /// Gives the OS Dropbox and OneDrive app keys (no request ever reaches them: the test stops before
    /// signing in: the sign-in window gets an error page) and answers the S3 address with an access refusal, as a bucket with wrong keys does.
    /// </summary>
    private static async Task RouteFakeServicesAsync(IBrowserContext context)
    {
        // The sign-in window shows the provider's own error page, as when a redirect URI is not registered.
        await context.RouteAsync("https://www.dropbox.com/**", route => route.FulfillAsync(new()
        {
            ContentType = "text/html",
            Body = "<!doctype html><title>Error</title><p>invalid_request: redirect_uri</p>"
        }));
        await context.RouteAsync("**/appsettings.json", async route =>
        {
            IAPIResponse original = await route.FetchAsync();
            string json = (await original.TextAsync())
                .Replace("\"FullAccessClientId\": \"\"", "\"FullAccessClientId\": \"e2e-dropbox-full\"", StringComparison.Ordinal)
                .Replace("\"AppFolderClientId\": \"\"", "\"AppFolderClientId\": \"e2e-dropbox-app\"", StringComparison.Ordinal)
                .Replace("\"ClientId\": \"\"", "\"ClientId\": \"e2e-onedrive\"", StringComparison.Ordinal);
            await route.FulfillAsync(new() { Response = original, Body = json });
        });
        Dictionary<string, string> cors = new()
        {
            ["Access-Control-Allow-Origin"] = "*",
            ["Access-Control-Allow-Methods"] = "GET, PUT, POST, DELETE, HEAD",
            ["Access-Control-Allow-Headers"] = "*",
            ["Access-Control-Expose-Headers"] = "ETag"
        };
        await context.RouteAsync($"{FakeS3Endpoint}/**", route => route.Request.Method == "OPTIONS"
            ? route.FulfillAsync(new() { Status = 204, Headers = cors })
            : route.FulfillAsync(new()
            {
                Status = 403,
                Headers = cors,
                ContentType = "application/xml",
                Body = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Error><Code>InvalidAccessKeyId</Code><Message>The access key does not exist.</Message></Error>"
            }));
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
