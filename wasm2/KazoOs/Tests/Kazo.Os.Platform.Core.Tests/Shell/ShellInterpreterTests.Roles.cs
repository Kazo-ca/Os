using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.Platform.Core.Roles;
using Kazo.Os.Platform.Core.Shell;
using Kazo.Os.Platform.Core.Tests.Roles;
using Kazo.Os.Simulation.Abstractions.Diagnostics;

namespace Kazo.Os.Platform.Core.Tests.Shell;

/// <summary>
/// ADR 0247 §2 in the shell: a command of a feature that requires a role is absent from the
/// completion and answers « role required » with 126 for a user who does not hold it.
/// </summary>
public sealed partial class ShellInterpreterTests
{
    [Fact]
    public async Task A_command_that_requires_a_role_the_user_lacks_exits_126_and_is_logged()
    {
        Fixture fixture = await Fixture.CreateAsync();

        Output output = await fixture.RunAsync("vmadmin status");

        Assert.Equal(126, output.ExitCode);
        Assert.Equal("hsh: vmadmin: role required\n", output.Stderr);
        Assert.Equal(ShellFailure.RoleRequired, output.Result.Failure);
        Assert.Equal(string.Empty, output.Stdout);
        AuditEntry denied = Assert.Single(fixture.Audit.Entries, e => e.Action == FeatureRoleService.DeniedAction);
        Assert.Equal(FeatureRoleFixtures.Admin().Id, denied.Properties["entryPoint"]);
    }

    [Fact]
    public async Task A_command_that_requires_a_role_runs_once_the_role_is_granted()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.Roles.GrantAsync(FeatureRoleFixtures.Manager, fixture.AliceAccount, "carol");

        Output output = await fixture.RunAsync("vmadmin status");

        Assert.Equal(0, output.ExitCode);
        Assert.Equal("status\n", output.Stdout);
    }

    [Fact]
    public async Task Root_under_sudo_runs_a_command_that_requires_a_role()
    {
        Fixture fixture = await Fixture.CreateAsync();

        Output output = await fixture.RunAsync("sudo vmadmin status", credentialPrompt: (_, _) =>
        {
            ElevationTicket ticket = fixture.Tickets.Issue(ElevationScope.SudoRoot, "admin-id", "admin");
            return Task.FromResult<ShellElevation?>(new ShellElevation(ticket.Token, ticket.Scope));
        });

        Assert.Equal(0, output.ExitCode);
        Assert.Equal("status\n", output.Stdout);
    }

    [Fact]
    public async Task The_completion_lists_a_command_that_requires_a_role_only_for_its_holders()
    {
        Fixture fixture = await Fixture.CreateAsync();

        bool before = fixture.Interpreter.GetInstalledCommandsFor(fixture.Principal, null).Any(command => command.Name == "vmadmin");
        await fixture.Roles.GrantAsync(FeatureRoleFixtures.Manager, fixture.AliceAccount, "carol");
        bool after = fixture.Interpreter.GetInstalledCommandsFor(fixture.Principal, null).Any(command => command.Name == "vmadmin");

        Assert.False(before);
        Assert.True(after);
    }
}
