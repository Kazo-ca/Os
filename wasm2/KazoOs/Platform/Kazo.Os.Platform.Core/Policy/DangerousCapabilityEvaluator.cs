using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Platform.Core.Policy;

/// <summary>State of one declared dangerous capability for one app and one user (ADR 0049 section 7).</summary>
public enum DangerousCapabilityState
{
    /// <summary>The app is a system app (ADR 0048): granted without approval.</summary>
    System = 0,

    /// <summary>A machine <c>allow</c> exists, the policy is <c>prompt</c> and the user did not refuse it.</summary>
    Allowed = 1,

    /// <summary>The session user refused it in <c>~/.config/kazoos/app-permissions.config</c>.</summary>
    DeniedByUser = 2,

    /// <summary>An administrator refused it machine-wide in <c>/etc/kazoos/app-permissions.config</c>.</summary>
    DeniedByAdministrator = 3,

    /// <summary><c>dangerousCapabilities=block</c> in <c>/etc/kazoos/security.config</c>.</summary>
    BlockedByPolicy = 4,

    /// <summary>No machine decision and no refusal by the user: the launch gate asks.</summary>
    Pending = 5
}

/// <summary>One declared dangerous capability and its current state.</summary>
/// <param name="Description">Title and risk sentence from <see cref="AppCapabilities.Describe"/>.</param>
/// <param name="State">Current state.</param>
public sealed record DangerousCapabilityStatus(CapabilityDescription Description, DangerousCapabilityState State)
{
    /// <summary>Gets the capability identifier.</summary>
    public string Capability => Description.Capability;

    /// <summary>Gets whether the app receives the capability at its next launch.</summary>
    public bool IsGranted => State is DangerousCapabilityState.System or DangerousCapabilityState.Allowed;
}

/// <summary>
/// Pure decision table of ADR 0049 sections 2 to 4: which declared dangerous capabilities of an
/// app are granted, refused or still pending.
/// </summary>
/// <remarks>
/// For a non-system app the first matching rule wins:
/// <list type="number">
/// <item>policy <c>block</c> → <see cref="DangerousCapabilityState.BlockedByPolicy"/> (existing <c>allow</c> lines are ignored);</item>
/// <item>machine <c>deny</c> → <see cref="DangerousCapabilityState.DeniedByAdministrator"/>;</item>
/// <item>user <c>deny</c> → <see cref="DangerousCapabilityState.DeniedByUser"/> (it beats a machine <c>allow</c>);</item>
/// <item>machine <c>allow</c> → <see cref="DangerousCapabilityState.Allowed"/>, except
/// <c>network.real.access</c> when the manifest declares a real-network port the administrator did
/// not approve (<c>[real-network-ports]</c>, ADR 0233 §4): then it is pending again, so the window
/// lists the new ports;</item>
/// <item>otherwise <see cref="DangerousCapabilityState.Pending"/>.</item>
/// </list>
/// A system app gets <see cref="DangerousCapabilityState.System"/> for every dangerous capability:
/// decision lines naming it are ignored without effect.
/// </remarks>
public static class DangerousCapabilityEvaluator
{
    /// <summary>Evaluates every dangerous capability <paramref name="manifest"/> declares, in manifest order.</summary>
    /// <param name="manifest">App manifest.</param>
    /// <param name="isSystemApp">Whether the app is a system app of the host (<see cref="AppCatalog.IsSystemApp"/>).</param>
    /// <param name="policy">Effective <c>dangerousCapabilities</c> policy.</param>
    /// <param name="decisions">Parsed machine and user decisions.</param>
    public static IReadOnlyList<DangerousCapabilityStatus> Evaluate(
        AppManifest manifest,
        bool isSystemApp,
        DangerousCapabilityPolicy policy,
        AppPermissionDecisions decisions) =>
        Evaluate(manifest, isSystemApp, policy, decisions, BuiltInCapabilityCatalog.Instance);

