using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Simulation.Abstractions.Settings;

namespace Kazo.Os.Platform.Core.Roles;

/// <summary>
/// <c>/etc/kazoos/roles.config</c> (ADR 0247 §1): the roles of the features an administrator granted
/// to ordinary users, one line per role, <c>x86.vm-admin=&lt;guid&gt;,&lt;guid&gt;</c>, by the users'
/// stable identifiers (ADR 0169), never their names. An administrator is never listed: they hold every
/// role implicitly. Lines of a feature that is not installed stay and count again when it returns.
/// </summary>
public static class FeatureRolesDocument
{
    /// <summary>The file name under <c>/etc/kazoos</c>.</summary>
    public const string FileName = "roles.config";

    /// <summary>The section of the grants.</summary>
    public const string Section = "roles";

    /// <summary>The machine file.</summary>
    public static OsSettingsFile MachineFile { get; } = new(OsSettingsLocation.System, FileName);

    /// <summary>The header written with the first grant.</summary>
    public const string DefaultContent =
        "# Roles of the features granted to ordinary users (ADR 0247). One role per line, set to the\n"
        + "# comma-separated ids of the users who hold it. Administrators hold every role and are not listed.\n"
        + "# Edit with: sudo kazorole grant|revoke <user> <role>, or Settings > Users.\n"
        + "[roles]\n";

    /// <summary>Reads the grants: role → user ids. Never fails; a broken id is skipped.</summary>
    /// <param name="content">The file's content, or <see langword="null"/>.</param>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> Parse(string? content)
    {
        Dictionary<string, IReadOnlySet<string>> grants = new(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(content))
        {
            return grants;
        }

        foreach (ConfigLine line in ConfigDocumentLines.Parse(content))
        {
            if (line.Kind == ConfigLineKind.KeyValue
                && !line.InMalformedSection
                && string.Equals(line.Group, Section, StringComparison.Ordinal)
                && line.Key is { Length: > 0 } role)
            {
                grants[role] = (line.Value ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(static id => Guid.TryParse(id, out _))
                    .Select(static id => Guid.Parse(id).ToString("N"))
                    .ToHashSet(StringComparer.Ordinal);
            }
        }

        return grants;
    }

    /// <summary>Writes the holders of one role, keeping every other line; no holder removes the line.</summary>
    /// <param name="content">The file's content, or <see langword="null"/> for a new file.</param>
    /// <param name="role">The role.</param>
    /// <param name="holders">The user ids who hold it.</param>
    public static string SetHolders(string? content, string role, IEnumerable<string> holders)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentNullException.ThrowIfNull(holders);
        string[] ids = [.. holders.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        string current = string.IsNullOrEmpty(content) ? DefaultContent : content;
        return ids.Length == 0
            ? ConfigDocumentEditor.RemoveValue(current, Section, role)
            : ConfigDocumentEditor.SetValue(current, Section, role, string.Join(',', ids));
    }
}

/// <summary>A role a feature of the running catalog declares.</summary>
/// <param name="FeatureId">The feature.</param>
/// <param name="FeatureName">Its name.</param>
/// <param name="Role">The role as the manifest declares it.</param>
public sealed record DeclaredFeatureRole(string FeatureId, string FeatureName, AppRoleManifest Role);

/// <summary>
/// The roles of the features (ADR 0247): what the installed features declare, who holds them (an
/// administrator holds them all; an ordinary user those <c>roles.config</c> grants them), and the
/// grants an administrator makes, each written to <c>/var/log/auth.log</c> (<c>role.grant</c>,
/// <c>role.revoke</c>). The grants are cached: <see cref="RefreshAsync"/> reads the file at boot and
/// each change reads it again, so building an app's context reads no file.
/// </summary>
public sealed class FeatureRoleService
{
    /// <summary>The audit action of a grant.</summary>
    public const string GrantAction = "role.grant";

    /// <summary>The audit action of a revocation.</summary>
    public const string RevokeAction = "role.revoke";

    /// <summary>The audit action of an entry point refused for want of its role.</summary>
    public const string DeniedAction = "feature.role-denied";

    private readonly Func<AppCatalog> _catalog;
    private readonly IOsSettingsService _settings;
    private readonly IAuditLog? _audit;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyDictionary<string, IReadOnlySet<string>> _grants = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

