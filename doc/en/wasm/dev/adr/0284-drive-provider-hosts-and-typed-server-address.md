> [Version française](../../../../fr/wasm/dev/adr/0284-drive-provider-hosts-and-typed-server-address.md)

# ADR 0284: hosts of a drive provider, allowed by `drives.provide`, and a server address the user types

## Status

**Accepted** on 2026-11-17: Benoit answered "1. A 2. A" to the two questions
of work item 7 (recorded at the end). Series `drive-providers/base`, work item
`drive-providers/7-developer-kit`.

- **Completes:** [ADR 0282](0282-third-party-drive-providers.md) §4 (direct
  HTTP, declared hosts). It does not change that decision: it says how the
  sentence "for a drive provider, the list is the one of its declaration"
  applies, and adds the case of a service whose address is only known when
  the user connects.
- **Builds on:** [ADR 0049](0049-dangerous-capabilities-and-admin-approval.md)
  (approval of dangerous capabilities) and [ADR 0233](0233-server-issued-real-network-grants.md) §4
  (an update that widens the declaration asks for approval again).

## Context

Work item 1 shipped `KazoDirectHttpClient`: a direct request requires
`network.direct.access` and a host of `directNetworkHosts`. Two gaps showed up
while writing the WebDAV sample provider (work item 7):

1. The `hosts` of `driveProviders` (ADR 0282 §1 and §4) were validated in the
   manifest but **never read** by the network gateway. A Feature that provides
   a drive also had to declare `network.direct.access` and copy its hosts into
   `directNetworkHosts`, with two approval lines for the same thing.
2. A WebDAV server (Nextcloud, ownCloud, a NAS) has **no host known in
   advance**: each user types their own. No declaration can name it, and a
   bare `*` is refused (ADR 0282 §4).

## Decision

### 1. `drives.provide` is enough for the declared hosts

- A Feature that holds `drives.provide` may send a direct request to any host
  in the `hosts` list of one of its `driveProviders` declarations, **without**
  `network.direct.access`. The two capabilities can still be held together:
  `directNetworkHosts` then serves the rest of the Feature's code.
- The approval screen of `drives.provide` (secure-desktop window and the
  terminal's text mode) names those hosts, as the one of
  `network.direct.access` names its own.
- The list the machine approved (section `[direct-network-hosts]`) keeps the
  hosts of both capabilities. An update that adds a host to a provider puts
  `drives.provide` back to pending, as for `network.direct.access`
  (ADR 0233 §4).
- The gateway decides in this order: a host of `directNetworkHosts` →
  `network.direct.access` required; otherwise a provider host or a typed
  server (§2) → `drives.provide` required; otherwise refused.

### 2. A "server address" field in the form

- A form field may carry `serverAddress: true`
  (`AppDriveFormFieldManifest.ServerAddress`). Validator rules: kind `url`
  only, never secret, required, **at most one** per provider, and only in a
  `form` connection.
- When a drive of that type opens, the OS reads that field's value, keeps its
  host when valid (absolute `https` address, no user name or password in the
  URL, a host the rules of `directNetworkHosts` accept: no IP address, no
  `localhost`, a name with a dot), and allows that host **to the Feature that
  provides the type**, and to it alone, **while the drive is open** (from
  mount to unmount). That is what "while the drive exists" means: an
  unmounted drive sends nothing.
- The permission is counted per drive: two drives on the same server keep it
  allowed until the second one closes.
- It also goes through `drives.provide`: without the capability (refused by
  the user, blocked by policy), the typed server is refused.
- The connection dialog writes the host in plain words before saving ("This
  provider will reach dav.example.org"), and the approval screen of
  `drives.provide` says that the provider reaches the server each user types
  in that field. The dialog is work item 6's work.

## Security

- The typed host is chosen by the user, not by the provider's author: the
  provider cannot open a host of its own choosing through this path, since the
  value comes from the form the OS draws and saves.
- The same provider stays bound to the hosts of its declaration for
  everything else. Other apps, and other Features, gain nothing.
- The limits of ADR 0282 still hold: there is no sandbox, and a package's
  JavaScript can call `fetch`. The Store review stays the real barrier; the
  developer guide says so.
- The server must accept CORS from the OS page. Nextcloud and most WebDAV
  servers do not by default: the guide explains how to check it and what to
  set.

## Rejected options

- **Q1 B. Hosts declared in advance only**: makes WebDAV impossible without a
  wildcard that is far too wide.
- **Q1 C. A `*` wildcard for providers only**: opens the whole Internet to the
  provider's code for its whole life.
- **Q2 B. Keep `network.direct.access` required**: two approvals and two lists
  for the same thing, and declaration `hosts` that serve no purpose.

## Consequences

- `Kazo.Os.App.Abstractions`: `AppDriveFormFieldManifest.ServerAddress`,
  `AppDriveProviders.HostsOf`, `AppDriveProviders.ServerAddressField`, the
  `manifest.schema.v1.json` schema and a fixture.
- `Kazo.Os.Platform.Core`: `DriveServerHosts` (counted registry of typed
  servers), the network gateway, the evaluator and approval screens,
  `FeatureDriveFolder` taking and returning the lease.
- WebDAV sample: its "Server address" field carries `ServerAddress`.
- Docs: `framework/drive-providers.md`, `dev/dangerous-capability-approval.md`,
  `dev/network-drive-sources.md`.

## Questions asked to Benoit

1. **How does a provider reach a server the user types (WebDAV)?**
   - A) **(Recommended, chosen)** The declaration marks a form field as
     "server address". The host the user types there is allowed to that
     provider while the drive exists, and the connection dialog writes it in
     plain words.
   - B) Only hosts declared in advance: the sample targets one service, and
     WebDAV stays impossible.
   - C) A `*` wildcard allowed to providers: simple, but the whole Internet is
     open to the provider's code.
2. **Is `drives.provide` enough for the hosts of the declaration?**
   - A) **(Recommended, chosen)** Yes: the hosts of its `driveProviders` are
     allowed to the Feature, and the approval screen of `drives.provide` names
     them.
   - B) No: the Feature also declares `network.direct.access` and copies its
     hosts into `directNetworkHosts`.
