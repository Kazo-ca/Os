using System.Text;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Apps.Packages;
using Kazo.Os.Platform.Core.Tests.Features;

namespace Kazo.Os.Platform.Core.Tests.Apps.Packages;

/// <summary>A feature's package that provides a service (ADR 0262 §1), and its installation.</summary>
public sealed class FeatureServicePackageTests
{
    private const string Contracts = "Kazo.Sample.Contracts";
    private const string Contract = "Kazo.Sample.Contracts.IContract, Kazo.Sample.Contracts";

    private static Dictionary<string, byte[]> Files(string featureId = AppPackageTests.FeatureId)
    {
        Dictionary<string, byte[]> files = AppPackageTests.FeatureFiles();
        string manifest = AppPackageTests.FeatureManifestJson().Replace(
            "\"kind\":\"feature\",",
            "\"kind\":\"feature\",\"providedServices\":[{\"contract\":\"" + Contract + "\",\"implementation\":\"Kazo.Feature.Service, Kazo.Feature\"}],",
            StringComparison.Ordinal);
        // The running SDK, so the catalog accepts the feature (a 2.x minimum is refused by SDK 3).
        manifest = manifest.Replace("\"minimumVersion\":\"2.0.0\"", $"\"minimumVersion\":\"{KazoOsSdk.Version}\"", StringComparison.Ordinal);
        files["manifest.json"] = Encoding.UTF8.GetBytes(manifest.Replace(AppPackageTests.FeatureId, featureId, StringComparison.Ordinal));
        return files;
    }

    private static byte[] Package(bool shared, string featureId = AppPackageTests.FeatureId)
    {
        byte[] library = TestLibraries.Build(Contracts, "1.0.0", shared);
        Dictionary<string, byte[]> files = Files(featureId);
        files[$"lib/{Contracts}.dll"] = library;
        if (featureId != AppPackageTests.FeatureId)
        {
            foreach (string path in AppPackageTests.FeatureManifestPaths)
            {
                files[path] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(files[path]).Replace(AppPackageTests.FeatureId, featureId, StringComparison.Ordinal));
            }
        }

        return AppPackageTests.Build(files, featureId, manifests: AppPackageTests.FeatureManifestPaths, libraries: [TestLibraries.Entry(library)]);
    }

    [Fact]
    public void A_feature_that_provides_a_contract_of_its_shared_library_is_read()
    {
        AppPackage package = AppPackage.Read(Package(shared: true));

        AppProvidedServiceManifest service = Assert.Single(package.Manifest.ProvidedServices!);
        Assert.Equal("Kazo.Sample.Contracts.IContract", service.ContractType);
        Assert.Equal(Contracts, service.ContractAssembly);
        Assert.Equal("Kazo.Feature.Service", service.ImplementationType);
        // The code of the service is one of the feature's assemblies: the lazy loader may load it.
        Assert.Contains("Kazo.Feature.dll", AppPackage.GetDeclaredAssemblies(package.Manifests));
    }

    [Fact]
    public void A_contract_outside_a_shared_library_of_the_package_is_refused()
    {
        AppPackageException refused = Assert.Throws<AppPackageException>(() => AppPackage.Read(Package(shared: false)));

        Assert.Equal("apps.package.service-contract-not-shared", refused.Code);
        Assert.Contains("KazoOsSharedLibrary", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_second_feature_that_provides_the_same_contract_is_refused()
    {
        AppPackage second = AppPackage.Read(Package(shared: true, featureId: "org.kazo.other"));
        AppManifest first = AppManifestJsonSerializer.NormalizeOptionalCollections(AppManifestJsonSerializer.DeserializeStrict(Encoding.UTF8.GetString(Files()["manifest.json"])));
        AppCatalog catalog = AppCatalog.Build([first]).Catalog!;
        Assert.True(catalog.Manifests.ContainsKey(AppPackageTests.FeatureId), string.Join(", ", catalog.Rejected.Select(static rejection => rejection.Code)));

        AppPackageException refused = Assert.Throws<AppPackageException>(() => AppPackageInstaller.CheckProvidedServices(second, catalog));

        Assert.Equal("apps.package.service-already-provided", refused.Code);
        Assert.Contains(AppPackageTests.FeatureId, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_update_of_the_provider_is_not_compared_with_itself()
    {
        AppPackage update = AppPackage.Read(Package(shared: true));
        AppManifest installed = AppManifestJsonSerializer.NormalizeOptionalCollections(AppManifestJsonSerializer.DeserializeStrict(Encoding.UTF8.GetString(Files()["manifest.json"])));

        AppPackageInstaller.CheckProvidedServices(update, AppCatalog.Build([installed]).Catalog!);
    }

    [Fact]
    public void The_catalog_names_the_provider_of_a_contract()
    {
        AppCatalog catalog = AppCatalog.Build([FeatureServiceFixtures.Feature(), FeatureServiceFixtures.Client()]).Catalog!;

        AppServiceProvider provider = catalog.FindServiceProvider(typeof(Kazo.Os.Tests.Support.IFixtureMachine).FullName!)!;

        Assert.Equal(FeatureServiceFixtures.FeatureId, provider.Feature.Id);
        Assert.Null(catalog.FindServiceProvider("Nobody.IContract"));
    }
}
