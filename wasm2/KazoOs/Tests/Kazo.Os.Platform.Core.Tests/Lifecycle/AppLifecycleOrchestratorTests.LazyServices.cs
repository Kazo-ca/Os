using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Tests.Lifecycle;

/// <summary>
/// Os#625: an automatic service whose code the browser host has not loaded yet (ADR 0047) still
/// starts at login, and only the services that start get their code loaded.
/// </summary>
public sealed partial class AppLifecycleOrchestratorTests
{
    private const string LazyFeatureId = "org.kazoos.lazy-services";
    private const string LazyAutomaticServiceId = LazyFeatureId + ".host";
    private const string LazyManualServiceId = LazyFeatureId + ".worker";

    [Fact]
    public async Task An_automatic_service_of_a_feature_starts_at_login_before_its_code_is_loaded()
    {
        Fixture fixture = LazyFeatureFixture();
        AuthenticatedPrincipal principal = await fixture.LoginAsync();

        IReadOnlyList<AppLaunchResult> results = await fixture.Orchestrator.StartAllServicesAsync(principal);

        AppLaunchResult result = Assert.Single(results);
        Assert.Equal(AppLaunchStatus.Launched, result.Status);
        Assert.Equal(LazyAutomaticServiceId, result.Process!.AppId);
        Assert.Equal([LazyAutomaticServiceId], fixture.Loader!.Loaded);
    }

    [Fact]
    public async Task A_service_set_to_manual_does_not_load_its_code_at_login()
    {
        Fixture fixture = LazyFeatureFixture();
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        await SeedServiceStartModeAsync(
            fixture.FileSystemService, principal.LoginName.Value, LazyAutomaticServiceId, ServiceStartMode.Manual);

        IReadOnlyList<AppLaunchResult> results = await fixture.Orchestrator.StartAllServicesAsync(principal);

        Assert.Empty(results);
        Assert.Empty(fixture.Loader!.Loaded);
    }

    [Fact]
    public async Task A_service_whose_code_does_not_load_does_not_stop_the_next_service()
    {
        Fixture fixture = LazyFeatureFixture();
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        await SeedServiceStartModeAsync(
            fixture.FileSystemService, principal.LoginName.Value, LazyManualServiceId, ServiceStartMode.Automatic);
        fixture.Loader!.Broken.Add(LazyAutomaticServiceId);

        IReadOnlyList<AppLaunchResult> results = await fixture.Orchestrator.StartAllServicesAsync(principal);

        Assert.Collection(
            results,
            broken =>
            {
                Assert.Equal(AppLaunchStatus.NotFound, broken.Status);
                Assert.Equal("lifecycle.app.asset-unavailable", broken.ErrorCode);
            },
            next => Assert.Equal(AppLaunchStatus.Launched, next.Status));
        Assert.Equal([LazyManualServiceId], fixture.Loader.Loaded);
    }

    /// <summary>A feature with an automatic and a manual service child, neither loaded yet.</summary>
    private static Fixture LazyFeatureFixture()
    {
        FeatureManifests feature = AppDefinitionMapper.ToFeatureManifests(new LazyServicesFeature(), KazoOsSdk.Version);
        AppEntryPointManifest entryPoint = new("Kazo.Os.Platform.Core.Tests", typeof(WaitingServiceApp).FullName!);
        AppManifest[] children = [.. feature.Children.Select(child => child with { EntryPoint = entryPoint })];
        return new Fixture(systemAppIds: [], lazyAppIds: [LazyAutomaticServiceId, LazyManualServiceId], [feature.Feature, .. children]);
    }

    /// <summary>A feature whose children are services (ADR 0260 §2, ADR 0262).</summary>
    private sealed class LazyServicesFeature : FeatureDefinition
    {
        public override string Id => LazyFeatureId;

        public override string Name => "Lazy services";

        public override string PublisherId => "org.kazoos";

        public override IReadOnlyList<FeatureEntryPoint> EntryPoints =>
        [
            new("host", "Host", AppKind.Service, typeof(WaitingServiceApp)) { AutoStart = true },
            new("worker", "Worker", AppKind.Service, typeof(WaitingServiceApp)),
        ];
    }
}
