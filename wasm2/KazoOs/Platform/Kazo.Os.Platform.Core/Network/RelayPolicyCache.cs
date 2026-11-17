using System.Net.Http;
using Kazo.Os.AppSdk.Net;
using Kazo.Os.Platform.Core.ServerConnection;
using Kazo.Os.Server.Contracts.Proxy;
using Kazo.Os.Simulation.Abstractions.ServerConnection;

namespace Kazo.Os.Platform.Core.Network;

/// <summary>
/// The relay policy of the connected Kazo server as the OS keeps it for its apps (ADR 0278 §1):
/// one per device, read at most every <see cref="Lifetime"/>, and read again as soon as the server
/// connection changes. <see cref="AppNetworkGateway"/> turns it into the <see cref="KazoRelayState"/>
/// of one app, which sees nothing else of the policy.
/// </summary>
public sealed class RelayPolicyCache
{
    /// <summary>How long a policy read stays valid (ADR 0278 §1).</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    private readonly IServerConnectionService _connection;
    private readonly IProxyClient _proxy;
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Entry? _entry;
    private int _generation;

    /// <summary>Creates the cache of one device.</summary>
    /// <param name="connection">The device's server connection (base URL and token).</param>
    /// <param name="proxy">The relay client that reads <c>GET api/proxy/policy</c>.</param>
    /// <param name="now">The clock; the system clock when omitted (tests pass their own).</param>
    public RelayPolicyCache(IServerConnectionService connection, IProxyClient proxy, Func<DateTimeOffset>? now = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _proxy = proxy ?? throw new ArgumentNullException(nameof(proxy));
        _now = now ?? (static () => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Raised, on any thread, when what the cache knows may have changed: the server connection
    /// changed (<see cref="Invalidate"/>) or a new read gave another policy. A gateway with
    /// subscribers computes its app's state again and raises its own event only when it changed.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Gets the policy of the connected server, or <see langword="null"/> when no server is
    /// connected, the OS holds no access token, or the server could not be reached.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async ValueTask<ProxyPolicyResponse?> GetAsync(CancellationToken cancellationToken = default)
    {
        ServerConnectionState? state = await _connection.GetStateAsync(cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool changed = false;
        ProxyPolicyResponse? policy;
        try
        {
            int generation = Volatile.Read(ref _generation);
            if (_entry is { } cached && cached.Generation == generation && cached.Server == state.ServerBaseUrl
                && _now() - cached.ReadAt < Lifetime)
            {
                return cached.Policy;
            }

            policy = await ReadAsync(state.ServerBaseUrl, cancellationToken).ConfigureAwait(false);
            changed = _entry is { } previous && Signature(previous.Policy) != Signature(policy);
            _entry = new Entry(state.ServerBaseUrl, policy, _now(), generation);
        }
        finally
        {
            _gate.Release();
        }

        if (changed)
        {
            Changed?.Invoke();
        }

        return policy;
    }

    /// <summary>
    /// Forgets the policy because the server connection changed (connected, disconnected, grants
    /// synchronized): the next read asks the server again, and <see cref="Changed"/> is raised.
    /// </summary>
    public void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        Changed?.Invoke();
    }

    private async ValueTask<ProxyPolicyResponse?> ReadAsync(string serverBaseUrl, CancellationToken cancellationToken)
    {
        try
        {
            string? accessToken = await _connection.EnsureAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            return accessToken is null
                ? null
                : await _proxy.GetPolicyAsync(new Uri(serverBaseUrl), accessToken, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ServerConnectionException or HttpRequestException or NotSupportedException)
        {
            // A server that does not answer relays nothing: for the app it is "no server".
            return null;
        }
    }

    /// <summary>The part of a policy an app's state depends on, to tell a real change from a re-read.</summary>
    private static string Signature(ProxyPolicyResponse? policy) =>
        policy is null
            ? "none"
            : string.Join(
                "|",
                policy.Enabled,
                string.Join(",", policy.GrantedApps ?? []),
                policy.AccountGrantedApps is null ? "?" : string.Join(",", policy.AccountGrantedApps),
                string.Join(",", policy.TcpBlockedPorts ?? []),
                string.Join(",", policy.UdpBlockedPorts ?? []));

    private sealed record Entry(string Server, ProxyPolicyResponse? Policy, DateTimeOffset ReadAt, int Generation);
}
