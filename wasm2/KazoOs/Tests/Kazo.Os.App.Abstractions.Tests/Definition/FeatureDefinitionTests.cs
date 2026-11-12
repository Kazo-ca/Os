using Kazo.Os.App.Abstractions;

namespace Kazo.Os.App.Abstractions.Tests.Definition;

/// <summary>
/// The feature kind of ADR 0243: <see cref="FeatureDefinition"/> maps to one feature manifest and
/// one child manifest per entry point, the validator binds them, and <c>parent</c> round-trips.
/// </summary>
public sealed class FeatureDefinitionTests
{
    /// <summary>A feature with three children, the shape of Os-x86 (ADR 0243 §7).</summary>
    public sealed class X86Feature : FeatureDefinition
    {
        public override string Id => "ca.kazo.x86";
        public override string Name => "Os-x86";
        public override string Version => "1.2.3";
        public override string PublisherId => "ca.kazo";
        public override IReadOnlyList<string> Capabilities =>
            [AppCapabilities.FileSystemUserHomeRead, AppCapabilities.NetworkRealAccess, AppCapabilities.DialogFileOpen];
        public override IReadOnlyList<string> RealNetworkPorts => ["tcp/8080"];
        public override IReadOnlyList<FeatureEntryPoint> EntryPoints =>
        [
            new("cli", "x86", AppKind.Terminal, typeof(CliEntryPoint))
            {
                Capabilities = [AppCapabilities.FileSystemUserHomeRead, AppCapabilities.NetworkRealAccess],
                Terminal = new TerminalCommand("x86", "x86 <image>")
            },
            new("permission", "Network permission", AppKind.Window, typeof(WindowEntryPoint))
            {
                Capabilities = [AppCapabilities.DialogFileOpen],
                LaunchVisibility = AppLaunchVisibility.Visible,
                Window = new WindowOptions { DefaultWidth = 480, DefaultHeight = 320 }
            },
            new("daemon", "x86 daemon", AppKind.Service, typeof(ServiceEntryPoint)) { AutoStart = true }
        ];
    }

    public sealed class CliEntryPoint;

    public sealed class WindowEntryPoint;

    public sealed class ServiceEntryPoint;

    [Fact]
    public void A_feature_maps_to_its_own_manifest_and_one_child_per_entry_point()
    {
        FeatureManifests manifests = AppDefinitionMapper.ToFeatureManifests(new X86Feature(), "3.2.0");

        AppManifest feature = manifests.Feature;
        Assert.Equal((AppKind.Feature, "ca.kazo.x86", "1.2.3"), (feature.Kind, feature.Id, feature.Version));
        Assert.Null(feature.EntryPoint);
        Assert.Null(feature.Platform);
        Assert.Null(feature.Parent);
        Assert.Equal(AppExecutionHost.MainThread, feature.Execution);
        Assert.Equal(["ca.kazo.x86.cli", "ca.kazo.x86.permission", "ca.kazo.x86.daemon"], manifests.Children.Select(child => child.Id));
        Assert.All(manifests.Children, child =>
        {
            Assert.Equal("ca.kazo.x86", child.Parent);
            Assert.Equal(("1.2.3", "ca.kazo", "3.2.0"), (child.Version, child.PublisherId, child.SdkCompatibility.MinimumVersion));
            Assert.Equal(AppExecutionHost.MainThread, child.Execution);
            Assert.Empty(child.FileHandlers);
        });

        AppManifest cli = manifests.Children[0];
        Assert.Equal(AppKind.Terminal, cli.Kind);
        Assert.Equal(new AppEntryPointManifest("Kazo.Os.App.Abstractions.Tests.dll", typeof(CliEntryPoint).FullName!), cli.EntryPoint);
        Assert.Equal("x86", cli.Terminal!.Name);
        Assert.Equal(["tcp/8080"], cli.RealNetworkPorts);
        Assert.Null(manifests.Children[1].RealNetworkPorts);
        Assert.Equal(480, manifests.Children[1].Window!.DefaultWidth);
        Assert.True(manifests.Children[2].AutoStart);
        // ToManifest on a feature gives the feature's own manifest.
        Assert.Equal(AppKind.Feature, AppDefinitionMapper.ToManifest(new X86Feature(), "3.2.0").Kind);
    }

