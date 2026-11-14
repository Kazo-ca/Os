using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Apps.Packages;

namespace Kazo.Os.Platform.Core.Tests.Apps.Packages;

/// <summary>
/// The channel of the official catalog follows the OS (ADR 0271 §6), and the version offered is the
/// newest one this OS runs (release channels, chantier 3).
/// </summary>
public sealed class ReleaseChannelTests
{
    private static readonly SemanticVersion Stable = SemanticVersion.Parse("3.4.0");
    private static readonly SemanticVersion Preview = SemanticVersion.Parse("3.4.0-preview.1");
    private static readonly Uri StableCatalog = new(AppPackageDefaults.OfficialCatalogAddress);
    private static readonly Uri BetaCatalog = new(AppPackageDefaults.OfficialBetaCatalogAddress);

    private static AppPackageDefaults DefaultsFor(SemanticVersion sdk) => AppPackageDefaults.Standard with
    {
        Catalogs = [new(AppSourcesConfig.OfficialCatalogName, new Uri(AppPackageDefaults.OfficialCatalogAddressFor(sdk)))],
    };

    [Fact]
    public void A_stable_os_reads_catalog_json_and_a_prerelease_reads_catalog_beta_json()
    {
        Assert.Equal(AppPackageDefaults.OfficialCatalogAddress, AppPackageDefaults.OfficialCatalogAddressFor(Stable));
        Assert.Equal(AppPackageDefaults.OfficialBetaCatalogAddress, AppPackageDefaults.OfficialCatalogAddressFor(Preview));
        Assert.EndsWith("/packages/catalog-beta.json", AppPackageDefaults.OfficialBetaCatalogAddress, StringComparison.Ordinal);
    }

    [Fact]
    public void The_standard_defaults_follow_the_channel_of_this_build()
    {
        Assert.Equal(
            new Uri(AppPackageDefaults.OfficialCatalogAddressFor(KazoOsSdk.SemanticVersion)),
            Assert.Single(AppPackageDefaults.Standard.Catalogs).Value);
    }

    [Fact]
    public void A_file_written_by_a_stable_os_reads_the_beta_catalog_on_a_prerelease()
    {
        string written = AppSourcesConfigDocument.BuildDefaultContent(DefaultsFor(Stable));

        AppSourcesConfig config = AppSourcesConfigDocument.Read(written, DefaultsFor(Preview));

        Assert.Equal(BetaCatalog, config.Catalogs["official"]);
        Assert.Equal(AppSourceTrust.Verified, config.GetCatalogTrust("official"));
    }

    [Fact]
    public void A_file_written_by_a_prerelease_reads_the_stable_catalog_on_a_stable_os()
    {
        string written = AppSourcesConfigDocument.BuildDefaultContent(DefaultsFor(Preview));

        AppSourcesConfig config = AppSourcesConfigDocument.Read(written, DefaultsFor(Stable));

        Assert.Equal(StableCatalog, config.Catalogs["official"]);
    }

    [Fact]
    public void Another_catalog_and_a_custom_official_address_are_kept_as_written()
    {
        const string Mirror = "https://example.org/mirror/catalog.json";
        const string Custom = "https://example.org/kazo/catalog.json";
        string written = $"[catalogs]\nofficial={Custom}\nmirror={Mirror}\nold-official={AppPackageDefaults.OfficialCatalogAddress}\n";

        AppSourcesConfig config = AppSourcesConfigDocument.Read(written, DefaultsFor(Preview));

        Assert.Equal(new Uri(Custom), config.Catalogs["official"]);
        Assert.Equal(new Uri(Mirror), config.Catalogs["mirror"]);
        // A second name for the official catalog follows the channel too: it is the same catalog.
        Assert.Equal(BetaCatalog, config.Catalogs["old-official"]);
    }

    [Fact]
    public void A_host_built_with_its_own_catalog_leaves_the_official_addresses_alone()
    {
        AppPackageDefaults host = AppPackageDefaults.Standard with { Catalogs = [new("company", new Uri("https://example.org/catalog.json"))] };

        AppSourcesConfig config = AppSourcesConfigDocument.Read($"[catalogs]\nofficial={AppPackageDefaults.OfficialBetaCatalogAddress}\n", host);

        Assert.Equal(BetaCatalog, config.Catalogs["official"]);
    }

