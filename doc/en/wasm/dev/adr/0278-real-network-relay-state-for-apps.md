> [Version française](../../../../fr/wasm/dev/adr/0278-real-network-relay-state-for-apps.md)

# ADR 0278: a network app knows whether the Kazo server's relay works

## Status

Accepted on 2026-11-16: Benoit chose Q1 = C (both).
Series `todo-wins/base`, for issue
[#418](https://github.com/Kazo-ca/Os/issues/418) (`TODO`).

- **Completes:** [ADR 0102](0102-server-network-relay-tcp-udp-and-policy.md)
  (TCP/UDP relay and its policy), [ADR 0233](0233-server-issued-real-network-grants.md)
  (real-network grants issued by the server) and [ADR 0235](0235-real-network-for-chat-apps.md)
  (real network for chat apps, blocked ports).
- **Does not change:** the dangerous capability `server.connection.manage` and
  `KazoServerConnection` stay reserved to administration tools.

## Context

- An app holding `network.real.access` goes through the optional Kazo server's
  relay to reach a real host (`KazoTcpClient`, `KazoUdpClient`, `KazoHttp` to a
  name the simulated network does not know).
- It cannot know **before trying** whether that relay works:
  `KazoServerConnection.GetStateAsync` requires `server.connection.manage`,
  which also lets it connect or disconnect the server. That is too much for a
  chat app.
- When it tries, four different causes give the same error
  `KazoNetworkError.HostNotFound` (`SocketError.HostNotFound`) in
  `AppNetworkGateway`:
  - no server connected (or no access token any more);
  - relay turned off by the operator (`RELAY_DISABLED`);
  - the app's grant not yet received by the server (`GRANT_MISSING`, ADR 0233);
  - a simulated domain name refused by the relay.

  A blocked port (`BLOCKED_PORT`) gives `ConnectionRefused`, like a real closed
  port.
- The IRC app therefore marks its networks "Kazo server required" and can only
  guess the real reason of a failure.

## Question asked to Benoit

**Q1. How does a network app know whether the relay works?**

- A. Read the state (`GetRelayStateAsync`) and get an event when it changes.
- B. Only distinct errors: the app still has to try, but the failure gives the
  real reason.
- **C. Both (recommended).** The app hides or shows the "through the Kazo
  server" option without trying, and a failure during the connection still
  gives the real reason (the state may change between the read and the call).

## Decision (C)

### 1. Read the relay's state

`Kazo.Os.AppSdk.Net.KazoRealNetwork`, a new static class of the SDK:

```csharp
public static ValueTask<KazoRelayState> GetRelayStateAsync(CancellationToken cancellationToken = default);
public static event EventHandler<KazoRelayState>? RelayStateChanged;

public sealed record KazoRelayState(KazoRelayStatus Status)
{
    public IReadOnlyList<int> TcpBlockedPorts { get; init; } = [];
    public IReadOnlyList<int> UdpBlockedPorts { get; init; } = [];
}

public enum KazoRelayStatus { NoServer, RelayDisabled, GrantPending, Ready }
```

- **Capability:** `network.real.access` alone. Without it, the call throws
  `UnauthorizedAccessException`, like the other real-network calls.
- **Computed** in `AppNetworkGateway` from what the OS already knows:
  - `NoServer`: no server connected, or no access token;
  - `RelayDisabled`: the relay policy (`KazoServerRelayPolicy.Enabled`) is
    false;
  - `GrantPending`: the app is not in the policy's `GrantedApps`;
  - `Ready`: otherwise. The blocked ports come from `TcpBlockedPorts` and
    `UdpBlockedPorts`.
- The policy is kept 30 seconds and read again when the server connection's
  state changes. The app sees **nothing else** of the policy (not the other
  granted apps, not the quotas, not the server's address).
- **Event:** `RelayStateChanged` fires when the status changes (server
  connected or disconnected, policy read again, grant synchronized), not on
  every read.
- An app in a Web Worker goes through its gateway (`RemoteNetworkGateway`), as
  for the other network calls.

### 2. Give the real reason of a failure

- `KazoNetworkError` gains three values: `RelayNoServer`, `RelayDisabled`,
  `RelayGrantPending`. A port the operator blocks gives
  `BlockedByAdministrator`, which already exists (ADR 0210 §6).
- To break no app, `KazoTcpClient` and `KazoUdpClient` still throw a
  `SocketException` with the same `SocketError` as before (`HostNotFound`, or
  `ConnectionRefused` for a blocked port). It is now a subclass
  `KazoRelayException : SocketException`, whose `Reason` gives the new value.
  Code catching `SocketException` keeps working.
- `KazoHttp` keeps its result and puts the new value in its error.
- A simulated domain refused by the relay stays `HostNotFound`: the name really
  does not exist on the Internet.

### 3. What does not change

- No new capability; `server.connection.manage` keeps everything it allows.
- The relay itself and its codes (`ProxyErrorCode`) do not change.

## Consequences

- The IRC app can hide the "through the Kazo server" option without a server,
  show it when one arrives, and say "the server has not received Kazo IRC's
  permission yet" instead of "host not found".
- Addition to the SDK's public API (`PublicAPI.Unshipped.txt`). An app using it
  asks for the OS version that brings it (`sdkCompatibility`).
- Cost: one policy request at most every 30 seconds per device, and only when
  an app reads the state.

## Work items

1. `todo-wins/10-relay-state`: `KazoRealNetwork`, the state in
   `AppNetworkGateway` and `RemoteNetworkGateway`, the event, the distinct
   errors. Tests: an app holding only `network.real.access` reads the state
   with no server, with a server without a grant, with a grant, relay turned
   off; a connection failure gives the right `Reason`. Docs
   `framework/sdk-network.md` and `dev/server-network-relay.md`.
2. In Kazo-ca/Os-App-IRC, after an OS version that holds the API: the "through
   the Kazo server" option follows the state, precise failure messages.
