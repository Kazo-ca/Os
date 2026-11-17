using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Diagnostics;
using Kazo.Os.Platform.Core.Elevation;
using Kazo.Os.Platform.Core.Policy;
using Kazo.Os.Platform.Core.Tests.Settings;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Tests.Features;

/// <summary>A feature's dangerous permission is asked in the OS's secure window (ADR 0246 §4, answer 3 A).</summary>
public sealed partial class FeaturePermissionsTests
{
    private sealed class CountingPrompt : IDangerousCapabilityApprovalPrompt
    {
        internal int Calls { get; private set; }

        internal DangerousCapabilityApprovalRequest? LastRequest { get; private set; }

        public Task<DangerousCapabilityApprovalOutcome> RequestAsync(DangerousCapabilityApprovalRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastRequest = request;
            return Task.FromResult(DangerousCapabilityApprovalOutcome.Dismissed);
        }
    }

    private static async Task<(DangerousCapabilityGate Gate, AuthenticatedPrincipal Principal, CountingPrompt Window)> GateAsync(AppManifest client)
    {
        SettingsFilesHarness files = new();
        await files.BootAsync();
        AuthenticatedPrincipal alice = await files.LoginAsync(files.Alice);
        AppCatalog catalog = AppCatalog.Build([FeaturePermissionFixtures.Feature(), client]).Catalog!;
        BoundedAuditLog audit = new(maxEntries: 50);
        CountingPrompt window = new();
        DangerousCapabilityGate gate = new(catalog, files.Grants, new AppPermissionStore(files.Settings) { Capabilities = WithFeature() }, files.Settings,
            new AdminElevationVerifier(files.Users, audit), audit, window)
        {
            Capabilities = WithFeature(),
        };
        return (gate, alice, window);
    }

    [Fact]
    public async Task A_pending_feature_permission_is_asked_in_the_secure_window_even_from_a_terminal()
    {
        AppManifest client = FeaturePermissionFixtures.Client();
        (DangerousCapabilityGate gate, AuthenticatedPrincipal alice, CountingPrompt window) = await GateAsync(client);
        CountingPrompt terminal = new();

        await gate.EnsureDecidedAsync(client, alice, terminal);

        Assert.Equal(0, terminal.Calls);
        Assert.Equal(1, window.Calls);
        CapabilityDescription asked = Assert.Single(window.LastRequest!.Capabilities);
        Assert.Equal((FeaturePermissionFixtures.Destroy, FeaturePermissionFixtures.FeatureId), (asked.Capability, asked.ProviderId));
        Assert.Equal("The app can delete a machine and its disk.", asked.RiskDescription);
    }

    [Fact]
    public async Task A_built_in_dangerous_capability_from_a_terminal_keeps_the_terminals_text_mode()
    {
        AppManifest client = FeaturePermissionFixtures.Client(AppCapabilities.ProcessManage);
        (DangerousCapabilityGate gate, AuthenticatedPrincipal alice, CountingPrompt window) = await GateAsync(client);
        CountingPrompt terminal = new();

        await gate.EnsureDecidedAsync(client, alice, terminal);

        Assert.Equal(1, terminal.Calls);
        Assert.Equal(0, window.Calls);
    }
}
