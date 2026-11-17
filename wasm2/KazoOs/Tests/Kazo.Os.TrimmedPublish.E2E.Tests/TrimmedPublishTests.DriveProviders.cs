using Microsoft.Playwright;

namespace Kazo.Os.TrimmedPublish.E2E.Tests;

/// <summary>
/// ADR 0282 (series <c>drive-providers/base</c>, work item 7): a third-party drive provider,
/// packed from <c>Apps/Samples/Kazo.Os.Samples.WebDavDrive</c> as a Store package would be, installed
/// by <c>kazopkg</c> as a feature for the machine in the trimmed OS. The full journey (connect by the
/// form, write, read back against a test WebDAV server) waits for the connect dialog of work item 6
/// and comes with work item 9 (Benoit, 2026-11-17: answer 2 A).
/// </summary>
public sealed partial class TrimmedPublishTests
{
    private const string DriveRepository = "Kazo-ca/Os-App-WebDavSample";

    /// <summary>
    /// Before the install, <c>mount -t webdav</c> does not know the type; <c>sudo kazopkg install</c>
    /// installs the feature for the machine and approves <c>drives.provide</c> with the
    /// administrator's consent, on a window that names the server address field; then, without a restart, <c>mount</c> knows the type and only asks
    /// for an account of it.
    /// </summary>
    [TrimmedPublishFact]
    public async Task An_installed_drive_provider_feature_adds_its_type_to_mount()
    {
        TrimmedPublishSettings settings = TrimmedPublishSettings.FromEnvironment()!;
        if (settings.DrivePackagesDirectory is null)
        {
            Log($"{TrimmedPublishSettings.DrivePackagesVariable} is not set: no drive provider package, drive provider check skipped.");
            return;
        }

        await using Session session = await Session.StartAsync(output);
        IPage page = session.Page;
        await ServePackagesLocallyAsync(session.Context, DriveRepository, settings.DrivePackagesDirectory);
        try
        {
            await NavigateWhenReadyAsync(page, settings.Url);
            await SignInAsync(page);
            await OpenAppAsync(page, "Terminal");
            ILocator rows = page.Locator("[data-terminal-panel] .xterm-rows").First;
            ILocator screen = page.Locator("[data-terminal-panel] .xterm-screen").First;
            await WaitForTerminalTextAsync(rows, text => PromptPattern().IsMatch(text), "the shell prompt");

            // --- Before: no feature provides the type ---
            await RunCommandAsync(page, screen, "mount -t webdav -o account=nobody");
            await WaitForTerminalTextAsync(rows, text => text.Contains("unknown filesystem type 'webdav'", StringComparison.Ordinal), "mount refusing the unknown type");
            Log("before the install, mount does not know the webdav type.");

            // --- Install: a feature, for the machine; the consent approves drives.provide ---
            await RunCommandAsync(page, screen, $"sudo kazopkg install {DriveRepository}");

            // sudo asks for the administrator, then an install for the machine asks the OS authentication
            // agent again (ADR 0048 §3), then shows the approval window of ADR 0189 §4, which names the
            // typed server (ADR 0284 §2). The prompts come one after the other: answer each until the window.
            ILocator approval = page.Locator(".iap-window").First;
            ILocator prompt = page.Locator(".credential-prompt-dialog, .aaa-window").First;
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(StepTimeoutMs * 3);
            while (!await approval.IsVisibleAsync())
            {
                Assert.True(DateTime.UtcNow < deadline, "The install approval window never opened.");
                // A prompt checking a password keeps its field disabled: answer only one that waits.
                ILocator waiting = prompt.Locator("input[type=password]:not([disabled])");
                if (await prompt.IsVisibleAsync() && await waiting.CountAsync() > 0 && string.IsNullOrEmpty(await waiting.InputValueAsync()))
                {
                    await AnswerAdministratorPromptAsync(prompt);
                }
                else
                {
                    await page.WaitForTimeoutAsync(200);
                }
            }

            await Assertions.Expect(approval.Locator(".iap-capability-hosts").First)
                .ToContainTextAsync("Server address", new() { Timeout = StepTimeoutMs });
            await approval.Locator(".iap-approve").ClickAsync();
            await approval.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = StepTimeoutMs });
            Log("the approval window named the typed server; the administrator approved the feature.");
            string installed = await WaitForTerminalTextAsync(
                rows, text => text.Contains("available now", StringComparison.Ordinal), "the end of the installation");
            Assert.Contains("drives.provide (!)", installed, StringComparison.Ordinal);
            Assert.Contains("approved for the whole machine", installed, StringComparison.Ordinal);
            Log("the WebDAV sample was installed as a feature; drives.provide was approved by the administrator.");
            session.AssertNoErrors("kazopkg install of a drive provider");

            // --- After, without a restart: the type is offered, mount only lacks an account ---
            await RunCommandAsync(page, screen, "mount -t webdav -o account=nobody");
            await WaitForTerminalTextAsync(rows, text => text.Contains("no webdav account named 'nobody'", StringComparison.Ordinal), "mount asking for a webdav account");
            Log("after the install, mount knows the webdav type and asks for an account of it.");

            session.AssertNoErrors("drive provider feature");
            await CloseAllWindowsAsync(page);
        }
        catch
        {
            await SaveScreenshotAsync(page, settings, "drive-provider");
            throw;
        }
    }

    /// <summary>
    /// Answers one administrator prompt, then waits until it is gone or replaced by the next one
    /// (whose password is empty): unlike <see cref="ConfirmAsAdministratorAsync"/>, it does not wait
    /// for every prompt to close, since the install opens a second one right after sudo's.
    /// </summary>
    private async Task AnswerAdministratorPromptAsync(ILocator prompt)
    {
        ILocator login = prompt.Locator("input[type=text]");
        if (await login.CountAsync() > 0 && string.IsNullOrEmpty(await login.InputValueAsync()))
        {
            await login.FillAsync(LoginName);
        }

        ILocator password = prompt.Locator("input[type=password]");
        await password.FillAsync(Password);
        await password.PressAsync("Enter");
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(StepTimeoutMs);
        while (await prompt.IsVisibleAsync() && await password.CountAsync() > 0 && !string.IsNullOrEmpty(await password.InputValueAsync()))
        {
            Assert.True(DateTime.UtcNow < deadline, "The administrator prompt did not take the password.");
            await prompt.Page.WaitForTimeoutAsync(200);
        }

        Log("the administrator confirmed.");
    }
}