    [Fact]
    public void A_feature_and_its_children_pass_the_validator_and_bind_together()
    {
        FeatureManifests manifests = AppDefinitionMapper.ToFeatureManifests(new X86Feature(), "3.2.0");

        Assert.True(AppManifestValidator.Validate(manifests.Feature).IsValid, Describe(AppManifestValidator.Validate(manifests.Feature)));
        Assert.All(manifests.Children, child => Assert.True(AppManifestValidator.Validate(child).IsValid, Describe(AppManifestValidator.Validate(child))));
        Assert.True(AppManifestValidator.ValidateFeature(manifests.Feature, manifests.Children).IsValid);
    }

    [Fact]
    public void A_child_asking_for_a_capability_outside_the_set_is_refused()
    {
        FeatureManifests manifests = AppDefinitionMapper.ToFeatureManifests(new X86Feature(), "3.2.0");
        AppManifest greedy = manifests.Children[0] with { Capabilities = [AppCapabilities.FileSystemSystemWrite] };

        ManifestValidationResult result = AppManifestValidator.ValidateFeature(manifests.Feature, [greedy, manifests.Children[1]]);

        ManifestValidationError error = Assert.Single(result.Errors);
        Assert.Equal("manifest.feature.capability-outside-set", error.Code);
        Assert.Contains(AppCapabilities.FileSystemSystemWrite, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_feature_with_an_entry_point_or_a_parent_is_refused()
    {
        AppManifest feature = AppDefinitionMapper.ToFeatureManifests(new X86Feature(), "3.2.0").Feature;

        ManifestValidationResult withEntryPoint = AppManifestValidator.Validate(feature with { EntryPoint = new AppEntryPointManifest("a.dll", "A.B") });
        ManifestValidationResult withParent = AppManifestValidator.Validate(feature with { Parent = "ca.kazo.other" });

        Assert.Contains(withEntryPoint.Errors, error => error.Code == "manifest.feature.entryPoint.forbidden");
        Assert.Contains(withParent.Errors, error => error.Code == "manifest.feature.parent.kind");
    }

    [Fact]
    public void A_child_whose_id_is_not_under_its_parent_or_whose_parent_is_a_child_is_refused()
    {
        FeatureManifests manifests = AppDefinitionMapper.ToFeatureManifests(new X86Feature(), "3.2.0");
        AppManifest cli = manifests.Children[0];

        Assert.Contains(AppManifestValidator.Validate(cli with { Id = "ca.kazo.other.cli" }).Errors, error => error.Code == "manifest.feature.parent.prefix");
        Assert.Contains(AppManifestValidator.Validate(cli with { Parent = "Not An Id" }).Errors, error => error.Code == "manifest.feature.parent.invalid");
        Assert.Contains(
            AppManifestValidator.ValidateFeature(manifests.Feature, [cli with { Parent = "ca.kazo.x86.cli", Id = "ca.kazo.x86.cli.sub" }]).Errors,
            error => error.Code == "manifest.feature.child.parent");
        Assert.Contains(AppManifestValidator.ValidateFeature(cli, []).Errors, error => error.Code == "manifest.feature.kind");
    }

    [Fact]
    public void Parent_round_trips_through_the_strict_serializer_and_is_omitted_for_an_app()
    {
        FeatureManifests manifests = AppDefinitionMapper.ToFeatureManifests(new X86Feature(), "3.2.0");

        string featureJson = AppManifestJsonSerializer.SerializeCanonical(manifests.Feature);
        string childJson = AppManifestJsonSerializer.SerializeCanonical(manifests.Children[0]);

        Assert.DoesNotContain("\"parent\"", featureJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"entryPoint\"", featureJson, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"feature\"", featureJson, StringComparison.Ordinal);
        Assert.Contains("\"parent\":\"ca.kazo.x86\"", childJson, StringComparison.Ordinal);
        Assert.Equal("ca.kazo.x86", AppManifestJsonSerializer.DeserializeStrict(childJson).Parent);
        Assert.Equal(AppKind.Feature, AppManifestJsonSerializer.DeserializeStrict(featureJson).Kind);
    }

    private static string Describe(ManifestValidationResult result) =>
        string.Join("; ", result.Errors.Select(error => $"{error.Code} {error.Path}: {error.Message}"));
}
