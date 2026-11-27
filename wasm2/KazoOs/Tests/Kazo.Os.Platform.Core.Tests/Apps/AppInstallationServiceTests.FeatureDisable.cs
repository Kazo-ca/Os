using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Apps;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Tests.Apps;

/// <summary>ADR 0287: a feature disabled in <c>/etc/kazoos/features.config</c> stays installed but does not run.</summary>
public sealed partial class AppInstallationServiceTests
{
    private const string FeatureUser = "org.test.feature-user";
    private const string FeaturesConfig = "/etc/kazoos/features.config";

    [Fact]
    public async Task Disabling_a_feature_keeps_it_installed_and_stops_its_entry_points_and_dependents()
    {
        Harness harness = await Harness.CreateAsync(extra: Window(FeatureUser, "Feature user", Needs(Feature)));
        AuthenticatedPrincipal alice = await harness.LoginAsync(harness.Settings.Alice);
        AppLaunchResult running = await harness.Orchestrator.LaunchAsync(new AppLaunchRequest(FeatureUser, alice, []));
        Assert.Equal(AppLaunchStatus.Launched, running.Status);

        AppInstallationResult result = await harness.Installation.DisableFeatureAsync(Feature, SystemWriter(harness));

        Assert.True(result.Success, string.Join(", ", result.Errors));
        Assert.Equal([Feature, FeatureUser, FeatureCli], result.AppIds.Order(StringComparer.Ordinal));
        Assert.Equal(running.Process!.Pid.Value, Assert.Single(result.StoppedProcesses!).Pid);
        string content = (await harness.Settings.ReadAsync(FeaturesConfig))!;
        Assert.Contains("[disabled]", content);
        Assert.Contains($"{Feature}=true", content);
        Assert.Equal("system", (await harness.Settings.StatAsync(FeaturesConfig))!.OwnerId);
        foreach (string appId in new[] { Feature, FeatureCli, FeatureUser })
        {
            Assert.Equal(AppInstallationState.Disabled, harness.Installation.GetState(appId));
            Assert.False(harness.Enablement.IsEnabled(appId));
        }

        // Not an uninstall: apps.config does not name it, and the other apps keep running.
        Assert.DoesNotContain(Feature, await harness.Settings.ReadAsync("/etc/kazoos/apps.config") ?? string.Empty);
        Assert.True(harness.Enablement.IsEnabled(Calc));
        Assert.Contains(harness.Log.Lines, line => line == $"apps: {Feature} disabled (by=alice)");
        Assert.Equal(AppLaunchStatus.Disabled, (await harness.Orchestrator.LaunchAsync(new AppLaunchRequest(FeatureUser, alice, []))).Status);
    }

    [Fact]
    public async Task Enabling_a_feature_brings_back_its_entry_points_and_dependents()
    {
        Harness harness = await Harness.CreateAsync(extra: Window(FeatureUser, "Feature user", Needs(Feature)));
        await harness.LoginAsync(harness.Settings.Alice);
        await harness.Installation.DisableFeatureAsync(Feature, SystemWriter(harness));

        AppInstallationResult result = await harness.Installation.EnableFeatureAsync(Feature, SystemWriter(harness));

        Assert.True(result.Success, string.Join(", ", result.Errors));
        Assert.Equal([Feature, FeatureUser, FeatureCli], result.AppIds.Order(StringComparer.Ordinal));
        foreach (string appId in new[] { Feature, FeatureCli, FeatureUser })
        {
            Assert.Equal(AppInstallationState.Installed, harness.Installation.GetState(appId));
            Assert.True(harness.Enablement.IsEnabled(appId));
        }

        Assert.DoesNotContain($"{Feature}=true", (await harness.Settings.ReadAsync(FeaturesConfig))!);
        Assert.Contains(harness.Log.Lines, line => line == $"apps: {Feature} enabled (by=alice)");
        Assert.Empty((await harness.Installation.EnableFeatureAsync(Feature, SystemWriter(harness))).AppIds);
    }

