using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Kazo.Os.TrimmedPublish.E2E.Tests;

/// <summary>
/// ADR 0282 (series <c>drive-providers/base</c>, work item 7): a third-party drive provider,
/// packed from <c>Apps/Samples/Kazo.Os.Samples.WebDavDrive</c> as a Store package would be, installed
/// by <c>kazopkg</c> as a feature for the machine in the trimmed OS, then used: a drive connected by
/// the form of "Connect a network drive", a file written and one read back against a test WebDAV
/// server (<see cref="RoutedWebDavServer"/>, work item 9; Benoit, 2026-11-17: answer 2 A).
/// </summary>
public sealed partial class TrimmedPublishTests
{
    private const string DriveRepository = "Kazo-ca/Os-App-WebDavSample";

    /// <summary>
    /// Before the install, <c>mount -t webdav</c> does not know the type; <c>sudo kazopkg install</c>
    /// installs the feature for the machine and approves <c>drives.provide</c> with the
    /// administrator's consent, on a window that names the server address field; then, without a
    /// restart, <c>mount</c> knows the type and writes the syntax of its form. The user connects a
    /// WebDAV server by the form of the File Explorer's "Connect a network drive" dialog, reads a file
    /// the server holds and writes one the server receives, through the drive in <c>/mnt</c>.
    /// </summary>
    [TrimmedPublishFact]
    public async Task An_installed_drive_provider_feature_mounts_a_server_by_its_form_and_reads_and_writes_it()
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
        RoutedWebDavServer server = new();
        server.PlaceFile("from-server.txt", "written by the server");
        await server.RouteAsync(session.Context);

        // Writing a new file first asks the server whether it exists: its 404 is expected.
        session.ExpectNotFoundFrom(RoutedWebDavServer.Origin);
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

            // --- After, without a restart: mount knows the type and writes the syntax of its form ---
            await RunCommandAsync(page, screen, "mount -t webdav");
            await WaitForTerminalTextAsync(
                rows, text => text.Contains("Usage: mount -t webdav -o", StringComparison.Ordinal) && text.Contains("server", StringComparison.Ordinal),
                "mount writing the syntax of the webdav form");
            Log("after the install, mount knows the webdav type and writes the syntax of its form.");
            await CloseAllWindowsAsync(page);

            // --- Connect: the File Explorer's dialog offers the type and shows its form ---
            await ConnectWebDavDriveAsync(page);
            Log($"the WebDAV drive was connected by its form; the server received: {string.Join("; ", server.Requests)}.");
            await CloseAllWindowsAsync(page);

            // --- Use: read the server's file, write one the server receives, read it back ---
            await OpenAppAsync(page, "Terminal");
            rows = page.Locator("[data-terminal-panel] .xterm-rows").First;
            screen = page.Locator("[data-terminal-panel] .xterm-screen").First;
            await WaitForTerminalTextAsync(rows, text => PromptPattern().IsMatch(text), "the shell prompt");
            await RunCommandAsync(page, screen, "clear; mount | grep webdav");
            string mounts = await WaitForTerminalTextAsync(rows, text => MountedWebDav().IsMatch(text), "the WebDAV drive in mount");
            string mountPoint = MountedWebDav().Match(mounts).Groups["target"].Value;
            Log($"the WebDAV drive is mounted on {mountPoint}.");

            await RunCommandAsync(page, screen, $"clear; cat '{mountPoint}/from-server.txt'");
            await WaitForTerminalTextAsync(rows, text => text.Contains("written by the server", StringComparison.Ordinal), "the server's file");
            Log("cat read the file the server holds.");

            await RunCommandAsync(page, screen, $"clear; echo written by kazo > '{mountPoint}/from-kazo.txt'; cat '{mountPoint}/from-kazo.txt'");
            await WaitForTerminalTextAsync(rows, text => text.Contains("written by kazo", StringComparison.Ordinal), "the written file read back");
            DateTime written = DateTime.UtcNow.AddMilliseconds(StepTimeoutMs);
            while (server.ReadFile("from-kazo.txt") is not { } received || !received.Contains("written by kazo", StringComparison.Ordinal))
            {
                Assert.True(DateTime.UtcNow < written, "The WebDAV server never received the written file.");
                await page.WaitForTimeoutAsync(200);
            }

            Assert.Contains("PUT", server.Methods);
            Log($"the file written in the drive reached the WebDAV server (PUT) and reads back; the server received: {string.Join("; ", server.Requests)}.");

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
    /// Opens the File Explorer's "Connect a network drive" dialog, chooses the WebDAV type the feature
    /// provides (named "Provided by"), fills its form (server address, user name, password) and mounts.
    /// </summary>
    private async Task ConnectWebDavDriveAsync(IPage page)
    {
        await OpenAppAsync(page, "File Explorer");
        await page.Locator(".sidebar-entry.network-connect").First.ClickAsync(new() { Timeout = StepTimeoutMs });
        ILocator dialog = page.Locator(".cnd-dialog").First;
        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = StepTimeoutMs });
        ILocator option = dialog.Locator(".cnd-option").Filter(new() { HasText = "WebDAV" }).First;
        await Assertions.Expect(option.Locator(".cnd-option-provider")).ToContainTextAsync("WebDAV drives", new() { Timeout = StepTimeoutMs });
        await option.ClickAsync();

        await dialog.Locator("input[data-field=server]").FillAsync(RoutedWebDavServer.ServerAddress);
        await dialog.Locator("input[data-field=username]").FillAsync(RoutedWebDavServer.UserName);
        await dialog.Locator("input[data-field=password]").FillAsync(RoutedWebDavServer.Password);
        await dialog.Locator(".cnd-actions .cnd-btn.primary").ClickAsync();
        try
        {
            await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = StepTimeoutMs });
        }
        catch (TimeoutException)
        {
            ILocator error = dialog.Locator(".cnd-error");
            Assert.Fail($"The WebDAV drive was not mounted: {(await error.CountAsync() > 0 ? await error.InnerTextAsync() : "the dialog stayed open")}.");
        }
    }

    /// <summary>A line of <c>mount</c> for the WebDAV drive, with its mount point.</summary>
    [GeneratedRegex(@" on (?<target>/mnt/.+?) type webdav")]
    private static partial Regex MountedWebDav();

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
