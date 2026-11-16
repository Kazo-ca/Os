using System.Net.Sockets;
using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.AppSdk.Net;

/// <summary>
/// The real network as an app holding <c>network.real.access</c> sees it (ADR 0278): whether the
/// optional Kazo server's relay can carry its connections, read without trying one.
/// </summary>
/// <remarks>
/// <para>
/// The state only needs <c>network.real.access</c>; without it, the calls throw
/// <see cref="UnauthorizedAccessException"/>, like the other real-network calls. The app sees
/// nothing else of the server: not its address, not the other granted apps, not the quotas.
/// </para>
/// <para>
/// The state can change between a read and a connection: a failed connection still says why
/// through <see cref="KazoRelayException.Reason"/> (sockets) or
/// <see cref="KazoHttpRequestException.NetworkError"/> (HTTP).
/// </para>
/// </remarks>
public static class KazoRealNetwork
{
    /// <summary>
    /// Reads the state of the relay for the calling app. The OS keeps the server's policy for
    /// 30 seconds and reads it again when the server connection changes.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The status and, when <see cref="KazoRelayStatus.Ready"/>, the ports the operator blocks.</returns>
    /// <exception cref="UnauthorizedAccessException">The app does not hold <c>network.real.access</c>.</exception>
    public static async ValueTask<KazoRelayState> GetRelayStateAsync(CancellationToken cancellationToken = default) =>
        await KazoNetworkErrorTranslator.CallAsync(gateway => gateway.GetRelayStateAsync(cancellationToken))
            .ConfigureAwait(false);

    /// <summary>
    /// Raised when the relay's status changes for the app (server connected or disconnected,
    /// policy read again, grant synchronized), not on every read. An app running in a Web Worker
    /// does not receive it: it reads the state again with <see cref="GetRelayStateAsync"/>.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">Adding a handler: the app does not hold <c>network.real.access</c>.</exception>
    public static event EventHandler<KazoRelayState>? RelayStateChanged
    {
        add
        {
            try
            {
                KazoNetworkErrorTranslator.Gateway.RelayStateChanged += value;
            }
            catch (AppGatewayAccessDeniedException denied)
            {
                throw new UnauthorizedAccessException(
                    $"The app does not hold the '{denied.Capability}' capability needed to follow the relay's state.", denied);
            }
        }

        remove => KazoNetworkErrorTranslator.Gateway.RelayStateChanged -= value;
    }
}

/// <summary>Whether the Kazo server's relay can carry the app's real-network connections (ADR 0278).</summary>
public enum KazoRelayStatus
{
    /// <summary>No Kazo server is connected, or the OS holds no access token for it.</summary>
    NoServer,

    /// <summary>A server is connected, but its operator turned the relay off.</summary>
    RelayDisabled,

    /// <summary>The relay works, but the server has not received the app's permission yet.</summary>
    GrantPending,

    /// <summary>The relay can carry the app's connections.</summary>
    Ready
}

/// <summary>State of the relay for one app (ADR 0278 §1).</summary>
/// <param name="Status">Whether the relay can carry the app's connections.</param>
public sealed record KazoRelayState(KazoRelayStatus Status)
{
    /// <summary>Gets the TCP ports the operator blocks; a connection to one fails with <see cref="KazoNetworkError.BlockedByAdministrator"/>.</summary>
    public IReadOnlyList<int> TcpBlockedPorts { get; init; } = [];

    /// <summary>Gets the UDP ports the operator blocks.</summary>
    public IReadOnlyList<int> UdpBlockedPorts { get; init; } = [];
}

/// <summary>
/// A <see cref="SocketException"/> thrown by <see cref="KazoTcpClient"/> and <see cref="KazoUdpClient"/>
/// when the Kazo server's relay could not carry the connection (ADR 0278 §2). It keeps the
/// <see cref="SocketError"/> of earlier versions (<see cref="SocketError.HostNotFound"/>, or
/// <see cref="SocketError.ConnectionRefused"/> for a blocked port), so code catching
/// <see cref="SocketException"/> keeps working; <see cref="Reason"/> gives the real cause.
/// </summary>
public sealed class KazoRelayException : SocketException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="reason">Why the relay refused: one of the relay values of <see cref="KazoNetworkError"/>, or <see cref="KazoNetworkError.BlockedByAdministrator"/>.</param>
    /// <param name="socketError">The <see cref="SocketError"/> a .NET socket reports.</param>
    /// <param name="message">A detail, or <see langword="null"/> for the default message of <paramref name="reason"/>.</param>
    public KazoRelayException(KazoNetworkError reason, SocketError socketError, string? message = null)
        : base((int)socketError, message ?? DefaultMessage(reason))
    {
        Reason = reason;
    }

    /// <summary>Gets why the relay could not carry the connection.</summary>
    public KazoNetworkError Reason { get; }

    /// <summary>Gets the stable code of <see cref="Reason"/>, for example <c>ERR_KAZO_RELAY_NO_SERVER</c>.</summary>
    public string Code => KazoNetworkErrorCodes.ToCode(Reason);

    private static string DefaultMessage(KazoNetworkError reason) => reason switch
    {
        KazoNetworkError.RelayNoServer => "No Kazo server is connected to relay this connection.",
        KazoNetworkError.RelayDisabled => "The Kazo server's operator turned the relay off.",
        KazoNetworkError.RelayGrantPending => "The Kazo server has not received this app's permission yet.",
        KazoNetworkError.BlockedByAdministrator => "The Kazo server's operator blocks this port.",
        _ => "The Kazo server's relay could not carry this connection."
    };
}
