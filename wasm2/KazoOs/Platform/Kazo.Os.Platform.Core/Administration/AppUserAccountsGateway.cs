using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Administration;
using Kazo.Os.AppSdk.UserEvents;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Kazo.Os.Platform.Core.Sessions;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Administration;

/// <summary>
/// Trusted <see cref="IAppUserAccountsGateway"/> (ADR 0120): lists accounts and changes the session
/// user's own password with no capability (the current password is the proof, as <c>passwd</c>
/// checks it through PAM); resetting or removing another account's password, and adding or deleting
/// an account (ADR 0169), check <c>users.manage</c> and ask the OS authentication agent for an
/// administrator first. The session user manages their own passkeys with no capability (ADR 0168):
/// the browser asks for their presence.
/// </summary>
internal sealed class AppUserAccountsGateway : IAppUserAccountsGateway
{
    private readonly Func<LocalPasswordService>? _passwords;
    private readonly ICapabilityChecker _capabilities;
    private readonly AdministratorGate _gate;
    private readonly SemaphoreSlim _eventsGate = new(1, 1);
    private readonly HashSet<LocalUserId> _eventsRead = [];
    private string? _eventsAdministrator;

    /// <summary>The <c>auth.log</c> action of a read of another account's event journal (ADR 0254 §3).</summary>
    public const string EventsReadOtherAction = "events.read-other";

    /// <summary>Creates the gateway for one app instance.</summary>
    /// <param name="passwords">Lazily resolved password service; <see langword="null"/> on a host without accounts.</param>
    /// <param name="capabilities">The app's capability checker.</param>
    /// <param name="gate">Administrator authentication for this app.</param>
    public AppUserAccountsGateway(Func<LocalPasswordService>? passwords, ICapabilityChecker capabilities, AdministratorGate gate)
    {
        _passwords = passwords;
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    }

    /// <summary>Gets the lazily resolved account service of ADR 0169; <see langword="null"/> leaves adding and deleting unsupported.</summary>
    public Func<LocalAccountService>? Accounts { get; init; }

    /// <summary>Gets the lazily resolved passkey service of ADR 0168; <see langword="null"/> on a host without WebAuthn.</summary>
    public Func<PasskeyService>? Passkeys { get; init; }

    /// <summary>Gets the lazily resolved "Create my account" requests of ADR 0187; <see langword="null"/> on a host without them.</summary>
    public Func<GuestAccountRequests>? GuestRequests { get; init; }

    /// <summary>Gets the lazily resolved roles of the features of ADR 0247; <see langword="null"/> on a host without them.</summary>
    public Func<Roles.FeatureRoleService?>? Roles { get; init; }

    /// <summary>Gets the users' event journals of ADR 0254; <see langword="null"/> on a host without them.</summary>
    public Diagnostics.IUserEventLog? UserEvents { get; init; }

    /// <summary>Gets <c>/var/log/auth.log</c>, where each read of another account's journal is written; optional.</summary>
    public Func<IAuditLog>? Audit { get; init; }

    /// <summary>Gets the clock of the <c>auth.log</c> entries; the system clock by default.</summary>
    public Func<DateTimeOffset> Clock { get; init; } = static () => DateTimeOffset.UtcNow;

