using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Apps.Packages;

namespace Kazo.Os.Platform.Core.Tests.Apps.Packages;

/// <summary>One copy of a shared library per page, and what a package may not bring (ADR 0261 §2, §3).</summary>
public sealed class InstalledLibrariesTests
{
    private const string Contract = "Kazo.Sample.Contracts";

    private static AppPackageLibrary Shared(string version, string name = Contract) =>
        new(name, Version.Parse(version), new string('0', 64), Shared: true);

    internal static InstalledApp App(string appId, params AppPackageLibrary[] libraries) => new(
        new AppPackageSource("Kazo-ca", appId, SemanticVersion.Parse("1.0.0")),
        Parse(AppPackageTests.ManifestJson().Replace(AppPackageTests.AppId, appId, StringComparison.Ordinal)))
    {
        Libraries = libraries,
    };

    internal static InstalledApp Feature(string appId, params AppPackageLibrary[] libraries) => new(
        new AppPackageSource("Kazo-ca", appId, SemanticVersion.Parse("1.0.0")),
        Parse(AppPackageTests.FeatureManifestJson().Replace(AppPackageTests.FeatureId, appId, StringComparison.Ordinal)))
    {
        Libraries = libraries,
    };

    private static AppManifest Parse(string json) =>
        AppManifestJsonSerializer.NormalizeOptionalCollections(AppManifestJsonSerializer.DeserializeStrict(json));

    [Fact]
    public void Without_a_feature_the_newest_copy_of_the_same_major_version_serves_the_app()
    {
        InstalledApp older = App("org.kazo.older", Shared("1.1.0.0"));
        InstalledApp newer = App("org.kazo.newer", Shared("1.2.0.0"));
        InstalledApp other = App("org.kazo.other", Shared("2.5.0.0"));

        SharedLibraryChoice choice = InstalledLibraries.Choose(older, older.Libraries[0], [older, newer, other]);

        Assert.Equal("org.kazo.newer", choice.Copy.Owner.AppId);
        Assert.Equal(new Version(1, 2, 0, 0), choice.Copy.Library.Version);
        Assert.Equal(SharedLibraryChoiceReason.NewestCompatible, choice.Reason);
        Assert.Equal("newest compatible, from org.kazo.newer", choice.Describe());
    }

    [Fact]
    public void The_copy_of_a_feature_serves_every_app_of_its_major_version()
    {
        InstalledApp app = App("org.kazo.app", Shared("1.3.0.0"));
        InstalledApp feature = Feature("org.kazo.x86", Shared("1.2.0.0"));

        SharedLibraryChoice choice = InstalledLibraries.Choose(app, app.Libraries[0], [app, feature]);

        Assert.Equal("org.kazo.x86", choice.Copy.Owner.AppId);
        Assert.Equal(SharedLibraryChoiceReason.Feature, choice.Reason);
        Assert.Equal("feature org.kazo.x86", choice.Describe());
    }

    [Fact]
    public void A_feature_of_another_major_version_does_not_serve_the_app()
    {
        // A feature installed after the app: nothing was refused, each keeps its major version.
        InstalledApp app = App("org.kazo.app", Shared("1.3.0.0"));
        InstalledApp feature = Feature("org.kazo.x86", Shared("2.0.0.0"));

        SharedLibraryChoice choice = InstalledLibraries.Choose(app, app.Libraries[0], [app, feature]);

        Assert.Equal("org.kazo.app", choice.Copy.Owner.AppId);
        Assert.Equal(SharedLibraryChoiceReason.NewestCompatible, choice.Reason);
    }

    [Fact]
    public void A_library_that_is_not_shared_is_never_a_copy_for_another_app()
    {
        InstalledApp app = App("org.kazo.app", Shared("1.0.0.0"));
        InstalledApp other = App("org.kazo.other", new AppPackageLibrary(Contract, new Version(1, 9, 0, 0), new string('0', 64), Shared: false));

        SharedLibraryChoice choice = InstalledLibraries.Choose(app, app.Libraries[0], [app, other]);

        Assert.Equal("org.kazo.app", choice.Copy.Owner.AppId);
        Assert.Empty(InstalledLibraries.GetSharedCopies(Contract, [other]));
    }

