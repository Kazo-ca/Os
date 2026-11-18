using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Diagnostics;
using Kazo.Os.Platform.Core.Elevation;
using Kazo.Os.Platform.Core.Policy;
using Kazo.Os.Platform.Core.Sessions;
using Kazo.Os.Platform.Core.Tests.Settings;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Tests.Policy;

/// <summary>
/// ADR 0282 §4: the direct-network hosts an administrator approved with
/// <c>network.direct.access</c> are kept in <c>[direct-network-hosts]</c>, the approval names them,
/// and an update that declares a new host asks again.
/// </summary>
public sealed class DirectNetworkHostApprovalTests
{
    private const string AppId = "org.example.drive";
    private const string MachineFile = "/etc/kazoos/app-permissions.config";
    private const string AdminPassword = "s3cret-admin";

    [Fact]
    public void The_machine_file_gives_the_approved_hosts_of_each_app()
    {
        AppPermissionDecisions decisions = AppPermissionDecisions.Parse(
            """
            [permissions]
            org.example.drive:network.direct.access=allow

            [direct-network-hosts]
            org.example.drive=API.example.com, *.example.org
            """,
            null);

        Assert.Equal(["*.example.org", "api.example.com"], decisions.GetApprovedDirectNetworkHosts(AppId).Order(StringComparer.Ordinal));
        Assert.Empty(decisions.GetApprovedDirectNetworkHosts("org.example.other"));
        Assert.Empty(decisions.Warnings);
    }

    [Fact]
    public void A_bad_host_drops_its_line_and_the_user_file_cannot_approve_hosts()
    {
        AppPermissionDecisions decisions = AppPermissionDecisions.Parse(
            "[direct-network-hosts]\norg.example.drive=api.example.com,*\n",
            "[direct-network-hosts]\norg.example.other=api.example.com\n");

        Assert.Empty(decisions.GetApprovedDirectNetworkHosts(AppId));
        Assert.Empty(decisions.GetApprovedDirectNetworkHosts("org.example.other"));
        Assert.Equal(2, decisions.Warnings.Count);
        Assert.Contains(decisions.Warnings, static warning => warning.Contains("only the machine file", StringComparison.Ordinal));
    }

    [Fact]
    public void The_document_writes_the_hosts_in_canonical_order_and_removes_them()
    {
        string written = AppPermissionsDocument.SetDirectNetworkHosts(null, AppId, ["api.example.com", "*.Example.org", "bad"]);
        string removed = AppPermissionsDocument.RemoveDirectNetworkHosts(written, AppId);

        Assert.Contains("[direct-network-hosts]", written, StringComparison.Ordinal);
        Assert.Contains("org.example.drive=*.example.org,api.example.com", written, StringComparison.Ordinal);
        Assert.Empty(AppPermissionDecisions.Parse(removed, null).GetApprovedDirectNetworkHosts(AppId));
    }

    [Fact]
    public void An_allowed_app_whose_hosts_were_all_approved_stays_allowed()
    {
        AppPermissionDecisions decisions = Allowed("api.example.com,*.example.org");

        Assert.Equal(DangerousCapabilityState.Allowed, StateOf(Drive("api.example.com"), decisions));
        Assert.Empty(DangerousCapabilityEvaluator.UnapprovedDirectNetworkHosts(Drive("api.example.com"), decisions));
    }

    [Fact]
    public void An_update_that_declares_a_new_host_asks_again()
    {
        AppPermissionDecisions decisions = Allowed("api.example.com");

        Assert.Equal(DangerousCapabilityState.Pending, StateOf(Drive("api.example.com", "upload.example.com"), decisions));
        Assert.Equal(["upload.example.com"], DangerousCapabilityEvaluator.UnapprovedDirectNetworkHosts(Drive("api.example.com", "upload.example.com"), decisions));
    }

    [Fact]
    public void The_approval_text_names_the_declared_hosts_only_for_network_direct_access()
    {
        AppManifest manifest = Drive("api.example.com", "*.example.org");

        Assert.Equal("*.example.org, api.example.com", DirectNetworkHostText.For(manifest, AppCapabilities.NetworkDirectAccess));
        Assert.Null(DirectNetworkHostText.For(manifest, AppCapabilities.NetworkRealAccess));
        Assert.Null(DirectNetworkHostText.For(Drive(), AppCapabilities.NetworkDirectAccess));
    }

