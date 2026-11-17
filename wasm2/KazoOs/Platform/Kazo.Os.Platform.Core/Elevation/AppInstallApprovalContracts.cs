using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Apps.Packages;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Elevation;

/// <summary>What an administrator approves in the second step of ADR 0189 §4.</summary>
public enum AppInstallApprovalOperation
{
    /// <summary>A new app.</summary>
    Install = 0,

    /// <summary>A new version of an installed app.</summary>
    Update = 1,

    /// <summary>The removal of an installed app.</summary>
    Remove = 2,

    /// <summary>The return of an installed app to the version its last update kept (ADR 0214 §4.1).</summary>
    Rollback = 3
}

/// <summary>
/// Step 2 of ADR 0189 §4: after an administrator signed in (step 1), the window shows the app, its
/// source and trust level, and the capabilities it asks for; the administrator approves or refuses.
/// </summary>
/// <param name="AppId">App identifier.</param>
/// <param name="Name">App name.</param>
/// <param name="Version">Version installed, updated to or removed.</param>
/// <param name="PreviousVersion">Version an update replaces, or <see langword="null"/>.</param>
/// <param name="SizeBytes">Size of the package's files, or <see langword="null"/> for a removal.</param>
/// <param name="Source">The source, such as <c>github:Kazo-ca/Os-App-Webamp@1.0.0</c>.</param>
/// <param name="Trust">Trust level of the source (ADR 0189 §2).</param>
/// <param name="Capabilities">Capabilities the app declares; dangerous ones are shown first (ADR 0049).</param>
/// <param name="Operation">Install, update or removal.</param>
/// <param name="SystemWide">Whether it is for every user (<c>/usr/lib/kazoos/apps</c>) or for the requesting user only.</param>
/// <param name="AdministratorLogin">The administrator who signed in at step 1.</param>
/// <param name="RequestedBy">Session user who asked, or <see langword="null"/>.</param>
/// <param name="Caller">App that asked (the Store, <c>kazopkg</c>, Settings).</param>
public sealed record AppInstallApprovalRequest(
    string AppId,
    string Name,
    string Version,
    string? PreviousVersion,
    long? SizeBytes,
    string Source,
    AppSourceTrust Trust,
    IReadOnlyList<string> Capabilities,
    AppInstallApprovalOperation Operation,
    bool SystemWide,
    string AdministratorLogin,
    AuthenticatedPrincipal? RequestedBy,
    AppManifest Caller)
{
    /// <summary>Gets why a personal install needs an administrator, when the block list stopped it (ADR 0189 §5).</summary>
    public AppBlockReason? BlockReason { get; init; }

    /// <summary>
    /// Gets whether a key of the source verified the package's signature (ADR 0214 §2);
    /// <see langword="null"/> for a removal or a rollback, which download nothing.
    /// </summary>
    public bool? Signed { get; init; }

    /// <summary>Gets whether the package is a feature (ADR 0260): the window says that one approval covers its capability set, once and for all.</summary>
    public bool IsFeature { get; init; }

    /// <summary>
    /// Gets the entry points of a feature (ADR 0260 §2), as "name (kind)" lines the window lists; empty for an app.
    /// </summary>
    public IReadOnlyList<string> FeatureEntryPoints { get; init; } = [];

    /// <summary>
    /// Gets the manifest of the package installed or updated, so the window names, under
    /// <c>network.direct.access</c> and <c>drives.provide</c>, the hosts the approval allows and the
    /// server address fields of its drive providers (ADR 0284 §1 and §2): approving a feature approves
    /// them once and for all. <see langword="null"/> when the window has nothing more to show.
    /// </summary>
    public AppManifest? Manifest { get; init; }

    /// <summary>
    /// Gets the packages installed with the app, dependencies first (ADR 0248 §1): the window lists
    /// each one, and the one approval covers the whole plan; empty for an app without missing dependency.
    /// </summary>
    public IReadOnlyList<AppInstallApprovalDependency> Dependencies { get; init; } = [];

    /// <summary>Gets the names of the installed dependencies the plan reactivates (ADR 0248 §1, ADR 0048).</summary>
    public IReadOnlyList<string> Reactivated { get; init; } = [];

    /// <summary>
    /// Gets the manifests of the features the plan installs (ADR 0248 §1, answer 268 A): the window
    /// describes the permissions they declare (ADR 0246) with their own title and risk, although the
    /// running OS does not know them yet.
    /// </summary>
    public IReadOnlyList<AppManifest> PlannedFeatures { get; init; } = [];

    /// <summary>
    /// Gets, for a removal, the names of the installed apps that need the package and are disabled
    /// with it (ADR 0248 §3); empty otherwise.
    /// </summary>
    public IReadOnlyList<string> DisabledDependents { get; init; } = [];
}

/// <summary>One package a plan installs with the requested app (ADR 0248 §1), as the approval window lists it.</summary>
/// <param name="AppId">Package identifier.</param>
/// <param name="Name">Package name.</param>
/// <param name="Version">Version installed.</param>
/// <param name="PreviousVersion">Version it replaces, or <see langword="null"/>.</param>
/// <param name="SizeBytes">Size of the package's files.</param>
/// <param name="Source">The source, pinned to its version.</param>
/// <param name="Trust">Trust level of the source.</param>
/// <param name="SystemWide">Whether it is installed for every user (always for a feature, ADR 0260 §4).</param>
/// <param name="IsFeature">Whether it is a feature: its whole permission set is approved with it (ADR 0260 §3).</param>
/// <param name="DangerousCapabilities">Its dangerous capabilities, the permissions it declares included (ADR 0246).</param>
public sealed record AppInstallApprovalDependency(
    string AppId,
    string Name,
    string Version,
    string? PreviousVersion,
    long SizeBytes,
    string Source,
    AppSourceTrust Trust,
    bool SystemWide,
    bool IsFeature,
    IReadOnlyList<string> DangerousCapabilities);

/// <summary>How the approval window ended.</summary>
public enum AppInstallApprovalOutcome
{
    /// <summary>The administrator approved.</summary>
    Approved = 0,

    /// <summary>The administrator refused, pressed Escape or closed the window.</summary>
    Refused = 1,

    /// <summary>No interactive surface could show the window: nothing is installed.</summary>
    Unavailable = 2
}

/// <summary>
/// The window of step 2 (ADR 0189 §4), drawn by the platform shell above every window like the
/// authentication prompt (ADR 0055 §5); shared by the Store, <c>kazopkg</c> and Settings.
/// </summary>
public interface IAppInstallApprovalPrompt
{
    /// <summary>Shows the window and waits until it closes.</summary>
    /// <param name="request">What to show.</param>
    /// <param name="cancellationToken">Cancels the window; it then ends as <see cref="AppInstallApprovalOutcome.Refused"/>.</param>
    Task<AppInstallApprovalOutcome> RequestAsync(AppInstallApprovalRequest request, CancellationToken cancellationToken = default);
}