    [Theory]
    // loaded, own, chosen, restart
    [InlineData("1.2.0.0", "1.2.0.0", "1.2.0.0", false)]
    [InlineData("1.3.0.0", "1.2.0.0", "1.2.0.0", false)] // a newer copy only adds members
    [InlineData("1.1.0.0", "1.1.0.0", "1.2.0.0", false)] // the loaded copy has all the app was built against
    [InlineData("1.1.0.0", "1.2.0.0", "1.2.0.0", true)]  // the app needs a higher minor version
    [InlineData("1.1.0.0", "1.3.0.0", "1.1.0.0", false)] // the feature's copy is the one a restart would load too
    [InlineData("1.4.0.0", "2.0.0.0", "2.0.0.0", true)]  // another major version is in the page
    public void A_restart_is_needed_only_when_the_loaded_copy_lacks_what_a_restart_would_bring(string loaded, string own, string chosen, bool expected)
    {
        Assert.Equal(expected, InstalledLibraries.NeedsRestart(Version.Parse(loaded), Version.Parse(own), Version.Parse(chosen)));
    }

    [Fact]
    public void An_app_built_against_a_newer_copy_than_the_one_in_the_page_waits_for_a_restart()
    {
        // ADR 0261 §3: 1.1 was loaded for the first app; the second one needs 1.2.
        InstalledApp first = App("org.kazo.first", Shared("1.1.0.0"));
        InstalledApp second = App("org.kazo.second", Shared("1.2.0.0"));

        IReadOnlyList<LibraryRestart> pending = InstalledLibraries.FindPendingRestarts(
            [second, first], name => name == Contract ? new Version(1, 1, 0, 0) : null);

        LibraryRestart restart = Assert.Single(pending);
        Assert.Equal(new LibraryRestart("org.kazo.second", Contract, new Version(1, 1, 0, 0), new Version(1, 2, 0, 0), "newest compatible, from org.kazo.second"), restart);
    }

    [Fact]
    public void A_feature_that_brings_a_newer_copy_than_the_one_in_the_page_waits_for_a_restart()
    {
        InstalledApp app = App("org.kazo.app", Shared("1.1.0.0"));
        InstalledApp feature = Feature("org.kazo.x86", Shared("1.2.0.0"));

        IReadOnlyList<LibraryRestart> pending = InstalledLibraries.FindPendingRestarts([app, feature], _ => new Version(1, 1, 0, 0));

        LibraryRestart restart = Assert.Single(pending);
        Assert.Equal("org.kazo.x86", restart.AppId);
        Assert.Equal("feature org.kazo.x86", restart.Reason);
    }

    [Fact]
    public void Nothing_waits_while_no_copy_is_in_the_page()
    {
        InstalledApp first = App("org.kazo.first", Shared("1.1.0.0"));
        InstalledApp second = App("org.kazo.second", Shared("1.2.0.0"));

        Assert.Empty(InstalledLibraries.FindPendingRestarts([first, second], static _ => null));
    }

    private static AppPackage Package(string appId, byte[] library, bool feature = false)
    {
        AppPackageLibrary described = AppPackageLibrary.Describe("x.dll", library);
        Dictionary<string, byte[]> files = feature ? AppPackageTests.FeatureFiles() : AppPackageTests.SampleFiles();
        string original = feature ? AppPackageTests.FeatureId : AppPackageTests.AppId;
        foreach (string path in files.Keys.Where(static path => path.EndsWith(".json", StringComparison.Ordinal)).ToArray())
        {
            files[path] = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(files[path]).Replace(original, appId, StringComparison.Ordinal));
        }

