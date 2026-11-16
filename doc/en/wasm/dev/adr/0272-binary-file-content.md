> [Version française](../../../../fr/wasm/dev/adr/0272-binary-file-content.md)

# ADR 0272: file content stored as binary in IndexedDB

## Status

Accepted on 2026-11-15, `storage/base` series
([plan](../plans/browser-storage-series-plan.md)). Benoit kept this lead on
2026-11-14 (storage analysis, §7) and answered the two questions on
2026-11-15: existing content is converted **all at once at the first boot,
with a waiting screen** (question 1, choice B), and a read checks **each chunk
by its own hash** (question 2, choice A). The profiling of
issue [#538](https://github.com/Kazo-ca/Os/issues/538), delivered on
2026-11-15, confirms the lead (see the consequences): the work item can start.

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
  (cleanup pass).
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

### 3. Existing content: converted all at once at the first boot

- At the first boot of the release that brings this ADR, **before the OS opens
  a session**, the whole `fsContent` store is converted: each base64 chunk is
  replaced, at the same key, by its binary form with its `chunkHash`.
- A **waiting screen** says so and shows the progress (bytes converted out of
  bytes to convert). The OS only starts once the conversion is over.
- The conversion runs on the JavaScript side, in batches of 16 MiB at most,
  one transaction per batch. It records its position in the `syncMetadata`
  store: if the tab is closed on the way, the next boot resumes there. When it
  is done, it records that the database is converted and never comes back.
- Only one tab converts; another tab opened meanwhile shows the same screen
  and waits.
- The cleanup pass (ADR 0273) runs first when it exists, so that orphaned
  content about to go is not converted.
- Replacing a chunk at the same key needs no extra space overall, and the
  space taken drops by about 25% at the end.
- A chunk that does not decode is left as it is and logged in
  `/var/log/syslog`; its file is then reported as damaged at read time
  (ADR 0018), as it would have been before.
- **After the conversion, the code only reads the binary form.** The content
  repository, the service worker (ADR 0274) and partial reads (ADR 0275)
  therefore have one form to know. The old form is only read by the conversion
  and by the restore of a backup (§5).
- Expected duration: in the order of 20 seconds per GiB in Chromium, from the
  measurements of the analysis (base64 read, hashing, binary write); this is
  an estimate, which the work item measures in the three browsers. A new or
  small profile passes the screen in under a second.

### 4. Checks at read time

- A read checks each chunk by its `chunkHash`, the numbers that follow each
  other and the total length. The SHA-256 of the whole file **is no longer
  recomputed at every read**: this is what allows partial reads (ADR 0275).
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
  from before 3.0.0, which only have the old form, and it runs before the
  conversion. It does not change. `RenamedContentRepair` reads content
  through the repository, after the conversion.

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
- The first boot after the update waits for the end of the conversion: a few
  seconds for most profiles, several minutes for a profile of several tens of
  GiB. The waiting screen says so.
- A single form to read afterwards: no double read to keep or to remove
  later.
- **This ADR fixes issue [#520](https://github.com/Kazo-ca/Os/issues/520).**
  The profiling of issue #538, done in the real OS for 52.5 MiB, gives 4.1 to
  4.9 s to save and 3.8 to 4.1 s to open in a Release publish, 96% of it in
  the content repository: managed SHA-256 1.5 s, base64 0.35 s, JSON interop
  and IndexedDB 1.98 s when writing. The 43 s of issue #520 came from a Debug
  host, where the same three items take 99% of the time. The analysis said
  storage accounted for only 3.4 s of 43 s: it compared a Release bench with a
  Debug measurement.
- Firefox and Safari were not measured; the work item measures the three
  browsers before it is merged.

## Rejected options

- **Convert in the background, in small batches, reading both forms**
  (recommended at first): the OS starts without waiting, but any code that
  reads `fsContent` must know both forms during the conversion. Rejected by
  Benoit in favour of the one-time conversion.
- **Never convert** (read both forms forever): the old content keeps its 34%
  overhead and the double read can never be removed.
- **OPFS for content**: the fastest, but a second store to keep consistent
  with IndexedDB (transactions, backup, reset). Kept for later (analysis,
  lead 9).
- **Keep the managed SHA-256**: 1.2 to 1.3 s for 52 MiB against 0.09 s.

## Benoit's decisions (2026-11-15)

| Question | Choices | Decision |
| --- | --- | --- |
| 1. How to convert the content already stored? | A. in the background, in small batches (recommended); B. all at once at the first boot, with a waiting screen; C. never | **B** |
| 2. What to check at every read? | A. each chunk by its own hash (recommended); B. the whole file as today; C. nothing at read time | **A** |

## Tasks

- [x] JavaScript module of the content repository, `[JSImport]`, `MemoryView` (`fileContent.js`, `BrowserFileContentChunkStore`).
- [x] Binary write with `chunkHash`, SHA-256 by WebCrypto.
- [x] Reading the binary form, per-chunk check.
- [x] Database at version 5, empty migration step. Older page: behaviour described in `browser-infrastructure.md` (version error, reload), no automated test.
- [x] Backup and restore (format 1 and schema version 4 kept, `BackupSchemaVersion`), diagnostic snapshot.
- [x] One-time conversion at the first boot: waiting screen with progress,
  resumed after a close (position in `syncMetadata`), one tab at a time (Web
  Lock), test on a version 4 database (`kazoos-v4.json`, with the v2 and v3
  dumps). Resuming after a close has no automated test.
- [x] Measurements before and after in Chromium (trimmed Release, 52.5 MiB save: 3.4 to 3.5 s before, 1.0 to 1.1 s after). Firefox and Safari: not measured, not available in the container. Read: not measured (a `cat` of 52.5 MiB in the Terminal did not finish within the journey's time).
- [x] Docs (FR and EN): `browser-infrastructure.md`, backup, memory,
  `features.md`, change entry.
