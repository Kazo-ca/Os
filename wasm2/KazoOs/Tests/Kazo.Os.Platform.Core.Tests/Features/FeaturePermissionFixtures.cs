using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Platform.Core.Tests.Features;

/// <summary>
/// A feature of fixture that declares permissions (ADR 0246 §1), the shape of Os-x86's
/// <c>x86.vm.use</c> (normal), <c>x86.vm.destroy</c> (dangerous) and <c>x86.vm</c> (dangerous, covers both).
/// </summary>
public sealed class PermissionFeature : FeatureDefinition
{
    public override string Id => FeaturePermissionFixtures.FeatureId;

    public override string Name => "Machines";

    public override IReadOnlyList<FeatureEntryPoint> EntryPoints => [];

    public override string? CapabilityPrefix => "machine";

    public override IReadOnlyList<ProvidedCapability> ProvidedCapabilities =>
    [
        new(FeaturePermissionFixtures.Use, CapabilityRisk.Normal, "Use the machines"),
        new(FeaturePermissionFixtures.Destroy, CapabilityRisk.Dangerous, "Destroy machines") { RiskStatement = "The app can delete a machine and its disk." },
        new(FeaturePermissionFixtures.All, CapabilityRisk.Dangerous, "Manage every machine")
        {
            RiskStatement = "The app can use, delete and export every machine.",
            Covers = [FeaturePermissionFixtures.Use, FeaturePermissionFixtures.Destroy],
        },
    ];
}

/// <summary>An app that asks for permissions of the feature, with a dependency on it.</summary>
public sealed class PermissionClient(params string[] capabilities) : AppDefinition
{
    public override string Id => FeaturePermissionFixtures.ClientId;

    public override string Name => "Machine client";

    public override AppKind Kind => AppKind.Window;

    public override Type EntryPoint => typeof(PermissionClient);

    public override IReadOnlyList<string> Capabilities => capabilities;

    public override IReadOnlyList<AppDependencyManifest> Dependencies => [new(FeaturePermissionFixtures.FeatureId, "1.0.0", Optional: true)];
}

/// <summary>Builds the manifests of the tests of ADR 0246.</summary>
public static class FeaturePermissionFixtures
{
    public const string FeatureId = "org.test.machines";
    public const string ClientId = "org.test.machine-client";
    public const string Use = "machine.vm.use";
    public const string Destroy = "machine.vm.destroy";
    public const string All = "machine.vm";

    public static AppManifest Feature() =>
        AppDefinitionMapper.ToFeatureManifests(new PermissionFeature(), KazoOsSdk.Version).Feature;

    public static AppManifest Client(params string[] capabilities) =>
        AppDefinitionMapper.ToManifest(new PermissionClient(capabilities.Length == 0 ? [Use, Destroy] : capabilities));

    public static AppCatalog Catalog(bool withFeature = true)
    {
        AppCatalogBuildResult built = AppCatalog.Build(withFeature ? [Feature(), Client()] : [Client()]);
        Assert.True(built.IsSuccess, string.Join(", ", built.Errors.Select(static error => error.Message)));
        return built.Catalog!;
    }
}
