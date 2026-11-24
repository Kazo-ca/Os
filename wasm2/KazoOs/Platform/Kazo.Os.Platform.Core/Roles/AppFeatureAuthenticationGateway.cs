using System.Globalization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Features;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.AppSdk.UserEvents;
using Kazo.Os.Platform.Core.Elevation;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Roles;

/// <summary>
/// The tickets of the operations of the features (ADR 0247 §3): valid 30 seconds, once, for the
/// feature, the operation and the user they name, like the sudo tickets of
/// <c>InMemoryElevationTicketStore</c>. The OS keeps the ticket it issued: what a caller presents is
/// only the token, and the stored ticket (its scope, who authenticated) is what counts.
/// </summary>
public sealed class FeatureOperationTickets
{
    /// <summary>How long a ticket stays valid.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    private readonly Func<DateTimeOffset> _clock;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, (KazoAuthenticationTicket Ticket, string UserId, KazoEventSubject? Subject)> _tickets = [];

    /// <summary>Creates the store.</summary>
    /// <param name="clock">The clock; the system clock by default.</param>
    public FeatureOperationTickets(Func<DateTimeOffset>? clock = null) => _clock = clock ?? (static () => DateTimeOffset.UtcNow);

    /// <summary>Issues a ticket for one operation of one feature, for the user whose process asked.</summary>
    /// <param name="featureId">The feature.</param>
    /// <param name="operation">The operation.</param>
    /// <param name="scope">Who authenticated.</param>
    /// <param name="authenticatedBy">The login that authenticated.</param>
    /// <param name="userId">The user whose process asked; only a process of that user can use the ticket.</param>
    /// <param name="subject">What the operation is about, written to the user's journal when the ticket is used (ADR 0254 §2).</param>
    public KazoAuthenticationTicket Issue(
        string featureId, string operation, KazoAuthenticationScope scope, string authenticatedBy, string userId, KazoEventSubject? subject = null)
    {
        DateTimeOffset now = _clock();
        KazoAuthenticationTicket ticket = new(Guid.NewGuid(), featureId, operation, scope, authenticatedBy, now + Lifetime);
        lock (_gate)
        {
            // Tickets nobody used are dropped once expired, so the store does not grow.
            foreach (Guid expired in _tickets.Where(entry => entry.Value.Ticket.ExpiresAtUtc <= now).Select(static entry => entry.Key).ToArray())
            {
                _tickets.Remove(expired);
            }

            _tickets[ticket.Token] = (ticket, userId, subject);
        }

        return ticket;
    }

    /// <summary>
    /// Uses a ticket up: once, for its feature, operation and user, before it expires. Returns the
    /// ticket the OS issued (never the caller's copy), or <see langword="null"/>.
    /// </summary>
    /// <param name="token">The ticket's token.</param>
    /// <param name="featureId">The feature presenting it.</param>
    /// <param name="operation">The operation about to run.</param>
    /// <param name="userId">The user whose process presents it.</param>
    public KazoAuthenticationTicket? TryConsume(Guid token, string featureId, string operation, string userId) =>
        TryConsume(token, featureId, operation, userId, out _);

    /// <summary>
    /// Uses a ticket up like <see cref="TryConsume(Guid, string, string, string)"/>, and gives the
    /// subject the feature named when it asked (ADR 0254 §2).
    /// </summary>
    /// <param name="token">The ticket's token.</param>
    /// <param name="featureId">The feature presenting it.</param>
    /// <param name="operation">The operation about to run.</param>
    /// <param name="userId">The user whose process presents it.</param>
    /// <param name="subject">What the operation is about, or <see langword="null"/>.</param>
    public KazoAuthenticationTicket? TryConsume(Guid token, string featureId, string operation, string userId, out KazoEventSubject? subject)
    {
        subject = null;
        lock (_gate)
        {
            if (!_tickets.Remove(token, out (KazoAuthenticationTicket Ticket, string UserId, KazoEventSubject? Subject) stored))
            {
                return null;
            }

            bool valid = stored.Ticket.ExpiresAtUtc > _clock()
                && string.Equals(stored.Ticket.FeatureId, featureId, StringComparison.Ordinal)
                && string.Equals(stored.Ticket.Operation, operation, StringComparison.Ordinal)
                && string.Equals(stored.UserId, userId, StringComparison.Ordinal);
            if (!valid)
            {
                return null;
            }

            subject = stored.Subject;
            return stored.Ticket;
        }
    }
}

/// <summary>
/// <see cref="IKazoFeatureAuthentication"/> for one process (ADR 0247 §3): only a feature or one of
/// its entry points may ask; the window is the OS's (<c>AdministratorAuthenticationOverlay</c>), never
/// one of the feature, which never sees a password. <c>RoleHolder</c>: the user in session
/// authenticates again and must hold the role of the entry point; <c>Administrator</c>: any enabled
/// administrator. Each attempt is written to <c>/var/log/auth.log</c> (<c>feature.authenticate</c>,
/// with <c>scope</c>); under <c>sudo</c> no second window opens.
/// </summary>
internal sealed class AppFeatureAuthenticationGateway : IKazoFeatureAuthentication
{
    private readonly Func<IAdministratorAuthenticationAgent>? _agent;
    private readonly Func<FeatureRoleService> _roles;
    private readonly AppManifest _caller;
    private readonly AuthenticatedPrincipal _principal;
    private readonly bool _isRootElevated;
    private readonly string? _featureId;
    private readonly string _featureName;
    private readonly Func<CultureInfo> _language;