    [Fact]
    public async Task Approving_network_direct_access_records_the_declared_hosts()
    {
        Harness harness = await Harness.CreateAsync(Drive("api.example.com", "*.example.org"));
        harness.Prompt.Script = async (request, ct) =>
        {
            await request.ApproveAsync("admin", AdminPassword, ct);
            return DangerousCapabilityApprovalOutcome.Approved;
        };

        DangerousCapabilityGateResult result = await harness.Gate.EnsureDecidedAsync(harness.App, harness.Principal, harness.Prompt);

        Assert.Equal(DangerousCapabilityApprovalOutcome.Approved, result.PromptOutcome);
        string machine = (await harness.Files.ReadAsync(MachineFile))!;
        Assert.Contains("org.example.drive=*.example.org,api.example.com", machine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_host_after_an_update_shows_the_window_again()
    {
        Harness harness = await Harness.CreateAsync(Drive("api.example.com", "upload.example.com"));
        await harness.Store.WriteMachineDecisionsAsync(AppId, [AppCapabilities.NetworkDirectAccess], AppPermissionDecision.Allow);
        await harness.Store.WriteApprovedDirectNetworkHostsAsync(AppId, ["api.example.com"]);

        await harness.Gate.EnsureDecidedAsync(harness.App, harness.Principal, harness.Prompt);

        Assert.Equal(1, harness.Prompt.Calls);
        Assert.Equal(AppCapabilities.NetworkDirectAccess, Assert.Single(harness.Prompt.LastRequest!.Capabilities).Capability);
    }

    [Fact]
    public async Task Settings_allow_approves_the_declared_hosts_and_a_reset_removes_them()
    {
        Harness harness = await Harness.CreateAsync(Drive("api.example.com"));
        BoundedAuditLog audit = new(maxEntries: 50);
        AppPermissionAdministration administration = new(
            harness.Catalog, harness.Store, harness.Files.Settings, new AdminElevationVerifier(harness.Files.Users, audit), audit);

        await administration.ApplyMachineDecisionAsync(AppId, AppCapabilities.NetworkDirectAccess, AppPermissionDecision.Allow, "admin", harness.Principal);
        string allowed = (await harness.Files.ReadAsync(MachineFile))!;
        await administration.ApplyMachineDecisionAsync(AppId, AppCapabilities.NetworkDirectAccess, null, "admin", harness.Principal);
        string reset = (await harness.Files.ReadAsync(MachineFile))!;

        Assert.Contains("org.example.drive=api.example.com", allowed, StringComparison.Ordinal);
        Assert.DoesNotContain("org.example.drive=api.example.com", reset, StringComparison.Ordinal);
    }

    private static AppPermissionDecisions Allowed(string hosts) => AppPermissionDecisions.Parse(
        $"[permissions]\norg.example.drive:network.direct.access=allow\n[direct-network-hosts]\norg.example.drive={hosts}\n", null);

    private static DangerousCapabilityState StateOf(AppManifest manifest, AppPermissionDecisions decisions) =>
        Assert.Single(DangerousCapabilityEvaluator.Evaluate(manifest, false, DangerousCapabilityPolicy.Prompt, decisions)).State;

    private static AppManifest Drive(params string[] hosts) => new()
    {
        Id = AppId,
        Name = "Drive",
        Version = "1.0.0",
        PublisherId = "org.example",
        Description = "Direct-network hosts fixture.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.Apps.Sample", "Kazo.Os.Apps.Sample.SampleApp"),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None,
        Capabilities = [AppCapabilities.NetworkDirectAccess],
        DirectNetworkHosts = hosts.Length == 0 ? null : hosts
    };

    private sealed class Harness
    {
        private Harness(SettingsFilesHarness files, AuthenticatedPrincipal principal, AppManifest manifest)
        {
            Files = files;
            Principal = principal;
            AppCatalogBuildResult built = AppCatalog.Build([manifest], []);
            Assert.True(built.IsSuccess, string.Join(", ", built.Errors.Select(error => error.Message)));
            Catalog = built.Catalog!;
            BoundedAuditLog audit = new(maxEntries: 200);
            Store = new AppPermissionStore(files.Settings);
            Gate = new DangerousCapabilityGate(Catalog, files.Grants, Store, files.Settings, new AdminElevationVerifier(files.Users, audit), audit);
        }

        internal SettingsFilesHarness Files { get; }
        internal AuthenticatedPrincipal Principal { get; }
        internal AppCatalog Catalog { get; }
        internal AppPermissionStore Store { get; }
        internal DangerousCapabilityGate Gate { get; }
        internal Prompt Prompt { get; } = new();
        internal AppManifest App => Catalog.Manifests[AppId];

        internal static async Task<Harness> CreateAsync(AppManifest manifest)
        {
            SettingsFilesHarness files = new();
            files.Users.CreateUser(
                LocalLoginName.Parse("admin"), "Admin", AppAuthority.Administrator, files.Alice.PrimaryGroupId,
                credential: LocalPasswordHasher.Create(AdminPassword, iterations: 100));
            await files.BootAsync();
            AuthenticatedPrincipal alice = await files.LoginAsync(files.Alice);
            return new Harness(files, alice, manifest);
        }
    }

    private sealed class Prompt : IDangerousCapabilityApprovalPrompt
    {
        internal Func<DangerousCapabilityApprovalRequest, CancellationToken, Task<DangerousCapabilityApprovalOutcome>> Script { get; set; } =
            (_, _) => Task.FromResult(DangerousCapabilityApprovalOutcome.Dismissed);

        internal int Calls { get; private set; }

        internal DangerousCapabilityApprovalRequest? LastRequest { get; private set; }

        public Task<DangerousCapabilityApprovalOutcome> RequestAsync(
            DangerousCapabilityApprovalRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastRequest = request;
            return Script(request, cancellationToken);
        }
    }
}
