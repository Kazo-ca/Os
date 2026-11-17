using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Kazo.Os.TrimmedPublish.E2E.Tests;

/// <summary>
/// ADR 0206: the <c>wwwroot/</c> files of an app installed by <c>kazopkg</c> are served at
/// <c>_content/&lt;assembly&gt;/</c> by the service worker, Web Worker scripts included. ADR 0221:
/// the files it does not declare, its collocated <c>.razor.js</c> and its scoped CSS too.
/// </summary>
public sealed partial class TrimmedPublishTests
{
    private const string ProbeRepository = "Kazo-ca/Os-App-InstalledAssets";
    private const string ProbeAppName = "Installed Assets Probe";
    private const string ProbeContent = "_content/Kazo.Os.Fixtures.InstalledAssets/";

    /// <summary>
    /// ADR 0274: serves a test file as the OS serves an installed app's file, without the installer:
    /// its content as one binary chunk of <c>fsContent</c> (ADR 0272) and a <c>servedFiles</c> row
    /// pointing at it. Defines <c>window.kazoTestServedFiles.put(address, text, type)</c> and
    /// <c>.remove(address)</c> on the page; the address is relative to the page's base.
    /// </summary>
    private const string ServedFileHelpersScript =
        """
        () => {
            const request = r => new Promise((resolve, reject) => { r.onsuccess = () => resolve(r.result); r.onerror = () => reject(r.error); });
            const open = () => request(indexedDB.open('kazoos'));
            const hex = buffer => Array.from(new Uint8Array(buffer), b => b.toString(16).padStart(2, '0')).join('');
            const done = tx => new Promise((resolve, reject) => { tx.oncomplete = resolve; tx.onabort = () => reject(tx.error); tx.onerror = () => reject(tx.error); });
            window.kazoTestServedFiles = {
                async put(address, text, type) {
                    const data = new TextEncoder().encode(text);
                    const contentHash = hex(await crypto.subtle.digest('SHA-256', data));
                    const db = await open();
                    try {
                        const tx = db.transaction(['fsContent', 'servedFiles'], 'readwrite');
                        tx.objectStore('fsContent').put({ contentHash, chunkIndex: 0, data, chunkHash: contentHash, createdUtcMs: Date.now() });
                        tx.objectStore('servedFiles').put({ url: address, contentHash, length: data.length, contentType: type,
                            appId: 'org.kazoos.tests.served', chunkSize: Math.max(1, data.length) });
                        await done(tx);
                    } finally {
                        db.close();
                    }
                },
                async remove(address) {
                    const db = await open();
                    try {
                        const tx = db.transaction(['fsContent', 'servedFiles'], 'readwrite');
                        const row = await request(tx.objectStore('servedFiles').get(address));
                        tx.objectStore('servedFiles').delete(address);
                        if (row) tx.objectStore('fsContent').delete([row.contentHash, 0]);
                        await done(tx);
                    } finally {
                        db.close();
                    }
                }
            };
        }
        """;

    /// <summary>The fixture's text in <c>Resources/ProbeStrings.fr.resx</c> (ADR 0222).</summary>
    private const string FrenchGreeting = "Bonjour depuis le paquet";

    /// <summary>What <c>ProbeLibrary.Describe</c> of the package's library answers (ADR 0261 §1).</summary>
    private const string LibraryAnswer = "library ready (Kazo.Fixtures.InstalledLibrary)";

