using System.Net.Sockets;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Net;
using Kazo.Os.Commands.Ping;
using Kazo.Os.Platform.Core.Network;
using Kazo.Os.Platform.Core.ServerConnection;
using Kazo.Os.Server.Contracts.Proxy;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Xunit;

namespace Kazo.Os.Network.Tests;

/// <summary>
/// ADR 0278: an app holding only <c>network.real.access</c> reads whether the Kazo server's relay
/// works (<see cref="KazoRealNetwork"/>), is told when it changes, and a failed connection says why.
/// </summary>
public sealed partial class Wave4NetworkTests
{
    private static readonly string RelayAppId = AppDefinitionMapper.ToManifest(new PingApp()).Id;

    /// <summary>Every capability but <c>network.real.access</c> is denied: it alone opens the state.</summary>
    private static GrantAllCapabilityChecker OnlyRealNetworkAccess() => new(
        AppCapabilities.NetworkSimulatedRead,
        AppCapabilities.NetworkSimulatedWrite,
        AppCapabilities.ServerConnectionManage);

    /// <summary>Enters an app whose gateway uses <paramref name="cache"/> as the device's policy cache.</summary>
    private void EnterRelayApp(ICapabilityChecker capabilities, RelayPolicyCache? cache = null)
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest(new PingApp());
        KazoExecutionContext.Enter(new StubAppExecutionContext(
            manifest,
            new AppNetworkGateway(_network, capabilities, manifest.Id, _serverConnection, _proxy) { RelayPolicy = cache }));
    }

    private static ProxyPolicyResponse Policy(bool enabled = true, IReadOnlyList<string>? accountGrantedApps = null) =>
        new(Guid.NewGuid(), 4, 0, 1024, 30, 10, ["http", "https", "tcp", "udp"], 30, [], Enabled: enabled, GrantedApps: ["org.kazoos.system.wget"])
        {
            TcpBlockedPorts = [25, 23],
            UdpBlockedPorts = [53],
            AccountGrantedApps = accountGrantedApps
        };

    [Fact]
    public async Task An_app_with_only_network_real_access_reads_no_server_without_a_server()
    {
        EnterRelayApp(OnlyRealNetworkAccess());

        KazoRelayState state = await KazoRealNetwork.GetRelayStateAsync();

        Assert.Equal(KazoRelayStatus.NoServer, state.Status);
        Assert.Empty(state.TcpBlockedPorts);
    }

    [Fact]
    public async Task An_app_reads_relay_disabled_when_the_operator_turned_the_relay_off()
    {
        UseServer(new ConnectedServerConnectionService(), new PolicyProxyClient(Policy(enabled: false, accountGrantedApps: [RelayAppId])));
        EnterRelayApp(OnlyRealNetworkAccess());

        Assert.Equal(KazoRelayStatus.RelayDisabled, (await KazoRealNetwork.GetRelayStateAsync()).Status);
    }

    [Fact]
    public async Task An_app_reads_grant_pending_when_the_server_has_not_received_its_grant()
    {
        UseServer(new ConnectedServerConnectionService(), new PolicyProxyClient(Policy(accountGrantedApps: ["org.other.app"])));
        EnterRelayApp(OnlyRealNetworkAccess());

        KazoRelayState state = await KazoRealNetwork.GetRelayStateAsync();

        Assert.Equal(KazoRelayStatus.GrantPending, state.Status);
        // The app sees nothing else of the policy.
        Assert.Empty(state.TcpBlockedPorts);
    }

    [Fact]
    public async Task An_app_reads_ready_with_the_blocked_ports_once_its_grant_reached_the_server()
    {
        UseServer(new ConnectedServerConnectionService(), new PolicyProxyClient(Policy(accountGrantedApps: [RelayAppId])));
        EnterRelayApp(OnlyRealNetworkAccess());

        KazoRelayState state = await KazoRealNetwork.GetRelayStateAsync();

        Assert.Equal(KazoRelayStatus.Ready, state.Status);
        Assert.Equal([23, 25], state.TcpBlockedPorts);
        Assert.Equal([53], state.UdpBlockedPorts);
    }

    [Fact]
    public async Task An_older_server_that_does_not_list_the_account_grants_counts_as_ready()
    {
        UseServer(new ConnectedServerConnectionService(), new PolicyProxyClient(Policy(accountGrantedApps: null)));
        EnterRelayApp(OnlyRealNetworkAccess());

        Assert.Equal(KazoRelayStatus.Ready, (await KazoRealNetwork.GetRelayStateAsync()).Status);
    }

    [Fact]
    public async Task An_app_the_operator_grants_itself_is_ready_without_an_account_grant()
    {
        ProxyPolicyResponse policy = Policy(accountGrantedApps: []) with { GrantedApps = [RelayAppId] };
        UseServer(new ConnectedServerConnectionService(), new PolicyProxyClient(policy));
        EnterRelayApp(OnlyRealNetworkAccess());

        Assert.Equal(KazoRelayStatus.Ready, (await KazoRealNetwork.GetRelayStateAsync()).Status);
    }

    [Fact]
    public async Task Reading_the_relay_state_or_following_it_needs_network_real_access()
    {
        UseServer(new ConnectedServerConnectionService(), new PolicyProxyClient(Policy(accountGrantedApps: [RelayAppId])));
        EnterRelayApp(new GrantAllCapabilityChecker(AppCapabilities.NetworkRealAccess));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => KazoRealNetwork.GetRelayStateAsync().AsTask());
        Assert.Throws<UnauthorizedAccessException>(() => KazoRealNetwork.RelayStateChanged += (_, _) => { });
    }

    [Fact]
    public async Task The_policy_is_read_once_for_30_seconds_and_again_when_the_connection_changes()
    {
        PolicyProxyClient proxy = new(Policy(accountGrantedApps: [RelayAppId]));
        DateTimeOffset now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
        ConnectedServerConnectionService connection = new();
        RelayPolicyCache cache = new(connection, proxy, () => now);
        UseServer(connection, proxy);
        EnterRelayApp(OnlyRealNetworkAccess(), cache);

        await KazoRealNetwork.GetRelayStateAsync();
        await KazoRealNetwork.GetRelayStateAsync();
        Assert.Equal(1, proxy.PolicyReads);

        now += RelayPolicyCache.Lifetime;
        await KazoRealNetwork.GetRelayStateAsync();
        Assert.Equal(2, proxy.PolicyReads);

        cache.Invalidate();
        await KazoRealNetwork.GetRelayStateAsync();
        Assert.Equal(3, proxy.PolicyReads);
    }

    [Fact]
    public async Task RelayStateChanged_fires_when_the_grant_reaches_the_server_and_not_on_a_read_that_changes_nothing()
    {
        PolicyProxyClient proxy = new(Policy(accountGrantedApps: []));
        ConnectedServerConnectionService connection = new();
        RelayPolicyCache cache = new(connection, proxy);
        UseServer(connection, proxy);
        EnterRelayApp(OnlyRealNetworkAccess(), cache);
        Assert.Equal(KazoRelayStatus.GrantPending, (await KazoRealNetwork.GetRelayStateAsync()).Status);
        List<KazoRelayStatus> seen = [];
        TaskCompletionSource<KazoRelayState> changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        KazoRealNetwork.RelayStateChanged += (_, state) =>
        {
            lock (seen)
            {
                seen.Add(state.Status);
            }

            changed.TrySetResult(state);
        };

        cache.Invalidate();
        await KazoRealNetwork.GetRelayStateAsync();
        proxy.Policy = Policy(accountGrantedApps: [RelayAppId]);
        cache.Invalidate();

        KazoRelayState state = await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(KazoRelayStatus.Ready, state.Status);
        await KazoRealNetwork.GetRelayStateAsync();
        lock (seen)
        {
            Assert.Equal([KazoRelayStatus.Ready], seen);
        }
    }

    [Fact]
    public async Task KazoHttp_without_a_server_says_no_server_and_keeps_its_name_resolution_error()
    {
        EnterRelayApp(new GrantAllCapabilityChecker());
        using KazoHttpClient client = new();

        KazoHttpRequestException failure = await Assert.ThrowsAsync<KazoHttpRequestException>(
            () => client.GetAsync("https://real-external-site.example/"));

        Assert.Equal(KazoNetworkError.RelayNoServer, failure.NetworkError);
        Assert.Equal("ERR_KAZO_RELAY_NO_SERVER", failure.ErrorCode);
        Assert.Equal(System.Net.Http.HttpRequestError.NameResolutionError, failure.HttpRequestError);
    }

    [Fact]
    public async Task KazoUdpClient_without_a_server_throws_host_not_found_and_says_no_server()
    {
        EnterRelayApp(new GrantAllCapabilityChecker());

        SocketException failure = await Assert.ThrowsAnyAsync<SocketException>(
            () => KazoUdpClient.ConnectAsync("ntp.real-external-site.example", 123));

        Assert.Equal(SocketError.HostNotFound, failure.SocketErrorCode);
        Assert.Equal(KazoNetworkError.RelayNoServer, Assert.IsType<KazoRelayException>(failure).Reason);
    }

    [Fact]
    public async Task A_simulated_name_of_the_internal_domain_stays_host_not_found()
    {
        EnterRelayApp(new GrantAllCapabilityChecker());

        SocketException failure = await Assert.ThrowsAnyAsync<SocketException>(
            () => KazoTcpClient.ConnectAsync("nowhere.hack", 443));

        Assert.Equal(SocketError.HostNotFound, failure.SocketErrorCode);
        Assert.IsNotType<KazoRelayException>(failure);
    }

    /// <summary>A relay client that only answers <c>GET api/proxy/policy</c>, counting the reads.</summary>
    private sealed class PolicyProxyClient(ProxyPolicyResponse policy) : IProxyClient
    {
        private int _reads;

        public ProxyPolicyResponse Policy { get; set; } = policy;

        public int PolicyReads => Volatile.Read(ref _reads);

        public Task<ProxyHttpResponse> ExecuteHttpRequestAsync(
            Uri serverBaseUrl, string accessToken, ProxyHttpRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<ProxyTcpProbeResponse> ExecuteTcpProbeAsync(
            Uri serverBaseUrl, string accessToken, ProxyTcpProbeRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<ProxyPolicyResponse> GetPolicyAsync(
            Uri serverBaseUrl, string accessToken, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _reads);
            return Task.FromResult(Policy);
        }
    }
}
