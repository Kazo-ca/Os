using System.Reflection;
using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Tools.ManifestGenerator.Tests;

/// <summary>One test per KOSAPP rule of ADR 0047 §1, run in-process on fixture types.</summary>
public sealed class AppDefinitionInspectorTests
{
    private static readonly Assembly Fixtures = typeof(ValidCommandApp).Assembly;

    private static InspectionResult Inspect(params Type[] types) =>
        AppDefinitionInspector.Inspect(Fixtures, types, "1.0.0");

    private static void AssertSingleCode(InspectionResult result, string code, Type subject)
    {
        Assert.False(result.IsSuccess);
        Assert.Empty(result.Manifests);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code && diagnostic.Subject == subject.FullName);
    }

    [Fact]
    public void Valid_definition_produces_a_validated_canonical_manifest()
    {
        InspectionResult result = Inspect(typeof(ValidCommandApp));

        Assert.True(result.IsSuccess);
        GeneratedManifest generated = Assert.Single(result.Manifests);
        Assert.Equal("org.kazoos.tests.valid", generated.Manifest.Id);
        Assert.Equal(typeof(ValidCommandApp).FullName, generated.DefinitionType);
        Assert.Equal(AppManifestJsonSerializer.SerializeCanonical(generated.Manifest), generated.CanonicalJson);
        Assert.Equal(new AppEntryPointManifest("Kazo.Os.Tools.ManifestGenerator.Tests.dll", typeof(FixtureCommand).FullName!), generated.Manifest.EntryPoint);
    }

    [Fact]
    public void Two_definitions_produce_two_manifests_sorted_by_id()
    {
        InspectionResult result = Inspect(typeof(ValidCommandApp), typeof(SecondCommandApp));

        Assert.True(result.IsSuccess);
        Assert.Equal(["org.kazoos.tests.second", "org.kazoos.tests.valid"], result.Manifests.Select(item => item.Manifest.Id));
    }

    [Fact]
    public void KOSAPP0001_no_definition() =>
        Assert.Equal(GeneratorCodes.NoDefinition, Assert.Single(Inspect().Diagnostics).Code);

    [Fact]
    public void KOSAPP0002_not_public() => AssertSingleCode(Inspect(typeof(InternalApp)), GeneratorCodes.NotPublic, typeof(InternalApp));

    [Fact]
    public void KOSAPP0003_abstract() => AssertSingleCode(Inspect(typeof(AbstractApp)), GeneratorCodes.Abstract, typeof(AbstractApp));

    [Fact]
    public void KOSAPP0004_not_an_app_definition() =>
        AssertSingleCode(Inspect(typeof(NotADefinition)), GeneratorCodes.NotAnAppDefinition, typeof(NotADefinition));

    [Fact]
    public void KOSAPP0005_no_parameterless_constructor() =>
        AssertSingleCode(Inspect(typeof(NoDefaultConstructorApp)), GeneratorCodes.NoParameterlessConstructor, typeof(NoDefaultConstructorApp));

    [Fact]
    public void KOSAPP0006_getter_throws() => AssertSingleCode(Inspect(typeof(ThrowingApp)), GeneratorCodes.DefinitionThrew, typeof(ThrowingApp));

    [Fact]
    public void KOSAPP0007_entry_point_in_another_assembly() =>
        AssertSingleCode(Inspect(typeof(ForeignEntryPointApp)), GeneratorCodes.EntryPointAssembly, typeof(ForeignEntryPointApp));

    [Fact]
    public void KOSAPP0008_non_deterministic_getter() => AssertSingleCode(Inspect(typeof(ClockApp)), GeneratorCodes.NonDeterministic, typeof(ClockApp));

    [Fact]
    public void KOSAPP0009_validator_rejects_manifest() => AssertSingleCode(Inspect(typeof(InvalidIdApp)), GeneratorCodes.InvalidManifest, typeof(InvalidIdApp));

    [Fact]
    public void KOSAPP0010_entry_point_breaks_discovery_rules() =>
        AssertSingleCode(Inspect(typeof(WrongKindApp)), GeneratorCodes.EntryPointKind, typeof(WrongKindApp));

    [Fact]
    public void KOSAPP0011_duplicate_id_in_one_assembly()
    {
        InspectionResult result = Inspect(typeof(ValidCommandApp), typeof(DuplicateIdApp));

        GeneratorDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(GeneratorCodes.DuplicateIdInAssembly, diagnostic.Code);
        Assert.Empty(result.Manifests);
    }

    [Fact]
    public void Any_error_suppresses_every_manifest()
    {
        InspectionResult result = Inspect(typeof(ValidCommandApp), typeof(ThrowingApp));

        Assert.False(result.IsSuccess);
        Assert.Empty(result.Manifests);
    }

    [Fact]
    public void A_feature_with_three_children_produces_the_feature_manifest_and_one_manifest_per_child()
    {
        InspectionResult result = Inspect(typeof(ValidFeature));

        Assert.True(result.IsSuccess, string.Join("; ", result.Diagnostics.Select(d => $"{d.Code} {d.Message}")));
        GeneratedManifest feature = Assert.Single(result.Manifests);
        Assert.Equal((AppKind.Feature, "org.kazoos.tests.feature"), (feature.Manifest.Kind, feature.Manifest.Id));
        Assert.Null(feature.Manifest.EntryPoint);
        Assert.Equal(AppManifestJsonSerializer.SerializeCanonical(feature.Manifest), feature.CanonicalJson);
        Assert.Equal(
            ["org.kazoos.tests.feature.cli", "org.kazoos.tests.feature.console", "org.kazoos.tests.feature.daemon"],
            feature.Children.Select(child => child.Manifest.Id));
        Assert.All(feature.Children, child => Assert.Equal("org.kazoos.tests.feature", child.Manifest.Parent));
        Assert.Equal([AppKind.Terminal, AppKind.Terminal, AppKind.Service], feature.Children.Select(child => child.Manifest.Kind));
        Assert.Contains("\"parent\":\"org.kazoos.tests.feature\"", feature.Children[0].CanonicalJson, StringComparison.Ordinal);
        Assert.Equal([typeof(FixtureCommand), typeof(FixtureCommand), typeof(FixtureService)], feature.EntryPointTypes);
    }

    [Fact]
    public void KOSAPP0032_feature_with_an_entry_point_of_its_own() =>
        AssertSingleCode(Inspect(typeof(FeatureWithEntryPoint)), GeneratorCodes.FeatureEntryPoint, typeof(FeatureWithEntryPoint));

    [Fact]
    public void KOSAPP0033_ordinary_definition_claiming_the_feature_kind() =>
        AssertSingleCode(Inspect(typeof(PlainFeatureKindApp)), GeneratorCodes.FeatureOnly, typeof(PlainFeatureKindApp));

    [Fact]
    public void KOSAPP0033_feature_next_to_another_app_in_one_project()
    {
        InspectionResult result = Inspect(typeof(ValidFeature), typeof(ValidCommandApp));

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == GeneratorCodes.FeatureOnly && diagnostic.Subject == typeof(ValidFeature).FullName);
    }

    [Fact]
    public void KOSAPP0009_child_asking_for_a_capability_outside_the_set()
    {
        InspectionResult result = Inspect(typeof(FeatureChildOutsideSet));

        AssertSingleCode(result, GeneratorCodes.InvalidManifest, typeof(FeatureChildOutsideSet));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("manifest.feature.capability-outside-set", StringComparison.Ordinal));
    }

    [Fact]
    public void Assembly_scan_finds_classes_marked_with_the_attribute_only()
    {
        InspectionResult result = AppDefinitionInspector.Inspect(Fixtures);

        Assert.True(result.IsSuccess, string.Join("; ", result.Diagnostics));
        Assert.Equal("org.kazoos.tests.marked", Assert.Single(result.Manifests).Manifest.Id);
    }

    [Fact]
    public void Sdk_minimum_version_comes_from_the_compiled_reference()
    {
        string abstractions = typeof(AppDefinition).Assembly.Location;

        Assert.Equal(KazoOsSdk.Version, AppDefinitionInspector.SdkMinimumVersionOf(Fixtures, ["/missing/Kazo.Os.App.Abstractions.dll", abstractions]));
        Assert.Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$", KazoOsSdk.Version);
    }

    [Fact]
    public void Declared_assets_are_hashed_from_wwwroot()
    {
        string root = Directory.CreateTempSubdirectory("kazoos-assets-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "data"));
            File.WriteAllText(Path.Combine(root, "data", "table.json"), "[1,2,3]");

            InspectionResult result = AppDefinitionInspector.Inspect(Fixtures, [typeof(AssetApp)], KazoOsSdk.Version, root);

            Assert.True(result.IsSuccess, string.Join("; ", result.Diagnostics));
            AssetManifest asset = Assert.Single(Assert.Single(result.Manifests).Manifest.Assets);
            Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("[1,2,3]"u8.ToArray())), asset.Sha256);
            Assert.Contains(asset.Sha256, Assert.Single(result.Manifests).CanonicalJson, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_declared_asset_without_a_file_fails_with_HOSAPP0023()
    {
        InspectionResult result = AppDefinitionInspector.Inspect(Fixtures, [typeof(AssetApp)], KazoOsSdk.Version, Path.Combine(Path.GetTempPath(), "no-such-wwwroot"));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == GeneratorCodes.AssetMissing);
    }

    [Fact]
    public void Sdk_minimum_version_falls_back_to_the_tool_version() =>
        Assert.Equal(KazoOsSdk.Version, AppDefinitionInspector.SdkMinimumVersionOf(Fixtures, []));

    [Fact]
    public void Diagnostic_uses_msbuild_canonical_error_format() =>
        Assert.Equal(
            "/src/App.csproj : error KOSAPP0005: [Ns.App] message",
            new GeneratorDiagnostic("KOSAPP0005", "Ns.App", "message").ToMsBuildLine("/src/App.csproj"));
}
