using Kazo.Os.AppSdk.Features;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Platform.Core.Elevation;
using Kazo.Os.Platform.Core.Roles;
using Kazo.Os.Platform.Core.Tests.Settings;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Tests.Roles;

/// <summary>
/// The OS's authentication of a feature's sensitive operations (ADR 0247 §3): the OS's window, a
/// ticket bound to the feature and the operation, used once, and each attempt in <c>auth.log</c>.
/// </summary>
public sealed class FeatureAuthenticationTests
{
    [Fact]
    public async Task The_holder_of_the_role_authenticates_with_their_own_password_and_gets_a_ticket_for_the_operation()
    {
        FeatureRoleHarness harness = await FeatureRoleHarness.CreateAsync();
        AuthenticatedPrincipal bob = await harness.LoginAsync(harness.Files.Bob);
        await harness.Roles.GrantAsync(FeatureRoleFixtures.Manager, harness.Files.Bob, "carol");
        TypedCredentials window = new(("bob", SettingsFilesHarness.Password));

        KazoAuthenticationTicket ticket = await harness.Gateway(harness.Agent(window), FeatureRoleFixtures.Admin(), bob)
            .AuthenticateAsync(FeatureRoleFixtures.DeleteShared, KazoAuthenticationScope.RoleHolder);

        Assert.Equal(1, window.Shown);
        Assert.Equal("bob", window.SuggestedLogin);
        Assert.Contains(FeatureRoleFixtures.DeleteShared, window.Operation, StringComparison.Ordinal);
        Assert.Equal((FeatureRoleFixtures.FeatureId, FeatureRoleFixtures.DeleteShared, KazoAuthenticationScope.RoleHolder, "bob"),
            (ticket.FeatureId, ticket.Operation, ticket.Scope, ticket.AuthenticatedBy));
        AuditEntry entry = Assert.Single(harness.Audit.Entries, e => e.Action == AdministratorAuthenticationAgent.FeatureAuditAction);
        Assert.Equal(("RoleHolder", FeatureRoleFixtures.DeleteShared), (entry.Properties["scope"], entry.Properties["operation"]));
        Assert.DoesNotContain(entry.Properties.Values, value => value.Contains(SettingsFilesHarness.Password, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_ticket_authorizes_its_own_operation_once_and_never_another_one()
    {
        FeatureRoleHarness harness = await FeatureRoleHarness.CreateAsync();
        AuthenticatedPrincipal carol = await harness.LoginAsync(harness.Carol);
        AppFeatureAuthenticationGateway gateway = harness.Gateway(harness.Agent(new TypedCredentials(("carol", SettingsFilesHarness.Password))), FeatureRoleFixtures.Admin(), carol);
        KazoAuthenticationTicket first = await gateway.AuthenticateAsync(FeatureRoleFixtures.DeleteShared, KazoAuthenticationScope.Administrator);
        KazoAuthenticationTicket second = await gateway.AuthenticateAsync(FeatureRoleFixtures.DeleteShared, KazoAuthenticationScope.Administrator);

        await Assert.ThrowsAsync<KazoUnauthorizedAccessException>(async () => await gateway.ConsumeAsync(first, "vm.change-limit"));
        await gateway.ConsumeAsync(second, FeatureRoleFixtures.DeleteShared);
        await Assert.ThrowsAsync<KazoUnauthorizedAccessException>(async () => await gateway.ConsumeAsync(second, FeatureRoleFixtures.DeleteShared));
    }

    [Fact]
    public async Task A_user_without_the_role_is_refused_before_any_window_and_the_refusal_is_logged()
    {
        FeatureRoleHarness harness = await FeatureRoleHarness.CreateAsync();
        AuthenticatedPrincipal alice = await harness.LoginAsync(harness.Files.Alice);
        TypedCredentials window = new(("alice", SettingsFilesHarness.Password));

        await Assert.ThrowsAsync<KazoUnauthorizedAccessException>(async () =>
            await harness.Gateway(harness.Agent(window), FeatureRoleFixtures.Admin(), alice)
                .AuthenticateAsync(FeatureRoleFixtures.DeleteShared, KazoAuthenticationScope.RoleHolder));

        Assert.Equal(0, window.Shown);
        AuditEntry denied = Assert.Single(harness.Audit.Entries, e => e.Action == FeatureRoleService.DeniedAction);
        Assert.Equal(AuditOutcome.Denied, denied.Outcome);
    }

    [Fact]
    public async Task Only_the_user_in_session_may_answer_for_the_role_holder_scope()
    {
        FeatureRoleHarness harness = await FeatureRoleHarness.CreateAsync();
        AuthenticatedPrincipal bob = await harness.LoginAsync(harness.Files.Bob);
        await harness.Roles.GrantAsync(FeatureRoleFixtures.Manager, harness.Files.Bob, "carol");
        // Carol is an administrator, but the operation asks Bob to prove it is him.
        TypedCredentials window = new(("carol", SettingsFilesHarness.Password));

        await Assert.ThrowsAsync<KazoUnauthorizedAccessException>(async () =>
            await harness.Gateway(harness.Agent(window), FeatureRoleFixtures.Admin(), bob)
                .AuthenticateAsync(FeatureRoleFixtures.DeleteShared, KazoAuthenticationScope.RoleHolder));

        Assert.Contains(harness.Audit.Entries, e => e.Action == AdministratorAuthenticationAgent.FeatureAuditAction && e.Outcome == AuditOutcome.Denied);
    }

    [Fact]
    public async Task An_ordinary_app_cannot_ask_for_a_feature_authentication()
    {
        FeatureRoleHarness harness = await FeatureRoleHarness.CreateAsync();
        AuthenticatedPrincipal carol = await harness.LoginAsync(harness.Carol);
        TypedCredentials window = new(("carol", SettingsFilesHarness.Password));

        await Assert.ThrowsAsync<KazoUnauthorizedAccessException>(async () =>
            await harness.Gateway(harness.Agent(window), FeatureRoleFixtures.OrdinaryApp(), carol)
                .AuthenticateAsync(FeatureRoleFixtures.DeleteShared, KazoAuthenticationScope.Administrator));

        Assert.Equal(0, window.Shown);
    }

    [Fact]
    public async Task Under_sudo_no_second_window_opens_and_the_pass_is_logged()
    {
        FeatureRoleHarness harness = await FeatureRoleHarness.CreateAsync();
        AuthenticatedPrincipal alice = await harness.LoginAsync(harness.Files.Alice);
        TypedCredentials window = new();

        KazoAuthenticationTicket ticket = await harness.Gateway(harness.Agent(window), FeatureRoleFixtures.Admin(), alice, isRootElevated: true)
            .AuthenticateAsync(FeatureRoleFixtures.DeleteShared, KazoAuthenticationScope.Administrator);

        Assert.Equal(0, window.Shown);
        Assert.Equal("root", ticket.AuthenticatedBy);
        AuditEntry entry = Assert.Single(harness.Audit.Entries, e => e.Action == AdministratorAuthenticationAgent.FeatureAuditAction);
        Assert.Equal("sudo", entry.Properties["via"]);
    }

    [Fact]
    public void A_ticket_expires_after_its_lifetime()
    {
        DateTimeOffset now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
        FeatureOperationTickets tickets = new(() => now);
        KazoAuthenticationTicket ticket = tickets.Issue(FeatureRoleFixtures.FeatureId, FeatureRoleFixtures.DeleteShared, KazoAuthenticationScope.RoleHolder, "bob");

        now += FeatureOperationTickets.Lifetime;

        Assert.False(tickets.TryConsume(ticket.Token, FeatureRoleFixtures.FeatureId, FeatureRoleFixtures.DeleteShared));
    }

    [Fact]
    public void A_ticket_of_another_feature_is_refused()
    {
        FeatureOperationTickets tickets = new();
        KazoAuthenticationTicket ticket = tickets.Issue(FeatureRoleFixtures.FeatureId, FeatureRoleFixtures.DeleteShared, KazoAuthenticationScope.RoleHolder, "bob");

        Assert.False(tickets.TryConsume(ticket.Token, "org.test.other", FeatureRoleFixtures.DeleteShared));
    }
}
