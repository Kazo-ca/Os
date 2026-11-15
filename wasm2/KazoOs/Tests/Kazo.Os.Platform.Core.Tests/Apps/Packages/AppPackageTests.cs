using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Apps.Packages;

namespace Kazo.Os.Platform.Core.Tests.Apps.Packages;

/// <summary>Reading and verifying a <c>.kazoapp</c> package and its index (ADR 0182 §1, §2, §5).</summary>
public sealed class AppPackageTests
{
    internal const string AppId = "org.kazo.sample";

    internal static string ManifestJson(string version = "1.0.0", string assetSha = "") =>
        "{\"schemaVersion\":1,\"id\":\"" + AppId + "\",\"name\":\"Sample\",\"version\":\"" + version + "\",\"publisherId\":\"pub.kazo\","
        + "\"description\":\"A sample.\",\"kind\":\"window\",\"platform\":{\"supported\":[\"desktop\"],\"entryPoints\":[{\"platforms\":[\"desktop\"],"
        + "\"assembly\":\"Kazo.Sample.dll\",\"type\":\"Kazo.Sample.SampleWindow\"}]},\"sdkCompatibility\":{\"minimumVersion\":\"2.0.0\"},"
        + "\"presentation\":{\"category\":\"utilities\",\"launchVisibility\":\"visible\",\"iconAssetPaths\":[]},\"localizations\":[],\"capabilities\":[],"
        + "\"declaredTopicPermissions\":[],\"resources\":{\"baselineCpuWeight\":0,\"burstCpuWeight\":0,\"baselineMemoryWeight\":0,\"burstMemoryWeight\":0,"
        + "\"baselineStorageIoWeight\":0,\"burstStorageIoWeight\":0,\"baselineNetworkIoWeight\":0,\"burstNetworkIoWeight\":0},\"intents\":[],\"dependencies\":[],"
        + "\"assets\":[" + (assetSha.Length == 0 ? string.Empty : "{\"path\":\"data/x.txt\",\"kind\":\"data\",\"sha256\":\"" + assetSha + "\"}") + "],"
        + "\"fileHandlers\":[],\"singleInstancePerUser\":false,\"autoStart\":false,\"supportsBack\":false,\"execution\":\"mainThread\"}";

    internal const string FeatureId = "org.kazo.feature";

    private const string ZeroResources =
        "\"resources\":{\"baselineCpuWeight\":0,\"burstCpuWeight\":0,\"baselineMemoryWeight\":0,\"burstMemoryWeight\":0,"
        + "\"baselineStorageIoWeight\":0,\"burstStorageIoWeight\":0,\"baselineNetworkIoWeight\":0,\"burstNetworkIoWeight\":0}";

    /// <summary>The manifest of a feature (ADR 0260): no entry point, the capability set.</summary>
    internal static string FeatureManifestJson(string version = "1.0.0") =>
        "{\"schemaVersion\":1,\"id\":\"" + FeatureId + "\",\"name\":\"Feature\",\"version\":\"" + version + "\",\"publisherId\":\"pub.kazo\","
        + "\"description\":\"A feature.\",\"kind\":\"feature\",\"sdkCompatibility\":{\"minimumVersion\":\"2.0.0\"},"
        + "\"presentation\":{\"category\":\"system\",\"launchVisibility\":\"hidden\",\"iconAssetPaths\":[]},\"localizations\":[],"
        + "\"capabilities\":[\"clipboard.read\",\"filesystem.system.read\"],\"declaredTopicPermissions\":[]," + ZeroResources + ",\"intents\":[],"
        + "\"dependencies\":[],\"assets\":[],\"fileHandlers\":[],\"singleInstancePerUser\":false,\"autoStart\":false,\"supportsBack\":false,\"execution\":\"mainThread\"}";

