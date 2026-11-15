using System.Reflection;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Features;
using Kazo.Os.Platform.Core.Features;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.Platform.Core.Tests.Features;

/// <summary>
/// The services features provide to apps (ADR 0262): resolved by contract, null when the feature is
/// absent or disabled, never cached as null, one instance per page in the feature's context.
/// </summary>
public sealed partial class FeatureServiceResolverTests
{
    private sealed class Page : IDisposable
    {
        public Page(params AppManifest[] manifests)
        {
            Catalog = Build(manifests).CreateLiveCopy();
            Enablement = new AppEnablementRegistry(Catalog);
            Resolver = new FeatureServiceResolver(
                Catalog,
                Enablement,
                (assembly, _) =>
                {
                    Loads.Add(assembly);
                    return ValueTask.FromResult<Assembly?>(typeof(MachineService).Assembly);
                },
                (runAs, implementation) =>
                {
                    Started.Add((runAs.Id, implementation));
                    return new MinimalAppExecutionContext(runAs, logging: FeatureLog, userId: "kazoos-svc");
                });
        }

        public AppCatalog Catalog { get; }

        public AppEnablementRegistry Enablement { get; }

        public FeatureServiceResolver Resolver { get; }

        public HashSet<string> Loads { get; } = [];

        /// <summary>Gets the feature's own log, <c>/var/log/apps/&lt;feature&gt;.log</c>.</summary>
        public FakeAppLoggingGateway FeatureLog { get; } = new();

        public List<(string AppId, Type Implementation)> Started { get; } = [];

        public void Install(params AppManifest[] manifests) => Catalog.ReplaceContents(Build(manifests));

        public void Dispose() => Resolver.Dispose();

        private static AppCatalog Build(IEnumerable<AppManifest> manifests)
        {
            AppCatalogBuildResult built = AppCatalog.Build(manifests);
            Assert.True(built.IsSuccess, string.Join(", ", built.Errors.Select(static error => error.Message)));
            return built.Catalog!;
        }
    }

    [Fact]
    public async Task An_app_gets_the_instance_of_the_feature_that_provides_the_contract_then_null_without_it()
    {
        // The case of Ix86 (Os-x86): a client resolves an interface a feature provides.
        AppManifest client = FeatureServiceFixtures.Client();
        using Page page = new(FeatureServiceFixtures.Feature(), client);

        (object? instance, KazoFeatureServiceStatus status) = await page.Resolver.ResolveAsync(client, typeof(IFixtureMachine));

        IFixtureMachine machine = Assert.IsAssignableFrom<IFixtureMachine>(instance);
        Assert.Equal(KazoFeatureServiceState.Available, status.State);
        Assert.Equal(FeatureServiceFixtures.FeatureId, status.FeatureId);
        Assert.Equal(FeatureServiceFixtures.FeatureVersion, status.InstalledVersion);
        // ADR 0262 §1: created in the feature's context, never in the caller's.
        Assert.Equal([(FeatureServiceFixtures.FeatureId, typeof(MachineService))], page.Started);
        Assert.Equal(["Kazo.Os.Platform.Core.Tests.dll"], page.Loads);
        using (KazoExecutionContext.Enter(new MinimalAppExecutionContext(client)))
        {
            Assert.Equal($"machine of {FeatureServiceFixtures.FeatureId} for {FeatureServiceFixtures.ClientId}", machine.Describe());
        }

        page.Install(client);
        (object? gone, KazoFeatureServiceStatus removed) = await page.Resolver.ResolveAsync(client, typeof(IFixtureMachine));

        Assert.Null(gone);
        Assert.Equal(KazoFeatureServiceState.NotInstalled, removed.State);
    }

    [Fact]
    public async Task Every_app_of_the_page_gets_the_same_instance()
    {
        AppManifest client = FeatureServiceFixtures.Client();
        using Page page = new(FeatureServiceFixtures.Feature(), client);

        (object? first, _) = await page.Resolver.ResolveAsync(client, typeof(IFixtureMachine));
        (object? second, _) = await page.Resolver.ResolveAsync(client, typeof(IFixtureMachine));

        Assert.Same(first, second);
        Assert.Single(page.Started);
    }

    [Fact]
    public async Task A_feature_installed_while_the_page_runs_is_found_at_the_next_call()
    {
        // ADR 0262 §2: the null is never cached (ADR 0214 §1).
        AppManifest client = FeatureServiceFixtures.Client();
        using Page page = new(client);

        (object? before, KazoFeatureServiceStatus absent) = await page.Resolver.ResolveAsync(client, typeof(IFixtureMachine));
        page.Install(FeatureServiceFixtures.Feature(), client);
        (object? after, _) = await page.Resolver.ResolveAsync(client, typeof(IFixtureMachine));

        Assert.Null(before);
        Assert.Equal(KazoFeatureServiceState.NotInstalled, absent.State);
        Assert.IsAssignableFrom<IFixtureMachine>(after);
    }
}
