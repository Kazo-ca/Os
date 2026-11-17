> [Version française](../../../fr/wasm/dev/network-drive-sources.md)

# Network drive sources

Work item `cloud-drives/1-drive-sources` of the
[`cloud-drives/base`](plans/cloud-drives-plan.md) series,
[ADR 0267](adr/0267-cloud-drives-as-network-drives.md) sections 3, 4 and 9.
Builds on the network drives of
[ADR 0181](adr/0181-host-folder-mounted-as-a-network-drive.md).

## Purpose

`NetworkDriveService` could only mount a folder of the computer chosen in the
browser's dialog. It now mounts what a **drive source** gives it, one per type:
`hostfs` (the folder of the computer) and, later, `dropbox`, `onedrive`, `s3`.
The rules of ADR 0181 stay the same for every type: mount point a direct child
of `/mnt`, modes `0700` and `0600`, `chmod` refused, device-local list,
unmounted when the user signs out, app access under ADR 0231.

This work item has **no real provider**: the connectors come in work items 3, 4
and 6, the interface in work item 5.

## Architecture

| Element | Project | Role |
| --- | --- | --- |
| `INetworkDriveSource` | `Kazo.Os.Simulation.Abstractions` (`FileSystem/NetworkDriveSourceContracts.cs`) | A source: `Type`, `IsAvailableAsync`, `CreateAsync` (asks the user what it needs), `OpenAsync` (opens again at login, without reaching the service), `ForgetAsync`, `AvailabilityChanged` event. |
| `NetworkDriveReference`, `NetworkDriveRequest`, `NetworkDriveCreation` | same | What the list keeps about a drive (name, key, account, root), what the user chose (account, root), and a created drive with its default mount name. |
| `IHostFolderQuota`, `HostFolderQuota` | `FileSystem/HostFolderContracts.cs` | Optional interface of an `IHostFolder`: size and free space. |
| `IConditionalHostFolder`, `HostFolderEntry.Revision` | same | Optional interface: the service's revision (`rev`, `eTag`, `ETag`) and conditional writes. |
| `HostFolderErrorKind.RevisionConflict`, `.Unavailable`, `HostFolderPermission.Unavailable` | same | A refused conditional write; a service that cannot be reached or an expired sign-in. |
| `HostFolderErrorKind.FileTooLarge` | same | A file of the service over 128 MiB (ADR 0245, ADR 0267 §10): listed, but reading it answers `FileTooLarge` (`EFBIG`). Added in work item 4 ([`onedrive-drives.md`](onedrive-drives.md)). |
| `HostFolderDriveSource` | `Kazo.Os.Platform.Core` (`FileSystem/HostFolders/`) | The `hostfs` source, over `IHostFolderAccess` (folder dialog, handles in IndexedDB). Unchanged behaviour. |
| `NetworkDriveService` | same | Takes the list of sources; mounts, restores, reconnects and forgets through the drive's source. |
| `HostFolderFileSystemProvider` | same | Provider of every network drive: `Type`, `MountSource`, `GetQuotaAsync`, service revision. |
| `FakeRemoteDriveSource` | `Tests/Kazo.Os.Tests.Support` | In-memory fake cloud source for tests (and for work item 5). |

The Ecosystem host (`EcosystemServiceCollectionExtensions`) gives the service
the `hostfs` source and then every `INetworkDriveSource` registered in
dependency injection: a connector is added with one
`services.AddScoped<INetworkDriveSource, …>()` line.

## Drive list

`~/.local/state/kazoos/network-drives.json`, `0600`, device-local:

```json
{
  "schemaVersion": 1,
  "drives": [
    { "mountPoint": "/mnt/Projects", "name": "Projects", "key": "…", "createdMountPoint": true, "type": "hostfs" },
    { "mountPoint": "/mnt/Dropbox", "name": "alice@example.test", "createdMountPoint": true,
      "type": "dropbox", "account": "<account reference>", "root": "Work" }
  ]
}
```

- No `type`: `hostfs`. A list written before this work item is read as it is
  and not rewritten until something changes.
- `account` is a **reference** into the cloud account list (work item 2), never
  a token. No `root`: the whole storage.
- A line whose type has no source on this host is dropped from the list at
  login, with a warning in the log.

## Behaviour

