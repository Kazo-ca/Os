> [Version française](../../../fr/wasm/dev/cloud-drive-connectors.md)

# Cloud connectors: shared base, contract suite and Dropbox

Work item `cloud-drives/3-dropbox` of the
[`cloud-drives/base`](plans/cloud-drives-plan.md) series, following
[ADR 0267](adr/0267-cloud-drives-as-network-drives.md), sections 4, 6, 8, 9
and 10. It builds on the [drive sources](network-drive-sources.md) (work
item 1) and the [cloud accounts](cloud-accounts.md) (work item 2). This
document describes the code shipped; the ADR remains the reference for the
decisions.

## Purpose

- Mount the user's Dropbox storage as a network drive under `/mnt/Dropbox`,
  the whole storage or the `Apps/Kazo OS` folder depending on the scope chosen
  when the account was connected.
- Give the next connectors (OneDrive, S3) a shared base and a **contract test
  suite** they all must pass.

## Architecture

| Piece | Project | Role |
| --- | --- | --- |
| `ICloudAccountTokens` | Platform.Core (`CloudDrives/`) | What a connector sees of the accounts: the account, an access token, its renewal after a refusal, the reconnection window. `CloudAccountService` implements it. |
| `CloudDriveConnection` | Platform.Core (`CloudDrives/`) | A drive's link to its account: adds the token, renews it once after a 401, translates network, timeout, 502/503/504 and 429; permission state. |
| `CloudDriveSource` | Platform.Core (`CloudDrives/`) | Base of the cloud sources (`INetworkDriveSource`): a drive of an account already connected, no window, reopened without network. |
| `DropboxDriveSource`, `DropboxFolder` | Platform.Core (`CloudDrives/Dropbox/`) | The Dropbox connector (API v2, `HttpClient`). |
| `DropboxApi`, `DropboxPaths` | Platform.Core (`CloudDrives/Dropbox/`) | Dropbox requests, metadata, errors and paths (internal). |
| `CloudHostFolderContractTests` | Platform.Core tests (`CloudDrives/`) | Shared contract suite, abstract. |
| `FakeCloudAccountTokens`, `FakeDropboxServer` | Platform.Core tests (`CloudDrives/`) | Fake accounts; fake Dropbox behind an `HttpMessageHandler`. |

The Dropbox source is registered as an `INetworkDriveSource` in
`EcosystemServiceCollectionExtensions`; `NetworkDriveService` receives it with
the other sources.

## Shared base

### Accounts and tokens

`ICloudAccountTokens` offers `FindAccountAsync`, `GetAccessTokenAsync`,
`RenewAccessTokenAsync` and `ReconnectAsync`, plus the `Changed` event. A
connector never sees a refresh token.
`CloudAccountService.RenewAccessTokenAsync(account, rejectedToken)` renews a
token the provider refused before the expiry the OS knows; when another
request already renewed it, the recent token is returned with no new call.

### Requests

`CloudDriveConnection.SendAsync` builds the request through a function (an
HTTP request is sent only once) and:

| Answer | Result |
| --- | --- |
| 401 | Token renewal, one retry only; a second 401 gives `Unavailable`. |
| Network failure, timeout, 502, 503, 504 | `HostFolderErrorKind.Unavailable`: the drive disconnects (§9). |
| 429 | `HostFolderErrorKind.Failure`, with the delay asked in the message. |
| Other | Handed back to the connector, which reads its provider's error format. |

An account that needs a reconnection, a locked keyring or an offline renewal
give `Unavailable`; a removed account gives `NotAllowed`.

### Permission and reconnection

`QueryPermissionAsync` never calls the service, so a session sign-in is never
slowed by a cloud drive:

| Account state | Permission |
| --- | --- |
| Removed | `Denied` |
| "Needs reconnect" | `Prompt` |
| No token possible now (offline) | `Unavailable` |
| Otherwise | `Granted` |

"Reconnect" (`RequestPermissionAsync`) opens the provider's sign-in window
when the account needs it; a window the browser blocks gives
`InvalidOperationException` (user gesture required), a closed window leaves
the drive as it was.

The source raises `AvailabilityChanged` when an account changes (reconnected)
and when the page comes back: `IHostActivitySignal.Resumed`, which
`hostFolder.js` now also raises on the browser's `online` event. Disconnected
drives then reconnect by themselves (§9).

### Creating a drive

The interface connects the account first (window opened in the click), then
calls `NetworkDriveService.MountAsync("dropbox", point, new
NetworkDriveRequest(account, root))`. `CreateAsync` opens no window and refuses
(`ArgumentException`) a missing or unknown account, or one of another
provider. The reference kept in the drive list is the account's name, its id
and the root; never a token. Forgetting a drive leaves the account connected.

