using System.Text;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Apps.Packages;
using Kazo.Os.Platform.Core.Tests.Features;

namespace Kazo.Os.Platform.Core.Tests.Apps.Packages;

/// <summary>Installing a feature that declares permissions, and an app that asks for them (ADR 0246 §2).</summary>
public sealed class FeaturePermissionPackageTests
{
    private static AppPackage FeaturePackage(string featureId, string prefix, string permission)
    {
        Dictionary<string, byte[]> files = AppPackageTests.FeatureFiles();
        string manifest = AppPackageTests.FeatureManifestJson()
            .Replace(
                "\"kind\":\"feature\",",
                "\"kind\":\"feature\",\"capabilityPrefix\":\"" + prefix + "\",\"providedCapabilities\":[{\"id\":\"" + permission + "\",\"risk\":\"normal\",\"title\":\"Use\"}],",
                StringComparison.Ordinal);
        files["manifest.json"] = Encoding.UTF8.GetBytes(manifest.Replace(AppPackageTests.FeatureId, featureId, StringComparison.Ordinal));
        foreach (string path in AppPackageTests.FeatureManifestPaths)
        {
            files[path] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(files[path]).Replace(AppPackageTests.FeatureId, featureId, StringComparison.Ordinal));
        }

        return AppPackage.Read(AppPackageTests.Build(files, featureId, manifests: AppPackageTests.FeatureManifestPaths));
    }

    private static AppCatalog Installed() => AppCatalog.Build([FeaturePermissionFixtures.Feature()]).Catalog!;

    [Fact]
    public void A_feature_whose_prefix_another_feature_took_is_refused()
    {
        AppPackage second = FeaturePackage("org.kazo.other", "machine", "machine.disk.read");

        AppPackageException refused = Assert.Throws<AppPackageException>(() => AppPackageInstaller.CheckProvidedCapabilities(second, Installed()));

        Assert.Equal("apps.package.capability-prefix-taken", refused.Code);
        Assert.Contains(FeaturePermissionFixtures.FeatureId, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_update_of_the_feature_keeps_its_own_prefix()
    {
        AppPackage update = FeaturePackage(FeaturePermissionFixtures.FeatureId, "machine", FeaturePermissionFixtures.Use);

        AppPackageInstaller.CheckProvidedCapabilities(update, Installed());
    }

    [Fact]
    public void A_feature_with_a_free_prefix_is_accepted()
    {
        AppPackage other = FeaturePackage("org.kazo.other", "disk", "disk.read");

        AppPackageInstaller.CheckProvidedCapabilities(other, Installed());
    }

    [Fact]
    public void An_app_that_asks_for_an_installed_features_permission_declares_that_feature()
    {
        Dictionary<string, byte[]> files = AppPackageTests.SampleFiles();
        string manifest = AppPackageTests.ManifestJson()
            .Replace("\"capabilities\":[]", "\"capabilities\":[\"" + FeaturePermissionFixtures.Use + "\"]", StringComparison.Ordinal)
            .Replace("\"dependencies\":[]", "\"dependencies\":[{\"appId\":\"org.kazo.unrelated\",\"minimumVersion\":\"1.0.0\",\"optional\":true}]", StringComparison.Ordinal);
        files["manifest.json"] = Encoding.UTF8.GetBytes(manifest);
        AppPackage app = AppPackage.Read(AppPackageTests.Build(files));

        AppPackageException refused = Assert.Throws<AppPackageException>(() => AppPackageInstaller.CheckProvidedCapabilities(app, Installed()));

        Assert.Equal("manifest.capability.unknown", refused.Code);
        Assert.Contains(FeaturePermissionFixtures.FeatureId, refused.Message, StringComparison.Ordinal);
    }
}
