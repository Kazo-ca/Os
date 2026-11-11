> [Version française](../../../../fr/wasm/dev/adr/0255-relay-identities-and-decisions-of-a-feature.md)

# ADR 0255: server relay: per-flow identities, decisions, limits and log supplied by a Feature

## Status

Proposed on 2026-11-11, for the Os-x86 feature
([Kazo-ca/Os-x86](https://github.com/Kazo-ca/Os-x86)), lot 1 of the
`feature/base` series. The questions asked to Benoit got their answers on
2026-11-11 (series 15 of the Os-x86 design); they are recorded at the end of
this ADR and the decision applies them. Source decisions: D20 (one bottle,
"prefix", per program, with its network namespace and its address; one network
decision per prefix), 161 A (private Ethernet network of Os-x86 between VMs,
one address per VM and per prefix without NAT, real exit through the relay; no
link with Kazo's simulated network in V1), 174 A ("the decision, the limits (4
streams, 300 s, 80/443) and the log live at the relay on the Kazo side, source
of truth; nftables in the guest as defense in depth; `relay_url` set by the
Feature, never exposed to the guest; UDP 53 and IPv6 blocked by an explicit
rule") and 181 A (Os-x86 declares `network.real.access`, BoxedWine no network
permission), 2026-11-11; Os-x86 trial E6 (the per-prefix source address is a
reliable label up to the relay; two decisions enforced and logged).

- **Completes:** [ADR 0102](0102-server-network-relay-tcp-udp-and-policy.md)
  and [ADR 0108](0108-server-relay-implementation-adjustments.md) (TCP and
  UDP relay, policy, quotas), [ADR 0233](0233-server-issued-real-network-grants.md)
  (server-issued grants, `POST /api/proxy/grants`) and
  [ADR 0235](0235-real-network-for-chat-apps.md) (declared ports,
  `BlockedPorts`).
- **Does not change:** the `network.real.access` capability and its approval,
  the always-blocked addresses, DNS pinning, the server-side grant check,
  `AllowedPorts` and `BlockedPorts`.
- **For:** the Os-x86 network adapter (page side), which relays each TCP
  flow of a prefix to the server relay with the prefix's identity, and the
  Os-x86 network permission window.

## Context

- A relay flow is identified by **account + device** (from the token) and
  **`AppId`** (from the request: `ProxyStreamRequest(Host, Port, AppId)`,
  `Server/Kazo.Os.Server.Contracts/Proxy/ProxyStreamContracts.cs:90`; sent by
  `Platform/Kazo.Os.Platform.Core/ServerConnection/IProxyClient.cs:239-275`
  as `ws(s)://…/api/proxy/tcp?host&port&appId[&tls&targetHost]`). No user
  identifier nor sub-identity travels; the grant is checked **per account and
  per app** (`Services/ProxyGrantAuthority.cs:71-77, 95-117`), all OS users
  together (ADR 0028 §1: connection per device).
- The limits are **per device**: `MaxStreamsPerDevice` 4
  (`Services/ProxyOptions.cs:108, :234`, enforced `ProxyService.cs:283-289`),
  `StreamIdleTimeoutSeconds` 300 (:114, :339), `MaxResponseBytes` 10 MiB
  (:99), TCP ports `[80, 443]` (:163) plus those the app declares
  (ADR 0235 §3, `[real-network-ports]` of `app-permissions.config`,
  `Policy/AppPermissionDecisions.cs:36-41`), `BlockedPorts` (:173, :180).
- The relay log (`ProxyService.cs:328-329 PROXY_TCP_OPEN`: host, address,
  port, appId, tls; `:377-378 CLOSE`: host, port, code, bytes, **without
  appId**) knows neither the user nor the program: for Os-x86, every bottle
  of every user would be "ca.kazo.x86".
- Os-x86 gives each prefix (bottle) an address on its private Ethernet
  network (E6: the source address is a reliable label up to the relay) and
  takes **one decision per prefix** (permission window: allow or deny, once,
  until the program ends or always, any destination or this host and port,
  rules imposed by the admin, D23). Benoit chose (174 A) that the decision,
  the limits and the log live **at the relay on the Kazo side**, so that a
  compromised guest cannot bypass them, and nftables in the guest only as
  defense.
- `KazoClientWebSocket` (ADR 0235 §1) and the direct WebSocket are not
  concerned: a VM opens no WebSocket, it emits TCP.

## Decision

### 1. One opaque identity per flow, supplied by the Feature

- `ProxyStreamRequest` gains an **`Identity`** field (string, 128 characters
  at most, `[A-Za-z0-9._:-]`), which only a **Feature** with
  `network.real.access` may set (`AppNetworkGateway` refuses it to an
  ordinary app: `UnauthorizedAccessException`, "flow identity reserved to
  features"). Os-x86 puts `prefix:<guid>` there (or `vm:<guid>` for a
  complete VM = one identity, D22). The server does not interpret it: it is
  a **label** under the app.
- SDK side: `KazoTcpConnectOptions.FlowIdentity` (ADR 0235 §2 for
  `KazoTcpConnectOptions`); `RelayedSocket` passes it on; the flow request
  sends it as `identity=`.
- The **source of truth of the label** is the Feature on the page side (it
  reads the source address of the prefix's Ethernet packet, E6); the guest
  never sees `relay_url` nor the token (174 A).

### 2. Decisions and limits per identity, recorded at the relay

- `POST /api/proxy/grants` (ADR 0233 §1) accepts from a **Feature** a record
  **per identity**: `ProxyGrantIssueRequest` gains `Identity`, `Decision`
  (`Allow`, `Deny`), `Scope` (`Any`, or a `host:port` list), `Until`
  (`Session`, `ProgramEnd`, `Always`, with a deadline for the first two), and
  `Limits` (`MaxStreams`, `IdleTimeoutSeconds`, `AllowedPorts`, each **at
  most** equal to what the device policy allows; the server caps). The record
  is written under the app's grant (`RecordIdOf(accountId, appId, userId)`,
  `ProxyGrantIssuer.cs:64`, extended with `identity`); revision per identity;
  audit `PROXY_IDENTITY_GRANT` / `_REVOKE`; synchronized like the other
  grants (read-only for the OS, ADR 0031).
- A Feature may only write under **its** `AppId` (checked through the token
  and the capability as in ADR 0233): Os-x86 only decides for its prefixes.
- **Enforcement** (`ProxyService`): a flow with `identity=` is accepted when
  the app's grant exists **and** an `Allow` identity record covers
  `host:port` and is not expired; otherwise refused with the new close code
  `4003 IDENTITY_DENIED` (or `4004 IDENTITY_UNDECIDED` when no record exists:
  the signal for the Feature to open its permission window, then retry).
  Without `identity=`, nothing changes for ordinary apps.
- **Limits per identity**: the identity's `MaxStreams` and `IdleTimeout`
  apply **in addition** to the per-device limits (a bottle cannot take the
  device's 4 streams alone when its limit is 2); the identity's
  `AllowedPorts` is an intersection with the app's, minus `BlockedPorts`. The
  device policy stays the ceiling (question 1 for its default with
  identities).
- **Explicit rules** (174 A): the relay refuses UDP 53 and any IPv6 target
  for a flow with an identity (`4003`), without configuration; the Feature
  supplies `/etc/hosts` to the guest.

### 3. Log per identity

- `PROXY_TCP_OPEN`, `PROXY_TLS_FAILED` and **`PROXY_TCP_CLOSE`** (which gains
  `appId`) carry `identity`; a refusal writes `PROXY_IDENTITY_DENIED`
  (identity, host, port, reason). The server audit is the source of truth
  (174 A); the OS reads back the entries of its identities through
  `GET /api/proxy/audit?appId=&identity=` (new route, app grant required,
  7 days, 1000 entries) for the Os-x86 rules page and the user's journal
  (ADR 0254, subject = the prefix).
- The log contains **no** OS user (the server does not know them, ADR 0028);
  the Feature links identity → user on its side (the prefix is filed by user
  GUID).

### 4. What does not change

- Without a connected server, no real network for a VM (as for any app:
  Kazo's simulated network is not touched, 161 A).
- Ordinary apps (IRC, browser) set no identity and see no difference; their
  quotas stay the device's.
- The permission window stays **Os-x86's** (D23, D49); the relay shows
  nothing.

## Rejected options

- **A. Decision and log in the page-side adapter only** (174 B): fewer ADRs,
  but the log depends on the page code and a mislabeled flow would pass; the
  server could not refuse.
- **B. Decision in the guest only (nftables)** (174 C): a compromised guest
  bypasses it.
- **C. One app grant per prefix** (each bottle = a distinct `AppId`,
  `ca.kazo.x86.prefix.<guid>`): hundreds of fictitious apps in the grants,
  in Settings ▸ Permissions and in `GrantedApps`; and one administrator
  approval per bottle.
- **D. A user in the identity**: the server does not know OS users (ADR 0028
  §1); adding one would change the server's session model; the Feature makes
  the link.
- **E. Let the Feature bypass the device limits**: no, "Allow" does not lift
  the relay limits (Os-x86 design, D14); identities share the ceiling.

## Consequences

- Server contracts (`Kazo.Os.Server.Contracts/Proxy`):
  `ProxyStreamRequest.Identity`, `ProxyGrantIssueRequest.Identity/Decision/Scope/Until/Limits`,
  codes `4003`, `4004`, audit route; server: `ProxyGrantIssuer`,
  `ProxyGrantAuthority` (per identity), `ProxyService` (check, limits, UDP 53
  and IPv6 rules, log), `ProxyOptions` (`MaxStreamsPerIdentityDefault`,
  question 1).
- OS: `KazoTcpConnectOptions.FlowIdentity` (reserved to Features),
  `IProxyClient.OpenStreamAsync` (`identity=`), `RealNetworkGrantReconciler`
  (identity records issued at approval and at every sync),
  `KazoRelayAudit.ReadAsync` (SDK, Features); `PublicAPI.Unshipped.txt`,
  `sdk-compat` fixture.
- Docs: [`server-network-relay.md`](../server-network-relay.md) ("Flow
  identities" section), [`sdk-network.md`](../../framework/sdk-network.md),
  [`capabilities.md`](../../framework/capabilities.md), `features.md`,
  `changes/unreleased` entry.
- Tests: flow with an identity and no record → `4004`; `Allow` on
  `host:port` → accepted, other host → `4003`; `Deny` → `4003`; `ProgramEnd`
  expiry; per-identity limit reached before the device's; UDP 53 and IPv6
  refused; ordinary app that sets an identity → exception; Feature that
  writes under another `AppId` → refused; log and audit route.

## Questions asked to Benoit

1. **The per-device limits (4 streams, 300 s) with identities?**
   - A) **(Recommended)** The device ceiling stays 4 streams by default; each
     identity has 2 streams and 300 s by default; the server administrator
     sets both (`MaxStreamsPerDevice`, `MaxStreamsPerIdentityDefault`). Two
     active bottles therefore share the 4 streams.
   - B) The device ceiling rises to 8 when identities are involved (at least
     one stream per identity for four bottles).
   - C) No per-identity limit; only the device's (one bottle can take it all).
2. **Where does the `Session` duration of a decision live?**
   - A) **(Recommended)** At the relay, with a deadline sent by the Feature
     (end of session = sign-out, the Feature then revokes, and the 24 h
     deadline protects when it could not).
   - B) Page side only: the Feature only sends `Always` to the relay and
     keeps `Session` and `ProgramEnd` in memory (contradicts "source of truth
     at the relay" for those two durations).

Maintainer's answers (2026-11-11): 1 A, 2 A.
