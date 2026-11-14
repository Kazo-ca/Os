using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Apps.Packages;

namespace Kazo.Os.Platform.Core.Tests.Apps.Packages;

/// <summary>
/// The app repositories renamed with Kazo OS 3.0.0 (ADR 0228): HackerOs-AppMarketPlace and
/// HackerOs-App-* became Os-AppMarketPlace and Os-App-*, but the rename migration of the profile
/// (ADR 0230) wrote KazoOs-AppMarketPlace and KazoOs-App-*, which do not exist.
/// </summary>
public sealed class RenamedAppRepositoriesTests
{
    // What a profile created before 3.0.0 holds after the rename migration, and before it.
    private const string MigratedCatalog = "https://raw.githubusercontent.com/Kazo-ca/KazoOs-AppMarketPlace/packages/catalog.json";
    private const string OldCatalog = "https://raw.githubusercontent.com/Kazo-ca/HackerOs-AppMarketPlace/packages/catalog.json";

    [Theory]
    [InlineData(MigratedCatalog)]
    [InlineData(OldCatalog)]
    public void A_file_written_before_3_0_0_reads_the_official_catalog_at_its_current_address(string written)
    {
        AppSourcesConfig config = AppSourcesConfigDocument.Read($"[catalogs]\nofficial={written}\n");

        Assert.Empty(config.Warnings);
        Assert.Equal(new Uri(AppPackageDefaults.OfficialCatalogAddressFor(KazoOsSdk.SemanticVersion)), config.Catalogs["official"]);
    }

    [Fact]
    public void Another_catalog_keeps_its_address()
    {
        const string Address = "https://raw.githubusercontent.com/someone/KazoOs-AppMarketPlace/packages/catalog.json";

        AppSourcesConfig config = AppSourcesConfigDocument.Read($"[catalogs]\nmine={Address}\n");

        Assert.Equal(new Uri(Address), config.Catalogs["mine"]);
    }

    [Theory]
    [InlineData("github:Kazo-ca/KazoOs-App-Webamp@1.0.0")]
    [InlineData("github:Kazo-ca/HackerOs-App-Webamp@1.0.0")]
    [InlineData("https://github.com/Kazo-ca/KazoOs-App-Webamp@1.0.0")]
    public void A_source_recorded_before_3_0_0_names_the_current_repository(string recorded)
    {
        Assert.True(AppPackageSource.TryParse(recorded, out AppPackageSource? source));

        Assert.Equal("Kazo-ca", source.Owner);
        Assert.Equal("Os-App-Webamp", source.Repository);
    }

    [Theory]
    [InlineData("Kazo-ca", "Os-App-Webamp")]
    [InlineData("someone", "KazoOs-App-Webamp")]
    [InlineData("Kazo-ca", "KazoOs")]
    public void Other_repositories_keep_their_name(string owner, string repository)
    {
        Assert.Equal(repository, RenamedAppRepositories.Correct(owner, repository));
    }
}
