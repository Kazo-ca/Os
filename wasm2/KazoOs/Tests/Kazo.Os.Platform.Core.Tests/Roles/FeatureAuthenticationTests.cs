using Kazo.Os.App.Abstractions.Policy;
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
        KazoAuthenticationTicket issued = await gateway.ConsumeAsync(second, FeatureRoleFixtures.DeleteShared);
        await Assert.ThrowsAsync<KazoUnauthorizedAccessException>(async () => await gateway.ConsumeAsync(second, FeatureRoleFixtures.DeleteShared));
        Assert.Equal((KazoAuthenticationScope.Administrator, "carol"), (issued.Scope, issued.AuthenticatedBy));
    }

    [Fact]
    public async Task The_ticket_the_OS_issued_counts_not_a_forged_copy_of_it()
    {
        FeatureRoleHarness harness = await FeatureRoleHarness.CreateAsync();
        AuthenticatedPrincipal bob = await harness.LoginAsync(harness.Files.Bob);
        await harness.Roles.GrantAsync(FeatureRoleFixtures.Manager, harness.Files.Bob, "carol");
        AppFeatureAuthenticationGateway gateway = harness.Gateway(harness.Agent(new TypedCredentials(("bob", SettingsFilesHarness.Password))), FeatureRoleFixtures.Admin(), bob);
        KazoAuthenticationTicket ticket = await gateway.AuthenticateAsync(FeatureRoleFixtures.DeleteShared, KazoAuthenticationScope.RoleHolder);

        KazoAuthenticationTicket issued = await gateway.ConsumeAsync(
            ticket with { Scope = KazoAuthenticationScope.Administrator, AuthenticatedBy = "carol" }, FeatureRoleFixtures.DeleteShared);

        Assert.Equal((KazoAuthenticationScope.RoleHolder, "bob"), (issued.Scope, issued.AuthenticatedBy));
    }

    [Fact]
    public async Task A_ticket_of_another_user_is_refused()
    {
        FeatureRoleHarness harness = await FeatureRoleHarness.CreateAsync();
        AuthenticatedPrincipal carol = await harness.LoginAsync(harness.Carol);
        AuthenticatedPrincipal bob = await harness.LoginAsync(harness.Files.Bob);
        KazoAuthenticationTicket carols = await harness.Gateway(harness.Agent(new TypedCredentials(("carol", SettingsFilesHarness.Password))), FeatureRoleFixtures.Admin(), carol)
            .AuthenticateAsync(FeatureRoleFixtures.DeleteShared, KazoAuthenticationScope.Administrator);

        await Assert.ThrowsAsync<KazoUnauthorizedAccessException>(async () =>
            await harness.Gateway(harness.Agent(new TypedCredentials()), FeatureRoleFixtures.Admin(), bob).ConsumeAsync(carols, FeatureRoleFixtures.DeleteShared));
    }

    [Fact]
    public async Task From_an_entry_point_open_to_all_the_holder_of_a_role_of_the_feature_may_use_the_role_holder_scope()
    {
        FeatureRoleHarness harness = await FeatureRoleHarness.CreateAsync();
        AuthenticatedPrincipal bob = await harness.LoginAsync(harness.Files.Bob);
        await harness.Roles.GrantAsync(FeatureRoleFixtures.Manager, harness.Files.Bob, "carol");
        TypedCredentials window = new(("bob", SettingsFilesHarness.Password));

        // ADR 0283: a subcommand of a command open to all (x86 admin) checks IsInRole, then asks the OS.
        KazoAuthenticationTicket ticket = await harness.Gateway(harness.Agent(window), FeatureRoleFixtures.Run(), bob)
            .AuthenticateAsync(FeatureRoleFixtures.DeleteShared, KazoAuthenticationScope.RoleHolder);

        Assert.Equal(1, window.Shown);
        Assert.Equal((KazoAuthenticationScope.RoleHolder, "bob"), (ticket.Scope, ticket.AuthenticatedBy));
    }

    [Fact]
    public async Task The_role_holder_scope_is_refused_from_an_entry_point_open_to_all_without_any_role_of_the_feature()
    {
        FeatureRoleHarness harness = await FeatureRoleHarness.CreateAsync();
        AuthenticatedPrincipal alice = await harness.LoginAsync(harness.Files.Alice);
        TypedCredentials window = new(("alice", SettingsFilesHarness.Password));

        await Assert.ThrowsAsync<KazoUnauthorizedAccessException>(async () =>
            await harness.Gateway(harness.Agent(window), FeatureRoleFixtures.Run(), alice)
                .AuthenticateAsync(FeatureRoleFixtures.DeleteShared, KazoAuthenticationScope.RoleHolder));

        Assert.Equal(0, window.Shown);
        Assert.Single(harness.Audit.Entries, e => e.Action == FeatureRoleService.DeniedAction);
    }

    [Fact]
    public async Task The_passkey_of_another_account_does_not_answer_for_the_role_holder()
    {
        FeatureRoleHarness harness = await FeatureRoleHarness.CreateAsync();
        AuthenticatedPrincipal bob = await harness.LoginAsync(harness.Files.Bob);
        await harness.Roles.GrantAsync(FeatureRoleFixtures.Manager, harness.Files.Bob, "carol");
        AdminElevationVerifier verifier = new(harness.Files.Users, harness.Audit);
        AdministratorAuthenticationAgent agent = new(verifier, harness.Audit, new CarolsPasskey())
        {
            UserVerifier = verifier.VerifyUserAsync,
            // Carol's passkey would be accepted for an administrator's operation.
            PasskeyVerifier = static (login, _) => Task.FromResult(login == "carol"
                ? new AdminElevationVerificationResult(true, "uid-carol", "carol", null)
                : AdminElevationVerificationResult.Failed("invalid-credentials")),
        };

        await Assert.ThrowsAsync<KazoUnauthorizedAccessException>(async () =>
            await harness.Gateway(agent, FeatureRoleFixtures.Admin(), bob)
                .AuthenticateAsync(FeatureRoleFixtures.DeleteShared, KazoAuthenticationScope.RoleHolder));
    }

    [Fact]
    public async Task An_unknown_scope_is_refused()
    {
        FeatureRoleHarness harness = await FeatureRoleHarness.CreateAsync();
        AuthenticatedPrincipal carol = await harness.LoginAsync(harness.Carol);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await harness.Gateway(harness.Agent(new TypedCredentials()), FeatureRoleFixtures.Admin(), carol)
                .AuthenticateAsync(FeatureRoleFixtures.DeleteShared, (KazoAuthenticationScope)7));
    }

    /// <summary>Answers the window with Carol's passkey, whatever account the window asks for.</summary>
    private sealed class CarolsPasskey : IAdministratorAuthenticationPrompt
    {
        public async Task<AdministratorAuthenticationOutcome> RequestAsync(
            AdministratorAuthenticationPromptRequest request, CancellationToken cancellationToken = default)
        {
            AdministratorAuthenticationAttempt attempt = await request.AuthenticateWithPasskeyAsync!("carol", cancellationToken);
            return attempt.Succeeded ? AdministratorAuthenticationOutcome.Approved : AdministratorAuthenticationOutcome.Cancelled;
        }
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
        KazoAuthenticationTicket ticket = tickets.Issue(FeatureRoleFixtures.FeatureId, FeatureRoleFixtures.DeleteShared, KazoAuthenticationScope.RoleHolder, "bob", "uid-bob");

        now += FeatureOperationTickets.Lifetime;

        Assert.Null(tickets.TryConsume(ticket.Token, FeatureRoleFixtures.FeatureId, FeatureRoleFixtures.DeleteShared, "uid-bob"));
    }

    [Fact]
    public void A_ticket_of_another_feature_is_refused()
    {
        FeatureOperationTickets tickets = new();
        KazoAuthenticationTicket ticket = tickets.Issue(FeatureRoleFixtures.FeatureId, FeatureRoleFixtures.DeleteShared, KazoAuthenticationScope.RoleHolder, "bob", "uid-bob");

        Assert.Null(tickets.TryConsume(ticket.Token, "org.test.other", FeatureRoleFixtures.DeleteShared, "uid-bob"));
    }
}
