using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Apps.Packages;
using Kazo.Os.Platform.Core.Tests.Apps.Packages;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Tests.Execution;

/// <summary>
/// The Store apps proposed for a file no installed app opens (ADR 0257 §2), read from the catalogs
/// the OS downloaded: the sample app of the official catalog declares <c>.sample</c>.
/// </summary>
public sealed partial class AppPackageGatewayTests
{
    private static readonly VirtualPath SampleFile = VirtualPath.Parse("/home/alice/notes.sample");

    [Fact]
    public async Task Nothing_is_proposed_and_nothing_is_downloaded_before_the_catalog_was_read()
    {
        Harness harness = await Harness.CreateAsync();
        harness.PublishCatalog("1.0.0");
        AuthenticatedPrincipal alice = await harness.Settings.LoginAsync(harness.Settings.Alice);
        CatalogAppSuggestions suggestions = new(harness.Installer, () => harness.Settings.CreateRootGateway(), harness.Catalog);

        IReadOnlyList<AppIntentSuggestedCatalogApp> apps = await suggestions.FindAsync(SampleFile, null, "open", alice);

        Assert.Empty(apps);
        Assert.Empty(harness.Downloads);
    }

    [Fact]
    public async Task The_update_check_keeps_the_catalog_so_its_apps_are_proposed()
    {
        Harness harness = await Harness.CreateAsync();
        harness.PublishCatalog("1.0.0");
        AuthenticatedPrincipal alice = await harness.Settings.LoginAsync(harness.Settings.Alice);
        using AppUpdateMonitor monitor = new(harness.Installer, () => harness.Settings.CreateRootGateway(), () => alice);
        CatalogAppSuggestions suggestions = new(harness.Installer, () => harness.Settings.CreateRootGateway(), harness.Catalog);

        await monitor.CheckNowAsync();
        harness.Now += AppPackageInstaller.CatalogCacheLifetime * 100;
        int downloads = harness.Downloads.Count;
        IReadOnlyList<AppIntentSuggestedCatalogApp> apps = await suggestions.FindAsync(SampleFile, null, "open", alice);

        AppIntentSuggestedCatalogApp sample = Assert.Single(apps);
        Assert.Equal((AppPackageTests.AppId, "Sample", AppSourcesConfig.OfficialCatalogName), (sample.AppId, sample.Name, sample.Catalog));
        Assert.Equal("A sample", sample.ShortDescriptions["en"]);
        Assert.Equal($"https://raw.githubusercontent.com/Kazo-ca/Os-AppMarketPlace/packages/packages/{AppPackageTests.AppId}/1.0.0/icon.svg", sample.IconUri);
        Assert.Equal(downloads, harness.Downloads.Count);
        Assert.Empty(await suggestions.FindAsync(VirtualPath.Parse("/home/alice/notes.other"), null, "open", alice));
    }

    [Fact]
    public async Task An_app_the_user_installed_is_not_proposed_again()
    {
        Harness harness = await Harness.CreateAsync();
        harness.PublishCatalog("1.0.0");
        CountingAgent agent = new(succeed: true);
        await (await harness.CreateGatewayAsync(agent)).InstallPackageAsync(AppPackageTests.AppId);
        AuthenticatedPrincipal alice = harness.Settings.Session.CurrentPrincipal!;
        CatalogAppSuggestions suggestions = new(harness.Installer, () => harness.Settings.CreateRootGateway(), harness.Catalog);

        Assert.Empty(await suggestions.FindAsync(SampleFile, null, "open", alice));
        Assert.Single(await suggestions.FindAsync(SampleFile, null, "open", user: null));
    }
}
