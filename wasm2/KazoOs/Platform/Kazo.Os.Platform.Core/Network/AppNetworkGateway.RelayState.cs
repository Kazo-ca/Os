using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Net;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Server.Contracts.Proxy;
using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.Platform.Core.Network;

/// <summary>
/// The relay's state as one app sees it (ADR 0278 §1): computed from what the OS already knows
/// (server connection, access token, the server's relay policy kept by <see cref="RelayPolicyCache"/>),
/// with <c>network.real.access</c> as the only capability, and an event when the status changes.
/// </summary>
public sealed partial class AppNetworkGateway
{
    private readonly Lock _relayGate = new();
    private readonly AppAmbientContextBinding _ambient = new();
    private EventHandler<KazoRelayState>? _relayStateChanged;
    private KazoRelayState? _lastRelayState;
    private RelayPolicyCache? _ownRelayPolicy;
    private RelayPolicyCache? _subscribedPolicy;
    private CancellationTokenRegistration _lifetimeRegistration;

    /// <summary>
    /// Gets the device's relay policy cache, shared by every app (ADR 0278 §1: one policy request at
    /// most every 30 seconds per device). When <see langword="null"/>, the gateway keeps its own.
    /// </summary>
    public RelayPolicyCache? RelayPolicy { get; init; }

    /// <summary>
    /// Gets the lifetime of the app's process: when it ends, the gateway stops following the
    /// relay's state, so a closed app is not kept alive by the device's cache.
    /// </summary>
    public CancellationToken Lifetime { get; init; }

    private RelayPolicyCache? Policies =>
        RelayPolicy ?? (_serverConnection is null || _proxy is null
            ? null
            : LazyInitializer.EnsureInitialized(ref _ownRelayPolicy, () => new RelayPolicyCache(_serverConnection, _proxy)));

    /// <inheritdoc />
    public async ValueTask<KazoRelayState> GetRelayStateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _capabilities.Require(AppCapabilities.NetworkRealAccess);
        KazoRelayState state = await ComputeRelayStateAsync(cancellationToken).ConfigureAwait(false);
        Remember(state);
        return state;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Adding a handler needs <c>network.real.access</c>. The handlers run inside the app's
    /// <see cref="Kazo.Os.AppSdk.KazoExecutionContext"/> (ADR 0043), on any thread.
    /// </remarks>
    public event EventHandler<KazoRelayState>? RelayStateChanged
    {
        add
        {
            if (value is null)
            {
                return;
            }

            _capabilities.Require(AppCapabilities.NetworkRealAccess);
            RelayPolicyCache? policies = Policies;
            bool subscribe;
            lock (_relayGate)
            {
                _relayStateChanged += value;
                subscribe = _subscribedPolicy is null && policies is not null && !Lifetime.IsCancellationRequested;
                if (subscribe)
                {
                    _subscribedPolicy = policies;
                }
            }

            if (subscribe)
            {
                policies!.Changed += OnRelayPolicyChanged;
                _lifetimeRegistration = Lifetime.Register(StopFollowingRelay);
                // The first state read is the reference the next ones are compared with.
                OnRelayPolicyChanged();
            }
        }

        remove
        {
            bool last;
            lock (_relayGate)
            {
                _relayStateChanged -= value;
                last = _relayStateChanged is null;
            }

            if (last)
            {
                StopFollowingRelay();
            }
        }
    }

    /// <summary>Binds the app context the event's handlers run under; called once by the factory.</summary>
    internal void BindAmbientContext(Kazo.Os.AppSdk.IAppExecutionContext context) => _ambient.Bind(context);

    private void StopFollowingRelay()
    {
        RelayPolicyCache? policies;
        lock (_relayGate)
        {
            policies = _subscribedPolicy;
            _subscribedPolicy = null;
            if (Lifetime.IsCancellationRequested)
            {
                _relayStateChanged = null;
            }
        }

        if (policies is not null)
        {
            policies.Changed -= OnRelayPolicyChanged;
        }

        _lifetimeRegistration.Dispose();
    }