    private static AppPackageIndex Index(params (string Version, string? MinimumSdk)[] versions) => new(
        "org.example.app",
        [.. versions.Select(static item => new AppPackageIndexEntry(SemanticVersion.Parse(item.Version), $"{item.Version}/app.kazoapp", new string('a', 64))
        {
            MinimumSdkVersion = item.MinimumSdk,
        })]);

    [Fact]
    public void The_newest_version_this_sdk_runs_is_offered()
    {
        AppPackageIndex index = Index(("1.2.0", "3.5.0"), ("1.1.0", "3.4.0"), ("1.0.0", "3.0.0"));

        Assert.Equal("1.1.0", AppVersionChoice.Offered(index, Stable)?.Version.ToString());
        Assert.Equal("1.0.0", AppVersionChoice.Offered(index, SemanticVersion.Parse("3.3.4"))?.Version.ToString());
    }

    [Fact]
    public void A_stable_os_offers_stable_versions_only()
    {
        AppPackageIndex index = Index(("1.1.0-beta.1", "3.4.0"), ("1.0.0", "3.0.0"));

        Assert.Equal("1.0.0", AppVersionChoice.Offered(index, Stable)?.Version.ToString());
    }

    [Fact]
    public void A_prerelease_os_offers_the_prereleases_it_runs()
    {
        AppPackageIndex index = Index(("1.0.1-beta.2", "3.4.0-preview.2"), ("1.0.1-beta.1", "3.4.0-preview.1"), ("1.0.0", "3.3.4"));

        Assert.Equal("1.0.1-beta.1", AppVersionChoice.Offered(index, Preview)?.Version.ToString());
        Assert.Equal("1.0.1-beta.2", AppVersionChoice.Offered(index, SemanticVersion.Parse("3.4.0-preview.2"))?.Version.ToString());
    }

    [Fact]
    public void A_stable_release_replaces_the_prerelease_it_follows()
    {
        AppPackageIndex index = Index(("1.0.1", "3.4.0-preview.1"), ("1.0.1-beta.1", "3.4.0-preview.1"));

        AppPackageIndexEntry? update = AppVersionChoice.Newest(index, Preview, SemanticVersion.Parse("1.0.1-beta.1"));

        Assert.Equal("1.0.1", update?.Version.ToString());
    }

    [Fact]
    public void An_installed_prerelease_keeps_following_prereleases_on_a_stable_os()
    {
        AppPackageIndex index = Index(("1.1.0-beta.2", null), ("1.1.0-beta.1", null), ("1.0.0", null));

        Assert.Equal("1.1.0-beta.2", AppVersionChoice.Newest(index, Stable, SemanticVersion.Parse("1.1.0-beta.1"))?.Version.ToString());
        Assert.Equal("1.0.0", AppVersionChoice.Newest(index, Stable, SemanticVersion.Parse("1.0.0"))?.Version.ToString());
    }

    [Fact]
    public void An_app_with_only_prereleases_offers_them()
    {
        AppPackageIndex index = Index(("0.2.0-alpha", null), ("0.1.0-alpha", null));

        Assert.Equal("0.2.0-alpha", AppVersionChoice.Offered(index, Stable)?.Version.ToString());
    }

    [Fact]
    public void A_version_of_unknown_range_counts_as_compatible()
    {
        AppPackageIndex index = Index(("2.0.0", null), ("1.0.0", "3.0.0"));

        Assert.Equal("2.0.0", AppVersionChoice.Offered(index, Stable)?.Version.ToString());
    }

    [Fact]
    public void When_no_version_runs_the_newest_stable_is_offered_for_the_install_to_refuse()
    {
        AppPackageIndex index = Index(("2.0.0", "4.0.0"), ("1.0.0", "3.5.0"));

        Assert.Null(AppVersionChoice.Newest(index, Stable));
        Assert.Equal("2.0.0", AppVersionChoice.Offered(index, Stable)?.Version.ToString());
    }

    [Fact]
    public void A_maximum_sdk_excludes_a_newer_os()
    {
        AppPackageIndex index = Index(("1.0.0", "3.0.0"));
        AppPackageIndex bounded = new(index.AppId, [index.Versions[0] with { MaximumSdkVersion = "3.3.9" }]);

        Assert.Null(AppVersionChoice.Newest(bounded, Stable));
    }
}
