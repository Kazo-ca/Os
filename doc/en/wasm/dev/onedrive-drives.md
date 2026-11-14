> [Version française](../../../fr/wasm/dev/onedrive-drives.md)

# OneDrive drives

Work item `cloud-drives/4-onedrive` of the
[`cloud-drives/base`](plans/cloud-drives-plan.md) series,
[ADR 0267](adr/0267-cloud-drives-as-network-drives.md) sections 4, 6, 8, 9 and 10.
Builds on the [drive sources](network-drive-sources.md) (work item 1) and the
[cloud accounts](cloud-accounts.md) (work item 2).

## Purpose

The user mounts the OneDrive of a Microsoft account they connected, at
`/mnt/OneDrive`. Everything goes through Microsoft Graph from the browser, with
no server and no secret. Depending on the scope chosen at sign-in, the drive
starts at the top of the OneDrive (`Files.ReadWrite`) or at the folder reserved
for Kazo OS (`Files.ReadWrite.AppFolder`, `Apps/Kazo OS`), and optionally below
a chosen folder.

## Architecture

| Piece | Project | Role |
| --- | --- | --- |
| `OneDriveDriveSource` | Platform.Core (`CloudDrives/OneDrive/`) | The `onedrive` source, on the shared `CloudDriveSource`: no window at creation, no network at reopening, `AvailabilityChanged` when an account changes or the network returns. |
| `OneDriveFolder` | same | The drive's `IHostFolder`, with `IHostFolderQuota` and `IConditionalHostFolder`. |
| `OneDriveGraphClient` (internal) | same | Graph requests through `CloudDriveConnection` (token, renewal after a 401, network and 5xx as `Unavailable`); pre-authenticated addresses through `HttpClient` alone, **without** the token; Graph's refusals as the OS's errors. |
| `CloudDriveConnection`, `CloudDriveSource`, `ICloudAccountTokens` | Platform.Core (`CloudDrives/`) | Shared base of the connectors, written in work item 3 (Dropbox). |
| `HostFolderErrorKind.FileTooLarge` | Simulation.Abstractions | A file over 128 MiB (ADR 0245, ADR 0267 §10): `EFBIG`. |

The Ecosystem host registers the source:
`services.AddScoped<INetworkDriveSource>(… new OneDriveDriveSource(CloudAccountService, ICloudProviderCatalog, new HttpClient(), IHostActivitySignal))`.

## Mapping to Graph

| Operation | Graph request |
| --- | --- |
| Item by path | `GET me/drive/root:/a/b:` or `me/drive/special/approot:/a/b:`; the top: `me/drive/root` or `me/drive/special/approot` |
| List | `GET …/children`, pages followed through `@odata.nextLink` (at most 1,000 pages) |
| Read | `GET` of the item, then `GET @microsoft.graph.downloadUrl` **without** the token. Never `/content`: its redirect breaks CORS. |
| Create a file | Check that the parent is a folder, then an empty `PUT …:/content?@microsoft.graph.conflictBehavior=fail` |
| Create a folder | `POST …/children` `{ name, folder: {}, @microsoft.graph.conflictBehavior: "fail" }` |
| Write, up to 4 MiB | `PUT …:/content`, with `If-Match: <eTag>` for a conditional write |
| Write, above | `POST …:/createUploadSession` (with `If-Match`), then `PUT` of 10 MiB pieces (multiples of 320 KiB) to `uploadUrl` **without** the token; an interrupted session is deleted |
| Delete | `DELETE` of the item (OneDrive's recycle bin); non-recursive: refused when `folder.childCount > 0` |
| Move, rename | `PATCH` `{ name, parentReference: { id } }` in one request |
| Quota | `GET me/drive?$select=quota`: `total`, `remaining`; none when the access does not allow it |

| Graph answer | OS error |
| --- | --- |
| 404 | `NotFound` |
| 409 | `AlreadyExists` |
| 412 | `RevisionConflict` |
| 403 `quotaLimitReached`, 507 | `NoSpace` |
| 413, size > 128 MiB | `FileTooLarge` |
| 401 after renewal, 403 | `Unavailable` (token), `NotAllowed` |
| 400 on a name | `InvalidName` |
| 429 | `Failure` ("try again later") |
| Network, timeout, 5xx | `Unavailable`: the drive disconnects and comes back by itself |

## Behaviour

- **Revision**: Graph's `eTag`; the conditional write sends it as `If-Match`,
  and a file changed elsewhere gives `RevisionConflict`.
- **Case**: OneDrive finds a name whatever its case, Kazo OS does not. An item
  whose name differs from the one asked is "not found"; creating `Report.TXT`
  next to `report.txt` gives `AlreadyExists`.
- **Refused names**, without a request: `" * : < > ? / \ |`, a leading or
  trailing space, a trailing dot. Graph's other refusals (`CON`, `.lock`…) give
  `InvalidName`.
- **OneNote**: a notebook (`package`) is neither a file nor a folder; it is not
  listed.
- **After 24 hours**: a single-page app's refresh token expires. The account
  becomes `NeedsReconnect`, the drive disconnects and answers `Prompt`;
  "Reconnect" opens Microsoft's window (`CloudAccountService.ReconnectAsync`),
  then the drive comes back with no other gesture.
- **Chosen root**: `Work/2026` (separators `/` or `\`, no `.` nor `..`). It is
  not checked at mount time; a missing folder gives `NotFound` to the
  operations.
- **Deletion**: OneDrive keeps the item in its recycle bin. Kazo OS still asks
  for the confirmation of ADR 0181.

## Tests

- `Tests/Kazo.Os.Platform.Core.Tests/CloudDrives/OneDrive/OneDriveContractTests.cs`:
  the shared contract suite (`CloudHostFolderContractTests`, 16 tests) on
  `FakeGraphServer`.
- `OneDriveFolderTests.cs`: pages, `eTag`, download without the token, app
  folder and root with escaped names, case, refused names, conditional write,
  upload session in pieces without the token, a file over 128 MiB, deletion,
  move, quota, Graph's refusals.
- `OneDriveDriveSourceTests.cs`: availability from the app id, creation with no
  window, reopening with no network, mounting through `NetworkDriveService`
  (read, write, quota, `/proc/mounts`), a drive disconnected after 24 hours then
  reconnected.
- `FakeGraphServer`: an in-memory fake Graph. No test calls Microsoft.

## Still to do

- A real trial by Benoit, with the Microsoft Entra registration ("Single-page
  application", redirect URIs from [`cloud-accounts.md`](cloud-accounts.md)):
  sign-in, both scopes, a large upload, reconnecting after 24 hours. Not done:
  the registration does not exist yet.
- The UI (work item 5).

## Task list

- [x] `onedrive` source on the shared base, two scopes, chosen root.
- [x] Graph folder: list, read through `downloadUrl`, create, simple and
  session writes, conditional write, delete, move, quota.
- [x] Graph errors to the OS's errors, `FileTooLarge`.
- [x] Registration in the Ecosystem host.
- [x] Contract suite, folder and source tests, fake Graph.
- [ ] Real trial with a Microsoft account.