    private void OnRelayPolicyChanged() => _ = RefreshRelayStateAsync();

    private async Task RefreshRelayStateAsync()
    {
        try
        {
            if (!_capabilities.Evaluate(AppCapabilities.NetworkRealAccess).Granted)
            {
                return;
            }

            Remember(await ComputeRelayStateAsync(CancellationToken.None).ConfigureAwait(false));
        }
        catch (AppGatewayAccessDeniedException)
        {
            // The grant was withdrawn meanwhile: the app no longer follows the relay.
        }
        catch (OperationCanceledException)
        {
            // The server connection was cancelled under us: the next change tries again.
        }
    }

    /// <summary>
    /// Records the latest state and raises <see cref="RelayStateChanged"/> when it differs from the
    /// previous one (never on the first read, nor on a read that changes nothing).
    /// </summary>
    private void Remember(KazoRelayState state)
    {
        EventHandler<KazoRelayState>? handlers;
        bool changed;
        lock (_relayGate)
        {
            changed = _lastRelayState is not null && !SameState(_lastRelayState, state);
            _lastRelayState = state;
            handlers = _relayStateChanged;
        }

        if (changed && handlers is not null)
        {
            _ambient.Wrap<KazoRelayState>(value => handlers(this, value))(state);
        }
    }

    private static bool SameState(KazoRelayState left, KazoRelayState right) =>
        left.Status == right.Status
        && left.TcpBlockedPorts.SequenceEqual(right.TcpBlockedPorts)
        && left.UdpBlockedPorts.SequenceEqual(right.UdpBlockedPorts);

    /// <summary>
    /// The state of the relay for this app (ADR 0278 §1): no server (or no token, or a server that
    /// does not answer), relay turned off, grant not received, or ready with the blocked ports.
    /// </summary>
    private async ValueTask<KazoRelayState> ComputeRelayStateAsync(CancellationToken cancellationToken)
    {
        if (Policies is not { } policies
            || await policies.GetAsync(cancellationToken).ConfigureAwait(false) is not { } policy)
        {
            return new KazoRelayState(KazoRelayStatus.NoServer);
        }

        return RelayStateOf(policy, _appId);
    }

    /// <summary>
    /// Reads one app's state in a server's relay policy. A server older than ADR 0278 does not list
    /// the account's granted apps: the grant is then assumed, and a refused connection still says
    /// <see cref="KazoNetworkError.RelayGrantPending"/>.
    /// </summary>
    /// <param name="policy">The server's policy.</param>
    /// <param name="appId">The app.</param>
    private static KazoRelayState RelayStateOf(ProxyPolicyResponse policy, string appId)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.Enabled)
        {
            return new KazoRelayState(KazoRelayStatus.RelayDisabled);
        }

        bool granted = policy.AccountGrantedApps is not { } grantedApps
            || grantedApps.Contains(appId, StringComparer.Ordinal)
            || (policy.GrantedApps?.Contains(appId, StringComparer.Ordinal) ?? false);
        if (!granted)
        {
            return new KazoRelayState(KazoRelayStatus.GrantPending);
        }

        return new KazoRelayState(KazoRelayStatus.Ready)
        {
            TcpBlockedPorts = [.. (policy.TcpBlockedPorts ?? []).Order()],
            UdpBlockedPorts = [.. (policy.UdpBlockedPorts ?? []).Order()]
        };
    }

    /// <summary>
    /// What a call to the real network answers when the OS has no route to it: "no server" for an
    /// app that holds <c>network.real.access</c> (ADR 0278 §2), "host not found" otherwise, as before.
    /// </summary>
    private KazoNetworkError NoRealRouteError() =>
        _capabilities.Evaluate(AppCapabilities.NetworkRealAccess).Granted
            ? KazoNetworkError.RelayNoServer
            : KazoNetworkError.HostNotFound;
}
