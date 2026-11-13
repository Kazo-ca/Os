using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Kazo.Os.TrimmedPublish.E2E.Tests;

/// <summary>
/// ADR 0258 (direct app launch link <c>?autolaunch=&lt;appId&gt;</c>) on the trimmed publish: the
/// whole journey in a real browser, with the official catalog answered locally.
/// </summary>
/// <remarks>
/// <para>
/// The official catalog address (<see cref="OfficialCatalogAddress"/>) is answered by a Playwright
/// route with a <c>catalog.json</c> built here: it lists the test package of ADR 0206
/// (<c>Tests/fixtures/installed-app</c>) as an app of <c>catalog:official</c>, signed with a P-256
/// key that <c>build/publish-trimmed.sh</c> creates for the run. The publish under test trusts the
/// public half of that key for <c>catalog:official</c>, next to the real key
/// (<c>-p:KazoOsAppSigningKeys</c>, ADR 0214 §2), so the install goes through the same signature check
/// as a real one. No private key is ever committed.
/// </para>
/// <para>
/// Each test starts a new browser context, so a new machine: no account until a test makes one.
/// </para>
/// </remarks>
public sealed partial class TrimmedPublishTests
{
    /// <summary>The address of the official catalog the published OS reads (ADR 0188).</summary>
    private const string OfficialCatalogAddress = "https://raw.githubusercontent.com/Kazo-ca/Os-AppMarketPlace/packages/catalog.json";

    /// <summary>The app id of the test package (its <c>manifest.json</c>).</summary>
    private const string ProbeAppId = "org.kazoos.fixtures.installed-assets";

    /// <summary>A well-formed id the official catalog does not list.</summary>
    private const string MissingAppId = "org.kazoos.fixtures.not-in-catalog";

    /// <summary>Folder of the catalog, next to <c>catalog.json</c>, where its packages are served from.</summary>
    private const string CatalogPackagesFolder = "apps/";

    /// <summary>
    /// ADR 0258 §3, Q3 and §2: with no account, the link starts the guest without a click, installs
    /// the app from the official catalog, launches it, and leaves the address without the
    /// parameter. A reload does nothing again (the guest's usual screen shows). A second visit with
    /// the link continues as the guest without a click and launches the app without downloading it
    /// again.
    /// </summary>
    [TrimmedPublishFact]
    public async Task A_direct_link_with_no_account_starts_the_guest_installs_and_launches_the_app()
    {
        TrimmedPublishSettings settings = TrimmedPublishSettings.FromEnvironment()!;
        if (!CanServeOfficialCatalog(settings))
        {
            return;
        }

        await using Session session = await Session.StartAsync(output);
        IPage page = session.Page;
        OfficialCatalogRoute catalog = await ServeOfficialCatalogAsync(session.Context, settings);
        try
        {
            // --- First visit: guest, install, launch ---
            await NavigateWhenReadyAsync(page, LinkAddress(settings, ProbeAppId));
            ILocator probe = page.Locator("[data-probe-status]");
            await Assertions.Expect(probe).ToHaveTextAsync("engine ready", new() { Timeout = BootTimeoutMs });
            Log("the link started the guest, installed the app and launched it.");

            Assert.Equal(1, catalog.PackageDownloads);
            Assert.DoesNotContain("autolaunch", page.Url, StringComparison.Ordinal);
            await Assertions.Expect(page.Locator(".app-link-progress")).ToHaveCountAsync(0, new() { Timeout = StepTimeoutMs });
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Create administrator" })).ToHaveCountAsync(0);
            await Assertions.Expect(ErrorNotification(page)).ToHaveCountAsync(0);
            Log($"address after the link: {page.Url}");
            session.AssertNoErrors("first visit");
            await CloseAllWindowsAsync(page);

            // --- Reload: the parameter is gone, the guest's usual sign-in screen shows ---
            await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            ILocator continueAsGuest = page.GetByRole(AriaRole.Button, new() { Name = "Continue as Guest" });
            await continueAsGuest.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = BootTimeoutMs });
            await page.WaitForTimeoutAsync(2_000);
            Assert.True(await continueAsGuest.IsVisibleAsync(), "A reload must not run the link again.");
            await Assertions.Expect(probe).ToHaveCountAsync(0);
            Log("a reload shows the guest's sign-in screen and runs nothing.");