    /// <summary>
    /// Evaluates every dangerous capability <paramref name="manifest"/> declares, in manifest order,
    /// as the catalog of the running OS classifies it (ADR 0246 §3): the built-in ones, the topic
    /// permissions and the dangerous permissions of the installed features.
    /// </summary>
    /// <param name="manifest">App manifest.</param>
    /// <param name="isSystemApp">Whether the app is a system app of the host.</param>
    /// <param name="policy">Effective <c>dangerousCapabilities</c> policy.</param>
    /// <param name="decisions">Parsed machine and user decisions.</param>
    /// <param name="catalog">The catalog of capabilities of the running OS.</param>
    public static IReadOnlyList<DangerousCapabilityStatus> Evaluate(
        AppManifest manifest,
        bool isSystemApp,
        DangerousCapabilityPolicy policy,
        AppPermissionDecisions decisions,
        ICapabilityCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(catalog);

        List<DangerousCapabilityStatus> statuses = [];
        foreach (string capability in manifest.Capabilities.Distinct(StringComparer.Ordinal))
        {
            if (catalog.Find(capability) is not { Risk: CapabilityRisk.Dangerous } description)
            {
                continue;
            }

            statuses.Add(new DangerousCapabilityStatus(
                description,
                isSystemApp ? DangerousCapabilityState.System : EvaluateOne(manifest, capability, policy, decisions)));
        }

        return statuses;
    }

    /// <summary>Gets whether <paramref name="manifest"/> declares at least one dangerous capability.</summary>
    /// <param name="manifest">App manifest.</param>
    public static bool DeclaresDangerousCapabilities(AppManifest manifest) =>
        DeclaresDangerousCapabilities(manifest, BuiltInCapabilityCatalog.Instance);

    /// <summary>Gets whether <paramref name="manifest"/> declares at least one capability the catalog classifies dangerous.</summary>
    /// <param name="manifest">App manifest.</param>
    /// <param name="catalog">The catalog of capabilities of the running OS.</param>
    public static bool DeclaresDangerousCapabilities(AppManifest manifest, ICapabilityCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(catalog);
        return manifest.Capabilities.Any(catalog.IsDangerous);
    }

