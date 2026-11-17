using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Apps.Packages;

namespace Kazo.Os.Platform.Core.Tests.FileSystem.DriveProviders;

/// <summary>One provider per drive type (ADR 0282 §1): the installer refuses a second feature that declares it.</summary>
public sealed class DriveTypeInstallTests
{
    [Fact]
    public void A_second_feature_that_provides_the_same_type_is_refused()
    {
        AppCatalog catalog = AppCatalog.Build([DriveProviderFixtures.Feature()]).Catalog!;
        AppManifest other = DriveProviderFixtures.Feature(id: "org.test.other");

        AppPackageException refused = Assert.Throws<AppPackageException>(() => AppPackageInstaller.CheckProvidedDriveTypes(other, catalog));

        Assert.Equal("apps.package.drive-type-already-provided", refused.Code);
        Assert.Contains(DriveProviderFixtures.FeatureId, refused.Message, StringComparison.Ordinal);
        Assert.Contains(DriveProviderFixtures.Type, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_update_of_the_provider_is_not_compared_with_itself()
    {
        AppCatalog catalog = AppCatalog.Build([DriveProviderFixtures.Feature()]).Catalog!;

        AppPackageInstaller.CheckProvidedDriveTypes(DriveProviderFixtures.Feature(version: "2.0.0"), catalog);
    }

    [Fact]
    public void The_catalog_names_the_provider_of_a_type()
    {
        AppCatalog catalog = AppCatalog.Build([DriveProviderFixtures.Feature()]).Catalog!;

        AppDriveProviderRegistration provider = catalog.FindDriveProvider(DriveProviderFixtures.Type)!;

        Assert.Equal(DriveProviderFixtures.FeatureId, provider.Feature.Id);
        Assert.Equal(typeof(Kazo.Os.Tests.Support.InMemoryDriveProvider).FullName, provider.Provider.ProviderType);
        Assert.Null(catalog.FindDriveProvider("nosuchtype"));
    }
}
