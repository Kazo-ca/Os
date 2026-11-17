using System.Globalization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Features;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Platform.Core.Elevation;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Roles;

/// <summary>
/// The tickets of the operations of the features (ADR 0247 §3): valid 30 seconds, once, for the
/// feature and the operation they name, like the sudo tickets of <c>InMemoryElevationTicketStore</c>.
/// </summary>
public sealed class FeatureOperationTickets
{
    /// <summary>How long a ticket stays valid.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    private readonly Func<DateTimeOffset> _clock;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, KazoAuthenticationTicket> _tickets = [];

    /// <summary>Creates the store.</summary>
    /// <param name="clock">The clock; the system clock by default.</param>
    public FeatureOperationTickets(Func<DateTimeOffset>? clock = null) => _clock = clock ?? (static () => DateTimeOffset.UtcNow);

    /// <summary>Issues a ticket for one operation of one feature.</summary>
    /// <param name="featureId">The feature.</param>
    /// <param name="operation">The operation.</param>
    /// <param name="scope">Who authenticated.</param>
    /// <param name="authenticatedBy">The login that authenticated.</param>
    public KazoAuthenticationTicket Issue(string featureId, string operation, KazoAuthenticationScope scope, string authenticatedBy)
    {
        KazoAuthenticationTicket ticket = new(Guid.NewGuid(), featureId, operation, scope, authenticatedBy, _clock() + Lifetime);
        lock (_gate)
        {
            _tickets[ticket.Token] = ticket;
        }

        return ticket;
    }

    /// <summary>Uses a ticket up: true once, for its feature and operation, before it expires.</summary>
    /// <param name="token">The ticket.</param>
    /// <param name="featureId">The feature presenting it.</param>
    /// <param name="operation">The operation about to run.</param>
    public bool TryConsume(Guid token, string featureId, string operation)
    {
        lock (_gate)
        {
            if (!_tickets.Remove(token, out KazoAuthenticationTicket? ticket))
            {
                return false;
            }

            return ticket.ExpiresAtUtc > _clock()
                && string.Equals(ticket.FeatureId, featureId, StringComparison.Ordinal)
                && string.Equals(ticket.Operation, operation, StringComparison.Ordinal);
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
    public async ValueTask<KazoAuthenticationTicket> AuthenticateAsync(
        string operation, KazoAuthenticationScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        string featureId = RequireFeature();
        FeatureRoleService roles = _roles();
        if (scope == KazoAuthenticationScope.RoleHolder
            && _caller.RequiresRole is { } role
            && !roles.HasRole(_principal, role, _isRootElevated))
        {
            roles.RecordDenied(_caller, _principal);
            throw new KazoUnauthorizedAccessException($"{_caller.Id}: the role '{role}' is required.");
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

        return roles.Tickets.Issue(featureId, operation, scope, result.AdministratorLogin ?? _principal.LoginName.Value);
    }

    /// <inheritdoc />
    public ValueTask ConsumeAsync(KazoAuthenticationTicket ticket, string operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        string featureId = RequireFeature();
        if (!_roles().Tickets.TryConsume(ticket.Token, featureId, operation))
        {
            throw new KazoUnauthorizedAccessException($"{_featureName}: the ticket does not authorize '{operation}' (expired, used, or for another operation).");
        }

        return ValueTask.CompletedTask;
    }

    private string RequireFeature() =>
        _featureId ?? throw new KazoUnauthorizedAccessException($"{_caller.Id} is not a feature: only a feature authenticates its operations (ADR 0247 §3).");
}