    /// <summary>
    /// Gets the real-network ports <paramref name="manifest"/> declares that the machine file does not
    /// approve yet (ADR 0233 §4); empty when every declared port was approved.
    /// </summary>
    /// <param name="manifest">App manifest.</param>
    /// <param name="decisions">Parsed machine and user decisions.</param>
    public static IReadOnlyList<RealNetworkPort> UnapprovedRealNetworkPorts(AppManifest manifest, AppPermissionDecisions decisions)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(decisions);
        IReadOnlySet<RealNetworkPort> approved = decisions.GetApprovedRealNetworkPorts(manifest.Id);
        return [.. RealNetworkPorts.ParseValid(manifest.RealNetworkPorts).Where(port => !approved.Contains(port))];
    }

    /// <summary>
    /// Gets the direct-network hosts <paramref name="manifest"/> declares that the machine file does
    /// not approve yet (ADR 0282 §4); empty when every declared host was approved.
    /// </summary>
    /// <param name="manifest">App manifest.</param>
    /// <param name="decisions">Parsed machine and user decisions.</param>
    public static IReadOnlyList<string> UnapprovedDirectNetworkHosts(AppManifest manifest, AppPermissionDecisions decisions) =>
        UnapprovedDirectNetworkHosts(manifest, decisions, AppCapabilities.NetworkDirectAccess);

    /// <summary>
    /// Gets the hosts <paramref name="capability"/> lets <paramref name="manifest"/> reach directly
    /// that the machine file does not approve yet (ADR 0282 §4, ADR 0284 §1); empty when every one
    /// was approved, or when the capability names no host.
    /// </summary>
    /// <param name="manifest">App manifest.</param>
    /// <param name="decisions">Parsed machine and user decisions.</param>
    /// <param name="capability"><c>network.direct.access</c> or <c>drives.provide</c>.</param>
    public static IReadOnlyList<string> UnapprovedDirectNetworkHosts(AppManifest manifest, AppPermissionDecisions decisions, string capability)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(decisions);
        IReadOnlySet<string> approved = decisions.GetApprovedDirectNetworkHosts(manifest.Id);
        return [.. DirectHostsFor(manifest, capability).Where(host => !approved.Contains(host))];
    }

    /// <summary>
    /// Gets the hosts a capability lets <paramref name="manifest"/> reach directly, canonical and
    /// sorted: its <c>directNetworkHosts</c> for <c>network.direct.access</c> (ADR 0282 §4), the
    /// hosts of its drive providers for <c>drives.provide</c> (ADR 0284 §1), none otherwise.
    /// </summary>
    /// <param name="manifest">App manifest.</param>
    /// <param name="capability">The capability.</param>
    public static IReadOnlyList<string> DirectHostsFor(AppManifest manifest, string capability)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return capability switch
        {
            AppCapabilities.NetworkDirectAccess => DirectNetworkHosts.ParseValid(manifest.DirectNetworkHosts),
            AppCapabilities.DrivesProvide => AppDriveProviders.HostsOf(manifest),
            _ => [],
        };
    }

    /// <summary>
    /// Gets the hosts the machine file approves for <paramref name="manifest"/> after the
    /// capabilities of <paramref name="allowed"/> were allowed and those of <paramref name="refused"/>
    /// refused (ADR 0284 §1): the hosts of the allowed ones, plus the hosts approved before that
    /// another capability still names, without the ones only a refused capability names.
    /// </summary>
    /// <param name="manifest">App manifest.</param>
    /// <param name="approvedBefore">The hosts the machine file approved before the decision.</param>
    /// <param name="allowed">The capabilities allowed now.</param>
    /// <param name="refused">The capabilities refused or reset now.</param>
    /// <returns>The hosts to write, or <see langword="null"/> to remove the line when none is left.</returns>
    public static IReadOnlyCollection<string>? HostsToApprove(
        AppManifest manifest, IReadOnlySet<string> approvedBefore, IReadOnlyCollection<string> allowed, IReadOnlyCollection<string> refused)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(approvedBefore);
        ArgumentNullException.ThrowIfNull(allowed);
        ArgumentNullException.ThrowIfNull(refused);
        IReadOnlyList<string> declared = manifest.Capabilities ?? [];
        string[] others = [.. HostCapabilities.Where(capability =>
            declared.Contains(capability, StringComparer.Ordinal) && !allowed.Contains(capability) && !refused.Contains(capability))];
        HashSet<string> stillNamed = new(others.SelectMany(capability => DirectHostsFor(manifest, capability)), StringComparer.Ordinal);
        SortedSet<string> hosts = new(allowed.SelectMany(capability => DirectHostsFor(manifest, capability)), StringComparer.Ordinal);
        hosts.UnionWith(approvedBefore.Where(stillNamed.Contains));
        return hosts.Count == 0 && !allowed.Any(HostCapabilities.Contains) ? null : hosts;
    }

    /// <summary>The capabilities whose approval names hosts (ADR 0282 §4, ADR 0284 §1).</summary>
    public static IReadOnlyList<string> HostCapabilities { get; } = [AppCapabilities.NetworkDirectAccess, AppCapabilities.DrivesProvide];

    private static DangerousCapabilityState EvaluateOne(
        AppManifest manifest, string capability, DangerousCapabilityPolicy policy, AppPermissionDecisions decisions)
    {
        string appId = manifest.Id;
        if (policy == DangerousCapabilityPolicy.Block)
        {
            return DangerousCapabilityState.BlockedByPolicy;
        }

        AppPermissionDecision? machine = decisions.GetMachineDecision(appId, capability);
        if (machine == AppPermissionDecision.Deny)
        {
            return DangerousCapabilityState.DeniedByAdministrator;
        }

        if (decisions.IsDeniedByUser(appId, capability))
        {
            return DangerousCapabilityState.DeniedByUser;
        }

        if (machine != AppPermissionDecision.Allow)
        {
            return DangerousCapabilityState.Pending;
        }

        // ADR 0233 §4: an update that declares a new port asks again, like a widened permission.
        if (string.Equals(capability, AppCapabilities.NetworkRealAccess, StringComparison.Ordinal)
            && UnapprovedRealNetworkPorts(manifest, decisions).Count > 0)
        {
            return DangerousCapabilityState.Pending;
        }

        // ADR 0282 §4 and ADR 0284 §1: the same for a new direct-network or drive-provider host.
        return HostCapabilities.Contains(capability)
            && UnapprovedDirectNetworkHosts(manifest, decisions, capability).Count > 0
                ? DangerousCapabilityState.Pending
                : DangerousCapabilityState.Allowed;
    }
}
