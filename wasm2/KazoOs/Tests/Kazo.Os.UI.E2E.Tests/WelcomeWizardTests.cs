using System.Diagnostics;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Kazo.Os.UI.E2E.Tests;

/// <summary>
/// ADR 0276: the welcome wizard shows at the first sign-in of an account, applies each choice at
/// the click, and does not come back at the next sign-in.
/// </summary>
public sealed class WelcomeWizardTests(ITestOutputHelper output)
{
    private const float StepTimeoutMs = 30000;

    [Fact]
    public async Task The_first_sign_in_walks_the_wizard_and_the_desktop_opens_with_the_choices()
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
            IPage page = await context.NewPageAsync();
            E2ESupport.AttachDiagnostics(page, output);

            await E2ESupport.NavigateWhenReadyAsync(page, address, output);

            // --- The first account is created: the wizard stands between the sign-in and the desktop ---
            ILocator login = page.GetByLabel("Login name");
            await login.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
            await login.FillAsync(E2ESupport.LoginName);
            await page.GetByLabel("Display name").FillAsync(E2ESupport.DisplayName);
            await page.GetByLabel("Password", new() { Exact = true }).FillAsync(E2ESupport.Password);
            await page.GetByLabel("Confirm password").FillAsync(E2ESupport.Password);
            await page.GetByRole(AriaRole.Button, new() { Name = "Create local profile" }).ClickAsync();

            ILocator wizard = page.Locator(".welcome-surface");
            await wizard.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 60000 });
            await Assertions.Expect(page.Locator(".desktop-shell")).ToHaveCountAsync(0);
            await Assertions.Expect(wizard.Locator(".welcome-progress li")).ToHaveCountAsync(4);
            output.WriteLine("[test] the wizard shows its four steps before the desktop.");

            // --- Language: the screen speaks French at the click ---
            await wizard.Locator("[data-locale='fr_CA.UTF-8']").ClickAsync();
            await Assertions.Expect(wizard.GetByRole(AriaRole.Heading, new() { Level = 1 }))
                .ToHaveTextAsync("Choisissez votre langue", new() { Timeout = StepTimeoutMs });
            await Assertions.Expect(wizard.Locator("[data-locale='fr_CA.UTF-8']")).ToHaveAttributeAsync("aria-pressed", "true");
            await wizard.Locator(".welcome-next").ClickAsync();

            // --- Theme: the chosen card is selected and stays so once the write is done ---
            ILocator theme = wizard.Locator("[data-theme-id='windows-98']");
            await theme.ClickAsync();
            await Assertions.Expect(theme).ToHaveAttributeAsync("aria-pressed", "true");
            await Assertions.Expect(theme).ToBeEnabledAsync(new() { Timeout = StepTimeoutMs });
            await Assertions.Expect(theme).ToHaveAttributeAsync("aria-pressed", "true");
            await wizard.Locator(".welcome-next").ClickAsync();

            // --- Wallpaper ---
            ILocator wallpaper = wizard.Locator("[data-wallpaper='aurora']");
            await wallpaper.ClickAsync();
            await Assertions.Expect(wallpaper).ToHaveAttributeAsync("aria-pressed", "true", new() { Timeout = StepTimeoutMs });
            await wizard.Locator(".welcome-next").ClickAsync();

            // --- Time zone: the last step offers « Finish » ---
            await wizard.Locator("#welcome-zone-search").FillAsync("Toronto");
            ILocator zone = wizard.Locator("[data-zone='America/Toronto']");
            await zone.ClickAsync();
            await Assertions.Expect(zone).ToHaveAttributeAsync("aria-pressed", "true", new() { Timeout = StepTimeoutMs });
            await Assertions.Expect(wizard.Locator(".welcome-clock")).ToContainTextAsync("America/Toronto");
            await wizard.Locator(".welcome-finish").ClickAsync();

            // --- The desktop opens in French; the files hold the choices ---
            ILocator launcher = page.GetByRole(AriaRole.Button, new() { Name = "Lanceur d'applications", Exact = true });
            await launcher.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 60000 });
            await Assertions.Expect(wizard).ToHaveCountAsync(0);
            output.WriteLine("[test] the desktop opened in French after « Terminer ».");

            await launcher.ClickAsync();
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Rechercher des applications" }).FillAsync("Terminal");
            await page.GetByRole(AriaRole.Button, new() { Name = "Lancer Terminal", Exact = true }).ClickAsync(new() { Timeout = 20000 });
            ILocator rows = page.Locator("[data-terminal-panel] .xterm-rows").First;
            ILocator screen = page.Locator("[data-terminal-panel] .xterm-screen").First;
            await WaitForTextAsync(rows, text => text.Contains('$', StringComparison.Ordinal), "the shell prompt");
            await screen.ClickAsync();
            await page.Keyboard.TypeAsync("cd ~/.config; grep windows-98 kazoos/appearance.config; grep aurora kazoos/appearance.config; grep Toronto kazoos/datetime.config; grep LANG locale.conf; grep desktop ~/.local/state/kazoos/welcome.json; echo done-files");
            await page.Keyboard.PressAsync("Enter");
            // The typed command holds « done-files » once; its answer is the second one.
            string files = await WaitForTextAsync(rows, text => Compact(text).Split("done-files").Length > 2, "the settings files");
            string compact = Compact(files);
            Assert.Contains("windows-98", compact, StringComparison.Ordinal);
            Assert.Contains("builtin:aurora", compact, StringComparison.Ordinal);
            Assert.Contains("America/Toronto", compact, StringComparison.Ordinal);
            Assert.Contains("LANG=fr_CA.UTF-8", compact, StringComparison.Ordinal);
            Assert.Contains("\"desktop\"", compact, StringComparison.Ordinal);
            output.WriteLine("[test] appearance.config, datetime.config, locale.conf and welcome.json hold the choices.");

            // --- Next sign-in: no wizard ---
            await page.ReloadAsync();
            await page.GetByLabel("Password", new() { Exact = true }).FillAsync(E2ESupport.Password, new() { Timeout = 60000 });
            await page.GetByRole(AriaRole.Button, new() { Name = "Start session", Exact = true }).ClickAsync();
            await launcher.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 60000 });
            await Assertions.Expect(wizard).ToHaveCountAsync(0);
            output.WriteLine("[test] the second sign-in opens the desktop directly.");
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }

    /// <summary>Joins the terminal's wrapped rows so a value cut by a line end still reads whole.</summary>
    private static string Compact(string text) =>
        text.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal).Replace("\u00a0", string.Empty, StringComparison.Ordinal);

    /// <summary>Polls the terminal's rendered rows until <paramref name="predicate"/> holds.</summary>
    private static async Task<string> WaitForTextAsync(ILocator rows, Func<string, bool> predicate, string what)
    {
        Stopwatch watch = Stopwatch.StartNew();
        string text = string.Empty;
        while (watch.ElapsedMilliseconds < StepTimeoutMs)
        {
            text = await rows.InnerTextAsync();
            if (predicate(text))
            {
                return text;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"Timed out waiting for {what}. Terminal shows: {text}");
    }
}
