using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Platform.Core.Tests.Roles;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.Core.Tests.Shell;

/// <summary>
/// ADR 0249 §2 in the kernel: a page of Settings ▸ Features is never launched alone, and its process
/// starts as a child of Settings only for a user who holds the role it requires (ADR 0247 §2).
/// </summary>
public sealed partial class ShellInterpreterTests
{
    [Fact]
    public async Task A_settings_page_is_never_launched_alone()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.Roles.GrantAsync(FeatureRoleFixtures.Manager, fixture.AliceAccount, "carol");

        AppLaunchResult result = await fixture.Orchestrator.LaunchAsync(new AppLaunchRequest(Fixture.SettingsPageId, fixture.Principal, []));

        Assert.Equal(AppLaunchStatus.NotFound, result.Status);
        Assert.Equal(AppLifecycleOrchestrator.SettingsPageNotLaunchableErrorCode, result.ErrorCode);
    }

    [Fact]
    public async Task The_process_of_a_settings_page_requires_its_role()
    {
        Fixture fixture = await Fixture.CreateAsync();

        AppLaunchResult result = await fixture.Orchestrator.StartAuxiliaryAsync(
            Fixture.SettingsPageId, fixture.Principal, ProcessId.FromInt64(1), ["--type=settings-page"]);

        Assert.Equal(AppLaunchStatus.RoleRequired, result.Status);
    }

    [Fact]
    public async Task The_process_of_a_settings_page_of_a_disabled_feature_never_starts()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.Roles.GrantAsync(FeatureRoleFixtures.Manager, fixture.AliceAccount, "carol");
        fixture.Enablement.MarkDisabled([FeatureRoleFixtures.FeatureId]);

        AppLaunchResult result = await fixture.Orchestrator.StartAuxiliaryAsync(
            Fixture.SettingsPageId, fixture.Principal, fixture.Host.ProcessId, ["--type=settings-page"]);

        Assert.Equal(AppLaunchStatus.Disabled, result.Status);
    }

    [Fact]
    public async Task The_process_of_a_settings_page_runs_as_the_page_under_the_given_parent()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.Roles.GrantAsync(FeatureRoleFixtures.Manager, fixture.AliceAccount, "carol");
        ProcessId parent = fixture.Host.ProcessId;

        AppLaunchResult result = await fixture.Orchestrator.StartAuxiliaryAsync(
            Fixture.SettingsPageId, fixture.Principal, parent, ["--type=settings-page"]);

        Assert.Equal(AppLaunchStatus.Launched, result.Status);
        Assert.Equal(Fixture.SettingsPageId, result.Context!.Manifest.Id);
        Assert.Equal(AppKind.SettingsPage, result.Process!.Kind);
        Assert.Equal(parent, result.Process.ParentPid);
        Assert.True(await fixture.Orchestrator.StopAsync(result.Process.Pid, ProcessExitReason.CloseRequested));
    }
}
