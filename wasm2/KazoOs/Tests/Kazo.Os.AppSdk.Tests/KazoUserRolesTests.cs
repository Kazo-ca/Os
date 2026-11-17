using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Features;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.AppSdk.Tests;

/// <summary>
/// <see cref="KazoUserInfo.HasRole(string)"/> (ADR 0247 §1, answer 233 B): the roles the context
/// carries, every role for an administrator and for root; no authentication on a host without it.
/// </summary>
public sealed class KazoUserRolesTests
{
    private const string Manager = "x86.vm-manager";

    [Fact]
    public void An_ordinary_user_holds_the_roles_granted_to_them_only()
    {
        KazoUserInfo user = KazoUser.Describe(new RolesContext(AppAuthority.User, isRoot: false, new HashSet<string> { Manager }));

        Assert.True(user.HasRole(Manager));
        Assert.False(user.HasRole("x86.other"));
        Assert.Equal([Manager], user.Roles);
    }

    [Fact]
    public void An_ordinary_user_without_a_grant_holds_no_role()
    {
        KazoUserInfo user = KazoUser.Describe(new RolesContext(AppAuthority.User, isRoot: false, new HashSet<string>()));

        Assert.False(user.HasRole(Manager));
    }

    [Fact]
    public void An_administrator_holds_every_role_implicitly()
    {
        KazoUserInfo user = KazoUser.Describe(new RolesContext(AppAuthority.Administrator, isRoot: false, new HashSet<string>()));

        Assert.True(user.HasRole(Manager));
    }

    [Fact]
    public void Root_under_sudo_holds_every_role()
    {
        Assert.True(KazoUser.Describe(new RolesContext(AppAuthority.User, isRoot: true, new HashSet<string>())).HasRole(Manager));
    }

    [Fact]
    public async Task A_host_without_feature_authentication_refuses_the_operation()
    {
        using IDisposable scope = KazoExecutionContext.Enter(new RolesContext(AppAuthority.User, isRoot: false, new HashSet<string>()));

        Assert.False(KazoExecutionContext.Current.FeatureAuthentication.IsSupported);
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await KazoFeatureAuthentication.AuthenticateAsync("x86.vm.delete-shared", KazoAuthenticationScope.RoleHolder));
    }

    private sealed class RolesContext(AppAuthority authority, bool isRoot, IReadOnlySet<string> roles) : IAppExecutionContext
    {
        public AppManifest Manifest => throw new NotSupportedException();

        public Guid InstanceId { get; } = Guid.NewGuid();

        public string UserId => "u-1";

        public string HomeDirectory => "/home/benoit";

        public bool IsRootElevated => isRoot;

        public string UserLoginName => "benoit";

        public string UserDisplayName => "Benoit L.";

        public AppAuthority UserAuthority => authority;

        public IReadOnlySet<string> Roles => roles;

        public IReadOnlySet<string> GrantedCapabilities { get; } = new HashSet<string>();

        public SessionId SessionId { get; } = SessionId.FromGuid(Guid.NewGuid());

        public ProcessId ProcessId { get; } = ProcessId.FromInt64(1);

        public CancellationToken CancellationToken => CancellationToken.None;

        public ICapabilityChecker Capabilities => throw new NotSupportedException();

        public IAppFileSystemGateway FileSystem => throw new NotSupportedException();

        public IAppEventGateway Events => throw new NotSupportedException();

        public IAppNotificationGateway Notifications => throw new NotSupportedException();

        public IAppLoggingGateway Logging => throw new NotSupportedException();

        public IAppClockGateway Clock => throw new NotSupportedException();

        public IAppProcessGateway Processes => throw new NotSupportedException();
    }
}
