using Kazo.Os.App.Abstractions;

namespace Kazo.Os.AppSdk;

/// <summary>
/// Describes the user a process runs as (ADR 0056, section 1), like the <c>passwd</c> entry Linux
/// returns for <c>getpwuid(geteuid())</c>.
/// </summary>
/// <param name="LoginName">Login name (<c>benoit</c>, or <c>root</c> under <c>sudo</c>).</param>
/// <param name="DisplayName">Display name captured at login (the GECOS field); <c>root</c> under <c>sudo</c>.</param>
/// <param name="HomeDirectory">Canonical home directory (<c>/home/benoit</c>, or <c>/root</c> under <c>sudo</c>).</param>
/// <param name="IsAdministrator">Whether the user is an administrator, or the process runs as root.</param>
public sealed record KazoUserInfo(string LoginName, string DisplayName, string HomeDirectory, bool IsAdministrator)
{
    /// <summary>Gets the roles of the installed features the user holds (ADR 0247 §1).</summary>
    public IReadOnlySet<string> Roles { get; init; } = NoRoles.Instance;

    /// <summary>
    /// Gets whether the user is in <paramref name="role"/> (ADR 0247 §1, ADR 0283). A role of the OS has
    /// a simple name: <see cref="KazoRoles.Administrator"/> (<c>admin</c>). A role of a feature is
    /// <c>&lt;feature&gt;:&lt;role&gt;</c>, the feature's prefix then its role, such as
    /// <c>x86:vm-manager</c>: granted by an administrator, or held implicitly by every administrator
    /// (and by root under <c>sudo</c>). Any other simple name is no role of the OS: <see langword="false"/>.
    /// </summary>
    /// <param name="role">The role, such as <c>admin</c> or <c>x86:vm-manager</c>.</param>
    /// <example><code>if (KazoUser.Current.IsInRole("x86:vm-manager")) { /* the admin subcommand */ }</code></example>
    public bool IsInRole(string role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        if (string.Equals(role, KazoRoles.Administrator, StringComparison.Ordinal))
        {
            return IsAdministrator;
        }

        return role.Contains(KazoRoles.FeatureSeparator, StringComparison.Ordinal) && (IsAdministrator || Roles.Contains(role));
    }
}

/// <summary>The names of the roles (ADR 0283).</summary>
public static class KazoRoles
{
    /// <summary>The role of the OS's administrators (and of root under <c>sudo</c>).</summary>
    public const string Administrator = "admin";

    /// <summary>Separates a feature's prefix from its role: <c>x86:vm-manager</c>.</summary>
    public const char FeatureSeparator = ':';
}

/// <summary>The empty set of roles.</summary>
internal static class NoRoles
{
    /// <summary>Gets the empty set.</summary>
    public static IReadOnlySet<string> Instance { get; } = new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>
/// The user the current process runs as (ADR 0056, section 1). Replaces
/// <c>ISessionService.CurrentPrincipal</c>, which apps may no longer inject (ADR 0055).
/// </summary>
/// <remarks>
/// No capability is needed: on Linux every process knows its own user (<c>getuid</c>,
/// <c>getpwuid</c>). The value describes the <b>effective</b> user: a process started through
/// <c>sudo</c> (ADR 0042) sees <c>root</c> and <c>/root</c>, like <c>whoami</c> under <c>sudo</c>.
/// </remarks>
public static class KazoUser
{
    /// <summary>Login name, display name and home directory of root, the effective user under <c>sudo</c>.</summary>
    public static readonly KazoUserInfo Root = new("root", "root", "/root", IsAdministrator: true);

    /// <summary>Gets the effective user of the current process.</summary>
    /// <exception cref="KazoExecutionContextMissingException">No app context is active.</exception>
    public static KazoUserInfo Current => Describe(KazoExecutionContext.Current);

    /// <summary>Gets whether the effective user is in <paramref name="role"/>: <see cref="KazoUserInfo.IsInRole(string)"/> of <see cref="Current"/>.</summary>
    /// <param name="role">The role, such as <c>admin</c> or <c>x86:vm-manager</c>.</param>
    /// <exception cref="KazoExecutionContextMissingException">No app context is active.</exception>
    public static bool IsInRole(string role) => Current.IsInRole(role);

    /// <summary>
    /// Describes the effective user of <paramref name="context"/>. Useful where no ambient scope is
    /// active, for instance when a component caches the value for rendering.
    /// </summary>
    /// <param name="context">The app context whose user is described.</param>
    public static KazoUserInfo Describe(IAppExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.IsRootElevated)
        {
            return Root;
        }

        return new KazoUserInfo(
            context.UserLoginName,
            context.UserDisplayName,
            context.HomeDirectory,
            AppAuthorityPolicy.Satisfies(context.UserAuthority, AppAuthority.Administrator))
        {
            Roles = context.Roles,
        };
    }
}
