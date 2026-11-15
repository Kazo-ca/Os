using System.Reflection;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Features;
using Kazo.Os.Platform.Core.Features;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.Platform.Core.Tests.Features;

/// <summary>An app asks through <see cref="KazoFeatures"/> (ADR 0262 §2), on its own gateway.</summary>
public sealed class KazoFeaturesTests
{
    private sealed class FeaturesContext(AppManifest manifest, IAppFeatureGateway features) : IAppExecutionContext
    {
        private readonly MinimalAppExecutionContext _inner = new(manifest);

        public AppManifest Manifest => _inner.Manifest;
        public Guid InstanceId => _inner.InstanceId;
        public string UserId => _inner.UserId;
        public AppAuthority UserAuthority => _inner.UserAuthority;
        public IReadOnlySet<string> GrantedCapabilities => _inner.GrantedCapabilities;
        public Kazo.Os.Simulation.Abstractions.Sessions.SessionId SessionId => _inner.SessionId;
        public Kazo.Os.Simulation.Abstractions.Processes.ProcessId ProcessId => _inner.ProcessId;
        public CancellationToken CancellationToken => _inner.CancellationToken;
        public Kazo.Os.Simulation.Abstractions.Gateways.ICapabilityChecker Capabilities => _inner.Capabilities;
        public Kazo.Os.Simulation.Abstractions.Gateways.IAppFileSystemGateway FileSystem => _inner.FileSystem;
        public Kazo.Os.Simulation.Abstractions.Gateways.IAppEventGateway Events => _inner.Events;
        public Kazo.Os.Simulation.Abstractions.Gateways.IAppNotificationGateway Notifications => _inner.Notifications;
        public Kazo.Os.Simulation.Abstractions.Gateways.IAppLoggingGateway Logging => _inner.Logging;
        public Kazo.Os.Simulation.Abstractions.Gateways.IAppClockGateway Clock => _inner.Clock;
        public Kazo.Os.Simulation.Abstractions.Gateways.IAppProcessGateway Processes => _inner.Processes;
        public IAppFeatureGateway Features { get; } = features;
    }

    private static (FeatureServiceResolver Resolver, AppCatalog Catalog) Page(params AppManifest[] manifests)
    {
        AppCatalog catalog = AppCatalog.Build(manifests).Catalog!.CreateLiveCopy();
        FeatureServiceResolver resolver = new(
            catalog,
            new AppEnablementRegistry(catalog),
            (_, _) => ValueTask.FromResult<Assembly?>(typeof(MachineService).Assembly),
            (runAs, _) => new MinimalAppExecutionContext(runAs, userId: "kazoos-svc"));
        return (resolver, catalog);
    }

    [Fact]
    public async Task The_facade_gives_the_instance_then_null_once_the_feature_is_removed()
    {
        AppManifest client = FeatureServiceFixtures.Client();
        (FeatureServiceResolver resolver, AppCatalog catalog) = Page(FeatureServiceFixtures.Feature(), client);
        using (resolver)
        using (KazoExecutionContext.Enter(new FeaturesContext(client, new AppFeatureGateway(resolver, client))))
        {
            Assert.True(KazoFeatures.IsAvailable<IFixtureMachine>());
            IFixtureMachine? machine = await KazoFeatures.GetServiceAsync<IFixtureMachine>();
            Assert.NotNull(machine);
            Assert.Equal($"machine of {FeatureServiceFixtures.FeatureId} for {FeatureServiceFixtures.ClientId}", machine.Describe());

            catalog.ReplaceContents(AppCatalog.Build([client]).Catalog!);

            Assert.False(KazoFeatures.IsAvailable<IFixtureMachine>());
            Assert.Null(await KazoFeatures.GetServiceAsync<IFixtureMachine>());
            Assert.Equal(KazoFeatureServiceState.NotInstalled, (await KazoFeatures.GetStatusAsync<IFixtureMachine>()).State);
        }
    }

    [Fact]
    public async Task Changed_runs_the_handler_in_the_apps_context_with_its_state()
    {
        AppManifest client = FeatureServiceFixtures.Client();
        (FeatureServiceResolver resolver, AppCatalog catalog) = Page(client);
        AppFeatureGateway gateway = new(resolver, client);
        FeaturesContext context = new(client, gateway);
        gateway.BindAmbientContext(context);
        List<(string Contract, KazoFeatureServiceState State, string? Ambient)> seen = [];
        gateway.Changed += (_, change) => seen.Add((change.Contract, change.State, KazoExecutionContext.CurrentOrNull?.Manifest.Id));

        using (resolver)
        {
            catalog.ReplaceContents(AppCatalog.Build([FeatureServiceFixtures.Feature(), client]).Catalog!);
        }

        Assert.Equal([(typeof(IFixtureMachine).FullName!, KazoFeatureServiceState.Available, (string?)FeatureServiceFixtures.ClientId)], seen);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Without_feature_services_every_contract_is_not_installed()
    {
        using (KazoExecutionContext.Enter(new MinimalAppExecutionContext(FeatureServiceFixtures.Client())))
        {
            Assert.Null(await KazoFeatures.GetServiceAsync<IFixtureMachine>());
            Assert.False(KazoFeatures.IsAvailable<IFixtureMachine>());
            Assert.Equal(KazoFeatureServiceStatus.NotInstalled, await KazoFeatures.GetStatusAsync<IFixtureMachine>());
        }
    }

    [Fact]
    public void Inside_a_service_the_caller_is_the_app_that_calls_and_the_feature_can_act_as_itself()
    {
        AppManifest client = FeatureServiceFixtures.Client();
        MinimalAppExecutionContext feature = new(FeatureServiceFixtures.Feature(), userId: "kazoos-svc");
        FeatureServiceContext context = new(feature, CancellationToken.None);

        Assert.Null(context.Caller);
        using (KazoExecutionContext.Enter(new MinimalAppExecutionContext(client)))
        {
            Assert.Equal(FeatureServiceFixtures.ClientId, context.Caller?.AppId);
            using (context.EnterFeature())
            {
                Assert.Same(feature, KazoExecutionContext.Current);
                Assert.Equal(FeatureServiceFixtures.ClientId, context.Caller?.AppId);
            }

            Assert.Equal(FeatureServiceFixtures.ClientId, KazoExecutionContext.Current.Manifest.Id);
        }
    }
}