    /// <summary>Creates the service.</summary>
    /// <param name="catalog">The running catalog, whose features declare the roles.</param>
    /// <param name="settings">Reads and writes <c>/etc/kazoos/roles.config</c>, as the system.</param>
    /// <param name="audit">Where grants and refusals are written (<c>/var/log/auth.log</c>); optional.</param>
    /// <param name="clock">The clock of the audit entries; the system clock by default.</param>
    public FeatureRoleService(Func<AppCatalog> catalog, IOsSettingsService settings, IAuditLog? audit = null, Func<DateTimeOffset>? clock = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _audit = audit;
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);

        // A grant written elsewhere (root editing roles.config, another process) is read again, so a
        // revocation takes effect without a restart.
        _settings.DocumentChanged += OnDocumentChanged;
    }

    /// <summary>Gets the tickets of the operations of the features (ADR 0247 §3), shared by every process of the session.</summary>
    public FeatureOperationTickets Tickets { get; } = new();

    /// <summary>Gets the roles the features of the running catalog declare, by role id.</summary>
    public IReadOnlyList<DeclaredFeatureRole> DeclaredRoles =>
        [.. _catalog().Manifests.Values
            .Where(static manifest => manifest.Kind == AppKind.Feature)
            .SelectMany(static feature => (feature.Roles ?? []).Select(role => new DeclaredFeatureRole(feature.Id, feature.Name, role)))
            .OrderBy(static declared => declared.Role.Id, StringComparer.Ordinal)];

    /// <summary>Reads <c>roles.config</c> again, after any grant in progress.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async ValueTask RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _grants = FeatureRolesDocument.Parse(await _settings.ReadFileAsync(FeatureRolesDocument.MachineFile, cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Gets the error of the last background read of <c>roles.config</c>, or <see langword="null"/>.</summary>
    public Exception? LastRefreshError { get; private set; }

    private void OnDocumentChanged(object? sender, OsSettingsChangedEventArgs args)
    {
        if (args.Concerns(FeatureRolesDocument.MachineFile.RelativePath))
        {
            _ = RefreshInBackgroundAsync();
        }
    }

    /// <summary>
    /// Reads the file again off the writer's stack (the event is raised while a write may hold the
    /// gate). A failed read keeps the previous grants and is kept in <see cref="LastRefreshError"/>.
    /// </summary>
    private async Task RefreshInBackgroundAsync()
    {
        try
        {
            await RefreshAsync().ConfigureAwait(false);
            LastRefreshError = null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LastRefreshError = exception;
        }
    }