- **Mount**: `MountAsync(type, point, request)`. Without a point, the source
  gives the default name (`/mnt/Dropbox`), then `-2`, `-3`… An unknown type or
  an unavailable source gives `NetworkDriveError.NotSupported`, a request the
  source refuses (unknown account) `InvalidRequest`, a source that needs a
  gesture `UserActivationRequired`. `MountAsync(point)` stays the folder of the
  computer.
- **Connected or not**: decided by `IHostFolder.QueryPermissionAsync`.
  `Granted` connects; `Prompt` waits for "Reconnect" (a user gesture);
  `Unavailable` waits for the source to come back.
- **Failure on the way**: an operation that gets `HostFolderErrorKind.Unavailable`
  disconnects the drive and answers `ProviderUnavailable`; the root stays
  visible as an empty folder.
- **Reconnects by itself** (§9): when a source raises `AvailabilityChanged`
  (network back, account signed in again), `ReconnectAvailableAsync` asks every
  disconnected drive again and reconnects those that answer `Granted`, with no
  gesture. A `hostfs` drive never comes back alone: the browser needs a gesture.
- **Revision**: when the service gives a revision, the file's revision in
  Kazo OS is a stable 62-bit hash of it; otherwise it comes from the time and
  size, as before. A write that expects a revision is compared in the OS first,
  then sent to the service with its condition
  (`IConditionalHostFolder.WriteIfMatchAsync`): a file changed elsewhere in
  between gives `RevisionConflict`, and the drive stays connected.
- **Quota**: `NetworkDriveService.GetQuotaAsync` and `KazoDriveInfo`
  (`TotalSize`, `AvailableFreeSpace`) give the service's quota when the folder
  knows it and the drive is connected; 0 otherwise, and `df` shows `-`.
- **`/proc/mounts`**: `host:<folder> /mnt/<name> hostfs rw,nosuid,nodev 0 0`
  for a folder of the computer, `<type>:<account> /mnt/<name> <type>
  rw,nosuid,nodev 0 0` for a cloud drive.
- **`df -T`**: same source and type; the sizes when the service has a quota
  (`dropbox:alice dropbox 4096 3072 1024 75% /mnt/Dropbox`).

## Writing a source

1. Implement `INetworkDriveSource`; `Type` in lower case, no space.
2. `CreateAsync` asks the user what it needs and returns the folder, its
   reference and the default mount name; `null` when the user closes.
3. `OpenAsync` builds the folder **without reaching the service**: the user's
   login must never wait for the network.
4. The folder implements `IHostFolder` and, when it can, `IHostFolderQuota` and
   `IConditionalHostFolder`. Offline or with an invalid token: throw
   `HostFolderException(HostFolderErrorKind.Unavailable)` and answer
   `Unavailable` (network) or `Prompt` (sign in again) to
   `QueryPermissionAsync`.
5. Raise `AvailabilityChanged` when the service may be reachable again.

## Sources shipped

| Type | Source | Doc |
| --- | --- | --- |
| `hostfs` | `HostFolderDriveSource` | this document, [ADR 0181](adr/0181-host-folder-mounted-as-a-network-drive.md) |
| `s3` | `S3DriveSource` (work item 6) | [`s3-drives.md`](s3-drives.md) |

A mount request also carries `Options` (settings that are not secret) and
`Secrets` (which the source keeps in the keyring, never in the drive list); the
`s3` source reads them.

## Types features provide

Work item `drive-providers/4-platform-adapter`,
[ADR 0282](adr/0282-third-party-drive-providers.md) §1 to §3. The contract the
provider's author sees is in
[`drive-providers.md`](../framework/drive-providers.md).