    /// <summary>
    /// The test package (<c>Tests/fixtures/installed-app</c>), answered in place of GitHub, is
    /// installed with <c>sudo kazopkg install</c> on the development address (no service worker from
    /// index.html there); it opens at once (ADR 0214), then again after the OS restarts: its module, the Web Worker it starts,
    /// the script that worker loads by a relative <c>importScripts</c>, and its image all load; so do
    /// its collocated module and its scoped CSS, which it never declared (ADR 0221), and its French
    /// text, read from the satellite assembly the package carries (ADR 0222).
    /// </summary>
    [TrimmedPublishFact]
    public async Task An_installed_app_finds_its_wwwroot_files_and_its_worker_scripts()
    {
        TrimmedPublishSettings settings = TrimmedPublishSettings.FromEnvironment()!;
        if (settings.PackagesDirectory is null)
        {
            Log($"{TrimmedPublishSettings.PackagesVariable} is not set: no test package, installed app check skipped.");
            return;
        }

        await using Session session = await Session.StartAsync(output);
        IPage page = session.Page;
        await ServePackagesLocallyAsync(session.Context, ProbeRepository, settings.PackagesDirectory);
        try
        {
            await NavigateWhenReadyAsync(page, settings.Url);
            await SignInAsync(page);

            // --- Install: kazopkg downloads index.json and the package, an administrator confirms ---
            await OpenAppAsync(page, "Terminal");
            ILocator rows = page.Locator("[data-terminal-panel] .xterm-rows").First;
            ILocator screen = page.Locator("[data-terminal-panel] .xterm-screen").First;
            await WaitForTerminalTextAsync(rows, text => PromptPattern().IsMatch(text), "the shell prompt");
            await RunCommandAsync(page, screen, $"sudo kazopkg install {ProbeRepository}");
            await ConfirmAsAdministratorAsync(page);
            await WaitForTerminalTextAsync(rows, text => text.Contains("available now", StringComparison.Ordinal), "the end of the installation");
            Log("the test package was installed.");
            session.AssertNoErrors("kazopkg install");
            await WaitForProbeIconAsync(page);
            Log("its icon is served before its first launch (issue #420).");

            // --- Without a restart (ADR 0214 §1): an app never loaded opens at once ---
            await OpenAppAsync(page, ProbeAppName);
            await Assertions.Expect(page.Locator("[data-probe-status]")).ToHaveTextAsync("engine ready", new() { Timeout = BootTimeoutMs });
            Log("the installed app opened without a restart.");
            await Assertions.Expect(page.Locator("[data-probe-french]")).ToHaveTextAsync(FrenchGreeting, new() { Timeout = StepTimeoutMs });
            Log("its French satellite assembly loaded without a restart.");
            // ADR 0242: CoreLib stays whole, so overloads the OS does not use itself still run.
            await Assertions.Expect(page.Locator("[data-probe-corelib]")).ToHaveTextAsync("corelib 42 0 2", new() { Timeout = StepTimeoutMs });
            // ADR 0280: System.Linq, System.Text.RegularExpressions and System.Threading.Channels kept whole.
            await Assertions.Expect(page.Locator("[data-probe-framework-libraries]")).ToHaveTextAsync("libraries a<1>b<2> Kazo_OS 4 True", new() { Timeout = StepTimeoutMs });
            Log("CoreLib overloads the OS does not use itself ran (Int32.Parse of a span, String.Compare with offsets, String.Split of strings).");
            // ADR 0261 §1 (issue #419): the package has two assemblies; the library is loaded before the app.
            await Assertions.Expect(page.Locator("[data-probe-library]")).ToHaveTextAsync(LibraryAnswer, new() { Timeout = StepTimeoutMs });
            Log("the library of the installed app (its second assembly) loaded without a restart.");

            // --- Restart (ADR 0182, Q5 C): the app joins the catalog at boot ---
            await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await SignInAsync(page);
            await WaitForProbeIconAsync(page);
            Log("after the restart, its icon is served before the app is opened (issue #420).");
            await OpenAppAsync(page, ProbeAppName);

            ILocator status = page.Locator("[data-probe-status]");
            await Assertions.Expect(status).ToHaveTextAsync("engine ready", new() { Timeout = BootTimeoutMs });
            Log("the module, its Web Worker and the worker's importScripts loaded.");
            await page.WaitForFunctionAsync(
                "() => { const image = document.querySelector('[data-probe-image]'); return !!image && image.complete && image.naturalWidth > 0; }",
                null, new PageWaitForFunctionOptions { Timeout = StepTimeoutMs });
            Log("the image loaded.");

            // --- ADR 0221: undeclared files packed by KazoOsPack, and the scoped CSS linked ---
            await Assertions.Expect(page.Locator("[data-probe-collocated]")).ToHaveTextAsync("collocated ready", new() { Timeout = StepTimeoutMs });
            Log("the collocated ProbeWindow.razor.js module loaded.");
            await page.WaitForFunctionAsync(
                "() => { const probe = document.querySelector('.probe'); return !!probe && getComputedStyle(probe).display === 'flex'; }",
                null, new PageWaitForFunctionOptions { Timeout = StepTimeoutMs });
            Log("the scoped CSS of the installed app applies.");

            // --- ADR 0222: the satellite assembly lib/fr/<assembly>.resources.dll, loaded by the OS ---
            await Assertions.Expect(page.Locator("[data-probe-french]")).ToHaveTextAsync(FrenchGreeting, new() { Timeout = StepTimeoutMs });
            Log("the French satellite assembly of the installed app loaded.");

            // --- ADR 0261 §1: the library the package carries in lib/, loaded at boot too ---
            await Assertions.Expect(page.Locator("[data-probe-library]")).ToHaveTextAsync(LibraryAnswer, new() { Timeout = StepTimeoutMs });
            Log("the library of the installed app loaded after a restart.");

            string worker = await page.EvaluateAsync<string>("() => navigator.serviceWorker.controller ? navigator.serviceWorker.controller.scriptURL : ''");
            Assert.Contains("mode=installed-apps", worker, StringComparison.Ordinal);
            string type = await page.EvaluateAsync<string>($"async () => (await fetch('{ProbeContent}engine/engine.js')).headers.get('Content-Type')");
            Assert.Equal("text/javascript", type);
            session.AssertNoErrors("installed app");
            await CloseAllWindowsAsync(page);
        }
        catch
        {
            await SaveScreenshotAsync(page, settings, "installed-app");
            throw;
        }
    }

    /// <summary>
    /// Issue #420: the icon the launcher, the Start menu and the desktop ask for answers 200 before
    /// the app is opened in the session.
    /// </summary>
    private static async Task WaitForProbeIconAsync(IPage page) =>
        await page.WaitForFunctionAsync(
            $"async () => (await fetch('{ProbeContent}images/dot.svg', {{ cache: 'no-store' }})).status === 200",
            null,
            new PageWaitForFunctionOptions { Timeout = StepTimeoutMs, PollingInterval = 500 });

