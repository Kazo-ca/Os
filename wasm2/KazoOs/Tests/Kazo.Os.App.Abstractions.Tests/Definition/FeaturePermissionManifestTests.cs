namespace Kazo.Os.App.Abstractions.Tests.Definition;

/// <summary>The permissions a feature declares, in its manifest (ADR 0246 §1, §2).</summary>
public sealed class FeaturePermissionManifestTests
{
    /// <summary>A feature that declares the permissions of Os-x86 (176 A).</summary>
    public sealed class X86Permissions : FeatureDefinition
    {
        public override string Id => "ca.kazo.x86";
        public override string Name => "Os-x86";
        public override IReadOnlyList<FeatureEntryPoint> EntryPoints => [];
        public override string? CapabilityPrefix => "x86";
        public override IReadOnlyList<ProvidedCapability> ProvidedCapabilities =>
        [
            new("x86.programs.launch", CapabilityRisk.Normal, "Launch programs"),
            new("x86.vm.use", CapabilityRisk.Normal, "Use virtual machines"),
            new("x86.vm.destroy", CapabilityRisk.Dangerous, "Delete virtual machines") { RiskStatement = "Deletes a machine and its disk." },
            new("x86.vm", CapabilityRisk.Dangerous, "Manage virtual machines")
            {
                RiskStatement = "Uses, deletes and exports every machine.",
                Covers = ["x86.vm.use", "x86.vm.destroy"],
            },
        ];
    }

    /// <summary>A client that asks for them, with a dependency on the feature.</summary>
    public sealed class Client(bool withDependency) : AppDefinition
    {
        public override string Id => "ca.kazo.boxedwine";
        public override string Name => "BoxedWine";
        public override AppKind Kind => AppKind.Window;
        public override Type EntryPoint => typeof(Client);
        public override IReadOnlyList<string> Capabilities => ["x86.programs.launch", "x86.vm", AppCapabilities.NetworkRealAccess];
        public override IReadOnlyList<AppDependencyManifest> Dependencies => withDependency ? [new("ca.kazo.x86", "1.0.0", Optional: true)] : [];
    }

    private static AppManifest Feature() => AppDefinitionMapper.ToFeatureManifests(new X86Permissions(), "3.2.0").Feature;

    private static string[] Codes(AppManifest manifest) => [.. AppManifestValidator.Validate(manifest).Errors.Select(static error => error.Code)];

    [Fact]
    public void A_feature_declares_its_permissions_under_its_prefix_and_they_round_trip()
    {
        AppManifest feature = Feature();
        AppManifest read = AppManifestJsonSerializer.DeserializeStrict(AppManifestJsonSerializer.SerializeCanonical(feature));

        Assert.Empty(Codes(feature));
        Assert.Equal("x86", read.CapabilityPrefix);
        Assert.Equal(["x86.programs.launch", "x86.vm", "x86.vm.destroy", "x86.vm.use"], read.ProvidedCapabilities!.Select(static capability => capability.Id));
        AppProvidedCapabilityManifest vm = read.ProvidedCapabilities!.Single(static capability => capability.Id == "x86.vm");
        Assert.Equal((CapabilityRisk.Dangerous, "Uses, deletes and exports every machine."), (vm.Risk, vm.RiskStatement));
        Assert.Equal(["x86.vm.destroy", "x86.vm.use"], vm.Covers);
        Assert.Contains("\"risk\":\"dangerous\"", AppManifestJsonSerializer.SerializeCanonical(feature), StringComparison.Ordinal);
    }

    [Fact]
    public void An_app_may_ask_for_a_features_permission_only_with_a_dependency()
    {
        Assert.Empty(Codes(AppDefinitionMapper.ToManifest(new Client(withDependency: true))));
        Assert.Contains("manifest.capability.unknown", Codes(AppDefinitionMapper.ToManifest(new Client(withDependency: false))));
    }

    [Theory]
    [InlineData(null, "manifest.capability.prefix")]
    [InlineData("X86", "manifest.capability.prefix")]
    [InlineData("x86.vm", "manifest.capability.prefix")]
    [InlineData("filesystem", "manifest.capability.prefix-reserved")]
    [InlineData("network", "manifest.capability.prefix-reserved")]
    public void The_prefix_is_one_segment_the_built_in_catalog_does_not_use(string? prefix, string code)
    {
        Assert.Contains(code, Codes(Feature() with { CapabilityPrefix = prefix }));
    }

    [Fact]
    public void Each_permission_is_under_the_prefix_and_a_dangerous_one_says_its_risk()
    {
        AppManifest feature = Feature() with
        {
            ProvidedCapabilities =
            [
                new("other.vm.use", CapabilityRisk.Normal, "Elsewhere"),
                new("x86.vm.destroy", CapabilityRisk.Dangerous, "No risk sentence"),
                new("x86.vm.use", CapabilityRisk.Normal, " "),
            ],
        };

        string[] codes = Codes(feature);

        Assert.Contains("manifest.capability.provided-id", codes);
        Assert.Contains("manifest.capability.risk-statement", codes);
        Assert.Contains("manifest.capability.title", codes);
    }

    [Fact]
    public void A_permission_covers_only_its_features_permissions_and_never_a_more_dangerous_one()
    {
        AppManifest feature = Feature() with
        {
            ProvidedCapabilities =
            [
                new("x86.vm.destroy", CapabilityRisk.Dangerous, "Delete") { RiskStatement = "Deletes." },
                new("x86.vm.use", CapabilityRisk.Normal, "Use") { Covers = ["x86.vm.destroy", "x86.elsewhere"] },
            ],
        };

        string[] codes = Codes(feature);

        Assert.Contains("manifest.capability.covers-risk", codes);
        Assert.Contains("manifest.capability.covers", codes);
    }

    [Fact]
    public void Only_a_feature_declares_permissions()
    {
        AppManifest app = AppDefinitionMapper.ToManifest(new Client(withDependency: true)) with { CapabilityPrefix = "x86" };

        Assert.Contains("manifest.capability.not-feature", Codes(app));
    }
}