    /// <inheritdoc />
    /// <remarks>
    /// ADR 0254 §3: one ticket per process. The first read of another account asks the OS's window;
    /// the administrator who answered is kept for this process (one gateway per app instance), like a
    /// viewer session, and each account read is written once to <c>auth.log</c>.
    /// </remarks>
    public async ValueTask<IReadOnlyList<KazoUserEventEntry>> ReadEventsAsync(
        string loginName, KazoUserEventFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loginName);
        ArgumentNullException.ThrowIfNull(filter);
        filter.EnsureValidLimit();
        Diagnostics.IUserEventLog journals = UserEvents ?? throw new NotSupportedException("This Kazo OS host keeps no user event journal.");
        IReadOnlyList<LocalUser> users = await Service().ListAsync(cancellationToken).ConfigureAwait(false);
        LocalUser user = users.FirstOrDefault(candidate => string.Equals(candidate.LoginName.Value, loginName.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"No account is named '{loginName}'.", nameof(loginName));
        if (user.Id != _gate.Principal.UserId)
        {
            await AuthorizeEventsReadAsync(user, cancellationToken).ConfigureAwait(false);
        }

        return await journals.ReadAsync(user.Id, filter, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks an administrator once for this process, then writes <c>events.read-other</c> to
    /// <c>auth.log</c> the first time each account is read.
    /// </summary>
    private async Task AuthorizeEventsReadAsync(LocalUser user, CancellationToken cancellationToken)
    {
        await _eventsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _eventsAdministrator ??= await _gate.AuthenticateAsync(
                _gate.Text["Admin.ReadUserEvents", user.LoginName.Value],
                EventsReadOtherAction,
                user.LoginName.Value,
                cancellationToken).ConfigureAwait(false);
            if (_eventsRead.Add(user.Id))
            {
                Audit?.Invoke().Record(new AuditEntry(
                    Clock(),
                    Guid.NewGuid(),
                    _gate.Principal.UserId.ToString(),
                    EventsReadOtherAction,
                    user.LoginName.Value,
                    AuditOutcome.Success,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["user"] = user.LoginName.Value,
                        ["admin"] = _eventsAdministrator,
                        ["requestedBy"] = _gate.Principal.LoginName.Value,
                    }));
            }
        }
        finally
        {
            _eventsGate.Release();
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<KazoFeatureRole>> ListRolesAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<KazoFeatureRole>>(Roles?.Invoke() is { } roles
            ? [.. roles.DeclaredRoles.Select(static declared => new KazoFeatureRole(
                declared.Role.Id, declared.Role.Title, declared.FeatureId, declared.FeatureName)
            {
                Description = declared.Role.Description,
            })]
            : []);

    /// <inheritdoc />
    public ValueTask<bool> GrantRoleAsync(string loginName, string role, CancellationToken cancellationToken = default) =>
        ChangeRoleAsync(loginName, role, grant: true, cancellationToken);

    /// <inheritdoc />
    public ValueTask<bool> RevokeRoleAsync(string loginName, string role, CancellationToken cancellationToken = default) =>
        ChangeRoleAsync(loginName, role, grant: false, cancellationToken);

    /// <summary>
    /// ADR 0247 §1: <c>users.manage</c>, then an administrator in the OS's window, then the line of
    /// <c>roles.config</c>. An administrator holds every role implicitly: a grant changes nothing and
    /// asks no one, like <c>kazorole grant</c> on an administrator.
    /// </summary>
    private async ValueTask<bool> ChangeRoleAsync(string loginName, string role, bool grant, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loginName);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        Roles.FeatureRoleService roles = Roles?.Invoke() ?? throw new NotSupportedException("This Kazo OS host has no roles of features.");
        _capabilities.Require(AppCapabilities.UsersManage);
        if (!roles.DeclaredRoles.Any(declared => string.Equals(declared.Role.Id, role, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"No installed feature declares the role '{role}'.", nameof(role));
        }

        IReadOnlyList<LocalUser> users = await Service().ListAsync(cancellationToken).ConfigureAwait(false);
        LocalUser user = users.FirstOrDefault(candidate => string.Equals(candidate.LoginName.Value, loginName.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"No account is named '{loginName}'.", nameof(loginName));
        if (grant && AppAuthorityPolicy.Satisfies(user.Authority, AppAuthority.Administrator))
        {
            return false;
        }

        string admin = await _gate.AuthenticateAsync(
            _gate.Text[grant ? "Admin.GrantRole" : "Admin.RevokeRole", role, user.LoginName.Value],
            grant ? "users.role-grant" : "users.role-revoke",
            user.LoginName.Value,
            cancellationToken).ConfigureAwait(false);
        return grant
            ? await roles.GrantAsync(role, user, admin, cancellationToken).ConfigureAwait(false)
            : await roles.RevokeAsync(role, user, admin, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The roles <paramref name="user"/> holds: every declared one for an administrator, its grants otherwise.</summary>
    private IReadOnlyList<string> RolesOf(LocalUser user)
    {
        if (Roles?.Invoke() is not { } roles)
        {
            return [];
        }

        bool administrator = AppAuthorityPolicy.Satisfies(user.Authority, AppAuthority.Administrator);
        string userId = user.Id.ToString();
        return [.. roles.DeclaredRoles
            .Select(static declared => declared.Role.Id)
            .Where(role => administrator || roles.HoldersOf(role).Contains(userId))];
    }

    /// <inheritdoc />
    public async ValueTask<bool> RequestGuestAccountCreationAsync(CancellationToken cancellationToken = default)
    {
        if (GuestRequests?.Invoke() is not { } requests)
        {
            return false;
        }

        // Only the guest's own session may ask: the form converts the guest and nothing else.
        IReadOnlyList<LocalUser> users = await Service().ListAsync(cancellationToken).ConfigureAwait(false);
        return users.Any(user => user.Id == _gate.Principal.UserId && user.IsGuest) && requests.RequestCreateAccount();
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<KazoUserAccount>> ListAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<LocalUser> users = await Service().ListAsync(cancellationToken).ConfigureAwait(false);
        return users
            .OrderBy(user => user.LoginName.Value, StringComparer.Ordinal)
            .Select(ToAccount)
            .ToArray();
    }

    /// <inheritdoc />
    public async ValueTask<KazoUserAccount> CreateAsync(KazoNewUserAccount account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        LocalAccountService service = Accounts?.Invoke() ?? throw new NotSupportedException("This Kazo OS host cannot add accounts.");
        _capabilities.Require(AppCapabilities.UsersManage);
        string login = account.LoginName.Trim();
        string admin = await _gate.AuthenticateAsync(_gate.Text["Admin.AddUser", login], "users.add", login, cancellationToken).ConfigureAwait(false);
        LocalUser created = await service.CreateAsync(admin, account, cancellationToken).ConfigureAwait(false);
        return ToAccount(created);
    }

    /// <inheritdoc />
    public async ValueTask DeleteAsync(string loginName, bool removeHome, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loginName);
        LocalAccountService service = Accounts?.Invoke() ?? throw new NotSupportedException("This Kazo OS host cannot delete accounts.");
        _capabilities.Require(AppCapabilities.UsersManage);
        string admin = await _gate.AuthenticateAsync(
            _gate.Text[removeHome ? "Admin.DeleteUserAndHome" : "Admin.DeleteUser", loginName],
            "users.delete",
            loginName,
            cancellationToken).ConfigureAwait(false);
        await service.DeleteAsync(_gate.Principal, admin, loginName, removeHome, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<KazoPasskey>> ListPasskeysAsync(CancellationToken cancellationToken = default)
    {
        PasskeyService service = Passkeys?.Invoke() ?? throw new NotSupportedException("This Kazo OS host has no passkeys.");
        IReadOnlyList<LocalPasskeyCredential> passkeys = await service.ListAsync(_gate.Principal.UserId, cancellationToken).ConfigureAwait(false);
        IReadOnlySet<string> unlocking = await service.GetKeyringPasskeyIdsAsync(_gate.Principal, cancellationToken).ConfigureAwait(false);
        return [.. passkeys.Select(passkey => ToPasskey(passkey, unlocking))];
    }

    /// <inheritdoc />
    public async ValueTask<KazoPasskey> AddPasskeyAsync(string name, CancellationToken cancellationToken = default)
    {
        PasskeyService service = Passkeys?.Invoke() ?? throw new NotSupportedException("This Kazo OS host has no passkeys.");
        LocalPasskeyCredential passkey = await service.RegisterAsync(_gate.Principal, name, cancellationToken).ConfigureAwait(false);
        IReadOnlySet<string> unlocking = await service.GetKeyringPasskeyIdsAsync(_gate.Principal, cancellationToken).ConfigureAwait(false);
        return ToPasskey(passkey, unlocking);
    }

    /// <inheritdoc />
    public ValueTask<bool> RemovePasskeyAsync(string passkeyId, CancellationToken cancellationToken = default)
    {
        PasskeyService service = Passkeys?.Invoke() ?? throw new NotSupportedException("This Kazo OS host has no passkeys.");
        return service.RemoveAsync(_gate.Principal, passkeyId, cancellationToken);
    }

    private KazoUserAccount ToAccount(LocalUser user) => new(
        user.LoginName.Value,
        user.DisplayName,
        user.Authority,
        user.Enabled,
        user.Credential is not null,
        user.UpdatedAtUtc,
        user.Id == _gate.Principal.UserId)
    {
        PasskeyCount = user.Passkeys.Count,
        IsGuest = user.IsGuest,
        Roles = RolesOf(user),
    };

    private static KazoPasskey ToPasskey(LocalPasskeyCredential passkey, IReadOnlySet<string> unlocking) =>
        new(passkey.Id, passkey.Name, passkey.CreatedAtUtc, passkey.LastUsedAtUtc, unlocking.Contains(passkey.Id));

    /// <inheritdoc />
    public ValueTask ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default) =>
        Service().ChangeOwnPasswordAsync(_gate.Principal, currentPassword, newPassword, cancellationToken);

    /// <inheritdoc />
    public async ValueTask ResetPasswordAsync(string loginName, string? newPassword, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loginName);
        LocalPasswordService service = Service();
        _capabilities.Require(AppCapabilities.UsersManage);
        bool remove = newPassword is null;
        string admin = await _gate.AuthenticateAsync(
            _gate.Text[remove ? "Admin.RemovePassword" : "Admin.ResetPassword", loginName],
            remove ? "users.password-delete" : "users.password-reset",
            loginName,
            cancellationToken).ConfigureAwait(false);
        await service.ResetPasswordAsync(_gate.Principal, admin, loginName, newPassword, cancellationToken).ConfigureAwait(false);
    }

    private LocalPasswordService Service() =>
        _passwords?.Invoke() ?? throw new NotSupportedException("This Kazo OS host cannot change passwords.");
}