    /// <summary>
    /// The published service worker (on the address where index.html registers it) answers a file the
    /// OS serves from the file system's content (ADR 0274), before its release cache, with a Range
    /// request, and for a Web
    /// Worker started under cross-origin isolation (ADR 0131).
    /// </summary>
    [TrimmedPublishFact]
    public async Task The_published_service_worker_serves_installed_app_files_to_a_worker()
    {
        TrimmedPublishSettings settings = TrimmedPublishSettings.FromEnvironment()!;
        if (settings.PwaUrl is null)
        {
            Log($"{TrimmedPublishSettings.PwaUrlVariable} is not set: no service worker, published worker check skipped.");
            return;
        }

        await using Session session = await Session.StartAsync(output);
        IPage page = session.Page;
        try
        {
            await NavigateWhenReadyAsync(page, settings.PwaUrl);
            await WaitForServiceWorkerControllerAsync(page);
            await SignInAsync(page);
            await page.EvaluateAsync(ServedFileHelpersScript);

            string answer = await page.EvaluateAsync<string>(
                """
                async content => {
                    const assets = await import('./_content/Kazo.Os.Infrastructure.Browser/installedAppAssets.js');
                    if (!await assets.prepare(10000)) return 'no service worker';
                    const files = window.kazoTestServedFiles;
                    await files.put(content + 'engine/engine.js', "self.engineAnswer = () => 'served';", 'text/javascript');
                    await files.put(content + 'engine/worker.js', "importScripts('engine.js'); self.onmessage = () => self.postMessage(self.engineAnswer());", 'text/javascript');
                    try {
                        // A Range request gets only the bytes it asks for (ADR 0274 §5).
                        const partial = await fetch(content + 'engine/engine.js', { headers: { Range: 'bytes=5-16' } });
                        const range = `${partial.status} ${partial.headers.get('Content-Range')} ${await partial.text()}`;
                        if (range !== '206 bytes 5-16/35 engineAnswer') return `range: ${range}`;
                        return await new Promise((resolve, reject) => {
                            const worker = new Worker(content + 'engine/worker.js');
                            worker.onmessage = event => { worker.terminate(); resolve(event.data); };
                            worker.onerror = () => { worker.terminate(); reject(new Error('worker failed')); };
                            worker.postMessage('ping');
                        });
                    } finally {
                        await files.remove(content + 'engine/engine.js');
                        await files.remove(content + 'engine/worker.js');
                    }
                }
                """, ProbeContent);

            Assert.Equal("served", answer);
            Log($"the published service worker served a worker and its importScripts (crossOriginIsolated={await page.EvaluateAsync<bool>("() => crossOriginIsolated")}).");
            session.AssertNoErrors("published service worker");
        }
        catch
        {
            await SaveScreenshotAsync(page, settings, "published-worker");
            throw;
        }
    }

    /// <summary>
    /// Answers the GitHub addresses of a test package of <paramref name="repository"/> from
    /// <paramref name="directory"/>, as <c>raw.githubusercontent.com</c> would (with
    /// <c>Access-Control-Allow-Origin: *</c>).
    /// </summary>
    private async Task ServePackagesLocallyAsync(IBrowserContext context, string repository, string directory)
    {
        string prefix = $"/{repository}/kazoos-packages/";
        string root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        await context.RouteAsync(new Regex("^" + Regex.Escape("https://raw.githubusercontent.com" + prefix)), async route =>
        {
            string relative = Uri.UnescapeDataString(new Uri(route.Request.Url).AbsolutePath)[prefix.Length..];
            string path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(root, StringComparison.Ordinal) || !File.Exists(path))
            {
                await route.FulfillAsync(new RouteFulfillOptions { Status = 404, Headers = new Dictionary<string, string> { ["Access-Control-Allow-Origin"] = "*" } });
                return;
            }

            Log($"served {relative} locally.");
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                BodyBytes = await File.ReadAllBytesAsync(path),
                Headers = new Dictionary<string, string> { ["Access-Control-Allow-Origin"] = "*", ["Content-Type"] = "application/octet-stream" },
            });
        });
    }

    /// <summary>
    /// Answers the administrator prompt <c>sudo</c> opens (the credential dialog of the shell, or the
    /// OS authentication agent of ADR 0055 §5) with the administrator's login and password.
    /// </summary>
    private async Task ConfirmAsAdministratorAsync(IPage page)
    {
        ILocator prompt = page.Locator(".credential-prompt-dialog, .aaa-window").First;
        await prompt.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = StepTimeoutMs });
        ILocator login = prompt.Locator("input[type=text]");
        if (await login.CountAsync() > 0 && string.IsNullOrEmpty(await login.InputValueAsync()))
        {
            await login.FillAsync(LoginName);
        }

        ILocator password = prompt.Locator("input[type=password]");
        await password.FillAsync(Password);
        await password.PressAsync("Enter");
        await prompt.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = StepTimeoutMs });
        Log("the administrator confirmed.");
    }
}
