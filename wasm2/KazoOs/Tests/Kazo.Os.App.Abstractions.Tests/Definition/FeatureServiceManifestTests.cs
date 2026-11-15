namespace Kazo.Os.App.Abstractions.Tests.Definition;

/// <summary>The services a feature provides, in its manifest (ADR 0262 §1).</summary>
public sealed class FeatureServiceManifestTests
{
    /// <summary>A feature that provides one contract, run as its daemon.</summary>
    public sealed class ProvidingFeature : FeatureDefinition
    {
        public override string Id => "ca.kazo.x86";
        public override string Name => "Os-x86";
        public override string PublisherId => "ca.kazo";
        public override IReadOnlyList<FeatureEntryPoint> EntryPoints =>
            [new("daemon", "x86 daemon", AppKind.Service, typeof(FeatureDefinitionTests.ServiceEntryPoint))];
        public override IReadOnlyList<FeatureService> ProvidedServices => [new(typeof(IServiceProvider), typeof(Provider))];
        public override string? ServiceEntryPoint => "daemon";
    }

    /// <summary>The implementation, in this assembly; the contract is in the core library.</summary>
    public sealed class Provider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private static AppManifest Feature() => AppDefinitionMapper.ToFeatureManifests(new ProvidingFeature(), "3.2.0").Feature;

    private static string[] Codes(ManifestValidationResult result) => [.. result.Errors.Select(static error => error.Code)];

    [Fact]
    public void The_mapper_writes_each_service_with_assembly_qualified_names_and_its_entry_point()
    {
        AppProvidedServiceManifest service = Assert.Single(Feature().ProvidedServices!);

        Assert.Equal($"System.IServiceProvider, {typeof(IServiceProvider).Assembly.GetName().Name}", service.Contract);
        Assert.Equal($"{typeof(Provider).FullName}, {typeof(Provider).Assembly.GetName().Name}", service.Implementation);
        Assert.Equal("ca.kazo.x86.daemon", service.EntryPoint);
        Assert.Equal("System.IServiceProvider", service.ContractType);
        Assert.Equal(typeof(Provider).Assembly.GetName().Name, service.ImplementationAssembly);
    }

    [Fact]
    public void The_services_round_trip_and_a_manifest_without_any_keeps_its_bytes()
    {
        FeatureManifests manifests = AppDefinitionMapper.ToFeatureManifests(new ProvidingFeature(), "3.2.0");
        string json = AppManifestJsonSerializer.SerializeCanonical(manifests.Feature);
        AppManifest read = AppManifestJsonSerializer.DeserializeStrict(json);

        Assert.Equal(manifests.Feature.ProvidedServices, read.ProvidedServices);
        Assert.True(AppManifestValidator.Validate(read).IsValid);
        Assert.True(AppManifestValidator.ValidateFeature(read, manifests.Children).IsValid);
        Assert.DoesNotContain("providedServices", AppManifestJsonSerializer.SerializeCanonical(manifests.Children[0]), StringComparison.Ordinal);
    }

    [Fact]
    public void Only_a_feature_provides_a_service()
    {
        AppManifest child = AppDefinitionMapper.ToFeatureManifests(new ProvidingFeature(), "3.2.0").Children[0] with
        {
            ProvidedServices = [new("Some.IContract, Some.Contracts", "Some.Service, Some")],
        };

        Assert.Contains("manifest.service.not-feature", Codes(AppManifestValidator.Validate(child)));
    }

    [Theory]
    [InlineData("Some.IContract", "Some.Service, Some")]
    [InlineData("Some.IContract, Some.Contracts, Version=1.0.0.0", "Some.Service, Some")]
    [InlineData("Some.IContract, Some.Contracts", "Some.Service")]
    public void A_service_names_its_types_with_their_simple_assembly(string contract, string implementation)
    {
        AppManifest feature = Feature() with { ProvidedServices = [new(contract, implementation)] };

        Assert.Contains("manifest.service.type", Codes(AppManifestValidator.Validate(feature)));
    }

    [Fact]
    public void A_contract_is_provided_once_and_never_from_the_assembly_of_its_implementation()
    {
        AppManifest feature = Feature() with
        {
            ProvidedServices =
            [
                new("Some.IContract, Some.Contracts", "Some.Service, Some"),
                new("Some.IContract, Some.Contracts", "Some.Other, Some"),
                new("Some.IOwn, Some", "Some.Own, Some"),
            ],
        };

        string[] codes = Codes(AppManifestValidator.Validate(feature));

        Assert.Contains("manifest.service.duplicate", codes);
        Assert.Contains("manifest.service.contract-assembly", codes);
    }

    [Fact]
    public void The_entry_point_of_a_service_is_a_child_of_the_feature()
    {
        FeatureManifests manifests = AppDefinitionMapper.ToFeatureManifests(new ProvidingFeature(), "3.2.0");
        AppManifest feature = manifests.Feature with
        {
            ProvidedServices = [manifests.Feature.ProvidedServices![0] with { EntryPoint = "ca.kazo.x86.nowhere" }],
        };

        Assert.Contains("manifest.service.entry-point", Codes(AppManifestValidator.ValidateFeature(feature, manifests.Children)));
    }
}
