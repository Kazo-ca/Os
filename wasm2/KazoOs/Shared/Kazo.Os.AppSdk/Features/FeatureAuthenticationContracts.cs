namespace Kazo.Os.AppSdk.Features;

/// <summary>Who authenticates for a sensitive operation of a feature (ADR 0247 §3).</summary>
public enum KazoAuthenticationScope
{
    /// <summary>The user in session authenticates again, like <c>sudo</c>; they must hold the role of the entry point.</summary>
    RoleHolder = 0,

    /// <summary>An enabled administrator authenticates, themselves or another account.</summary>
    Administrator = 1,
}

/// <summary>
/// The proof that the OS authenticated a sensitive operation of a feature (ADR 0247 §3): valid
/// 30 seconds, once, for the operation and the user it names. The feature presents it with
/// <see cref="KazoFeatureAuthentication.ConsumeAsync(KazoAuthenticationTicket, string, CancellationToken)"/>
/// before it acts; a ticket of another operation is refused. Only its <see cref="Token"/> counts: the
/// OS answers with the ticket it issued, whose <see cref="Scope"/> the feature reads.
/// </summary>
/// <param name="Token">The ticket's identifier.</param>
/// <param name="FeatureId">The feature it was issued to.</param>
/// <param name="Operation">The operation it authorizes, such as <c>x86.vm.delete-shared</c>.</param>
/// <param name="Scope">Who authenticated.</param>
/// <param name="AuthenticatedBy">The login of the account that authenticated.</param>
/// <param name="ExpiresAtUtc">When it stops being valid.</param>
public sealed record KazoAuthenticationTicket(
    Guid Token, string FeatureId, string Operation, KazoAuthenticationScope Scope, string AuthenticatedBy, DateTimeOffset ExpiresAtUtc);

/// <summary>
/// The OS's authentication for the sensitive operations of a feature (ADR 0247 §3): the OS's own
/// window, never a dialog of the feature, which never sees a password. For the entry points and
/// services of a feature only; an ordinary app is refused.
/// </summary>
public interface IKazoFeatureAuthentication
{
    /// <summary>Gets whether the host can authenticate an operation.</summary>
    bool IsSupported => true;

    /// <summary>
    /// Asks the OS to authenticate <paramref name="operation"/>; three failures cancel. The
    /// <see cref="KazoAuthenticationScope.RoleHolder"/> scope is for an entry point marked
    /// <c>RequiresRole</c>, whose role the user must hold; elsewhere (an entry point without a role, a
    /// service) it is refused, and <see cref="KazoAuthenticationScope.Administrator"/> applies.
    /// </summary>
    /// <param name="operation">The operation, shown in the window and written to <c>/var/log/auth.log</c>.</param>
    /// <param name="scope">Who authenticates.</param>
    /// <param name="cancellationToken">Cancels the window.</param>
    /// <returns>The ticket of the operation.</returns>
    ValueTask<KazoAuthenticationTicket> AuthenticateAsync(string operation, KazoAuthenticationScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Presents a ticket before the operation: it is used up, and refused when expired, used, of
    /// another user or for another operation.
    /// </summary>
    /// <param name="ticket">The ticket <see cref="AuthenticateAsync"/> returned.</param>
    /// <param name="operation">The operation about to run.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>The ticket the OS issued: read its <see cref="KazoAuthenticationTicket.Scope"/> from here, never from the copy presented.</returns>
    ValueTask<KazoAuthenticationTicket> ConsumeAsync(KazoAuthenticationTicket ticket, string operation, CancellationToken cancellationToken = default);
}

/// <summary>The authentication of a host that has none: every call is refused.</summary>
public sealed class UnsupportedFeatureAuthentication : IKazoFeatureAuthentication
{
    private UnsupportedFeatureAuthentication()
    {
    }

    /// <summary>Gets the single instance.</summary>
    public static UnsupportedFeatureAuthentication Instance { get; } = new();

    /// <inheritdoc />
    public bool IsSupported => false;

    /// <inheritdoc />
    public ValueTask<KazoAuthenticationTicket> AuthenticateAsync(string operation, KazoAuthenticationScope scope, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This host cannot authenticate an operation of a feature.");

    /// <inheritdoc />
    public ValueTask<KazoAuthenticationTicket> ConsumeAsync(KazoAuthenticationTicket ticket, string operation, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This host cannot authenticate an operation of a feature.");
}

/// <summary>
/// The OS's authentication of the sensitive operations of a feature (ADR 0247 §3). A role opens the
/// entry points; each sensitive operation (delete a shared machine, change a limit) asks the OS again.
/// </summary>
/// <example>
/// <code>
/// KazoAuthenticationTicket ticket = await KazoFeatureAuthentication.AuthenticateAsync("x86.vm.delete-shared", KazoAuthenticationScope.RoleHolder);
/// KazoAuthenticationTicket issued = await KazoFeatureAuthentication.ConsumeAsync(ticket, "x86.vm.delete-shared");
/// // … delete the machine.
/// </code>
/// </example>
public static class KazoFeatureAuthentication
{
    /// <summary>Asks the OS to authenticate <paramref name="operation"/> in its own window; three failures cancel.</summary>
    /// <param name="operation">The operation, shown in the window and written to <c>/var/log/auth.log</c>.</param>
    /// <param name="scope">Who authenticates.</param>
    /// <param name="cancellationToken">Cancels the window.</param>
    /// <exception cref="IO.KazoUnauthorizedAccessException">The caller is not a feature, or the authentication failed or was cancelled.</exception>
    public static ValueTask<KazoAuthenticationTicket> AuthenticateAsync(
        string operation, KazoAuthenticationScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        return KazoExecutionContext.Current.FeatureAuthentication.AuthenticateAsync(operation, scope, cancellationToken);
    }

    /// <summary>Presents a ticket before the operation runs: it is used up.</summary>
    /// <param name="ticket">The ticket <see cref="AuthenticateAsync"/> returned.</param>
    /// <param name="operation">The operation about to run.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>The ticket the OS issued, whose scope and login are authoritative.</returns>
    /// <exception cref="IO.KazoUnauthorizedAccessException">The ticket is expired, used, of another user, of another feature or of another operation.</exception>
    public static ValueTask<KazoAuthenticationTicket> ConsumeAsync(KazoAuthenticationTicket ticket, string operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        return KazoExecutionContext.Current.FeatureAuthentication.ConsumeAsync(ticket, operation, cancellationToken);
    }
}
