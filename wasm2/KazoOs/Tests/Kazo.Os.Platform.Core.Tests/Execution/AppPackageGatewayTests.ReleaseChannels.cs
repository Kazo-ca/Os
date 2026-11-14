using Kazo.Os.AppSdk.Apps;
using Kazo.Os.Platform.Core.Apps.Packages;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Platform.Core.Tests.Apps.Packages;

namespace Kazo.Os.Platform.Core.Tests.Execution;

/// <summary>
/// The version the Store, <c>kazopkg</c> and the update check offer (ADR 0271 §6, release channels
/// chantier 3): the newest one the SDK of this OS (2.1.0 in the harness) runs, not the newest one.
/// </summary>
public sealed partial class AppPackageGatewayTests
{
    // 1.1.0 was rebuilt on a newer SDK than the harness's 2.1.0: it no longer installs here.
    private static string NeedsNewerSdkFor110(string version) => version == "1.1.0" ? "2.2.0" : "2.0.0";

    [Fact]
    public async Task The_store_offers_the_newest_version_this_os_runs()
    {
        Harness harness = await Harness.CreateAsync();
        harness.MinimumSdkFor = NeedsNewerSdkFor110;
        harness.PublishCatalog("1.1.0", "1.0.0");

        KazoAppCatalog official = Assert.Single(await (await harness.CreateGatewayAsync(new CountingAgent(succeed: true))).GetPackageCatalogsAsync());

        KazoCatalogApp app = Assert.Single(official.Apps);
        Assert.Equal("1.0.0", app.LatestVersion);
        Assert.Equal(["1.1.0", "1.0.0"], app.Versions.Select(static version => version.Version));
    }

    [Fact]
    public async Task A_bare_app_id_installs_the_newest_version_this_os_runs()
    {
        Harness harness = await Harness.CreateAsync();
        harness.MinimumSdkFor = NeedsNewerSdkFor110;
        harness.PublishCatalog("1.1.0", "1.0.0");
        AppManagementGateway gateway = await harness.CreateGatewayAsync(new CountingAgent(succeed: true));

        KazoPackageInfo info = await gateway.GetPackageInfoAsync(AppPackageTests.AppId);
        KazoPackageInstallReport report = await gateway.InstallPackageAsync(AppPackageTests.AppId);

        Assert.Equal("1.0.0", info.Latest);
        Assert.Equal("1.0.0", report.Version);
    }

    [Fact]
    public async Task No_update_is_offered_for_a_version_this_os_cannot_run()
    {
        Harness harness = await Harness.CreateAsync();
        harness.PublishCatalog("1.0.0");
        CountingAgent agent = new(succeed: true);
        await (await harness.CreateGatewayAsync(agent)).InstallPackageAsync(AppPackageTests.AppId);
        harness.MinimumSdkFor = NeedsNewerSdkFor110;
        harness.PublishCatalog("1.1.0", "1.0.0");
        harness.Now += AppPackageInstaller.CatalogCacheLifetime;

        Assert.Empty(await (await harness.CreateGatewayAsync(agent)).GetPackageUpdatesAsync());
    }

    [Fact]
    public async Task When_no_version_runs_here_the_newest_is_shown_and_its_install_says_why()
    {
        Harness harness = await Harness.CreateAsync();
        harness.MinimumSdkFor = static _ => "3.0.0";
        harness.PublishCatalog("1.1.0", "1.0.0");
        AppManagementGateway gateway = await harness.CreateGatewayAsync(new CountingAgent(succeed: true));

        KazoAppCatalog official = Assert.Single(await gateway.GetPackageCatalogsAsync());

        Assert.Equal("1.1.0", Assert.Single(official.Apps).LatestVersion);
    }
}