    /// <summary>Creates the gateway for one process.</summary>
    /// <param name="agent">The OS's authentication agent; none refuses every operation.</param>
    /// <param name="roles">The roles of the features and their tickets.</param>
    /// <param name="caller">The manifest of the process.</param>
    /// <param name="principal">The user in session.</param>
    /// <param name="isRootElevated">Whether the process runs under <c>sudo</c>.</param>
    /// <param name="featureName">The name of the caller's feature.</param>
    /// <param name="language">The language of the window's text.</param>
    public AppFeatureAuthenticationGateway(
        Func<IAdministratorAuthenticationAgent>? agent,
        Func<FeatureRoleService> roles,
        AppManifest caller,
        AuthenticatedPrincipal principal,
        bool isRootElevated,
        string? featureName,
        Func<CultureInfo>? language = null)
    {
        _agent = agent;
        _roles = roles ?? throw new ArgumentNullException(nameof(roles));
        _caller = caller ?? throw new ArgumentNullException(nameof(caller));
        _principal = principal ?? throw new ArgumentNullException(nameof(principal));
        _isRootElevated = isRootElevated;
        _featureId = caller.Kind == AppKind.Feature ? caller.Id : caller.Parent;
        _featureName = caller.Kind == AppKind.Feature ? caller.Name : featureName ?? caller.Parent ?? caller.Name;
        _language = language ?? (static () => CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public ValueTask<KazoAuthenticationTicket> AuthenticateAsync(
        string operation, KazoAuthenticationScope scope, CancellationToken cancellationToken = default) =>
        AuthenticateAsync(operation, scope, subject: null, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<KazoAuthenticationTicket> AuthenticateAsync(
        string operation, KazoAuthenticationScope scope, KazoEventSubject? subject, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        if (subject is not null)
        {
            // Checked now, so a malformed subject fails before the window, not when the ticket is used.
            Diagnostics.FileUserEventLog.Validate(
                new KazoUserEvent(Diagnostics.OsUserEvents.FeatureOperationAuthenticated, operation, subject), allowSystemKinds: true);
        }

        if (!Enum.IsDefined(scope))
        {
            throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown authentication scope.");
        }

        string featureId = RequireFeature();
        FeatureRoleService roles = _roles();
        if (scope == KazoAuthenticationScope.RoleHolder && !HoldsRoleFor(roles, featureId))
        {
            roles.RecordDenied(_caller, _principal);
            throw new KazoUnauthorizedAccessException(_caller.RequiresRole is { } required
                ? $"{_caller.Id}: the role '{required}' is required."
                : $"{_caller.Id}: the RoleHolder scope needs a role of the feature '{featureId}'.");
        }

        IAdministratorAuthenticationAgent agent = _agent?.Invoke()
            ?? throw new KazoUnauthorizedAccessException("No authentication agent is available on this host.");
        AdministratorAuthenticationRequest request = new(
            _caller,
            Strings.For(_language())["Feature.Authenticate.Operation", _featureName, operation],
            operation,
            _principal,
            _isRootElevated)
        {
            RoleHolderLogin = scope == KazoAuthenticationScope.RoleHolder ? _principal.LoginName.Value : null,
            Scope = scope.ToString(),
        };
        AdministratorAuthenticationResult result = await agent.AuthenticateAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new KazoUnauthorizedAccessException($"{_featureName}: '{operation}' was not authenticated ({result.FailureReason}).");
        }

        return roles.Tickets.Issue(
            featureId, operation, scope, result.AdministratorLogin ?? _principal.LoginName.Value, _principal.UserId.ToString(), subject);
    }

    /// <inheritdoc />
    public ValueTask<KazoAuthenticationTicket> ConsumeAsync(KazoAuthenticationTicket ticket, string operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        string featureId = RequireFeature();
        FeatureRoleService roles = _roles();
        if (roles.Tickets.TryConsume(ticket.Token, featureId, operation, _principal.UserId.ToString(), out KazoEventSubject? subject) is not { } stored)
        {
            throw new KazoUnauthorizedAccessException($"{_featureName}: the ticket does not authorize '{operation}' (expired, used, of another user, or for another operation).");
        }

        // ADR 0254 §2: the sensitive operation is about to run; the user's journal says so, with the
        // subject the feature named, the OS as author and the entry point as sender.
        roles.UserEvents?.TryWriteSystem(
            Diagnostics.UserEventOwner.Of(_principal),
            new Diagnostics.UserEventAuthor(Diagnostics.OsUserEvents.AuthenticationAppId, SenderAppId: _caller.Id),
            Diagnostics.OsUserEvents.FeatureOperation(_featureName, featureId, stored, subject));
        return ValueTask.FromResult(stored);
    }

    /// <summary>
    /// ADR 0283: the RoleHolder scope needs the role of an entry point marked <c>RequiresRole</c>, or,
    /// from another entry point (a subcommand of a command open to all, such as <c>x86 admin</c>), any
    /// role of the feature: the feature checks the exact one with <c>KazoUser.IsInRole</c>. A user with
    /// no role of the feature never gets the scope.
    /// </summary>
    private bool HoldsRoleFor(FeatureRoleService roles, string featureId)
    {
        if (_caller.RequiresRole is { } required)
        {
            return roles.HasRole(_principal, required, _isRootElevated);
        }

        IReadOnlySet<string> held = roles.RolesOf(_principal, _isRootElevated);
        return roles.DeclaredRoles.Any(declared =>
            string.Equals(declared.FeatureId, featureId, StringComparison.Ordinal) && held.Contains(declared.Role.Id));
    }

    private string RequireFeature() =>
        _featureId ?? throw new KazoUnauthorizedAccessException($"{_caller.Id} is not a feature: only a feature authenticates its operations (ADR 0247 §3).");
}
