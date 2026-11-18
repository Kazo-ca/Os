using System.Globalization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.UserEvents;

namespace Kazo.Os.Platform.Core.Diagnostics;

/// <summary>
/// The events the OS writes itself to a user's journal for its own personal actions (ADR 0254,
/// section 2): a dangerous permission granted or refused, a role granted or revoked, an app installed
/// for oneself, a synchronization conflict resolved. Each one is also in <c>auth.log</c> or
/// <c>syslog</c> where it was before; the journal tells the user, with the name of what it is about.
/// </summary>
/// <remarks>
/// The message is written in the language of the session that caused the event, like an app writes
/// its own (<see cref="CultureInfo.CurrentUICulture"/> follows the session language).
/// </remarks>
internal static class OsUserEvents
{
    /// <summary>Author of the permission events: the OS's permission manager.</summary>
    public const string PermissionsAppId = "org.kazoos.permissions";

    /// <summary>Author of the role events: the OS's account manager.</summary>
    public const string AccountsAppId = "org.kazoos.accounts";

    /// <summary>Author of the installation events: the OS's package manager.</summary>
    public const string PackagesAppId = "org.kazoos.packages";

    /// <summary>Author of the synchronization events: the OS's synchronization with the server.</summary>
    public const string SyncAppId = "org.kazoos.sync";

    /// <summary>Kind of a dangerous permission granted to an app for the user.</summary>
    public const string PermissionGranted = "kazoos.permission.granted";

    /// <summary>Kind of a dangerous permission the user refused to an app.</summary>
    public const string PermissionRefused = "kazoos.permission.refused";

    /// <summary>Kind of a role of a Feature granted to the user.</summary>
    public const string RoleGranted = "kazoos.role.granted";

    /// <summary>Kind of a role of a Feature taken from the user.</summary>
    public const string RoleRevoked = "kazoos.role.revoked";

    /// <summary>Kind of an app installed for the user alone.</summary>
    public const string AppInstalled = "kazoos.app.installed";

    /// <summary>Kind of a synchronization conflict resolved for one of the user's files.</summary>
    public const string SyncConflictResolved = "kazoos.sync.conflict-resolved";

    /// <summary>Builds the event of dangerous permissions granted to <paramref name="app"/> after an administrator approved them.</summary>
    /// <param name="app">App that received the permissions.</param>
    /// <param name="capabilities">The permissions, comma-separated.</param>
    /// <param name="administrator">Login of the administrator who approved them.</param>
    public static KazoUserEvent PermissionsGranted(AppManifest app, string capabilities, string administrator)
    {
        ArgumentNullException.ThrowIfNull(app);
        return new KazoUserEvent(
            PermissionGranted,
            Text("UserEvent.PermissionGranted", app.Name, capabilities),
            AppSubject(app),
            KazoEventSeverity.Info,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["capabilities"] = capabilities,
                ["admin"] = administrator,
            });
    }

    /// <summary>Builds the event of dangerous permissions the user refused to <paramref name="app"/>.</summary>
    /// <param name="app">App the permissions were refused to.</param>
    /// <param name="capabilities">The permissions, comma-separated.</param>
    public static KazoUserEvent PermissionsRefused(AppManifest app, string capabilities)
    {
        ArgumentNullException.ThrowIfNull(app);
        return new KazoUserEvent(
            PermissionRefused,
            Text("UserEvent.PermissionRefused", app.Name, capabilities),
            AppSubject(app),
            KazoEventSeverity.Warning,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["capabilities"] = capabilities });
    }

    /// <summary>Builds the event of a role granted to or taken from the user (ADR 0247, section 4).</summary>
    /// <param name="role">The role, <c>&lt;Feature&gt;:&lt;role&gt;</c>.</param>
    /// <param name="granted">Whether the role was granted rather than revoked.</param>
    /// <param name="administrator">Login of the administrator who changed it.</param>
    public static KazoUserEvent RoleChanged(string role, bool granted, string administrator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        return new KazoUserEvent(
            granted ? RoleGranted : RoleRevoked,
            Text(granted ? "UserEvent.RoleGranted" : "UserEvent.RoleRevoked", role, administrator),
            new KazoEventSubject("role", role, role),
            granted ? KazoEventSeverity.Info : KazoEventSeverity.Warning,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["admin"] = administrator });
    }

    /// <summary>Builds the event of an app installed for the user alone (ADR 0208).</summary>
    /// <param name="app">The installed app.</param>
    /// <param name="version">Its version.</param>
    public static KazoUserEvent Installed(AppManifest app, string version)
    {
        ArgumentNullException.ThrowIfNull(app);
        return new KazoUserEvent(
            AppInstalled,
            Text("UserEvent.AppInstalled", app.Name, version),
            AppSubject(app),
            KazoEventSeverity.Info,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["version"] = version });
    }

    /// <summary>Builds the event of a synchronization conflict resolved on one of the user's files (ADR 0101).</summary>
    /// <param name="path">The file, relative to the user's home.</param>
    /// <param name="choice">
    /// What was kept: <c>KeepMine</c>, <c>KeepOther</c> or <c>KeepBoth</c>, or <c>Same</c> when both
    /// devices ended with the same file.
    /// </param>
    /// <param name="automatic">Whether the OS resolved it without asking.</param>
    public static KazoUserEvent ConflictResolved(string path, string choice, bool automatic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new KazoUserEvent(
            SyncConflictResolved,
            Text(automatic ? "UserEvent.SyncConflictResolvedAutomatically" : "UserEvent.SyncConflictResolved", path, Text($"UserEvent.SyncChoice.{choice}")),
            new KazoEventSubject("file", path, Path.GetFileName(path.TrimEnd('/'))),
            automatic ? KazoEventSeverity.Info : KazoEventSeverity.Warning,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["choice"] = choice,
                ["automatic"] = automatic ? "true" : "false",
            });
    }

    private static KazoEventSubject AppSubject(AppManifest app) => new("app", app.Id, app.Name);

    private static string Text(string key, params object[] arguments) =>
        Strings.For(CultureInfo.CurrentUICulture)[key, arguments].Value;
}