    /// <summary>One child of the feature: a command or a service, naming the feature as its parent.</summary>
    internal static string FeatureChildJson(string suffix, string kind, string capabilities, string? command = null, string version = "1.0.0") =>
        "{\"schemaVersion\":1,\"id\":\"" + FeatureId + "." + suffix + "\",\"name\":\"" + suffix + "\",\"version\":\"" + version + "\",\"publisherId\":\"pub.kazo\","
        + "\"description\":\"An entry point.\",\"kind\":\"" + kind + "\",\"parent\":\"" + FeatureId + "\",\"entryPoint\":{\"assembly\":\"Kazo.Feature.dll\",\"type\":\"Kazo.Feature." + suffix + "\"},"
        + "\"sdkCompatibility\":{\"minimumVersion\":\"2.0.0\"},\"presentation\":{\"category\":\"system\",\"launchVisibility\":\"hidden\",\"iconAssetPaths\":[]},"
        + "\"localizations\":[],\"capabilities\":[" + capabilities + "],\"declaredTopicPermissions\":[]," + ZeroResources + ",\"intents\":[],\"dependencies\":[],\"assets\":[],\"fileHandlers\":[],"
        + (command is null ? string.Empty : "\"terminal\":{\"name\":\"" + command + "\",\"aliases\":[],\"usage\":\"" + command + "\"},")
        + "\"singleInstancePerUser\":false,\"autoStart\":false,\"supportsBack\":false,\"execution\":\"mainThread\"}";

    /// <summary>The files of a feature package with three children (ADR 0260 §2): two commands and a service.</summary>
    internal static Dictionary<string, byte[]> FeatureFiles(string version = "1.0.0") => new(StringComparer.Ordinal)
    {
        ["manifest.json"] = Encoding.UTF8.GetBytes(FeatureManifestJson(version)),
        ["entrypoints/cli.json"] = Encoding.UTF8.GetBytes(FeatureChildJson("cli", "terminal", "\"filesystem.system.read\"", "x86", version)),
        ["entrypoints/console.json"] = Encoding.UTF8.GetBytes(FeatureChildJson("console", "terminal", "\"clipboard.read\"", "x86-console", version)),
        ["entrypoints/daemon.json"] = Encoding.UTF8.GetBytes(FeatureChildJson("daemon", "service", string.Empty, null, version)),
        ["lib/Kazo.Feature.dll"] = [1, 2, 3],
    };

    /// <summary>The child manifests a feature package lists in <c>kazoos-package.json</c>.</summary>
    internal static readonly string[] FeatureManifestPaths = ["entrypoints/cli.json", "entrypoints/console.json", "entrypoints/daemon.json"];

    internal static Dictionary<string, byte[]> SampleFiles(string version = "1.0.0")
    {
        byte[] asset = Encoding.UTF8.GetBytes("hello");
        return new(StringComparer.Ordinal)
        {
            ["manifest.json"] = Encoding.UTF8.GetBytes(ManifestJson(version, AppPackage.Sha256(asset))),
            ["lib/Kazo.Sample.dll"] = [1, 2, 3],
            ["wwwroot/data/x.txt"] = asset,
        };
    }