| Element | Role |
| --- | --- |
| `AppCatalog.GetDriveProviders()`, `FindDriveProvider(type)`, `AppDriveProviderRegistration` | The types the catalog's features declare, one per type. |
| `IDynamicDriveSources`, `FeatureDriveSources` (`FileSystem/DriveProviders/`) | The sources that come and go: one `FeatureDriveSource` per type of an installed and enabled feature, kept while the same version provides it; `Changed` on every install, enable, disable, removal or update. |
| `FeatureDriveSource` | The source of a provided type. `CreateAsync` requires an account of that type (never the provider); `OpenAsync` calls nothing. |
| `FeatureDriveFolder` (internal) | The `IHostFolder` of one drive, over `KazoDriveFolder`: quota, conditional writes, translation of errors and states, one renewal of a refused token. |
| `FeatureDriveProviderLoader` | Loads the provider at the first drive of the type, one instance per page, in the feature's process and context; a failure is never kept. |
| `FeatureDriveContextFactory`, `FeatureDriveConnection` | What the OS lends the provider for one drive: the context bound to the account and the form's non-secret values. |
| `InMemoryDriveProvider`, `InMemoryDriveService` (`Tests/Kazo.Os.Tests.Support`) | In-memory fake provider and service, reused by work items 6 and 7. |

`NetworkDriveService` reads these sources at each use, next to the host's (a
type of the host always wins): a type installed while the page runs is offered
at once. `ReconnectAvailableAsync` waits for a mount or restore in progress, so
a provider that answers while its drive is being mounted does reconnect it.

**Login never waits for a provider.** Restoring the list loads nothing: the
drive is mounted disconnected, the provider is loaded and opened in the
background (20 s at most), then the source raises `AvailabilityChanged` and the
drive reconnects by itself. A provider that fails, throws or is too slow leaves
the drive disconnected and writes `/var/log/apps/<feature>.log`.

The Ecosystem host registers `FeatureDriveProviderLoader` and
`FeatureDriveSources` and gives them to `NetworkDriveService`. The context lent
to the provider comes from work item 5
(`CloudAccountService.CreateDriveProviderContextAsync`), with the non-secret
values of a form account (`CloudAccount.Options`); an account that is gone or of
another type gives `SignInRequired` and the drive stays disconnected.

**Missing provider** (feature removed or disabled): the expected behaviour
(lines kept, greyed drives, remount when the type comes back) waits for two
answers of the maintainer on how to recognise a line's provider; it is not
built yet. Today `NetworkDriveService` unmounts the drives of a type that left
and keeps the lines of the OS's shipped types only (work item 3).

## Tests

- `Tests/Kazo.Os.Platform.Core.Tests/FileSystem/HostFolders/NetworkDriveSourceTests.cs`:
  cloud mount with default name and numbering, list with type, account and root,
  unknown type and refused request, restore through the source and forget on
  unmount, old list read as `hostfs`, mounted disconnected while offline then
  reconnected by itself, failure on the way, expired sign-in waiting for
  "Reconnect", service revision and refused conditional write, quota up to
  `KazoDriveInfo`, folder of the computer unchanged.
- `Tests/Kazo.Os.Commands.Tests/MountCommandTests.cs`: `df -T` with a cloud
  drive, a drive without quota and a folder of the computer.
- `Tests/Kazo.Os.Platform.Core.Tests/FileSystem/DriveProviders/`: the shipped
  connectors' contract suite on `InMemoryDriveProvider` through the adapter
  (`FeatureDriveContractTests`), login that does not wait for a slow provider,
  timeout, constructor that throws, missing assembly, process that does not
  start, one instance per page in the feature's context, account of another
  type refused, types that follow the catalog hot (`FeatureDriveSourceTests`),
  mount of a type installed hot and restore with no wait
  (`FeatureDriveMountTests`), refusal of a second provider of the same type
  (`DriveTypeInstallTests`).
- The existing `hostfs` drive tests pass unchanged.

## Decisions

- New enum members are added **after** the existing ones, to keep their values.
- A drive's type is written to the list on every new mount, `hostfs` too; an
  old line keeps having no type.
- The `ProviderId` of a `hostfs` drive does not change (`hostfs:/mnt/<name>`),
  so neither do its entries' IDs.

## Task list

- [x] Source contract, references and requests.
- [x] `hostfs` source with no change of behaviour.
- [x] Multi-source `NetworkDriveService`, list with type, account and root.
- [x] Service quota and revision, conditional writes.
- [x] `/proc/mounts`, `df`, `KazoDriveInfo` with the type and quota.
- [x] Unavailable drive mounted disconnected, reconnects by itself.
- [x] In-memory fake remote source and tests.
- [x] Types features provide: registry, adapter, lazy load, login with no wait (ADR 0282, work item 4).
- [ ] Missing provider: lines kept and remount (ADR 0282, waits for the maintainer).
