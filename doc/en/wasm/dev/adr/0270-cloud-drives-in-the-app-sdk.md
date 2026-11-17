> [Version française](../../../../fr/wasm/dev/adr/0270-cloud-drives-in-the-app-sdk.md)

# ADR 0270: cloud accounts and cloud drives go through the public SDK, behind `cloud-accounts.manage`

## Status

Accepted on 2026-11-13. Benoit chose option A in the thread of work item
`cloud-drives/5-ui`. Delivered by that work item
([`cloud-drives-ui.md`](../cloud-drives-ui.md)).

- **Supersedes:** the sentence of [ADR 0267](0267-cloud-drives-as-network-drives.md),
  "Consequences" section, saying the app SDK does not change except for the
  format and size `KazoDriveInfo` reports. The rest of ADR 0267 is unchanged.
- **Builds on:** ADR 0055 (system apps use only the public SDK), ADR 0049
  (dangerous capabilities), ADR 0181 (network drives),
  [ADR 0269](0269-cloud-tokens-stay-on-the-device.md) (tokens on the device).

## Context

ADR 0267 §11 puts the interface in the Explorer ("Connect a network drive…"),
in Settings (cloud accounts page) and in the terminal
(`mount -t dropbox|onedrive|s3`). All three are apps: they reach the OS only
through the public SDK. That SDK could only mount a folder of the computer
(`KazoDriveInfo.MountHostFolderAsync`).

Three options were put to Benoit:

- **A** (chosen): extend the public SDK, behind a dangerous capability.
- B: a dialog drawn by the OS and a single SDK method; the Settings page would
  still have needed another door.
- C: an internal contract injected into system apps; against ADR 0055 and
  impossible in a worker.

## Decision

- **`KazoDriveInfo`** gains `GetNetworkDriveTypesAsync()` (the types the OS can
  mount here: `hostfs` when the browser has the folder dialog, the configured
  providers, `s3`) and `MountNetworkDriveAsync(KazoNetworkDriveRequest, point)`.
  `KazoDriveInfo.Account` gives a cloud drive's account.
- **`KazoNetworkDriveRequest`**: type, account, root, `Options` (non-secret
  settings, e.g. the S3 address and bucket) and `Secrets` (S3 keys). Secrets
  cross once, from the app to the OS, which seals them (ADR 0269); `ToString`
  never prints them.
- **`KazoCloudAccounts`** (namespace `Kazo.Os.AppSdk.CloudAccounts`):
  `CanConnectAsync`, `PrepareAsync`, `GetProvidersAsync`, `GetAccountsAsync`,
  `ConnectAsync(provider, scope)`, `ReconnectAsync`, `RemoveAsync`. An app only
  receives references: never a token or a key.
- **Dangerous capability `cloud-accounts.manage`**: required by
  `KazoCloudAccounts` (except `CanConnectAsync`) and by `MountNetworkDriveAsync`
  for any type but `hostfs`. A folder of the computer still needs no
  capability. Explorer, Settings and `mount` declare it.
- The new gateway interface methods have a default body and
  `IAppExecutionContext.CloudAccounts` answers "not supported" by default:
  existing apps and hosts compile unchanged.
- Both gateways also work in a worker (`mount` runs there); the sign-in window
  opens on the page, inside the user's gesture.

## Consequences

- More public API in `Kazo.Os.AppSdk` and `Kazo.Os.App.Abstractions`
  (`PublicAPI.Unshipped.txt`), with no break for the compatibility fixture.
- `NetworkDriveRequest` (Simulation.Abstractions) gains `Options` and
  `Secrets`, shared with the S3 source of work item 6.
- A third-party app may ask for `cloud-accounts.manage`; like every dangerous
  capability, the user must approve it.
