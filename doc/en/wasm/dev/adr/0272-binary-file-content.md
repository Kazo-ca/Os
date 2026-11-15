> [Version française](../../../../fr/wasm/dev/adr/0272-binary-file-content.md)

# ADR 0272: file content stored as binary in IndexedDB

## Status

Proposed on 2026-11-15, `storage/base` series
([plan](../plans/browser-storage-series-plan.md)). Benoit kept this lead on
2026-11-14 (storage analysis, §7). Two questions are still asked to Benoit (at
the end); the decision applies the recommendation of each until he answers.
The work item also waits for the result of issue
[#538](https://github.com/Kazo-ca/Os/issues/538) for its place in the series,
not for its content.

- **Supersedes:** point 2 of [ADR 0245](0245-raise-the-maximum-file-size.md)
  ("stored format unchanged", chunks encoded in base64) and, in point 2 of
  [ADR 0094](0094-deferred-decisions-status-and-maximum-file-size.md), the
  encoding of the chunks only.
- **Keeps:** the 256 KiB chunks, the `[SHA-256 of the file, number]` key,
  whole-file deduplication, the batches of 64 chunks per transaction and the
  128 MiB limit (ADR 0094 and 0245).
- **Builds on:** [ADR 0015](0015-browser-storage-and-indexeddb-adapter.md)
  (IndexedDB adapter), [ADR 0018](0018-indexeddb-failure-and-recovery-policy.md)
  (failures and recovery) and [ADR 0273](0273-short-orphan-content-retention.md)
  (cleanup pass, which also carries the conversion).
- **Analysis:** [`browser-storage-optimization-analysis.md`](../plans/browser-storage-optimization-analysis.md),
  findings C4 to C6, lead 2.

## Context

- A content chunk is today the record
  `{contentHash, chunkIndex, dataBase64, createdUtcMs}` of the `fsContent`
  store (`IndexedDbFileContentRepository`). Base64 takes 1.34 times the size
  of the file.
- Records go from .NET to JavaScript as JSON through `IJSRuntime`, and the
  SHA-256 is computed in .NET at write time **and at every read**.
- Measured for 52 MiB (Release, no AOT): write 3.37 s, read 1.86 s. The same
  content as `Uint8Array`, passed without JSON and hashed by WebCrypto: 0.40 s
  and 0.36 s.
- Other code reads or writes `dataBase64` without going through the
  repository: `IndexedDbBackupService` (backup, the whole store as JSON),
  `StorageSnapshotRedactor` (masks the content in a diagnostic snapshot),
  `RenamedOsDatabaseRewriter` and `RenamedContentRepair` (rename migration,
  ADR 0230).
- The `kazoos` database is at version 4. A page of an older release of the OS
  can be open at the same time as a newer page (another tab).

## Decision

### 1. Form of a chunk

A chunk written from this ADR on is the record:

```text
{ contentHash, chunkIndex, data: Uint8Array, chunkHash, createdUtcMs }
```

- `data`: the bytes of the chunk, with no encoding.
- `chunkHash`: the SHA-256 of the chunk, in hexadecimal. It checks a chunk
  read alone (§4); it is not a key.
- The store, the `[contentHash, chunkIndex]` key, the `contentHash` index and
  the chunk size do not change. `contentHash` stays the SHA-256 of the whole
  file: sync (ADR 0030), the check of app files (ADR 0098 §5) and
  deduplication keep using it.

The 4 MiB chunks measured in the analysis read a little faster (0.12 s against
0.19 s) but would change the chunk protocol of sync: they are not kept.

### 2. Passing the bytes, and hashing

- The content repository has its own JavaScript module in
  `Kazo.Os.Infrastructure.Browser/wwwroot/`, imported with `[JSImport]`. Bytes
  cross as `JSType.MemoryView`, with no JSON and no base64. The other stores
  keep the JSON adapter.
- The SHA-256 of the file and of each chunk are computed by WebCrypto
  (`crypto.subtle.digest`). WebCrypto does not hash piece by piece: the file
  is therefore present once in full on the JavaScript side during a write
  (128 MiB at most). Streamed writing, which also avoids the copy on the .NET
  side, is the subject of [ADR 0275](0275-partial-read-and-file-address.md) §4.
- `/proc/diskstats` keeps counting the bytes and the time of each transaction
  (ADR 0057 §4).

### 3. Existing content: read both forms, convert in the background

- A read accepts a chunk in either form, chunk by chunk: one file can have
  converted chunks and others not.
- A write only writes the binary form.
- The **cleanup pass** (ADR 0273) converts the old chunks in the background,
  in batches of 16 MiB at most, walking the store by key. It records its
  position in the `syncMetadata` store and resumes there after a restart. When
  the walk is over, it records that the database is converted and does not
  start again. Converting a chunk replaces the record at the same key: no
  extra space is needed, and the space taken drops by about 25% at the end.
- The conversion stops while an app writes or reads a large file, and does not
  run when the tab is hidden.
- Reading the old form stays in the code as long as the backup carries it
  (§5).

### 4. Checks at read time

- A read checks each chunk by its `chunkHash`, the numbers that follow each
  other and the total length. The SHA-256 of the whole file **is no longer
  recomputed at every read**: this is what allows partial reads (ADR 0275).
- A chunk in the old form has no `chunkHash`: a file that still has some is
  checked as today, by the SHA-256 of the whole file.
- The whole-file check stays available on demand (cleanup pass, backup,
  restore, sync).

### 5. Backup, diagnostics, rename migration

- **Backup** (`IndexedDbBackupService`, ADR 0099): the document keeps
  `formatVersion: 1` and the `dataBase64` field. The export encodes the binary
  chunks in base64 in the document; the restore decodes them and writes the
  binary form. A backup made before this ADR therefore restores, and a backup
  made after it stays readable by a previous release of the OS. Streamed
  backup (lead 11 of the analysis) is not in this ADR.
- **Diagnostic snapshot** (`StorageSnapshotRedactor`): masks `data` and
  `dataBase64`.
- **Rename migration** (`RenamedOsDatabaseRewriter`): it only reads databases
  from before 3.0.0, which only have the old form. It does not change.
  `RenamedContentRepair` reads content through the repository.

### 6. Database version: 5

The `kazoos` database goes to **version 5**. The migration step changes no
store for this ADR ([ADR 0274](0274-single-copy-of-app-files.md) adds its own
there). The number is a guard: a page of a previous release of the OS can no
longer open the database, instead of failing file by file on a chunk it cannot
read. The older tab receives `versionchange` and closes its connection
(`indexedDb.js`); its next operation fails with a version error. The work item
checks what that tab then shows and, if needed, makes it offer the restart
onto the new version (ADR 0223, 0239) rather than a file error.

## Consequences

- Writes about 8 times faster and reads about 5 times faster for the storage
  layer (measurements of the analysis, Chromium); 25% less space once the
  database is converted.
- **No way back**: once the database is at version 5, a previous release of
  the OS no longer opens it. Putting an older release back online leaves the
  profiles already moved without access to their files until the new release
  is back.
- During the conversion, the database holds both forms; any code that reads
  `fsContent` must go through the repository or accept both.
- The time gained does not fix issue
  [#520](https://github.com/Kazo-ca/Os/issues/520) by itself: the storage
  layer accounts for only about 3.4 s of 43 s there (issue #538).
- Firefox and Safari were not measured; the work item measures the three
  browsers before it is merged.

## Rejected options

- **Convert all at once at boot, with a waiting screen**: simple to reason
  about, but a profile of several GiB blocks the OS for several minutes at the
  first boot after the update.
- **Never convert** (read both forms forever): the old content keeps its 34%
  overhead and the double read can never be removed.
- **OPFS for content**: the fastest, but a second store to keep consistent
  with IndexedDB (transactions, backup, reset). Kept for later (analysis,
  lead 9).
- **Keep the managed SHA-256**: 1.2 to 1.3 s for 52 MiB against 0.09 s.

## Questions asked to Benoit

1. **How to convert the content already stored?** A. in the background, in
   small batches, reading both forms in the meantime (recommended, §3);
   B. all at once at the first boot, with a waiting screen; C. never.
2. **What to check at every read?** A. each chunk by its own hash
   (recommended, §4); B. the whole file as today (no partial read, 1.2 s more
   for 52 MiB); C. nothing at read time, only during the cleanup pass.

## Tasks

- [ ] JavaScript module of the content repository, `[JSImport]`, `MemoryView`.
- [ ] Binary write with `chunkHash`, SHA-256 by WebCrypto.
- [ ] Reading both forms, per-chunk check.
- [ ] Database at version 5, empty migration step, test of an older page.
- [ ] Backup and restore (format 1 kept), diagnostic snapshot.
- [ ] Background conversion in the cleanup pass, resumed after a restart.
- [ ] Measurements before and after in Chromium, Firefox and Safari.
- [ ] Docs (FR and EN): `browser-infrastructure.md`, backup, memory,
  `features.md`, change entry.
