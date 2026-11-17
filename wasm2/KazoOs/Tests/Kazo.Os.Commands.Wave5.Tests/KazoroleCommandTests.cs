using System.Collections.Immutable;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Administration;
using Kazo.Os.Commands.Kazorole;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Xunit;

namespace Kazo.Os.Commands.Wave5.Tests;

/// <summary>
/// <c>kazorole</c> (ADR 0247 §1): lists the roles of the features with their holders; under <c>sudo</c>
/// grants or takes one back; a grant to an administrator changes nothing and says so (exit 0).
/// </summary>
public sealed class KazoroleCommandTests
{
    private const string Manager = "x86.vm-manager";

    [Fact]
    public async Task List_shows_each_role_with_the_ordinary_accounts_that_hold_it()
    {
        FakeRoles roles = new();

        (int exit, string stdout, _) = await RunAsync(root: false, roles, "list");

        Assert.Equal(0, exit);
        Assert.Contains($"{Manager}\tVM manager (Virtual machines)\tbob", stdout, StringComparison.Ordinal);
        Assert.Contains("Administrators hold every role, implicitly.", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Under_sudo_grant_gives_the_role_to_an_ordinary_account()
    {
        FakeRoles roles = new();

        (int exit, string stdout, _) = await RunAsync(root: true, roles, "grant", "alice", Manager);

        Assert.Equal(0, exit);
        Assert.Equal(("alice", Manager, true), roles.Changed);
        Assert.Contains("alice now holds the role", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Under_sudo_revoke_takes_the_role_back()
    {
        FakeRoles roles = new();

        (int exit, _, _) = await RunAsync(root: true, roles, "revoke", "bob", Manager);

        Assert.Equal(0, exit);
        Assert.Equal(("bob", Manager, false), roles.Changed);
    }

    [Fact]
    public async Task Grant_to_an_administrator_changes_nothing_and_exits_0()
    {
        FakeRoles roles = new();

        (int exit, string stdout, _) = await RunAsync(root: true, roles, "grant", "carol", Manager);

        Assert.Equal(0, exit);
        Assert.Null(roles.Changed);
        Assert.Contains("carol is an administrator and already holds every role", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_sudo_grant_is_refused()
    {
        FakeRoles roles = new();

        (int exit, _, string stderr) = await RunAsync(root: false, roles, "grant", "alice", Manager);

        Assert.Equal(1, exit);
        Assert.Null(roles.Changed);
        Assert.Contains("use sudo", stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("grant", "alice")]
    [InlineData("bogus")]
    public async Task A_wrong_usage_exits_2(params string[] args)
    {
        (int exit, _, string stderr) = await RunAsync(root: true, new FakeRoles(), args);

        Assert.Equal(2, exit);
        Assert.Contains("usage: kazorole", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_account_or_role_exits_1()
    {
        (int noUser, _, string userError) = await RunAsync(root: true, new FakeRoles(), "grant", "dave", Manager);
        (int noRole, _, string roleError) = await RunAsync(root: true, new FakeRoles(), "grant", "alice", "x86.unknown");

        Assert.Equal(1, noUser);
        Assert.Contains("user 'dave' does not exist", userError, StringComparison.Ordinal);
        Assert.Equal(1, noRole);
        Assert.Contains("No installed feature declares the role 'x86.unknown'.", roleError, StringComparison.Ordinal);
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(bool root, FakeRoles roles, params string[] args)
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest(new KazoroleApp());
        StringWriter stdout = new();
        StringWriter stderr = new();
        TerminalExecutionContext context = new(
            app: new KazoroleContext(manifest, root, roles),
            arguments: args.ToImmutableArray(),
            standardInput: TextReader.Null,
            standardOutput: stdout,
            standardError: stderr,
            workingDirectory: "/home/alice",
            environment: ImmutableDictionary<string, string>.Empty);
        int exit = await new KazoroleCommand(manifest).ExecuteAsync(context, CancellationToken.None);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Alice and Bob (ordinary, Bob holds the role), Carol (administrator); one declared role.</summary>
    private sealed class FakeRoles : IAppUserAccountsGateway
    {
        public (string Login, string Role, bool Grant)? Changed { get; private set; }

        public ValueTask<IReadOnlyList<KazoUserAccount>> ListAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<KazoUserAccount>>(
            [
                new("alice", "Alice", AppAuthority.User, true, true, DateTimeOffset.UnixEpoch, true),
                new("bob", "Bob", AppAuthority.User, true, true, DateTimeOffset.UnixEpoch, false) { Roles = [Manager] },
                new("carol", "Carol", AppAuthority.Administrator, true, true, DateTimeOffset.UnixEpoch, false) { Roles = [Manager] },
            ]);

        public ValueTask<IReadOnlyList<KazoFeatureRole>> ListRolesAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<KazoFeatureRole>>([new(Manager, "VM manager", "org.kazoos.x86", "Virtual machines")]);

        public ValueTask<bool> GrantRoleAsync(string loginName, string role, CancellationToken cancellationToken = default) =>
            Change(loginName, role, grant: true);

        public ValueTask<bool> RevokeRoleAsync(string loginName, string role, CancellationToken cancellationToken = default) =>
            Change(loginName, role, grant: false);

        private ValueTask<bool> Change(string loginName, string role, bool grant)
        {
            if (role != Manager)
            {
                throw new ArgumentException($"No installed feature declares the role '{role}'.", nameof(role));
            }

            Changed = (loginName, role, grant);
            return ValueTask.FromResult(true);
        }

        public ValueTask ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask ResetPasswordAsync(string loginName, string? newPassword, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class KazoroleContext(AppManifest manifest, bool root, IAppUserAccountsGateway accounts) : IAppExecutionContext
    {
        public AppManifest Manifest { get; } = manifest;
        public Guid InstanceId { get; } = Guid.NewGuid();
        public string UserId => "user";
        public string UserLoginName => "alice";
        public AppAuthority UserAuthority => AppAuthority.User;
        public bool IsRootElevated => root;
        public IAppUserAccountsGateway UserAccounts => accounts;
        public IReadOnlySet<string> GrantedCapabilities => new HashSet<string>(Manifest.Capabilities);
        public SessionId SessionId { get; } = SessionId.FromGuid(Guid.NewGuid());
        public ProcessId ProcessId { get; } = ProcessId.FromInt64(1);
        public CancellationToken CancellationToken => CancellationToken.None;
        public ICapabilityChecker Capabilities => throw new NotImplementedException();
        public IAppFileSystemGateway FileSystem => throw new NotImplementedException();
        public IAppEventGateway Events => throw new NotImplementedException();
        public IAppNotificationGateway Notifications => throw new NotImplementedException();
        public IAppLoggingGateway Logging => throw new NotImplementedException();
        public IAppClockGateway Clock => throw new NotImplementedException();
        public IAppProcessGateway Processes => throw new NotImplementedException();
    }
}
