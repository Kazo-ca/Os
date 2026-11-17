using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Platform.Core.Diagnostics;
using Kazo.Os.Platform.Core.Elevation;
using Kazo.Os.Platform.Core.Roles;
using Kazo.Os.Platform.Core.Sessions;
using Kazo.Os.Platform.Core.Tests.Settings;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Tests.Roles;

/// <summary>
/// A feature of fixture that declares a role (ADR 0247 §1), the shape of a « VM manager » role: its
/// <c>admin</c> command requires the role, its <c>run</c> command is for every user.
/// </summary>
public sealed class RoleFeature : FeatureDefinition
{
    public override string Id => FeatureRoleFixtures.FeatureId;

    public override string Name => "Virtual machines";

    public override string? CapabilityPrefix => "vm";

    public override IReadOnlyList<FeatureRole> Roles =>
    [
        new(FeatureRoleFixtures.Manager, "VM manager") { Description = "Opens the administration of the machines.", Grants = ["admin"] },
    ];

    public override IReadOnlyList<FeatureEntryPoint> EntryPoints =>
    [
        new("admin", "VM administration", AppKind.Terminal, typeof(RoleFeature))
        {
            RequiresRole = FeatureRoleFixtures.Manager,
            Terminal = new TerminalCommand("vmadmin", "vmadmin"),
        },
        new("run", "Run a machine", AppKind.Terminal, typeof(RoleFeature)) { Terminal = new TerminalCommand("vmrun", "vmrun") },
    ];
}

/// <summary>Builds the manifests, accounts and services of the tests of ADR 0247.</summary>
public static class FeatureRoleFixtures
{
    public const string FeatureId = "org.test.vm";
    public const string Manager = "vm.manager";
    public const string DeleteShared = "vm.delete-shared";

    public static FeatureManifests Manifests() => AppDefinitionMapper.ToFeatureManifests(new RoleFeature(), KazoOsSdk.Version);

    public static AppManifest Admin() => Manifests().Children.Single(child => child.Id == $"{FeatureId}.admin");

    public static AppManifest Run() => Manifests().Children.Single(child => child.Id == $"{FeatureId}.run");

    /// <summary>An ordinary app, not part of any feature.</summary>
    public static AppManifest OrdinaryApp() => new()
    {
        Id = "org.test.notes",
        Name = "Notes",
        Version = "1.0.0",
        PublisherId = "org.test",
        Description = "Notes",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.Platform.Core.Tests", "org.test.notes.EntryPoint"),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None,
    };

    /// <summary>The same account with another authority: an administrator demoted to <see cref="AppAuthority.User"/>.</summary>
    public static AuthenticatedPrincipal WithAuthority(AuthenticatedPrincipal principal, AppAuthority authority) => new(
        principal.SessionId,
        principal.UserId,
        principal.LoginName,
        principal.DisplayName,
        authority,
        principal.PrimaryGroupId,
        principal.GroupIds,
        principal.InstallationId,
        principal.DeviceId,
        principal.AuthenticatedAtUtc);
}

/// <summary>The settings files, Alice and Bob (ordinary users), Carol (administrator) and the roles service.</summary>
internal sealed class FeatureRoleHarness
{
    private FeatureRoleHarness(SettingsFilesHarness files, LocalUser carol, AppCatalog catalog)
    {
        Files = files;
        Carol = carol;
        Catalog = catalog;
        Roles = new FeatureRoleService(() => Catalog, files.Settings, Audit, () => files.Clock.UtcNow);
    }

    internal SettingsFilesHarness Files { get; }

    internal LocalUser Carol { get; }

    internal AppCatalog Catalog { get; }

    internal BoundedAuditLog Audit { get; } = new(maxEntries: 100);

    internal FeatureRoleService Roles { get; }

    internal static async Task<FeatureRoleHarness> CreateAsync()
    {
        SettingsFilesHarness files = new();
        await files.BootAsync();
        LocalGroup group = files.Groups.CreateGroup(LocalLoginName.Parse("carol"));
        LocalUser carol = files.Users.CreateUser(
            LocalLoginName.Parse("carol"), "Carol", AppAuthority.Administrator, group.Id,
            credential: LocalPasswordHasher.Create(SettingsFilesHarness.Password, iterations: 100));
        AppCatalogBuildResult built = AppCatalog.Build([.. FeatureRoleFixtures.Manifests().All, FeatureRoleFixtures.OrdinaryApp()]);
        Assert.True(built.IsSuccess, string.Join("; ", built.Errors));
        AppCatalog catalog = built.Catalog!;
        return new FeatureRoleHarness(files, carol, catalog);
    }

    /// <summary>The principal of <paramref name="user"/>'s session; the harness keeps one session, so several users are built here.</summary>
    internal Task<AuthenticatedPrincipal> LoginAsync(LocalUser user) => Task.FromResult(new AuthenticatedPrincipal(
        SessionId.FromGuid(Guid.NewGuid()),
        user.Id,
        user.LoginName,
        user.DisplayName,
        user.Authority,
        user.PrimaryGroupId,
        [user.PrimaryGroupId, .. user.AdditionalGroupIds],
        InstallationId.FromGuid(Guid.NewGuid()),
        DeviceId.FromGuid(Guid.NewGuid()),
        DateTimeOffset.UnixEpoch));

    /// <summary>The OS's agent, checking real passwords; <paramref name="prompt"/> types what the user types.</summary>
    internal AdministratorAuthenticationAgent Agent(IAdministratorAuthenticationPrompt prompt)
    {
        AdminElevationVerifier verifier = new(Files.Users, Audit);
        return new AdministratorAuthenticationAgent(verifier, Audit, prompt) { UserVerifier = verifier.VerifyUserAsync };
    }

    internal AppFeatureAuthenticationGateway Gateway(
        IAdministratorAuthenticationAgent agent, AppManifest caller, AuthenticatedPrincipal principal, bool isRootElevated = false) =>
        new(() => agent, () => Roles, caller, principal, isRootElevated, "Virtual machines");

    internal Task<string?> ReadRolesConfigAsync() => Files.ReadAsync("/etc/kazoos/roles.config");
}

/// <summary>Types a fixed list of credentials in the OS's window, like a user, until one works.</summary>
internal sealed class TypedCredentials(params (string Login, string Password)[] attempts) : IAdministratorAuthenticationPrompt
{
    internal int Shown { get; private set; }

    internal string? SuggestedLogin { get; private set; }

    internal string? Operation { get; private set; }

    public async Task<AdministratorAuthenticationOutcome> RequestAsync(
        AdministratorAuthenticationPromptRequest request, CancellationToken cancellationToken = default)
    {
        Shown++;
        SuggestedLogin = request.SuggestedAdministratorLogin;
        Operation = request.Operation;
        foreach ((string login, string password) in attempts)
        {
            AdministratorAuthenticationAttempt attempt = await request.AuthenticateAsync(login, password, cancellationToken);
            if (attempt.Succeeded)
            {
                return AdministratorAuthenticationOutcome.Approved;
            }
        }

        return AdministratorAuthenticationOutcome.Cancelled;
    }
}
