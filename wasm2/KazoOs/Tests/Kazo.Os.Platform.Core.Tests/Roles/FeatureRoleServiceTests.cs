using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Roles;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Tests.Roles;

/// <summary>
/// The roles of the features (ADR 0247 §1, answer 233 B): granted user by user in
/// <c>/etc/kazoos/roles.config</c>, implicit for every administrator, written to <c>auth.log</c>.
/// </summary>
public sealed class FeatureRoleServiceTests
{
    [Fact]
    public async Task The_roles_the_installed_features_declare_are_listed_with_their_feature()
    {
        FeatureRoleHarness harness = await FeatureRoleHarness.CreateAsync();

        DeclaredFeatureRole declared = Assert.Single(harness.Roles.DeclaredRoles);

        Assert.Equal((FeatureRoleFixtures.Manager, FeatureRoleFixtures.FeatureId, "Virtual machines", "VM manager"),
            (declared.Role.Id, declared.FeatureId, declared.FeatureName, declared.Role.Title));
    }

    [Fact]
    public async Task A_role_granted_to_a_user_is_held_by_that_user_only_and_written_by_id()
    {
        FeatureRoleHarness harness = await FeatureRoleHarness.CreateAsync();
        AuthenticatedPrincipal alice = await harness.LoginAsync(harness.Files.Alice);
        AuthenticatedPrincipal bob = await harness.LoginAsync(harness.Files.Bob);

        bool changed = await harness.Roles.GrantAsync(FeatureRoleFixtures.Manager, harness.Files.Bob, "carol");

        Assert.True(changed);
        Assert.True(harness.Roles.HasRole(bob, FeatureRoleFixtures.Manager));
        Assert.False(harness.Roles.HasRole(alice, FeatureRoleFixtures.Manager));
        Assert.Contains(FeatureRoleFixtures.Manager, harness.Roles.RolesOf(bob));
        Assert.Empty(harness.Roles.RolesOf(alice));
        string config = (await harness.ReadRolesConfigAsync())!;
        Assert.Contains($"{FeatureRoleFixtures.Manager}={harness.Files.Bob.Id}", config, StringComparison.Ordinal);
        Assert.DoesNotContain("bob", config, StringComparison.Ordinal);
        AuditEntry entry = Assert.Single(harness.Audit.Entries, e => e.Action == FeatureRoleService.GrantAction);
        Assert.Equal(("carol", FeatureRoleFixtures.Manager), (entry.Properties["admin"], entry.Properties["role"]));
    }

    [Fact]
    public async Task An_administrator_holds_every_role_with_no_line_and_a_grant_changes_nothing()
    {
        FeatureRoleHarness harness = await FeatureRoleHarness.CreateAsync();
        AuthenticatedPrincipal carol = await harness.LoginAsync(harness.Carol);

        bool changed = await harness.Roles.GrantAsync(FeatureRoleFixtures.Manager, harness.Carol, "carol");

        Assert.False(changed);
        Assert.True(harness.Roles.HasRole(carol, FeatureRoleFixtures.Manager));
        Assert.Equal([FeatureRoleFixtures.Manager], harness.Roles.RolesOf(carol));
        Assert.DoesNotContain(harness.Carol.Id.ToString(), (await harness.ReadRolesConfigAsync()) ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(harness.Audit.Entries, e => e.Action == FeatureRoleService.GrantAction);
    }

    [Fact]
    public async Task An_administrator_demoted_to_user_loses_the_implicit_roles()
    {
        FeatureRoleHarness harness = await FeatureRoleHarness.CreateAsync();
        AuthenticatedPrincipal carol = await harness.LoginAsync(harness.Carol);

        AuthenticatedPrincipal demoted = FeatureRoleFixtures.WithAuthority(carol, AppAuthority.User);

        Assert.False(harness.Roles.HasRole(demoted, FeatureRoleFixtures.Manager));
        Assert.False(FeatureRoleService.OpensTo(harness.Roles, FeatureRoleFixtures.Admin(), demoted));
        Assert.True(FeatureRoleService.OpensTo(harness.Roles, FeatureRoleFixtures.Admin(), carol));
    }

    [Fact]
    public async Task Root_under_sudo_holds_every_role()
    {
        FeatureRoleHarness harness = await FeatureRoleHarness.CreateAsync();
        AuthenticatedPrincipal alice = await harness.LoginAsync(harness.Files.Alice);

        Assert.True(harness.Roles.HasRole(alice, FeatureRoleFixtures.Manager, isRootElevated: true));
    }

    [Fact]
    public async Task A_revoked_role_is_no_longer_held_and_the_change_is_logged()
    {
        FeatureRoleHarness harness = await FeatureRoleHarness.CreateAsync();
        AuthenticatedPrincipal bob = await harness.LoginAsync(harness.Files.Bob);
        await harness.Roles.GrantAsync(FeatureRoleFixtures.Manager, harness.Files.Bob, "carol");

        bool changed = await harness.Roles.RevokeAsync(FeatureRoleFixtures.Manager, harness.Files.Bob, "carol");

        Assert.True(changed);
        Assert.False(harness.Roles.HasRole(bob, FeatureRoleFixtures.Manager));
        Assert.Single(harness.Audit.Entries, e => e.Action == FeatureRoleService.RevokeAction);
    }

    [Fact]
    public async Task The_grants_are_read_back_from_roles_config_by_a_new_service()
    {
        FeatureRoleHarness harness = await FeatureRoleHarness.CreateAsync();
        AuthenticatedPrincipal bob = await harness.LoginAsync(harness.Files.Bob);
        await harness.Roles.GrantAsync(FeatureRoleFixtures.Manager, harness.Files.Bob, "carol");
        FeatureRoleService afterReboot = new(() => harness.Catalog, harness.Files.Settings);

        Assert.False(afterReboot.HasRole(bob, FeatureRoleFixtures.Manager));
        await afterReboot.RefreshAsync();

        Assert.True(afterReboot.HasRole(bob, FeatureRoleFixtures.Manager));
    }

    [Fact]
    public async Task A_role_no_installed_feature_declares_cannot_be_granted()
    {
        FeatureRoleHarness harness = await FeatureRoleHarness.CreateAsync();

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await harness.Roles.GrantAsync("vm.unknown", harness.Files.Bob, "carol"));
    }

    [Fact]
    public async Task An_entry_point_without_a_role_opens_to_everyone_and_one_with_a_role_to_its_holders()
    {
        FeatureRoleHarness harness = await FeatureRoleHarness.CreateAsync();
        AuthenticatedPrincipal alice = await harness.LoginAsync(harness.Files.Alice);

        Assert.True(FeatureRoleService.OpensTo(harness.Roles, FeatureRoleFixtures.Run(), alice));
        Assert.False(FeatureRoleService.OpensTo(harness.Roles, FeatureRoleFixtures.Admin(), alice));
        // Without the service (a host with no roles), only an administrator sees it: the check fails closed.
        Assert.False(FeatureRoleService.OpensTo(null, FeatureRoleFixtures.Admin(), alice));
    }

    [Fact]
    public void Roles_config_is_parsed_by_role_with_the_ids_of_the_users()
    {
        Guid bob = Guid.NewGuid();

        IReadOnlyDictionary<string, IReadOnlySet<string>> grants = FeatureRolesDocument.Parse(
            $"[roles]\n{FeatureRoleFixtures.Manager}={bob}\n");

        Assert.Equal([bob.ToString("N")], grants[FeatureRoleFixtures.Manager]);
    }
}