    [Theory]
    [InlineData(Calc, "apps.app.not-feature")]
    [InlineData(FeatureCli, "apps.app.not-feature")]
    [InlineData(Term, "apps.app.system")]
    [InlineData(Unknown, "apps.app.not-found")]
    public async Task Only_a_feature_can_be_disabled(string appId, string error)
    {
        Harness harness = await Harness.CreateAsync();
        await harness.LoginAsync(harness.Settings.Alice);

        AppInstallationResult disabled = await harness.Installation.DisableFeatureAsync(appId, SystemWriter(harness));
        AppInstallationResult enabled = await harness.Installation.EnableFeatureAsync(appId, SystemWriter(harness));

        Assert.False(disabled.Success);
        Assert.Equal($"{error}:{appId}", Assert.Single(disabled.Errors));
        Assert.Equal($"{error}:{appId}", Assert.Single(enabled.Errors));
        Assert.Null(await harness.Settings.ReadAsync(FeaturesConfig));
    }

    [Fact]
    public async Task An_ordinary_user_cannot_write_features_config()
    {
        Harness harness = await Harness.CreateAsync();
        AuthenticatedPrincipal alice = await harness.LoginAsync(harness.Settings.Alice);
        IAppFileSystemGateway asAlice = new AppFileSystemGateway(
            harness.Settings.FileSystem, harness.Settings.Clock, OsSettingsService.CreateUserOperation(alice),
            [.. alice.GroupIds.Select(static id => id.ToString())]);

        // ADR 0287 §1: only the OS's writer, once an administrator authenticated, changes the system file.
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => harness.Installation.DisableFeatureAsync(Feature, asAlice).AsTask());

        Assert.Null(await harness.Settings.ReadAsync(FeaturesConfig));
        Assert.True(harness.Enablement.IsEnabled(FeatureCli));
    }

    [Fact]
    public async Task A_feature_both_uninstalled_and_disabled_shows_uninstalled()
    {
        Harness harness = await Harness.CreateAsync();
        await harness.LoginAsync(harness.Settings.Alice);

        await harness.Installation.UninstallAsync(Feature, AppInstallationScope.User);
        await harness.Installation.DisableFeatureAsync(Feature, SystemWriter(harness));

        Assert.Equal(AppInstallationState.UninstalledForUser, harness.Installation.GetState(Feature));
        Assert.Equal(AppInstallationState.UninstalledForUser, harness.Installation.GetState(FeatureCli));
    }

    [Fact]
    public async Task A_hand_edit_of_features_config_applies_and_ignores_what_is_not_a_feature()
    {
        Harness harness = await Harness.CreateAsync();
        await harness.LoginAsync(harness.Settings.Alice);

        // Like `sudo nano /etc/kazoos/features.config`: no call to the service.
        await harness.Settings.WriteAsRootAsync(FeaturesConfig, $"[disabled]\n{Calc}=true\n{Feature}=true\n");
        await WaitUntilAsync(() => !harness.Enablement.IsEnabled(FeatureCli));

        Assert.True(harness.Enablement.IsEnabled(Calc));
        Assert.Contains(harness.Log.Lines, line => line.Contains($"{Calc} is not a feature", StringComparison.Ordinal));

        await harness.Settings.WriteAsRootAsync(FeaturesConfig, $"[disabled]\n{Feature}=false\n");
        await WaitUntilAsync(() => harness.Enablement.IsEnabled(FeatureCli));
    }

    [Fact]
    public void The_features_document_keeps_the_other_lines_and_reads_only_true_feature_lines()
    {
        AppCatalog catalog = AppCatalog.Build(
            [
                Window(Calc, "Calc"),
                Window(Feature, "Feature") with { Kind = AppKind.Feature, EntryPoint = null }
            ],
            []).Catalog!;

        string written = FeaturesConfigDocument.MarkDisabled(null, Feature);
        string edited = FeaturesConfigDocument.MarkEnabled($"# mine\n[disabled]\n{Unknown}=true\n{Feature}=true\n", Feature);
        FeaturesConfigReadResult read = FeaturesConfigDocument.Read($"[disabled]\n{Feature}=maybe\n{Unknown}=true\n", catalog);

        Assert.StartsWith(FeaturesConfigDocument.DefaultContent, written, StringComparison.Ordinal);
        Assert.Contains($"{Feature}=true", written);
        Assert.Equal($"# mine\n[disabled]\n{Unknown}=true\n", edited);
        Assert.Empty(read.DisabledFeatureIds);
        Assert.Contains("'maybe' is not true or false", Assert.Single(read.Warnings));
    }

    /// <summary>The OS's own writer, as the management gateway hands it once an administrator authenticated.</summary>
    private static IAppFileSystemGateway SystemWriter(Harness harness) =>
        new AppFileSystemGateway(harness.Settings.FileSystem, harness.Settings.Clock, OsSettingsService.CreateSystemOperation(), ["system"]);
}
