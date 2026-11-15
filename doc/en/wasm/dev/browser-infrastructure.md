> [Version française](../../../fr/wasm/dev/browser-infrastructure.md)

# Browser infrastructure: `Kazo.Os.Infrastructure.Browser`

## Purpose

`Infrastructure/Kazo.Os.Infrastructure.Browser` is the only layer that
touches the browser: IndexedDB persistence (ADR 0015), recovery after
a storage failure (ADR 0018), host clipboard, encryption through
WebCrypto, cross-origin isolation, Web Workers. The rest of the OS never calls
`IJSRuntime` directly: it goes through the contracts of
`Shared/Kazo.Os.Simulation.Abstractions` that this layer implements.

The platform (`Platform/Kazo.Os.Platform.Core`) does not depend on this project
([`platform-core.md`](platform-core.md)). It is `AddKazoOsEcosystem`
(`OS/Kazo.Os.Ecosystem.Host`) that registers the browser
implementations, in the host's composition.

## What is where

| Folder | Content |
|---|---|
| `Schema/` | `KazoOsIndexedDbSchema` (database name, version, stores and indexes), schema model, content storage policy (`FileContentStoragePolicy`). |
| `FileSystem/` | File system provider (`IndexedDbFileSystemProvider`), reader and writer, mutation planner, seeding, maintenance, chunked content. |
| `Sessions/`, `Policy/`, `Catalog/`, `Settings/`, `ServerConnection/`, `Sync/`, `WebData/` | One IndexedDB repository per domain (users, groups, grants, app catalog, legacy settings, server connection, sync cursors and states, ASP.Kazo site databases). |
| `Diagnostics/` | Persisted diagnostic and audit log, storage inspector. |
| `Backup/` | Storage backup and restore (`IndexedDbBackupService`). |
| `Rename/` | Migration of the data stored under the HackerOS names, at boot and at restore (ADR 0230, [`rename-migration.md`](rename-migration.md)). |
| `Recovery/` | `BrowserStorageRecoveryClassifier`: translates a browser error into a recovery state (ADR 0018, `StorageRecoveryState`). |
| `Storage/` | `BrowserStorageManager`: quota estimation, persistence request, low-space thresholds. |
| `Interop/` | `IndexedDbInteropAdapter` (JavaScript calls), `WebCryptoPasswordHasher`, host clipboard channel. |
| `Isolation/` | `BrowserCrossOriginIsolation`: COOP/COEP state (ADR 0131, 0146). |
| `Workers/` | `BrowserWebWorkerRuntime`: Web Workers runtime on the browser side ([`worker-processes.md`](worker-processes.md)). |
| `Globalization/` | Language satellites loaded by the browser. |
| `wwwroot/` | JavaScript scripts served under `_content/Kazo.Os.Infrastructure.Browser/`: `indexedDb.js`, `fileContent.js` and `binaryChunks.js` (binary file content, ADR 0272), `storageManager.js`, `webSiteDatabases.js`, `renamedStorage.js` (rename migration), `cryptoHasher.js`, `hostClipboard.js`, `crossOriginIsolation.js`, `sharedRing.js` (shared ring of the workers' pipes), `workerHost.js` and `workerBootstrap.js`. |

## IndexedDB database

- Name: `kazoos`. Current version: **5** (`KazoOsIndexedDbSchema.CurrentVersion`).
  Changing the version is the only way to add, remove or modify a
  store or an index; each upgrade has its migration step.
- Main stores: `users`, `groups`, `sessions`, `settings`, `fsEntries`
  (metadata: files, folders, links), `fsLinks`, `fsContent` (chunks),
  `catalog`, `grants`, `audit`, `diagnostics`, `syncMetadata`, `serverConnection`,
  `syncCursors`, `syncRecordState`, and one database per ASP.Kazo site (`kazoweb.<appId>`).
- File content: split into chunks of at most 256 KiB by default
  (`FileContentStoragePolicy.DefaultMaxChunkSizeBytes`), addressed by the SHA-256 of
  the whole file and the chunk number; two identical files share their content, but
  not two files that only have some chunks in common.
- Since version 5 (ADR 0272) a chunk is stored as bytes: `{contentHash, chunkIndex,
  data: Uint8Array, chunkHash, createdUtcMs}`, where `chunkHash` is the SHA-256 of the
  chunk alone. `IndexedDbFileContentRepository` goes through `IFileContentChunkStore`
  (`BrowserFileContentChunkStore`, `[JSImport]` of `fileContent.js` with a
  `MemoryView`): no base64 and no JSON between C# and the browser. A write hashes in
  WebCrypto, then stores 64 chunks per transaction; a read checks each chunk (number,
  length, `chunkHash`) and refuses a damaged file instead of returning wrong bytes.
- The JSON adapter (`indexedDb.js`) still sees chunks in their base64 form:
  `binaryChunks.js` turns a binary chunk it reads into `dataBase64`, and an operation
  marked `BinaryChunk` (backup restore, rename migration) back into bytes after
  checking its hash. Backups keep format 1 and schema version 4
  (`KazoOsIndexedDbSchema.BackupSchemaVersion`), so an older release still restores them.
- One-time conversion: at the first boot on version 5, before the session starts,
  `FileContentConversion.RunAsync` converts the base64 chunks of older releases by
  batches of 16 MiB, behind a waiting screen with progress
  (`storageConversion.js` of the Ecosystem host). The position is saved in
  `syncMetadata` (`fileContentConversion`), so a closed tab resumes where it stopped;
  a Web Lock keeps it to one tab, the others wait. A damaged chunk is kept as it is
  and named in the log, like `fsck`; if the conversion fails the OS does not start
  and the screen offers to retry.
- A tab of an older release still open when version 5 opens the database closes its
  connection; its next operation fails with a version error and the person reloads.

## Recovery

`BrowserStorageRecoveryClassifier` sorts a failure into one of the states of
`StorageRecoveryState`: storage unavailable, quota exhausted, migration
failed, invalid content. The context (`StorageRecoveryContext`) distinguishes
startup (boot) from an ordinary operation. A destructive reset
always goes through an explicit confirmation (`AwaitDestructiveConfirmation`).
The rule: never turn an operational failure into silent data loss
(ADR 0018). The PWA cache recovery procedure is in
[`pwa-cache-recovery.md`](pwa-cache-recovery.md); the machine backup
in [`system-backup.md`](system-backup.md).

## Points to know

- Metadata mutations are ordered batches of IndexedDB operations,
  built by `IndexedDbFileSystemMutationPlanner` to be atomic. Content
  chunks are addressed by hash; those that are no longer referenced
  (rewritten or deleted files) are collected by a bounded pass
  (`IndexedDbFileSystemMaintenance`, `IStorageReclaimer`), after a retention period:
  1 day (`OrphanRetention`), 5 minutes when storage is low
  (`LowSpaceOrphanRetention`) ([ADR 0273](adr/0273-short-orphan-content-retention.md)).
  `StorageSpaceMonitor` runs it when the desktop shows, every hour, after a deletion
  in the trash, and when storage becomes low, before warning (Os#537). The pass runs
  in `runOrphanPass` (`indexedDb.js`): nothing while the tab is hidden, and one tab at
  a time (Web Locks). It reads keys only and chunk 0 of unreferenced contents, never
  the whole `fsContent` store; its delete transaction checks references and age
  again. A content's age is the time of its chunk 0 (undated content counts as old).
  A write that reuses stored content gives its chunk 0 the new time, and the
  transaction that publishes the file checks that all its chunks are present
  (`assertCount`): when one is missing, the content is written again once, then the
  write fails with an I/O error. A rewrite done by the system (`AppAuthority.System`)
  deletes the old content in the same transaction when nothing references it any more.
- A file's path is resolved in one request (`resolvePath`, ADR 0090,
  [`filesystem-path-lookup.md`](filesystem-path-lookup.md)).
- The `wwwroot/*.js` scripts are contract-tested by
  `Tests/Kazo.Os.Infrastructure.Browser.Tests` (Node tests for `sharedRing.js`)
  and `Tests/Kazo.Os.E2E.Tests` (`IndexedDbBrowserContractTests`, in a real browser).
- The old IndexedDB migration notes (versions, resumption, recovery
  contract) are archived in [`old/`](old/); they describe the earlier
  state and are not authoritative for the current schema.

## Links

- Decisions: [ADR 0015](adr/0015-browser-storage-and-indexeddb-adapter.md), [ADR 0018](adr/0018-indexeddb-failure-and-recovery-policy.md), [ADR 0099](adr/0099-os-backup-and-low-storage.md), [ADR 0131](adr/0131-cross-origin-isolation.md).
- File system: [`virtual-proc-and-dev.md`](virtual-proc-and-dev.md), [`../framework/kazo-file-api.md`](../framework/kazo-file-api.md).
- Host and build: [`building-and-testing.md`](building-and-testing.md).