    /// <summary>
    /// Gets the roles <paramref name="principal"/> holds among those the installed features declare:
    /// all of them for an administrator (ADR 0247 §1, answer 233 B), the granted ones otherwise.
    /// </summary>
    /// <param name="principal">The user.</param>
    /// <param name="isRootElevated">Whether the process runs as root under <c>sudo</c>, which holds every role.</param>
    public IReadOnlySet<string> RolesOf(AuthenticatedPrincipal principal, bool isRootElevated = false)
    {
        ArgumentNullException.ThrowIfNull(principal);
        bool administrator = isRootElevated || AppAuthorityPolicy.Satisfies(principal.Authority, AppAuthority.Administrator);
        string userId = principal.UserId.ToString();
        return DeclaredRoles
            .Select(static declared => declared.Role.Id)
            .Where(role => administrator || (_grants.TryGetValue(role, out IReadOnlySet<string>? holders) && holders.Contains(userId)))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Gets whether <paramref name="principal"/> holds <paramref name="role"/>.</summary>
    /// <param name="principal">The user.</param>
    /// <param name="role">The role.</param>
    /// <param name="isRootElevated">Whether the process runs as root under <c>sudo</c>.</param>
    public bool HasRole(AuthenticatedPrincipal principal, string role, bool isRootElevated = false) =>
        RolesOf(principal, isRootElevated).Contains(role);

    /// <summary>
    /// Gets whether <paramref name="entryPoint"/> opens to <paramref name="principal"/> (ADR 0247 §2): it
    /// requires no role, or the user holds it. Without the service (a host with no roles), only an
    /// administrator or root sees an entry point that requires a role: the check fails closed.
    /// </summary>
    /// <param name="roles">The roles of the features, or <see langword="null"/>.</param>
    /// <param name="entryPoint">The entry point.</param>
    /// <param name="principal">The user.</param>
    /// <param name="isRootElevated">Whether the process runs as root under <c>sudo</c>.</param>
    public static bool OpensTo(FeatureRoleService? roles, AppManifest entryPoint, AuthenticatedPrincipal principal, bool isRootElevated = false)
    {
        ArgumentNullException.ThrowIfNull(entryPoint);
        ArgumentNullException.ThrowIfNull(principal);
        if (entryPoint.RequiresRole is not { } role)
        {
            return true;
        }

        return roles is not null
            ? roles.HasRole(principal, role, isRootElevated)
            : isRootElevated || AppAuthorityPolicy.Satisfies(principal.Authority, AppAuthority.Administrator);
    }

    /// <summary>Gets the ordinary users <c>roles.config</c> lists for <paramref name="role"/>.</summary>
    /// <param name="role">The role.</param>
    public IReadOnlySet<string> HoldersOf(string role) =>
        _grants.TryGetValue(role, out IReadOnlySet<string>? holders) ? holders : new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Grants <paramref name="role"/> to <paramref name="user"/> after an administrator authenticated.
    /// An administrator already holds every role: nothing is written and this returns <see langword="false"/>.
    /// </summary>
    /// <param name="role">A role an installed feature declares.</param>
    /// <param name="user">The user.</param>
    /// <param name="administrator">The login of the administrator who authenticated, for <c>auth.log</c>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether the file changed.</returns>
    /// <exception cref="ArgumentException">No installed feature declares the role.</exception>
    public ValueTask<bool> GrantAsync(string role, LocalUser user, string administrator, CancellationToken cancellationToken = default) =>
        ChangeAsync(role, user, administrator, grant: true, cancellationToken);

    /// <summary>Takes <paramref name="role"/> back from <paramref name="user"/> after an administrator authenticated.</summary>
    /// <param name="role">The role.</param>
    /// <param name="user">The user.</param>
    /// <param name="administrator">The login of the administrator who authenticated.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether the file changed.</returns>
    public ValueTask<bool> RevokeAsync(string role, LocalUser user, string administrator, CancellationToken cancellationToken = default) =>
        ChangeAsync(role, user, administrator, grant: false, cancellationToken);

    /// <summary>Writes <c>feature.role-denied</c> to <c>auth.log</c>: an entry point refused for want of its role (ADR 0247 §4).</summary>
    /// <param name="app">The entry point.</param>
    /// <param name="principal">The user refused.</param>
    public void RecordDenied(AppManifest app, AuthenticatedPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(principal);
        _audit?.Record(new AuditEntry(
            _clock(), Guid.NewGuid(), principal.UserId.ToString(), DeniedAction, app.Id, AuditOutcome.Denied,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["app"] = app.Parent ?? app.Id,
                ["entryPoint"] = app.Id,
                ["user"] = principal.UserId.ToString(),
                ["role"] = app.RequiresRole ?? "-",
            }));
    }

    private async ValueTask<bool> ChangeAsync(string role, LocalUser user, string administrator, bool grant, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentNullException.ThrowIfNull(user);
        if (DeclaredRoles.All(declared => !string.Equals(declared.Role.Id, role, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"No installed feature declares the role '{role}'.", nameof(role));
        }

        if (grant && AppAuthorityPolicy.Satisfies(user.Authority, AppAuthority.Administrator))
        {
            // Answer 233 B: an administrator holds every role; roles.config never lists one.
            return false;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? content = await _settings.ReadFileAsync(FeatureRolesDocument.MachineFile, cancellationToken).ConfigureAwait(false);
            IReadOnlyDictionary<string, IReadOnlySet<string>> grants = FeatureRolesDocument.Parse(content);
            HashSet<string> holders = grants.TryGetValue(role, out IReadOnlySet<string>? current)
                ? new HashSet<string>(current, StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            string userId = user.Id.ToString();
            bool changed = grant ? holders.Add(userId) : holders.Remove(userId);
            if (changed)
            {
                await _settings.WriteFileAsync(
                        FeatureRolesDocument.MachineFile, FeatureRolesDocument.SetHolders(content, role, holders), writer: null, cancellationToken)
                    .ConfigureAwait(false);
                _audit?.Record(new AuditEntry(
                    _clock(), Guid.NewGuid(), administrator, grant ? GrantAction : RevokeAction, role, AuditOutcome.Success,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["admin"] = administrator,
                        ["user"] = userId,
                        ["role"] = role,
                    }));
            }

            _grants = FeatureRolesDocument.Parse(await _settings.ReadFileAsync(FeatureRolesDocument.MachineFile, cancellationToken).ConfigureAwait(false));
            return changed;
        }
        finally
        {
            _gate.Release();
        }
    }
}
