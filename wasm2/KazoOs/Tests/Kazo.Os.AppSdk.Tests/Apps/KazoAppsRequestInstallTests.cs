using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Apps;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.AppSdk.Tests.Apps;

/// <summary>
/// <see cref="KazoApps.RequestInstallAsync"/> (ADR 0248 §2): an app asks for an optional dependency;
/// the Store opens on its page, or, without a Store, the user is told to use <c>kazopkg</c>. The app
/// never installs anything itself.
/// </summary>
public sealed class KazoAppsRequestInstallTests
{
    [Fact]
    public async Task RequestInstallAsync_opens_the_store_on_the_package()
    {
        FakeAppIntentGateway intents = new();
        KazoInstallRequestResult result;
        using (Enter(intents))
        {
            result = await KazoApps.RequestInstallAsync("org.kazo.feature");
        }

        (string appId, IReadOnlyList<string> arguments) = Assert.Single(intents.Requests);
        Assert.Equal(KazoApps.StoreAppId, appId);
        Assert.Equal(["--app=org.kazo.feature"], arguments);
        Assert.True(result.StoreOpened);
        Assert.Null(result.Message);
    }

    [Theory]
    [InlineData(AppIntentLaunchOutcome.NotFound)]
    [InlineData(AppIntentLaunchOutcome.Disabled)]
    public async Task RequestInstallAsync_without_a_store_tells_to_use_kazopkg(AppIntentLaunchOutcome outcome)
    {
        FakeAppIntentGateway intents = new FakeAppIntentGateway().WithResult(KazoApps.StoreAppId, new AppIntentLaunchResult(outcome));
        KazoInstallRequestResult result;
        using (Enter(intents))
        {
            result = await KazoApps.RequestInstallAsync("org.kazo.feature");
        }

        Assert.False(result.StoreOpened);
        Assert.Contains("kazopkg install org.kazo.feature", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestInstallAsync_reports_a_store_that_fails_to_start()
    {
        FakeAppIntentGateway intents = new FakeAppIntentGateway().WithResult(
            KazoApps.StoreAppId, new AppIntentLaunchResult(AppIntentLaunchOutcome.Faulted, "apps.launch.failed"));
        using (Enter(intents))
        {
            await Assert.ThrowsAsync<KazoAppLaunchException>(() => KazoApps.RequestInstallAsync("org.kazo.feature"));
        }
    }

    private static IDisposable Enter(FakeAppIntentGateway intents) =>
        KazoExecutionContext.Enter(new MinimalAppExecutionContext(AppDefinitionMapper.ToManifest(new CallerApp()), intents: intents));

    private sealed class CallerApp : AppDefinition
    {
        public override string Id => "org.example.caller";

        public override string Name => "Caller";

        public override AppKind Kind => AppKind.Window;

        public override Type EntryPoint => typeof(CallerApp);

        public override IReadOnlyList<string> Capabilities => [AppCapabilities.AppsLaunch];
    }
}