            // --- Second visit with the link: the guest continues by itself, no new download ---
            await page.GotoAsync(LinkAddress(settings, ProbeAppId), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await Assertions.Expect(probe).ToHaveTextAsync("engine ready", new() { Timeout = BootTimeoutMs });
            Assert.Equal(1, catalog.PackageDownloads);
            Assert.DoesNotContain("autolaunch", page.Url, StringComparison.Ordinal);
            Log("the lone guest continued by itself and the app opened without a new download.");
            session.AssertNoErrors("second visit");
            await CloseAllWindowsAsync(page);
        }
        catch
        {
            await SaveScreenshotAsync(page, settings, "app-link-guest");
            throw;
        }
    }

    /// <summary>
    /// ADR 0258 §4: when an account exists, the sign-in screen says which app the link names, and
    /// after sign-in the Kazo OS Store opens at the app's page; nothing is downloaded or installed.
    /// </summary>
    [TrimmedPublishFact]
    public async Task A_direct_link_with_an_account_opens_the_store_at_the_app_page()
    {
        TrimmedPublishSettings settings = TrimmedPublishSettings.FromEnvironment()!;
        if (!CanServeOfficialCatalog(settings))
        {
            return;
        }

        await using Session session = await Session.StartAsync(output);
        IPage page = session.Page;
        OfficialCatalogRoute catalog = await ServeOfficialCatalogAsync(session.Context, settings);
        try
        {
            await NavigateWhenReadyAsync(page, settings.Url);
            await SignInAsync(page);
            await CloseAllWindowsAsync(page);

            await page.GotoAsync(LinkAddress(settings, ProbeAppId), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            ILocator notice = page.Locator(".app-link-notice");
            await Assertions.Expect(notice).ToContainTextAsync(ProbeAppId, new() { Timeout = BootTimeoutMs });
            Assert.DoesNotContain("autolaunch", page.Url, StringComparison.Ordinal);
            Log($"the sign-in screen says: {await notice.InnerTextAsync()}");

            await SignInAsync(page);
            ILocator detail = page.Locator("article.store-detail");
            await Assertions.Expect(detail.GetByRole(AriaRole.Heading, new() { Name = ProbeAppName })).ToBeVisibleAsync(new() { Timeout = StepTimeoutMs });
            Log("the Kazo OS Store opened at the app's page after sign-in.");

            Assert.Equal(0, catalog.PackageDownloads);
            await Assertions.Expect(page.Locator("[data-probe-status]")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator(".app-link-progress")).ToHaveCountAsync(0);
            session.AssertNoErrors("account");
            await CloseAllWindowsAsync(page);
        }
        catch
        {
            await SaveScreenshotAsync(page, settings, "app-link-account");
            throw;
        }
    }

    /// <summary>
    /// ADR 0258 §5: an app the official catalog does not offer ends with a notification whose button
    /// opens the Kazo OS Store; « Cancel » of the progress window stops the download with no
    /// notification and no app.
    /// </summary>
    [TrimmedPublishFact]
    public async Task A_direct_link_reports_a_missing_app_and_can_be_cancelled()
    {
        TrimmedPublishSettings settings = TrimmedPublishSettings.FromEnvironment()!;
        if (!CanServeOfficialCatalog(settings))
        {
            return;
        }

        await using Session session = await Session.StartAsync(output);
        IPage page = session.Page;
        OfficialCatalogRoute catalog = await ServeOfficialCatalogAsync(session.Context, settings);
        try
        {
            // --- Missing app: the guest starts, a notification explains, its button opens the Store ---
            await NavigateWhenReadyAsync(page, LinkAddress(settings, MissingAppId));
            ILocator failure = ErrorNotification(page);
            await Assertions.Expect(failure).ToHaveCountAsync(1, new() { Timeout = BootTimeoutMs });
            await Assertions.Expect(failure).ToContainTextAsync(MissingAppId);
            Log($"the notification says: {await failure.InnerTextAsync()}");
            await failure.GetByRole(AriaRole.Button, new() { Name = "Open the Kazo OS Store" }).ClickAsync();
            await Assertions.Expect(page.Locator(".store-root")).ToBeVisibleAsync(new() { Timeout = StepTimeoutMs });
            Log("its button opened the Kazo OS Store.");
            Assert.Equal(0, catalog.PackageDownloads);
            session.AssertNoErrors("missing app");
            await CloseAllWindowsAsync(page);

            // --- Cancel: the download is held until « Cancel » ---
            catalog.HoldPackage();
            await page.GotoAsync(LinkAddress(settings, ProbeAppId), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            ILocator progress = page.Locator($".app-link-progress[data-app-id='{ProbeAppId}']");
            await Assertions.Expect(progress).ToContainTextAsync(ProbeAppName, new() { Timeout = BootTimeoutMs });
            await catalog.PackageRequested.WaitAsync(TimeSpan.FromMilliseconds(StepTimeoutMs));
            await progress.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
            await Assertions.Expect(progress).ToHaveCountAsync(0, new() { Timeout = StepTimeoutMs });
            catalog.ReleasePackage();
            await page.WaitForTimeoutAsync(2_000);
            await Assertions.Expect(page.Locator("[data-probe-status]")).ToHaveCountAsync(0);
            await Assertions.Expect(ErrorNotification(page)).ToHaveCountAsync(0);
            Assert.Equal(0, catalog.PackageDownloads);
            Log("Cancel closed the progress window: no app, no notification.");
            session.AssertNoErrors("cancel");
        }
        catch
        {
            await SaveScreenshotAsync(page, settings, "app-link-failure");
            throw;
        }
    }

    /// <summary>The address of the published OS with the link of <paramref name="appId"/>.</summary>
    private static string LinkAddress(TrimmedPublishSettings settings, string appId) => $"{settings.Url}/?autolaunch={appId}";

    /// <summary>The bubble of the failure notification of a link (« The app could not be opened »).</summary>
    private static ILocator ErrorNotification(IPage page) =>
        page.GetByRole(AriaRole.Region, new() { Name = "New notifications" })
            .Locator("article.notification-toast")
            .Filter(new() { HasText = "The app could not be opened" });

    private bool CanServeOfficialCatalog(TrimmedPublishSettings settings)
    {
        if (settings.PackagesDirectory is null || settings.SigningKeyPath is null)
        {
            Log($"{TrimmedPublishSettings.PackagesVariable} or {TrimmedPublishSettings.SigningKeyVariable} is not set: direct link check skipped.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Answers the official catalog with the test package, signed with the run's key. Every other
    /// address of the catalog repository answers 404.
    /// </summary>
    private async Task<OfficialCatalogRoute> ServeOfficialCatalogAsync(IBrowserContext context, TrimmedPublishSettings settings)
    {
        OfficialCatalogRoute catalog = OfficialCatalogRoute.Create(settings.PackagesDirectory!, settings.SigningKeyPath!);
        string root = OfficialCatalogAddress[..(OfficialCatalogAddress.LastIndexOf('/') + 1)];
        string rootPath = new Uri(root).AbsolutePath;
        await context.RouteAsync(new Regex("^" + Regex.Escape(root)), async route =>
        {
            string relative = Uri.UnescapeDataString(new Uri(route.Request.Url).AbsolutePath)[rootPath.Length..];
            if (relative == "catalog.json")
            {
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 200,
                    Body = catalog.CatalogJson,
                    Headers = new Dictionary<string, string> { ["Access-Control-Allow-Origin"] = "*", ["Content-Type"] = "application/json" },
                });
                return;
            }

            if (relative == CatalogPackagesFolder + catalog.PackagePath)
            {
                Log($"served {relative} locally.");
                await catalog.ServePackageAsync(route);
                return;
            }

            Log($"{relative} is not in the test catalog: 404.");
            await route.FulfillAsync(new RouteFulfillOptions { Status = 404, Headers = new Dictionary<string, string> { ["Access-Control-Allow-Origin"] = "*" } });
        });
        return catalog;
    }

    /// <summary>
    /// The test catalog: one app, the test package, signed with the run's key. Counts the package
    /// downloads, and can hold the next one until the test releases it.
    /// </summary>
    private sealed class OfficialCatalogRoute
    {
        private readonly byte[] _package;
        private TaskCompletionSource? _hold;
        private TaskCompletionSource _requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _downloads;

        private OfficialCatalogRoute(string catalogJson, string packagePath, byte[] package)
        {
            CatalogJson = catalogJson;
            PackagePath = packagePath;
            _package = package;
        }

        /// <summary>The <c>catalog.json</c> served at the official address.</summary>
        public string CatalogJson { get; }

        /// <summary>The package path of <c>index.json</c>, served under <see cref="CatalogPackagesFolder"/>.</summary>
        public string PackagePath { get; }

        /// <summary>The packages actually delivered to the browser.</summary>
        public int PackageDownloads => Volatile.Read(ref _downloads);

        /// <summary>Completes when a held package is requested.</summary>
        public Task PackageRequested => _requested.Task;

        /// <summary>Builds <c>catalog.json</c> from the test package's <c>index.json</c> and signs the package.</summary>
        public static OfficialCatalogRoute Create(string packagesDirectory, string signingKeyPath)
        {
            using JsonDocument index = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(packagesDirectory, "index.json")));
            JsonElement latest = index.RootElement.GetProperty("versions")[0];
            string version = latest.GetProperty("version").GetString()!;
            string path = latest.GetProperty("path").GetString()!;
            byte[] package = File.ReadAllBytes(Path.Combine(packagesDirectory, path));

            // ADR 0214 §2: ECDSA P-256 with SHA-256 over the package bytes, IEEE P1363 (r||s), base64.
            using ECDsa key = ECDsa.Create();
            key.ImportFromPem(File.ReadAllText(signingKeyPath));
            byte[] signature = key.SignData(package, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

            JsonObject catalog = new()
            {
                ["schemaVersion"] = 1,
                ["categories"] = new JsonArray(new JsonObject
                {
                    ["id"] = "utilities",
                    ["labels"] = new JsonObject { ["en"] = "Utilities", ["fr"] = "Utilitaires" },
                }),
                ["apps"] = new JsonArray(new JsonObject
                {
                    ["appId"] = ProbeAppId,
                    ["name"] = ProbeAppName,
                    ["category"] = "utilities",
                    ["tags"] = new JsonArray(),
                    ["shortDescriptions"] = new JsonObject { ["en"] = "Test package of the direct link journey." },
                    ["license"] = "MIT",
                    ["repository"] = $"https://github.com/{ProbeRepository}",
                    ["latestVersion"] = version,
                    ["versions"] = new JsonArray(new JsonObject
                    {
                        ["version"] = version,
                        ["commit"] = "0000000000000000000000000000000000000000",
                        ["package"] = CatalogPackagesFolder + path,
                        ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(package)),
                        ["size"] = package.Length,
                        ["signature"] = Convert.ToBase64String(signature),
                    }),
                }),
            };
            return new OfficialCatalogRoute(catalog.ToJsonString(), path, package);
        }

        /// <summary>Holds the next package request until <see cref="ReleasePackage"/>.</summary>
        public void HoldPackage()
        {
            _requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>Ends a held request with a network failure, as a lost connection would.</summary>
        public void ReleasePackage() => _hold?.TrySetResult();

        /// <summary>Answers a package request: held and then aborted, or delivered and counted.</summary>
        public async Task ServePackageAsync(IRoute route)
        {
            if (_hold is { } hold)
            {
                _requested.TrySetResult();
                await hold.Task;
                _hold = null;
                try
                {
                    await route.AbortAsync();
                }
                catch (PlaywrightException)
                {
                    // The browser already dropped the request when « Cancel » aborted it.
                }

                return;
            }

            Interlocked.Increment(ref _downloads);
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                BodyBytes = _package,
                Headers = new Dictionary<string, string> { ["Access-Control-Allow-Origin"] = "*", ["Content-Type"] = "application/octet-stream" },
            });
        }
    }
}
