namespace Kazo.Os.App.Abstractions.Tests.Definition;

/// <summary>
/// The roles a feature declares, in its manifest (ADR 0247 §1, ADR 0283): <c>&lt;prefix&gt;:&lt;name&gt;</c>
/// under its own prefix, and <c>requiresRole</c> naming one of them.
/// </summary>
public sealed class FeatureRoleManifestTests
{
    /// <summary>A feature that declares one role, with an entry point that requires it.</summary>
    public sealed class X86Roles(string roleId, string? requiredRole = null) : FeatureDefinition
    {
        public override string Id => "ca.kazo.x86";
        public override string Name => "Os-x86";
        public override string? CapabilityPrefix => "x86";
        public override IReadOnlyList<FeatureRole> Roles => [new(roleId, "VM manager") { Grants = ["admin"] }];
        public override IReadOnlyList<FeatureEntryPoint> EntryPoints =>
        [
            new("admin", "VM administration", AppKind.Terminal, typeof(X86Roles))
            {
                RequiresRole = requiredRole ?? roleId,
                Terminal = new TerminalCommand("x86-admin", "x86-admin"),
            },
        ];
    }

    private static string[] Codes(FeatureDefinition definition)
    {
        FeatureManifests manifests = AppDefinitionMapper.ToFeatureManifests(definition, "3.2.0");
        return [.. manifests.All.SelectMany(static manifest => AppManifestValidator.Validate(manifest).Errors).Select(static error => error.Code)];
    }

    [Fact]
    public void A_role_under_the_features_own_prefix_and_a_colon_is_valid_and_round_trips()
    {
        FeatureManifests manifests = AppDefinitionMapper.ToFeatureManifests(new X86Roles("x86:vm-manager"), "3.2.0");
        AppManifest read = AppManifestJsonSerializer.DeserializeStrict(AppManifestJsonSerializer.SerializeCanonical(manifests.Feature));

        Assert.Empty(Codes(new X86Roles("x86:vm-manager")));
        Assert.Equal("x86:vm-manager", Assert.Single(read.Roles!).Id);
        Assert.Equal("x86:vm-manager", Assert.Single(manifests.Children).RequiresRole);
    }

    [Theory]
    [InlineData("x86.vm-manager")]
    [InlineData("other:vm-manager")]
    [InlineData("x86:VM")]
    [InlineData("x86:")]
    public void A_role_outside_the_features_own_prefix_or_badly_named_is_refused(string roleId)
    {
        Assert.Contains("manifest.role.id", Codes(new X86Roles(roleId)));
    }

    [Fact]
    public void An_entry_point_cannot_require_a_role_its_feature_does_not_declare()
    {
        FeatureManifests manifests = AppDefinitionMapper.ToFeatureManifests(new X86Roles("x86:vm-manager", "x86:other"), "3.2.0");

        Assert.Contains(
            "manifest.feature.role-unknown",
            AppManifestValidator.ValidateFeature(manifests.Feature, manifests.Children).Errors.Select(static error => error.Code));
    }
}