## Dropbox connector

| Operation | Dropbox call |
| --- | --- |
| Describe | `files/get_metadata` (the account's root has no metadata: the OS describes it) |
| List | `files/list_folder`, then `files/list_folder/continue` while `has_more` |
| Read | `files/download` |
| Create a file | `files/upload`, `add` mode |
| Create a folder | `files/create_folder_v2` |
| Write | `files/upload`, `overwrite` mode |
| Conditional write | `files/upload`, `update` mode with the expected `rev` |
| Delete | `files/delete_v2` |
| Move, rename | `files/move_v2` |
| Quota | `users/get_space_usage` (`allocation.allocated`, `used`) |

- **Revision**: the file's `rev` is `HostFolderEntry.Revision`. A write that
  expects a revision goes in `update` mode with `autorename=false` and
  `strict_conflict=true`; Dropbox's refusal becomes the OS's revision conflict.
- **Scope** (§8): Dropbox fixes the scope per app, so the connector has
  nothing to do; an "app folder" account sees `Apps/Kazo OS` as its root. A
  chosen **root** (`/Work/Docs`) prefixes every path.
- **Case**: Dropbox ignores case and keeps the case of creation; the OS does
  not ignore it. A path whose last name differs only by case is "not found"
  (the OS sees the entry under its real name), creating `a.txt` next to
  `A.txt` is "already exists", and a rename that changes only the case goes
  through a temporary name (two `move_v2`), which Dropbox refuses in one call.
- **Parents**: Dropbox creates missing parent folders; the connector checks
  the parent before a creation, as a folder of the computer does.
- **Non-recursive delete** of a non-empty folder: refused by the connector
  (`NotEmpty`), as Dropbox always deletes the content.
- **Size**: simple upload, 150 MB at most at Dropbox; the OS stops at 128 MiB
  first ([ADR 0245](adr/0245-raise-the-maximum-file-size.md)).
- **Headers**: the argument of the content endpoints goes in
  `Dropbox-API-Arg`, in ASCII (other characters are written `\uXXXX`); the
  JSON body of RPC calls has type `application/json` with no `charset`. JSON
  is written with `Utf8JsonWriter` and read with `JsonDocument`, which
  trimming keeps.
- **Errors**: Dropbox's 409 answers carry an `error_summary`
  (`path/not_found/…`) translated to `NotFound`, `TypeMismatch`,
  `AlreadyExists` (or `RevisionConflict` for a conditional write), `NoSpace`,
  `InvalidName`, `NotAllowed`; 403 gives `NotAllowed`.

## Contract suite

`CloudHostFolderContractTests` is an abstract xUnit class. A connector derives
it and provides an `ICloudDriveContractService`: its fake service (paths
`/a/b`), the expected quota, a cut network, refusing the current token (401)
and HTTP failures (`FailNext(count, status)`). It checks: listing, revisions,
missing entries and wrong kinds, whole reads (names outside ASCII), creation
(name taken, parent missing), writing, conditional writing and conflict,
deleting (non-recursive refused), moving (name taken), quota, offline then
network back, 503 and 429, renewal after 401, renewal impossible offline,
account to reconnect, blocked window, removed account. Example:
`CloudDrives/Dropbox/DropboxContractTests.cs`.

## Tests

- `DropboxContractTests`: the contract suite against `FakeDropboxServer`.
- `DropboxDriveTests`: case, case-only rename, request formats, paged
  listings, root, source (refused accounts, no window, reopening), "app
  folder" scope, drive mounted by `NetworkDriveService` (`/proc/mounts`,
  quota, stale write refused), disconnection offline and reconnection by
  itself.
- `CloudAccountServiceTests`: renewal of a token refused before its expiry.

No test calls Dropbox. A real trial with Benoit's App keys is planned before
the series is merged into `main`: it will confirm the PKCE exchange, the
access token lifetime and Dropbox's CORS headers from os.kazo.ca.

## Tasks

- [x] Shared base: `ICloudAccountTokens`, `CloudDriveConnection`, `CloudDriveSource`.
- [x] Renewal of a refused token (`RenewAccessTokenAsync`).
- [x] Dropbox connector: operations, `rev` revision, `update` mode, quota.
- [x] Dropbox case, parents, non-recursive delete.
- [x] Reconnection by itself when the network returns (`online` event).
- [x] Shared contract suite and fake Dropbox.
- [x] Source registered in the host.
- [ ] Real trial by Benoit with his two App keys (series, before `main`).
