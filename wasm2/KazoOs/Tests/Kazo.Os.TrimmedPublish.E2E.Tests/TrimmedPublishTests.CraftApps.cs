using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Kazo.Os.TrimmedPublish.E2E.Tests;

/// <summary>
/// ADR 0259 (series <c>craft-host/base</c>, work item 4): a Craft app window built on the
/// <c>Kazo.Os.AppSdk.Craft</c> module, installed by <c>kazopkg</c> in the trimmed OS, end to end.
/// The test package (<c>Tests/fixtures/craft-app</c>) carries only its window, its iframe page and
/// a fake Craft app; the module's assembly and scripts come from the OS.
/// </summary>
public sealed partial class TrimmedPublishTests
{
    private const string CraftRepository = "Kazo-ca/Os-App-CraftFixture";
    private const string CraftAppName = "Craft Fixture";

    /// <summary>
    /// Install, then open a file with <c>xdg-open</c> (the manifest's file handler, the window's
    /// launch argument, <c>open</c>), type (changes: <c>*</c> in the title), Ctrl+S (rewrite, then
    /// <c>saved</c> back to the app), F12 (a chord the window reserves: Save As through the OS
    /// dialog), an error the app reports after <c>ready</c>, the OS theme followed, the file picker
    /// answered by the OS Open dialog, the close guard, and the app's settings kept for the Kazo user.
    /// </summary>
    [TrimmedPublishFact]
    public async Task A_craft_app_on_the_os_module_opens_saves_and_follows_the_os()
    {
        TrimmedPublishSettings settings = TrimmedPublishSettings.FromEnvironment()!;
        if (settings.CraftPackagesDirectory is null)
        {
            Log($"{TrimmedPublishSettings.CraftPackagesVariable} is not set: no Craft test package, Craft app journey skipped.");
            return;
        }

        await using Session session = await Session.StartAsync(output);
        IPage page = session.Page;
        await ServePackagesLocallyAsync(session.Context, CraftRepository, settings.CraftPackagesDirectory);
        try
        {
            await NavigateWhenReadyAsync(page, settings.Url);
            await SignInAsync(page);

            // --- Install, and a file of the app's format in the home folder ---
            await OpenAppAsync(page, "Terminal");
            ILocator rows = page.Locator("[data-terminal-panel] .xterm-rows").First;
            ILocator screen = page.Locator("[data-terminal-panel] .xterm-screen").First;
            await WaitForTerminalTextAsync(rows, text => PromptPattern().IsMatch(text), "the shell prompt");
            await RunCommandAsync(page, screen, $"sudo kazopkg install {CraftRepository}");
            await ConfirmAsAdministratorAsync(page);
            await WaitForTerminalTextAsync(rows, text => text.Contains("available now", StringComparison.Ordinal), "the end of the installation");
            Log("the Craft test package was installed.");
            await RunCommandAsync(page, screen, "echo seed > seed.fake");

            // --- Open: the file handler starts the window on the file, the app receives it ---
            await RunCommandAsync(page, screen, "xdg-open seed.fake");
            ILocator window = CraftWindow(page);
            await ApproveWhenAskedAsync(page, window);
            ILocator title = window.Locator("h2").First;
            IFrameLocator app = window.FrameLocator("iframe.craft-frame-iframe");
            await Assertions.Expect(app.Locator("[data-fake-name]")).ToHaveTextAsync("seed.fake", new() { Timeout = BootTimeoutMs });
            await Assertions.Expect(app.Locator("[data-fake-text]")).ToContainTextAsync("seed", new() { Timeout = StepTimeoutMs });
            await Assertions.Expect(title).ToHaveTextAsync("seed.fake — Craft Fixture", new() { Timeout = StepTimeoutMs });
            Log("xdg-open started the Craft app window on the file; the fake Craft app received it.");

            // --- Theme and language: the window passed the OS scheme and the user's language ---
            await Assertions.Expect(app.Locator("[data-fake-theme]")).ToHaveTextAsync("dark", new() { Timeout = StepTimeoutMs });
            await Assertions.Expect(app.Locator("[data-fake-language]")).ToHaveTextAsync("en", new() { Timeout = StepTimeoutMs });
            await Assertions.Expect(app.Locator("[data-fake-launches]")).ToHaveTextAsync("launches 1", new() { Timeout = StepTimeoutMs });

            // --- Changes: the title takes a '*'; Ctrl+S rewrites the file and the app hears 'saved' ---
            await app.Locator("canvas").ClickAsync();
            await page.Keyboard.TypeAsync("X");
            await Assertions.Expect(title).ToHaveTextAsync("*seed.fake — Craft Fixture", new() { Timeout = StepTimeoutMs });
            await page.Keyboard.PressAsync("Control+s");
            await Assertions.Expect(title).ToHaveTextAsync("seed.fake — Craft Fixture", new() { Timeout = StepTimeoutMs });
            await Assertions.Expect(app.Locator("[data-fake-dirty]")).ToHaveTextAsync("saved", new() { Timeout = StepTimeoutMs });
            Assert.Equal(0, await page.Locator("section.file-dialog").CountAsync());
            Log("typing marked the window modified; Ctrl+S rewrote the bound file without a dialog and cleared it.");

            // --- Save As through a reserved chord: F12 goes to the window, which asks the OS dialog ---
            await page.Keyboard.TypeAsync("Y");
            await page.Keyboard.PressAsync("F12");
            ILocator saveDialog = page.Locator("section.file-dialog", new() { HasText = "Save the document" });
            await saveDialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = StepTimeoutMs });
            await saveDialog.GetByLabel("File name").FillAsync("copy.fake");
            await saveDialog.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
            await saveDialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = StepTimeoutMs });
            await Assertions.Expect(title).ToHaveTextAsync("copy.fake — Craft Fixture", new() { Timeout = StepTimeoutMs });
            await Assertions.Expect(app.Locator("[data-fake-name]")).ToHaveTextAsync("copy.fake", new() { Timeout = StepTimeoutMs });
            await Assertions.Expect(app.Locator("[data-fake-dirty]")).ToHaveTextAsync("saved", new() { Timeout = StepTimeoutMs });
            Log("F12 reached the window through the iframe; Save As bound the document to copy.fake.");

            // --- An error after ready is about the document: shown by the window, the app keeps running ---
            await app.Locator("canvas").ClickAsync();
            await page.Keyboard.TypeAsync("!");
            await Assertions.Expect(window.Locator("[data-craft-fixture-notice]")).ToContainTextAsync("failed its check", new() { Timeout = StepTimeoutMs });
            await Assertions.Expect(window.Locator("[data-craft-fixture-state]")).ToHaveCountAsync(0);
            Log("an error reported after ready reached the window as a notice; the app kept running.");

            // --- The OS theme changes: the window hears the new scheme and tells the app ---
            await window.EvaluateAsync("element => element.closest('[data-theme]').setAttribute('data-theme', 'windows-98')");
            await Assertions.Expect(app.Locator("[data-fake-theme]")).ToHaveTextAsync("light", new() { Timeout = StepTimeoutMs });
            await window.EvaluateAsync("element => element.closest('[data-theme]').setAttribute('data-theme', 'kazoos')");
            await Assertions.Expect(app.Locator("[data-fake-theme]")).ToHaveTextAsync("dark", new() { Timeout = StepTimeoutMs });
            Log("a light theme and back: the app followed the OS color scheme.");

            // --- The app's file picker is answered by the OS Open dialog (and reads what Ctrl+S wrote) ---
            await app.Locator("canvas").ClickAsync();
            await page.Keyboard.PressAsync("Control+o");
            ILocator pickDialog = page.Locator("section.file-dialog", new() { HasText = "Pick a file for the Craft app" });
            await pickDialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = StepTimeoutMs });
            await pickDialog.GetByRole(AriaRole.Row, new() { NameString = "seed.fake", Exact = false }).ClickAsync();
            await pickDialog.GetByRole(AriaRole.Button, new() { Name = "Open" }).ClickAsync();
            await pickDialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = StepTimeoutMs });
            ILocator picked = app.Locator("[data-fake-picked]");
            await Assertions.Expect(picked).ToContainTextAsync("picked seed.fake: seed", new() { Timeout = StepTimeoutMs });
            await Assertions.Expect(picked).ToContainTextAsync("X", new() { Timeout = StepTimeoutMs });
            Log("the app's file input went to the OS Open dialog; the app read the file Ctrl+S had written.");

            // --- Close with changes: the window asks (ui.inspect), "No" closes without saving ---
            await app.Locator("canvas").ClickAsync();
            await page.Keyboard.TypeAsync("Z");
            await Assertions.Expect(title).ToHaveTextAsync("*copy.fake — Craft Fixture", new() { Timeout = StepTimeoutMs });
            await window.Locator("button.chrome-button-close").ClickAsync();
            ILocator question = page.Locator(".message-box-dialog", new() { HasText = "Save the changes to copy.fake?" });
            await question.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = StepTimeoutMs });
            await question.GetByRole(AriaRole.Button, new() { Name = "No" }).ClickAsync();
            await Assertions.Expect(window).ToHaveCountAsync(0, new() { Timeout = StepTimeoutMs });
            Log("closing with changes asked first; 'No' closed the window.");

            // --- A new window, no file: the app's blank document, and its settings kept for this user ---
            await OpenAppAsync(page, CraftAppName);
            await ApproveWhenAskedAsync(page, window);
            await Assertions.Expect(app.Locator("[data-fake-launches]")).ToHaveTextAsync("launches 2", new() { Timeout = BootTimeoutMs });
            await Assertions.Expect(title).ToHaveTextAsync("Untitled — Craft Fixture", new() { Timeout = StepTimeoutMs });
            Log("a second window started blank and read the setting the first one wrote (~/.config/<app>/web-storage.json).");

            session.AssertNoErrors("Craft app journey");
            await CloseAllWindowsAsync(page);
        }
        catch
        {
            await SaveScreenshotAsync(page, settings, "craft-app");
            throw;
        }
    }

    /// <summary>The Craft test package's window, found by the app name at the end of its title.</summary>
    private static ILocator CraftWindow(IPage page) =>
        page.Locator("[data-window-id]").Filter(new() { Has = page.Locator("h2", new() { HasTextRegex = CraftTitlePattern() }) });

    /// <summary>
    /// Waits for <paramref name="window"/>, answering the administrator approval an app with
    /// dangerous capabilities asks for at its first launch (ADR 0048).
    /// </summary>
    private async Task ApproveWhenAskedAsync(IPage page, ILocator window)
    {
        ILocator approval = page.Locator("[role=alertdialog]").Filter(new() { HasText = "Administrator authorization required" });
        ILocator launchError = page.Locator(".lazy-launch-error");
        Stopwatch elapsed = Stopwatch.StartNew();
        while (await window.CountAsync() == 0)
        {
            if (await approval.IsVisibleAsync())
            {
                await approval.GetByLabel("Password").FillAsync(Password);
                await approval.GetByRole(AriaRole.Button, new() { Name = "Allow" }).ClickAsync();
                Log($"'{CraftAppName}' approved by the administrator.");
            }

            if (await launchError.IsVisibleAsync())
            {
                throw new InvalidOperationException($"'{CraftAppName}' did not start: {await launchError.InnerTextAsync()}");
            }

            if (elapsed.ElapsedMilliseconds > StepTimeoutMs)
            {
                throw new TimeoutException($"'{CraftAppName}' opened no window.");
            }

            await Task.Delay(100);
        }
    }

    [GeneratedRegex("Craft Fixture$")]
    private static partial Regex CraftTitlePattern();
}
