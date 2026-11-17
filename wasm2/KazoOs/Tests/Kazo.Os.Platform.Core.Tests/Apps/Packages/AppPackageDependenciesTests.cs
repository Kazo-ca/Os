using System.Text;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Apps;
using Kazo.Os.Platform.Core.Apps.Packages;

namespace Kazo.Os.Platform.Core.Tests.Apps.Packages;

/// <summary>
/// The pieces of ADR 0248 that stand alone: the <c>dependencies</c> of <c>catalog.json</c> (§5), the
/// version range of a dependency, and the <c>[disabled-by-dependency]</c> lines of <c>apps.config</c> (§3).
/// </summary>
public sealed class AppPackageDependenciesTests
{
    private static readonly Uri Address = new("https://example.org/catalog/packages/catalog.json");

    private static AppCatalogVersion ParseVersion(string dependencies)
    {
        string version = $$$"""{"version":"1.0.0","package":"packages/org.example.client/1.0.0/org.example.client-1.0.0.kazoapp","sha256":"{{{new string('a', 64)}}}","size":10,"capabilities":[]{{{dependencies}}}}""";
        AppCatalogListing listing = AppCatalogDocument.Parse("official", Address, Encoding.UTF8.GetBytes(
            $$$"""{"schemaVersion":1,"categories":[],"apps":[{"appId":"org.example.client","name":"Client","versions":[{{{version}}}]}]}"""));
        return Assert.Single(Assert.Single(listing.Apps).Versions);
    }

    [Fact]
    public void A_catalog_version_lists_its_dependencies_as_the_manifest_says_them()
    {
        AppCatalogVersion version = ParseVersion(
            ""","dependencies":[{"appId":"org.kazo.feature","minimumVersion":"1.0.0","maximumVersion":"1.9.9"},{"appId":"org.kazo.extra","minimumVersion":"2.0.0","optional":true}]""");

        Assert.Equal(
            [new AppDependencyManifest("org.kazo.feature", "1.0.0", "1.9.9"), new AppDependencyManifest("org.kazo.extra", "2.0.0", null, Optional: true)],
            version.Dependencies);
    }

    [Fact]
    public void A_catalog_without_dependencies_says_nothing_and_a_broken_entry_is_skipped()
    {
        Assert.Null(ParseVersion(string.Empty).Dependencies);
        AppCatalogVersion version = ParseVersion(
            ""","dependencies":[{"appId":"","minimumVersion":"1.0.0"},{"appId":"org.kazo.feature","minimumVersion":"one"},{"appId":"org.kazo.ok","minimumVersion":"1.0.0"}]""");
        Assert.Equal(["org.kazo.ok"], version.Dependencies!.Select(static dependency => dependency.AppId));
    }

    [Theory]
    [InlineData("0.9.0", false)]
    [InlineData("1.0.0", true)]
    [InlineData("1.9.9", true)]
    [InlineData("2.0.0", false)]
    public void A_range_includes_both_bounds(string version, bool inRange)
    {
        AppDependencyManifest dependency = new("org.kazo.feature", "1.0.0", "1.9.9");

        Assert.Equal(inRange, AppDependencyRange.Contains(dependency, SemanticVersion.Parse(version)));
        Assert.Equal("1.0.0 - 1.9.9", AppDependencyRange.Describe(dependency));
        Assert.Equal(">= 1.0.0", AppDependencyRange.Describe(dependency with { MaximumVersion = null }));
    }

    [Fact]
    public void A_removal_records_the_apps_it_disables_and_a_reinstall_forgets_only_them()
    {
        // A user's own uninstall of another app is not touched by the dependency's return.
        string content = AppsConfigDocument.MarkUninstalled(null, ["org.example.chosen"], AppsConfigDocument.MachineDefaultContent);

        string removed = AppsConfigDocument.MarkDisabledByDependency(content, ["org.example.client"], "org.kazo.feature", AppsConfigDocument.MachineDefaultContent);
        IReadOnlyList<string> disabled = AppsConfigDocument.ReadDisabledByDependency(removed, "org.kazo.feature");
        string reinstalled = AppsConfigDocument.ForgetDisabledByDependency(AppsConfigDocument.MarkInstalled(removed, disabled), disabled);

        Assert.Equal(["org.example.client"], disabled);
        Assert.Empty(AppsConfigDocument.ReadDisabledByDependency(removed, "org.kazo.other"));
        Assert.Contains("org.example.client", removed, StringComparison.Ordinal);
        Assert.DoesNotContain("org.example.client", reinstalled, StringComparison.Ordinal);
        Assert.Contains("org.example.chosen", reinstalled, StringComparison.Ordinal);
    }
}
