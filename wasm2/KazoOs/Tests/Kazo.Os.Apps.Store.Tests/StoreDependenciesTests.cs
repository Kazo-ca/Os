using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Apps;
using Xunit;

namespace Kazo.Os.Apps.Store.Tests;

/// <summary>
/// The dependencies of an app on its page (ADR 0248 §2): « installed », « to install with this app »,
/// « optional », and the warning of an installed version out of range; for a feature to install,
/// the permissions its installation approves (answer 268 A).
/// </summary>
public sealed class StoreDependenciesTests
{
    private const string FeatureId = "org.kazo.feature";
    private static readonly IReadOnlyDictionary<string, string> NothingRunning = new Dictionary<string, string>(StringComparer.Ordinal);

    private static KazoCatalogApp Feature(params string[] capabilities) =>
        new("official", FeatureId, "Feature", "system", [], new Dictionary<string, string> { ["en"] = "A feature" },
            "MIT", "https://github.com/example/feature", "1.0.0",
            [new KazoCatalogAppVersion("1.0.0", 4096, capabilities)], KazoPackageTrust.Verified)
        {
            Kind = KazoCatalogItemKind.Feature,
        };

    private static StoreCatalog Store(KazoCatalogApp app, params KazoInstalledPackage[] installed) =>
        new([new KazoAppCatalog("official", KazoPackageTrust.Verified, [], [app])], installed, [], new HashSet<string>(StringComparer.Ordinal));

    [Fact]
    public void A_required_feature_not_installed_is_installed_with_the_app_and_shows_its_permissions()
    {
        StoreCatalog store = Store(Feature(AppCapabilities.FileSystemSystemRead, "clipboard.read"));

        StoreDependency dependency = Assert.Single(StoreDependencies.Describe(
            [new KazoPackageDependency(FeatureId, "1.0.0", null, Optional: false)], store.Find, NothingRunning));

        Assert.Equal(StoreDependencyState.WithThisApp, dependency.State);
        Assert.Equal("Feature", dependency.Name);
        Assert.True(dependency.IsFeature);
        Assert.Equal(">= 1.0.0", dependency.Range);
        Assert.Equal(
            new[] { AppCapabilities.FileSystemSystemRead, "clipboard.read" }.Where(AppCapabilities.IsDangerous).Order(StringComparer.Ordinal),
            dependency.DangerousPermissions);
    }

    [Fact]
    public void An_installed_dependency_in_range_is_installed()
    {
        StoreCatalog store = Store(Feature(), new KazoInstalledPackage(FeatureId, "catalog:official/" + FeatureId, "1.2.0", Loaded: true));

        StoreDependency dependency = Assert.Single(StoreDependencies.Describe(
            [new KazoPackageDependency(FeatureId, "1.0.0", "1.9.9", Optional: false)], store.Find, NothingRunning));

        Assert.Equal(StoreDependencyState.Installed, dependency.State);
        Assert.Equal("1.2.0", dependency.InstalledVersion);
        Assert.Empty(dependency.DangerousPermissions);
    }

    [Fact]
    public void An_installed_version_out_of_range_is_a_warning()
    {
        StoreCatalog store = Store(
            Feature(AppCapabilities.FileSystemSystemRead), new KazoInstalledPackage(FeatureId, "catalog:official/" + FeatureId, "2.0.0", Loaded: true));

        StoreDependency dependency = Assert.Single(StoreDependencies.Describe(
            [new KazoPackageDependency(FeatureId, "1.0.0", "1.9.9", Optional: false)], store.Find, NothingRunning));

        Assert.Equal(StoreDependencyState.OutOfRange, dependency.State);
        Assert.Equal("2.0.0", dependency.InstalledVersion);
        Assert.Equal("1.0.0 – 1.9.9", dependency.Range);
    }

    [Fact]
    public void An_optional_dependency_absent_is_optional_and_a_built_in_app_counts_as_installed()
    {
        StoreCatalog store = Store(Feature());
        Dictionary<string, string> running = new(StringComparer.Ordinal) { ["org.kazo.terminal"] = "3.4.1" };

        IReadOnlyList<StoreDependency> dependencies = StoreDependencies.Describe(
            [new KazoPackageDependency(FeatureId, "1.0.0", null, Optional: true), new KazoPackageDependency("org.kazo.terminal", "3.0.0", null, Optional: false)],
            store.Find,
            running);

        // Required ones first.
        Assert.Equal(["org.kazo.terminal", FeatureId], dependencies.Select(static dependency => dependency.AppId));
        Assert.Equal(StoreDependencyState.Installed, dependencies[0].State);
        Assert.Equal(StoreDependencyState.Optional, dependencies[1].State);
        Assert.Empty(dependencies[1].DangerousPermissions);
    }
}