        files["lib/" + described.FileName] = library;
        return AppPackage.Read(AppPackageTests.Build(
            files, appId, manifests: feature ? AppPackageTests.FeatureManifestPaths : null, libraries: [TestLibraries.Entry(library)]));
    }

    [Fact]
    public void A_shared_library_of_another_major_version_than_the_features_copy_is_refused()
    {
        AppPackage package = Package("org.kazo.client", TestLibraries.Build(Contract, "2.0.0", shared: true));
        InstalledApp feature = Feature("org.kazo.x86", Shared("1.4.0.0"));

        AppPackageException refused = Assert.Throws<AppPackageException>(() => InstalledLibraries.CheckMajorVersions(package, [feature]));

        Assert.Equal("apps.package.library-major-mismatch", refused.Code);
        Assert.Contains("2.0.0", refused.Message, StringComparison.Ordinal);
        Assert.Contains("1.4.0", refused.Message, StringComparison.Ordinal);
        Assert.Contains("org.kazo.x86", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_feature_two_major_versions_are_both_installed()
    {
        // The maintainer's answer to question 1 of ADR 0261: A, they coexist.
        AppPackage package = Package("org.kazo.client", TestLibraries.Build(Contract, "2.0.0", shared: true));

        InstalledLibraries.CheckMajorVersions(package, [App("org.kazo.other", Shared("1.4.0.0"))]);
    }

    [Fact]
    public void The_same_major_version_as_the_features_copy_is_accepted_whatever_the_minor()
    {
        AppPackage package = Package("org.kazo.client", TestLibraries.Build(Contract, "1.9.0", shared: true));

        InstalledLibraries.CheckMajorVersions(package, [Feature("org.kazo.x86", Shared("1.4.0.0"))]);
    }

    [Fact]
    public void An_update_of_a_feature_is_not_compared_with_its_own_installed_copy()
    {
        AppPackage update = Package("org.kazo.x86", TestLibraries.Build(Contract, "2.0.0", shared: true), feature: true);

        InstalledLibraries.CheckMajorVersions(update, [Feature("org.kazo.x86", Shared("1.4.0.0"))]);
    }

    [Fact]
    public void A_library_with_the_name_of_an_assembly_of_the_runtime_is_refused()
    {
        AppPackage package = Package("org.kazo.client", TestLibraries.Build("System.Text.Json", "9.0.0"));

        AppPackageException refused = Assert.Throws<AppPackageException>(() => InstalledLibraries.CheckReservedNames(
            package, LoadedRuntimeApiSurface.Instance, new HashSet<string>(), new HashSet<string>()));

        Assert.Equal("apps.package.library-reserved", refused.Code);
        Assert.Contains("System.Text.Json", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_library_with_the_name_of_an_assembly_of_an_app_of_the_os_is_refused()
    {
        // A built-in app is loaded on demand: the runtime does not have its assembly yet.
        AppPackage package = Package("org.kazo.client", TestLibraries.Build("Kazo.Builtin.App", "1.0.0"));

        AppPackageException refused = Assert.Throws<AppPackageException>(() => InstalledLibraries.CheckReservedNames(
            package, LoadedRuntimeApiSurface.Instance, new HashSet<string> { "Kazo.Builtin.App.dll" }, new HashSet<string>()));

        Assert.Equal("apps.package.library-reserved", refused.Code);
    }

    [Fact]
    public void A_library_the_page_loaded_from_another_package_is_not_an_assembly_of_the_os()
    {
        // The runtime has it (another installed app loaded its copy), yet the OS does not bring it.
        AppPackage package = Package("org.kazo.client", TestLibraries.Build("System.Text.Json", "9.0.0"));

        InstalledLibraries.CheckReservedNames(
            package, LoadedRuntimeApiSurface.Instance, new HashSet<string>(), new HashSet<string> { "System.Text.Json" });
    }

    [Fact]
    public void A_library_that_is_not_shared_is_never_compared_at_installation()
    {
        // ADR 0256 §1: an ordinary library goes with its app; neither the OS nor the Store manages it.
        AppPackage package = Package("org.kazo.client", TestLibraries.Build(Contract, "2.0.0"));
        AppPackageLibrary ordinary = new(Contract, new Version(1, 0, 0, 0), new string('0', 64), Shared: false);

        InstalledLibraries.CheckMajorVersions(package, [Feature("org.kazo.x86", Shared("1.4.0.0")), App("org.kazo.other", ordinary)]);
    }

    [Fact]
    public void A_library_that_is_not_shared_never_waits_for_a_restart()
    {
        // ADR 0256 §1 and §4: another copy in the page serves the app; no restart is offered for it.
        AppPackageLibrary ordinary = new(Contract, new Version(2, 0, 0, 0), new string('0', 64), Shared: false);
        InstalledApp app = App("org.kazo.app", ordinary);

        Assert.Empty(InstalledLibraries.FindPendingRestarts([app], static _ => new Version(1, 0, 0, 0)));
    }

    [Theory]
    [InlineData("1.2.0.0", "1.2.0")]
    [InlineData("1.2.3.4", "1.2.3.4")]
    [InlineData("1.2", "1.2")]
    public void A_version_is_written_without_an_empty_revision(string version, string expected)
    {
        Assert.Equal(expected, InstalledLibraries.Format(Version.Parse(version)));
    }
}