    internal static byte[] Build(
        Dictionary<string, byte[]> files,
        string appId = AppId,
        string version = "1.0.0",
        Dictionary<string, string>? listed = null,
        string descriptionFileName = AppPackage.DescriptionFileName,
        IReadOnlyList<string>? manifests = null,
        IReadOnlyList<object>? libraries = null)
    {
        listed ??= files.ToDictionary(static pair => pair.Key, static pair => AppPackage.Sha256(pair.Value), StringComparer.Ordinal);
        Dictionary<string, object> root = new(StringComparer.Ordinal) { ["appId"] = appId, ["version"] = version, ["files"] = listed };
        if (manifests is not null)
        {
            root["manifests"] = manifests;
        }

        if (libraries is not null)
        {
            // ADR 0261 §1: the libraries of the app, as TestLibraries.Entry describes each one.
            root["libraries"] = libraries;
        }

        string description = JsonSerializer.Serialize(root);
        using MemoryStream stream = new();
        using (ZipArchive zip = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string path, byte[] bytes) in files.Append(new(descriptionFileName, Encoding.UTF8.GetBytes(description))))
            {
                using Stream entry = zip.CreateEntry(path).Open();
                entry.Write(bytes);
            }
        }

        return stream.ToArray();
    }

    [Fact]
    public void A_complete_package_reads_with_its_manifest_assemblies_and_assets()
    {
        AppPackage package = AppPackage.Read(Build(SampleFiles()));

        Assert.Equal(AppId, package.AppId);
        Assert.Equal(SemanticVersion.Parse("1.0.0"), package.Version);
        Assert.Equal(["Kazo.Sample.dll"], package.Assemblies.Keys);
        Assert.Equal("hello", Encoding.UTF8.GetString(package.Files["wwwroot/data/x.txt"]));
    }

    [Fact]
    public void A_satellite_of_a_declared_assembly_is_kept_apart_from_the_assemblies()
    {
        Dictionary<string, byte[]> files = SampleFiles();
        files["lib/fr/Kazo.Sample.resources.dll"] = [7, 7];

        AppPackage package = AppPackage.Read(Build(files));

        Assert.Equal(["Kazo.Sample.dll"], package.Assemblies.Keys);
        Assert.Equal(["fr/Kazo.Sample.resources.dll"], package.SatelliteAssemblies.Keys);
        Assert.Equal(new byte[] { 7, 7 }, package.SatelliteAssemblies["fr/Kazo.Sample.resources.dll"]);
    }

    [Theory]
    [InlineData("lib/fr/Kazo.Sdk.resources.dll")]
    [InlineData("lib/fr/Kazo.Sample.dll")]
    [InlineData("lib/fr/sub/Kazo.Sample.resources.dll")]
    [InlineData("lib/f_r/Kazo.Sample.resources.dll")]
    public void A_satellite_that_belongs_to_no_declared_assembly_or_culture_is_refused(string path)
    {
        Dictionary<string, byte[]> files = SampleFiles();
        files[path] = [7];

        Assert.Equal("apps.package.unexpected-file", Assert.Throws<AppPackageException>(() => AppPackage.Read(Build(files))).Code);
    }

    [Theory]
    [InlineData("lib/fr/Kazo.Sample.resources.dll", true, "fr", "Kazo.Sample.dll")]
    [InlineData("lib/fr-CA/Kazo.Sample.resources.dll", true, "fr-CA", "Kazo.Sample.dll")]
    [InlineData("lib/zh-Hant/Kazo.Sample.resources.dll", true, "zh-Hant", "Kazo.Sample.dll")]
    [InlineData("lib/Kazo.Sample.dll", false, "", "")]
    [InlineData("lib/fr/Kazo.Sample.dll", false, "", "")]
    [InlineData("wwwroot/fr/Kazo.Sample.resources.dll", false, "", "")]
    public void Satellite_paths_name_their_culture_and_assembly(string path, bool expected, string culture, string assembly)
    {
        bool parsed = AppPackage.TryParseSatellitePath(path, out string parsedCulture, out string parsedAssembly);

        Assert.Equal((expected, culture, assembly), (parsed, parsedCulture, parsedAssembly));
    }

    [Fact]
    public void A_feature_package_reads_its_three_children_listed_by_the_description()
    {
        AppPackage package = AppPackage.Read(Build(FeatureFiles(), FeatureId, manifests: FeatureManifestPaths));

        Assert.True(package.IsFeature);
        Assert.Equal(["org.kazo.feature.cli", "org.kazo.feature.console", "org.kazo.feature.daemon"], package.Children.Select(child => child.Id));
        Assert.All(package.Children, child => Assert.Equal(FeatureId, child.Parent));
        Assert.Equal(["Kazo.Feature.dll"], package.Assemblies.Keys);
        Assert.Equal(["org.kazo.feature", "org.kazo.feature.cli", "org.kazo.feature.console", "org.kazo.feature.daemon"], package.Manifests.Select(manifest => manifest.Id));
    }

    [Fact]
    public void A_feature_package_without_the_manifest_list_reads_every_entrypoints_file()
    {
        AppPackage package = AppPackage.Read(Build(FeatureFiles(), FeatureId));

        Assert.Equal(3, package.Children.Count);
    }

    [Fact]
    public void A_feature_child_outside_the_set_or_a_child_as_root_manifest_is_refused()
    {
        Dictionary<string, byte[]> greedy = FeatureFiles();
        greedy["entrypoints/cli.json"] = Encoding.UTF8.GetBytes(FeatureChildJson("cli", "terminal", "\"filesystem.system.write\"", "x86"));
        Dictionary<string, byte[]> alone = new(StringComparer.Ordinal)
        {
            ["manifest.json"] = Encoding.UTF8.GetBytes(FeatureChildJson("cli", "terminal", string.Empty, "x86")),
            ["lib/Kazo.Feature.dll"] = [1],
        };

        AppPackageException outsideSet = Assert.Throws<AppPackageException>(() => AppPackage.Read(Build(greedy, FeatureId, manifests: FeatureManifestPaths)));
        AppPackageException childAlone = Assert.Throws<AppPackageException>(() => AppPackage.Read(Build(alone, FeatureId + ".cli")));

        Assert.Equal("apps.package.bad-manifest", outsideSet.Code);
        Assert.Contains("filesystem.system.write", outsideSet.Message, StringComparison.Ordinal);
        Assert.Equal("apps.package.bad-manifest", childAlone.Code);
        Assert.Contains("never alone", childAlone.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ordinary_package_with_a_child_manifest_is_refused()
    {
        Dictionary<string, byte[]> files = SampleFiles();
        files["entrypoints/cli.json"] = Encoding.UTF8.GetBytes(FeatureChildJson("cli", "terminal", string.Empty, "x86"));

        Assert.Equal("apps.package.unexpected-file", Assert.Throws<AppPackageException>(() => AppPackage.Read(Build(files))).Code);
    }

    [Fact]
    public void An_altered_file_is_refused()
    {
        Dictionary<string, byte[]> files = SampleFiles();
        Dictionary<string, string> listed = files.ToDictionary(static pair => pair.Key, static pair => AppPackage.Sha256(pair.Value));
        files["lib/Kazo.Sample.dll"] = [9, 9, 9];

        Assert.Equal("apps.package.hash-mismatch", Assert.Throws<AppPackageException>(() => AppPackage.Read(Build(files, listed: listed))).Code);
    }

    [Fact]
    public void An_unlisted_file_is_refused()
    {
        Dictionary<string, byte[]> files = SampleFiles();
        Dictionary<string, string> listed = files.ToDictionary(static pair => pair.Key, static pair => AppPackage.Sha256(pair.Value));
        files["lib/Extra.dll"] = [4];

        Assert.Equal("apps.package.unlisted-file", Assert.Throws<AppPackageException>(() => AppPackage.Read(Build(files, listed: listed))).Code);
    }

    [Fact]
    public void A_missing_entry_point_assembly_is_refused()
    {
        Dictionary<string, byte[]> files = SampleFiles();
        files.Remove("lib/Kazo.Sample.dll");

        Assert.Equal("apps.package.missing-assembly", Assert.Throws<AppPackageException>(() => AppPackage.Read(Build(files))).Code);
    }

    [Fact]
    public void A_file_outside_the_known_folders_is_refused()
    {
        Dictionary<string, byte[]> files = SampleFiles();
        files["run.sh"] = [1];

        Assert.Equal("apps.package.unexpected-file", Assert.Throws<AppPackageException>(() => AppPackage.Read(Build(files))).Code);
    }

    [Fact]
    public void A_package_that_names_another_app_or_version_is_refused()
    {
        Assert.Equal("apps.package.id-mismatch", Assert.Throws<AppPackageException>(() => AppPackage.Read(Build(SampleFiles(), appId: "org.kazo.other"))).Code);
        Assert.Equal("apps.package.version-mismatch", Assert.Throws<AppPackageException>(() => AppPackage.Read(Build(SampleFiles(), version: "2.0.0"))).Code);
    }

    [Fact]
    public void A_path_that_climbs_out_of_the_package_is_refused()
    {
        Dictionary<string, byte[]> files = SampleFiles();
        files["wwwroot/../../etc/passwd"] = [1];

        Assert.Equal("apps.package.unsafe-path", Assert.Throws<AppPackageException>(() => AppPackage.Read(Build(files))).Code);
    }

    [Fact]
    public void Garbage_is_refused_as_corrupt()
    {
        Assert.Equal("apps.package.corrupt", Assert.Throws<AppPackageException>(() => AppPackage.Read([1, 2, 3, 4])).Code);
    }

    [Fact]
    public void The_index_lists_versions_newest_first_and_prefers_stable_ones()
    {
        string sha = new('a', 64);
        AppPackageIndex index = AppPackageIndex.Parse(Encoding.UTF8.GetBytes(
            "{\"appId\":\"org.kazo.sample\",\"versions\":["
            + "{\"version\":\"1.0.0\",\"path\":\"1.0.0/s.kazoapp\",\"sha256\":\"" + sha + "\"},"
            + "{\"version\":\"2.0.0-beta\",\"path\":\"2.0.0-beta/s.kazoapp\",\"sha256\":\"" + sha + "\"},"
            + "{\"version\":\"1.1.0\",\"path\":\"1.1.0/s.kazoapp\",\"sha256\":\"" + sha + "\"}]}"));

        Assert.Equal(["2.0.0-beta", "1.1.0", "1.0.0"], index.Versions.Select(static entry => entry.Version.ToString()));
        Assert.Equal("1.1.0", index.Latest!.Version.ToString());
        Assert.Equal("1.0.0/s.kazoapp", index.Find(SemanticVersion.Parse("1.0.0"))!.Path);
        Assert.Null(index.Find(SemanticVersion.Parse("9.0.0")));
    }

    [Fact]
    public void A_package_made_before_the_rename_reads_with_its_kazoos_package_json()
    {
        // ADR 0228 §2: packages published before Kazo OS name their description hackeros-package.json.
        AppPackage package = AppPackage.Read(Build(SampleFiles(), descriptionFileName: AppPackage.LegacyDescriptionFileName));

        Assert.Equal(AppId, package.AppId);
        Assert.Equal("hello", Encoding.UTF8.GetString(package.Files["wwwroot/data/x.txt"]));
    }

    [Fact]
    public void The_index_still_accepts_packages_published_under_the_old_extension()
    {
        string sha = new('a', 64);
        AppPackageIndex index = AppPackageIndex.Parse(Encoding.UTF8.GetBytes(
            "{\"appId\":\"org.kazo.sample\",\"versions\":["
            + "{\"version\":\"1.0.0\",\"path\":\"1.0.0/s.hackapp\",\"sha256\":\"" + sha + "\"},"
            + "{\"version\":\"1.1.0\",\"path\":\"1.1.0/s.kazoapp\",\"sha256\":\"" + sha + "\"}]}"));

        Assert.Equal("1.0.0/s.hackapp", index.Find(SemanticVersion.Parse("1.0.0"))!.Path);
        Assert.True(AppPackage.HasPackageExtension("x.kazoapp"));
        Assert.True(AppPackage.HasPackageExtension("x.hackapp"));
        Assert.False(AppPackage.HasPackageExtension("x.zip"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"appId\":\"x\",\"versions\":[{\"version\":\"1.0.0\",\"path\":\"../x.kazoapp\",\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}]}")]
    [InlineData("{\"appId\":\"x\",\"versions\":[{\"version\":\"1.0.0\",\"path\":\"x.zip\",\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}]}")]
    [InlineData("{\"appId\":\"x\",\"versions\":[{\"version\":\"1.0.0\",\"path\":\"x.kazoapp\",\"sha256\":\"short\"}]}")]
    [InlineData("not json")]
    public void An_invalid_index_is_refused(string json)
    {
        Assert.Equal("apps.index.invalid", Assert.Throws<AppPackageException>(() => AppPackageIndex.Parse(Encoding.UTF8.GetBytes(json))).Code);
    }
}
