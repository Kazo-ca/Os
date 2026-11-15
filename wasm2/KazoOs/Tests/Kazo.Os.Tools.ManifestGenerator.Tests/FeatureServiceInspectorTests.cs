using System.Reflection;
using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Tools.ManifestGenerator.Tests;

/// <summary>The build checks of ADR 0262: a service the OS can create (<c>KOSAPP0035</c>) and a declared dependency (<c>KOSAPP0034</c>).</summary>
public sealed class FeatureServiceInspectorTests
{
    private static readonly Assembly Fixtures = typeof(ValidCommandApp).Assembly;

    private static InspectionResult Inspect(Type type) => AppDefinitionInspector.Inspect(Fixtures, [type], "1.0.0");

    [Fact]
    public void A_valid_service_is_written_in_the_feature_manifest()
    {
        InspectionResult result = Inspect(typeof(ProvidingFeature));

        Assert.True(result.IsSuccess, string.Join("; ", result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        AppProvidedServiceManifest service = Assert.Single(Assert.Single(result.Manifests).Manifest.ProvidedServices!);
        Assert.Equal("org.kazoos.tests.feature.daemon", service.EntryPoint);
        Assert.Equal(typeof(FixtureServiceProvider).FullName, service.ImplementationType);
    }

    [Theory]
    [InlineData(typeof(FeatureWithWrongProvider), "does not implement")]
    [InlineData(typeof(FeatureWithUncreatableProvider), "public constructor")]
    [InlineData(typeof(FeatureWithOwnContract), "shared library")]
    [InlineData(typeof(FeatureWithUnknownServiceEntryPoint), "ServiceEntryPoint")]
    public void KOSAPP0035_a_service_the_os_cannot_create(Type feature, string reason)
    {
        InspectionResult result = Inspect(feature);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == GeneratorCodes.FeatureService && diagnostic.Message.Contains(reason, StringComparison.Ordinal));
    }

    [Fact]
    public void KOSAPP0034_an_app_that_calls_KazoFeatures_without_any_dependency()
    {
        byte[] bytes = File.ReadAllBytes(Fixtures.Location);
        AppManifest withoutDependency = Assert.Single(Inspect(typeof(ValidCommandApp)).Manifests).Manifest;

        IReadOnlyList<GeneratorDiagnostic> diagnostics = FeatureDependencyInspector.Inspect(bytes, [withoutDependency], "fixtures");

        Assert.True(FeatureDependencyInspector.UsesFacade(bytes));
        Assert.Equal(GeneratorCodes.FeatureDependency, Assert.Single(diagnostics).Code);
        Assert.Empty(FeatureDependencyInspector.Inspect(
            bytes, [withoutDependency with { Dependencies = [new("ca.kazo.x86", "1.0.0", Optional: true)] }], "fixtures"));
    }

    [Fact]
    public void An_assembly_that_never_calls_KazoFeatures_needs_no_dependency()
    {
        byte[] bytes = File.ReadAllBytes(typeof(AppManifest).Assembly.Location);

        Assert.False(FeatureDependencyInspector.UsesFacade(bytes));
    }
}
