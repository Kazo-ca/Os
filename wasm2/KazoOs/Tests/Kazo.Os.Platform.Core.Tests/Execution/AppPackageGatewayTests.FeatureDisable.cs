using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Apps;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Platform.Core.Apps;
using Kazo.Os.Platform.Core.Apps.Packages;
using Kazo.Os.Platform.Core.Elevation;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Platform.Core.Tests.Apps.Packages;
using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.Platform.Core.Tests.Execution;

/// <summary>
/// ADR 0287 §4 and §7 through the management gateway: an administrator disables or enables a feature,
/// nothing else can be disabled, and removing a feature's package forgets its "disabled" line.
/// </summary>
public sealed partial class AppPackageGatewayTests
{
    private const string FeaturesConfig = "/etc/kazoos/features.config";

    [Fact]
    public async Task Disabling_and_enabling_a_feature_ask_an_administrator_then_hand_the_system_writer_on()
    {
        Harness harness = await Harness.CreateAsync();
        harness.UseCatalogWithFeature();
        CountingAgent agent = new(succeed: true);
        RecordingInstallation installation = new();
        AppManagementGateway gateway = await harness.CreateGatewayAsync(agent, installation: installation);

        KazoDisableReport report = await gateway.DisableFeatureAsync(AppPackageTests.FeatureId);
        Assert.Equal("apps.feature.disable", agent.LastRequest!.Action);
        IReadOnlyList<string> enabled = await gateway.EnableFeatureAsync(AppPackageTests.FeatureId);

        Assert.Equal([AppPackageTests.FeatureId], report.AppIds);
        Assert.Equal([AppPackageTests.FeatureId], enabled);
        Assert.Equal(2, agent.Calls);
        Assert.Equal("apps.feature.enable", agent.LastRequest!.Action);
        Assert.Equal([$"disable:{AppPackageTests.FeatureId}", $"enable:{AppPackageTests.FeatureId}"], installation.Calls);
        Assert.All(installation.Writers, Assert.NotNull);
    }

    [Fact]
    public async Task Only_a_feature_can_be_disabled_and_no_password_is_asked_for_anything_else()
    {
        Harness harness = await Harness.CreateAsync();
        harness.UseCatalogWithFeature();
        CountingAgent agent = new(succeed: true);
        RecordingInstallation installation = new();
        AppManagementGateway gateway = await harness.CreateGatewayAsync(agent, installation: installation);

        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.DisableFeatureAsync("org.test.term").AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => gateway.EnableFeatureAsync("org.test.missing").AsTask());

        Assert.Equal(0, agent.Calls);
        Assert.Empty(installation.Calls);
    }

    [Fact]
    public async Task A_failed_authentication_disables_nothing()
    {
        Harness harness = await Harness.CreateAsync();
        harness.UseCatalogWithFeature();
        RecordingInstallation installation = new();
        AppManagementGateway gateway = await harness.CreateGatewayAsync(new CountingAgent(succeed: false), installation: installation);

        await Assert.ThrowsAsync<KazoUnauthorizedAccessException>(() => gateway.DisableFeatureAsync(AppPackageTests.FeatureId).AsTask());

        Assert.Empty(installation.Calls);
    }

    [Fact]
    public async Task Removing_a_feature_forgets_its_disabled_line_so_a_new_install_arrives_enabled()
    {
        Harness harness = await Harness.CreateAsync();
        harness.PublishFeature(Trusted, "1.0.0");
        AppManagementGateway gateway = await harness.CreateGatewayAsync(
            new CountingAgent(succeed: true), approval: new FakeApproval(AppInstallApprovalOutcome.Approved));
        await gateway.InstallPackageAsync(Trusted);
        harness.UseCatalogWithFeature();
        await harness.Settings.WriteAsRootAsync(FeaturesConfig, $"# kept\n[disabled]\n{AppPackageTests.FeatureId}=true\norg.other=true\n");
        gateway = await harness.CreateGatewayAsync(
            new CountingAgent(succeed: true), approval: new FakeApproval(AppInstallApprovalOutcome.Approved));

        await gateway.RemovePackageAsync(AppPackageTests.FeatureId);

        Assert.Equal("# kept\n[disabled]\norg.other=true\n", await harness.Settings.ReadAsync(FeaturesConfig));
    }

    private sealed partial class Harness
    {
        /// <summary>The running catalog with the feature loaded, as after its install (ADR 0214 §1).</summary>
        internal void UseCatalogWithFeature()
        {
            AppCatalogBuildResult built = AppCatalog.Build(
                [Window("org.test.term", "Term"), Window(AppPackageTests.FeatureId, "Feature") with { Kind = AppKind.Feature, EntryPoint = null }],
                ["org.test.term"]);
            Assert.True(built.IsSuccess, string.Join(", ", built.Errors.Select(error => error.Message)));
            Catalog = built.Catalog!;
        }
    }

    /// <summary>Records the feature changes the gateway asks for, and succeeds with the feature alone.</summary>
    private sealed class RecordingInstallation : IAppInstallationService
    {
        public List<string> Calls { get; } = [];

        public List<IAppFileSystemGateway?> Writers { get; } = [];

        public event EventHandler? Changed { add { } remove { } }

        public AppInstallationState GetState(string appId) => AppInstallationState.Installed;

        public IReadOnlyList<string> GetUninstallClosure(string appId) => [appId];

        public ValueTask<AppInstallationResult> UninstallAsync(
            string appId, AppInstallationScope scope, IAppFileSystemGateway? writer = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<AppInstallationResult> ReinstallAsync(
            string appId, AppInstallationScope scope, IAppFileSystemGateway? writer = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<AppInstallationResult> DisableFeatureAsync(string featureId, IAppFileSystemGateway writer, CancellationToken cancellationToken = default)
        {
            Calls.Add($"disable:{featureId}");
            Writers.Add(writer);
            return ValueTask.FromResult(new AppInstallationResult(true, [featureId], [], []));
        }

        public ValueTask<AppInstallationResult> EnableFeatureAsync(string featureId, IAppFileSystemGateway writer, CancellationToken cancellationToken = default)
        {
            Calls.Add($"enable:{featureId}");
            Writers.Add(writer);
            return ValueTask.FromResult(new AppInstallationResult(true, [featureId], []));
        }
    }
}
