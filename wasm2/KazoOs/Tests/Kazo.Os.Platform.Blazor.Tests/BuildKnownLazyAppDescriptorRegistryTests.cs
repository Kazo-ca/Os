using System.Reflection;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.Platform.Blazor.LazyLoading;
using Kazo.Os.Platform.Core;
using Kazo.Os.Platform.Core.Lifecycle;

namespace Kazo.Os.Platform.Blazor.Tests;

/// <summary>Verifies lazy assets become validated lifecycle descriptors exactly once.</summary>
public sealed class BuildKnownLazyAppDescriptorRegistryTests
{
    [Fact]
    public async Task Concurrent_known_app_requests_load_and_discover_once()
    {
        AppManifest manifest = CreateManifest();
        AppCatalog catalog = Assert.IsType<AppCatalog>(AppCatalog.Build([manifest]).Catalog);
        var transport = new RecordingTransport(typeof(TestWindow).Assembly);
        var assemblies = new BuildKnownAssemblyLoaderRegistry([manifest.EntryPoint!.Assembly], transport);
        var registry = new BuildKnownLazyAppDescriptorRegistry(catalog, assemblies);

        AppDescriptorLoadResult[] results = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => registry.EnsureAvailableAsync(manifest.Id, CancellationToken.None).AsTask()));

        Assert.All(results, result => Assert.Equal(AppDescriptorLoadStatus.Available, result.Status));
        Assert.Equal(1, transport.CallCount);
        Assert.True(registry.Descriptors.TryGetValue(manifest.Id, out var descriptor));
        Assert.Equal(typeof(TestWindow), descriptor.EntryPointType);
    }

    [Fact]
    public async Task Undeclared_app_is_not_loaded()
    {
        AppManifest manifest = CreateManifest();
        AppCatalog catalog = Assert.IsType<AppCatalog>(AppCatalog.Build([manifest]).Catalog);
        var transport = new RecordingTransport(typeof(TestWindow).Assembly);
        var assemblies = new BuildKnownAssemblyLoaderRegistry([manifest.EntryPoint!.Assembly], transport);
        var registry = new BuildKnownLazyAppDescriptorRegistry(catalog, assemblies);

        AppDescriptorLoadResult result = await registry.EnsureAvailableAsync("org.kazoos.unknown", CancellationToken.None);

        Assert.Equal(AppDescriptorLoadStatus.NotDeclared, result.Status);
        Assert.Equal(0, transport.CallCount);
    }

    [Fact]
    public async Task Known_app_is_discovered_without_requiring_other_lazy_catalog_assemblies()
    {
        AppManifest requested = CreateManifest();
        AppManifest deferred = CreateManifest() with { Id = "org.kazoos.deferred-test" };
        AppCatalog catalog = Assert.IsType<AppCatalog>(AppCatalog.Build([requested, deferred]).Catalog);
        var transport = new RecordingTransport(typeof(TestWindow).Assembly);
        var assemblies = new BuildKnownAssemblyLoaderRegistry([requested.EntryPoint!.Assembly], transport);
        var registry = new BuildKnownLazyAppDescriptorRegistry(catalog, assemblies);

        AppDescriptorLoadResult result = await registry.EnsureAvailableAsync(
            requested.Id,
            CancellationToken.None);

        Assert.Equal(AppDescriptorLoadStatus.Available, result.Status);
        Assert.True(registry.Descriptors.ContainsKey(requested.Id));
        Assert.False(registry.Descriptors.ContainsKey(deferred.Id));
    }

    [Fact]
    public async Task A_child_of_a_feature_is_discovered_with_its_feature_and_without_its_siblings_assemblies()
    {
        // Os#621: the discovery catalog held the child alone, which the catalog refuses
        // (catalog.feature.parent.missing), so no child of a feature could launch.
        FeatureManifests feature = AppDefinitionMapper.ToFeatureManifests(new LazyFeature(), KazoOsSdk.Version);
        AppManifest window = feature.Children.Single(static child => child.Id == "org.kazoos.lazy-feature.window");
        AppManifest sibling = feature.Children.Single(static child => child.Id == "org.kazoos.lazy-feature.other") with
        {
            EntryPoint = new AppEntryPointManifest("Kazo.Os.Deferred.Sibling.dll", "Kazo.Os.Deferred.Sibling.Window"),
        };
        AppCatalog catalog = Assert.IsType<AppCatalog>(AppCatalog.Build([feature.Feature, window, sibling]).Catalog);
        var transport = new RecordingTransport(typeof(TestWindow).Assembly);
        var assemblies = new BuildKnownAssemblyLoaderRegistry([window.EntryPoint!.Assembly], transport);
        var registry = new BuildKnownLazyAppDescriptorRegistry(catalog, assemblies);

        AppDescriptorLoadResult result = await registry.EnsureAvailableAsync(window.Id, CancellationToken.None);

        Assert.Equal(AppDescriptorLoadStatus.Available, result.Status);
        Assert.Equal(typeof(TestWindow), registry.Descriptors[window.Id].EntryPointType);
        Assert.False(registry.Descriptors.ContainsKey(sibling.Id));
        Assert.False(registry.Descriptors.ContainsKey(feature.Feature.Id));
    }

    /// <summary>A feature with two window children (ADR 0260 §2).</summary>
    public sealed class LazyFeature : FeatureDefinition
    {
        public override string Id => "org.kazoos.lazy-feature";

        public override string Name => "Lazy feature";

        public override string PublisherId => "pub.kazoos";

        public override IReadOnlyList<FeatureEntryPoint> EntryPoints =>
        [
            new("window", "Lazy window", AppKind.Window, typeof(TestWindow)),
            new("other", "Other window", AppKind.Window, typeof(TestWindow)),
        ];
    }

    private static AppManifest CreateManifest() => new()
    {
        Id = "org.kazoos.lazy-test",
        Name = "Lazy test",
        Version = "1.0.0",
        PublisherId = "pub.kazoos",
        Description = "Validates build-known lazy descriptor discovery.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest($"{typeof(TestWindow).Assembly.GetName().Name}.dll", typeof(TestWindow).FullName!),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = new AppResourceProfileManifest(0, 0, 0, 0, 0, 0, 0, 0)
    };

    private sealed class TestWindow : WindowAppBase;

    private sealed class RecordingTransport(Assembly assembly) : IBuildKnownAssemblyTransport
    {
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<Assembly>> LoadAsync(IReadOnlyList<string> names, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult<IReadOnlyList<Assembly>>([assembly]);
        }
    }
}
