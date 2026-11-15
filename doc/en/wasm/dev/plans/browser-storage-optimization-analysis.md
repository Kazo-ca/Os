> [Version française](../../../../fr/wasm/dev/plans/browser-storage-optimization-analysis.md)

# Analysis: optimizing storage in the browser

Analysis done on 2026-11-14 on `main` (version 3.3.4). **No code is changed** and no ADR
number is taken: this document describes what the OS keeps in the browser, what it
costs, and the ways to do better. Each avenue that is kept will need its ADR before the
code. Benoit's decisions are in §7.

## 1. Summary

1. **Space is never reclaimed.** The content of a deleted or rewritten file stays in
   IndexedDB: the cleanup pass exists, but no host calls it. On the published site, a
   new profile already has 3 orphan contents out of 25 after the first boot, and 4 out
   of 26 after one reload.
2. **File content takes 1.34 times its size**, because it is written as base64 in JSON
   records. The same content in binary takes its own size.
3. **The write and read path can be about 8 times and 5 times faster** (52 MiB:
   3.4 s → 0.4 s to write, 1.9 s → 0.36 s to read) with binary records, bytes passed
   without JSON, and the browser's SHA-256.
4. **This is indeed the cause of the slowness of issue [#520](https://github.com/Kazo-ca/Os/issues/520).**
   Profiling in the real OS ([#538](https://github.com/Kazo-ca/Os/issues/538), delivered
   on 2026-11-15) shows that in a Release publish, a 52.5 MiB save takes 4.1 to 4.9 s,
   of which 96 % is in the content repository. The 43 s of the issue came from a Debug
   host. Binary content (avenue 2) therefore fixes #520. The first version of this
   analysis said the opposite, wrongly (see §3.4).
5. **An installed app is kept in three copies**: the current version and the previous
   one in the file system (as base64), plus a copy of the served files in Cache
   Storage. A 64 MiB app takes about 236 MiB.
6. **Storage is not protected against eviction**: `persist()` is never called.
   [ADR 0250](https://github.com/Kazo-ca/Os/blob/feature/base/doc/en/wasm/dev/adr/0250-storage-persistence-and-eviction.md)
   (in `feature/base`, not built yet) already covers this point.

## 2. What the OS keeps in the browser

| Place | Content | Notes |
| --- | --- | --- |
| IndexedDB `kazoos` (version 4) | File metadata (`fsEntries`, `fsLinks`), file content (`fsContent`), users, groups, permissions, audit log, diagnostics, sync | All the user's data is here. |
| IndexedDB `kazoweb.<appId>` | One database per ASP.Kazo site | |
| Cache Storage `kazoos-cache-<version>` | The whole published release of the OS (325 entries, 52.2 MB), put there by the service worker | Used to work offline. |
| Cache Storage `kazoos-installed-apps` | Copy of the `wwwroot` files of installed apps, served at `_content/<assembly>/…` (ADR [0206](../adr/0206-serve-installed-app-assets.md), [0225](../adr/0225-serve-installed-app-files-to-every-page.md)) | Copied again from the file system at the first launch of each session. |
| Cache Storage `kazoos-isolation` | State of origin isolation | Negligible. |
| OPFS | Nothing | Only erased by the factory reset. |

**Format of a file's content** (`IndexedDbFileContentRepository`,
`FileContentStoragePolicy`):

- chunks of 256 KiB, 64 chunks (16 MiB) per transaction, at most 128 MiB per file
  (ADR [0245](../adr/0245-raise-the-maximum-file-size.md));
- one record per chunk: `{contentHash, chunkIndex, dataBase64, createdUtcMs}`;
- the key is `[contentHash, chunkIndex]`, where `contentHash` is the SHA-256 **of the
  whole file**: two identical files share their content, but two files that differ by
  a single byte share nothing;
- the SHA-256 is computed in .NET on write, and computed again on every read;
- records go from .NET to JavaScript as JSON (`IJSRuntime`).

**App packages**: at most 64 MiB for the archive, 128 MiB once extracted, 2000 files.
Files are extracted into `/usr/lib/kazoos/apps/<appId>/<version>/` (or
`~/.local/share/kazoos/apps/` for a per-user install). The installer keeps the current
version and the previous one.

**Space policy**: low storage under 64 MiB or 10 % free, 5 % reserve for root (at most
256 MiB), orphan content retention of 30 days
(ADR [0094](../adr/0094-deferred-decisions-status-and-maximum-file-size.md) for the retention,
[0099](../adr/0099-os-backup-and-low-storage.md) for low storage).

## 3. Measurements

Headless Chromium 153, 4 cores, on-disk profile, 52 MiB of random bytes. The benchmarks
are throwaway and are not in the repository.

### 3.1 In JavaScript only

| Method | Write | Read | Space used |
| --- | --- | --- | --- |
| IndexedDB, current shape (base64, JSON) | 1.68 s | 0.56 s | 72.8 MB (1.34×) |
| IndexedDB, binary, 256 KiB chunks | 0.18 s | 0.19 s | 54.7 MB |
| IndexedDB, binary, 4 MiB chunks | 0.17 s | 0.12 s | 54.5 MB |
| OPFS, `createWritable` | 0.14 s | 0.06 s | partial read of 64 KiB: 2.1 ms |
| OPFS, synchronous access in a worker | 0.09 s | 0.03 s | partial read: 0.1 ms |
| Cache Storage | 0.17 s | 0.08 s | 54.5 MB |

SHA-256 through WebCrypto: 0.09 s. Gzip compression (`CompressionStream`): 0.9 to 1.2 s
to compress, 0.1 to 0.2 s to decompress; ratio 1.0 on random bytes, 0.10 on repetitive
text.

### 3.2 From .NET (Release, no AOT, as in production)

A Blazor WebAssembly project that reproduces the content repository with the real
`indexedDb.js`.

| Path | Write | Read |
| --- | --- | --- |
| Current | 3.37 s | 1.86 s |
| Binary + WebCrypto SHA-256 + bytes passed without JSON | 0.40 s | 0.36 s |

Detail of the current path, write: hashing and splitting 1.36 s (of which 1.2 to 1.3 s
for hashing alone), base64 0.27 s, interop and IndexedDB 1.75 s (of which 0.34 to
0.38 s to serialize each 16 MiB batch to JSON). Read: interop and IndexedDB 1.25 s,
decoding and hashing 0.58 s, SDK copies 0.04 s.

### 3.3 Published site (os.kazo.ca, new profile)

- Usage: 71.6 MB, of which 52.2 MB for `kazoos-cache-<version>` (325 entries).
- `persisted`: false.
- 23 files, 24 KB of useful content; 25 contents of which 3 orphans after the first
  boot, 26 of which 4 orphans after one reload. Each boot adds some.

### 3.4 What this says about issue #520

The issue measures, for a 52.5 MB `.psd`: 43.4 s to save, 40.0 s to open (2.8 s in
PhotoCraft itself), and memory going from 1391 to 2588 MiB. The storage layer, measured
alone in Release, takes 3.4 s and 1.9 s.

**Correction of 2026-11-15.** The first version of this analysis compared these two
sets of numbers and concluded that about 90 % of the time was elsewhere than in
storage. That was wrong: the 43 s of the issue had been measured on a Debug host, and
the benchmark in Release. The profiling of issue
[#538](https://github.com/Kazo-ca/Os/issues/538), done in the real OS in a Release
publish, gives:

- a 52.5 MiB save takes 4.1 to 4.9 s, close to the 3.4 s of the benchmark;
- 96 % of that time is in the content repository (hashing, base64, JSON, IndexedDB);
- the first save raises memory by about 780 MiB.

Storage is therefore indeed the cause of #520, binary content (avenue 2) fixes it, and
streamed write (avenue 4) stays justified by memory. The full report is
`large-file-save-profile.md` (branch `docs/large-file-save-profile`).

One observation stays apart: on the published site,
`head -c 1048576 /dev/urandom > a.bin` in the terminal had not finished after
4 min 30. The terminal path therefore has its own problem, unrelated to IndexedDB.

## 4. Findings

- **C1. Orphan contents are never collected.**
  `IndexedDbFileSystemMaintenance.InitializeAsync` and `CleanupAsync` have no caller
  outside tests. Every rewritten file leaves its old content forever.
- **C2. The cleanup pass would not scale.** It starts with a `getAll` of the whole
  `fsContent` store, so it loads all content in memory.
- **C3. The 30-day retention is long** for storage under a quota: even once the pass is
  wired, a 50 MB file saved ten times takes 500 MB for a month.
- **C4. Base64 and JSON cost 34 % of space** and half of the write time.
- **C5. The managed SHA-256 is slow** (1.2 to 1.3 s for 52 MiB, against 0.09 s through
  WebCrypto), and it is done again on every read.
- **C6. Everything is in memory in full.** Writing keeps the list of all chunks,
  reading a complete `MemoryStream`, and `KazoFile` makes its own copies. There is no
  partial read and no streamed write. This is the likely cause of the memory peak of
  issue #520.
- **C7. Deduplication is per whole file**, not per chunk. `browser-infrastructure.md`
  said the opposite; it is corrected in the same change.
- **C8. Installed apps are in three copies** (see §1, point 5), and the copy to Cache
  Storage is done again every session.
- **C9. `persist()` is never called**: `RequestPersistenceAsync` has no caller. The
  browser may erase everything if it runs out of space.
- **C10. The log rewrites its whole file** at every flush (1 s): `LogFileWriter` reads
  the file, appends, and rewrites everything. With C1, each flush leaves up to 1 MiB of
  orphan.
- **C11. The backup loads everything in memory**: `IndexedDbBackupService` does a
  `getAll` of each store, then JSON, then gzip.

## 5. Avenues

1. **Reclaim space.** Rewrite the pass to read keys only, wire it at boot (in the
   background) and when space is low, shorten the retention, and delete the old content
   at once when the system rewrites its own files (logs, settings). Small, no format
   change, immediate gain.
2. **Binary content.** `Uint8Array` records instead of base64, bytes passed through
   `[JSImport]` and `JSType.MemoryView` instead of JSON, SHA-256 through WebCrypto.
   Measured gain: 8× on write, 5× on read, 25 % less space. Needs an ADR: migration of
   existing records (or reading both shapes), backup format, readers that read
   `dataBase64` directly.
3. **Profile issue #520 in the real OS**, layer by layer, before fixing it.
   Done on 2026-11-15 (#538, see §3.4): the time is in the content repository.
4. **Partial read and streamed write.** Read a range of chunks without loading the
   file, write chunk by chunk without keeping the list. Lowers the memory peak; needs a
   new look at hash verification (per chunk instead of on the whole file).
5. **File address served by the service worker**
   ([#379](https://github.com/Kazo-ca/Os/issues/379)): an app reads a large file with
   `fetch` and `Range`, without going through .NET. Builds on avenue 4.
6. **A single copy of app files.** Option A: the service worker serves `_content/…`
   directly from the file store, and the copy in Cache Storage goes away. Option B:
   served files live only in Cache Storage and are no longer extracted into the file
   system. With avenue 2, a 64 MiB app goes from about 236 MiB to 64 MiB (128 while the
   previous version is kept).
7. **Per-chunk compression** (`CompressionStream`), only for types that compress (text,
   JSON, XML, logs): ratio 0.10 on text, but about 1 s for 52 MiB and no gain on images
   or archives.
8. **Per-chunk deduplication**: key by the chunk's hash. Useful for successive versions
   of a large document and versions of an app; changes the format and the collection
   (reference count per chunk).
9. **OPFS for large objects.** The fastest and the only one with a true partial read
   (0.1 ms), but synchronous access needs a worker, and it makes a second store to keep
   consistent with IndexedDB (transactions, backup, reset). To keep for later, if
   avenues 2 and 4 are not enough.
10. **Build ADR 0250**: `persist()` on a gesture, eviction detection, Settings ▸ Storage
    page, `KazoStorage` API.
11. **Streamed backup**: store by store, with a cursor, compressed as it goes.
12. **Lighten the release precache**: 52 MB are cached at first load; rarely used parts
    could be cached on demand.

## 6. Proposed order

| Order | Avenue | Why there |
| --- | --- | --- |
| 1 | 1. Reclaim space | Small, no format risk, fixes a real leak in production. |
| 2 | 3. Profile #520 | Says where to put the effort next. Done: binary content fixes #520. |
| 3 | 2. Binary content | The largest measured gain; changes the format, so early. |
| 4 | 10. ADR 0250 | Already decided; protects the data. |
| 5 | 6. One copy of app files | Large space gain for apps of 30 to 160 MB. |
| 6 | 4 then 5. Partial read, file address | Fixes the memory peak of large documents. |
| 7 | 7, 11. Compression, streamed backup | Targeted gains. |
| 8 | 8, 9, 12 | Only if a measured need asks for it. |

Avenues 1 and 3 start as two independent `TODO` issues (#537 and #538). The rest
makes a `storage/base` series, with its ADRs before the code.

## 7. Benoit's decisions (2026-11-14)

The three questions of the analysis were asked to Benoit, who kept the
recommendations.

1. **Where to start.** Two `TODO` issues now:
   [#537](https://github.com/Kazo-ca/Os/issues/537) (avenue 1, reclaim space) and
   [#538](https://github.com/Kazo-ca/Os/issues/538) (avenue 3, profile #520). The rest
   makes a `storage/base` series, with its ADRs before the code.
2. **Where an installed app's files live.** Option A of avenue 6: the file system stays
   the only source, as on a real machine; the service worker serves `_content/…` from
   the file store, and the copy in Cache Storage goes away.
3. **How long to keep an orphan content.** Short, from a few hours to a day, and at
   once when space is low. The exact value is set by the ADR that replaces the 30-day
   retention of ADR 0094.

## 8. Limits

- Measured on one machine and one browser (Chromium). Firefox and Safari have other
  costs for IndexedDB and OPFS, and Safari has stricter quotas.
- Random bytes are the worst case for compression and say nothing about deduplication.
- The .NET benchmark reproduces the content repository, not the whole OS: no metadata,
  no permissions, no memory pressure.
- The time of a save in the real OS was not measured by this analysis; it was
  measured afterwards by issue #538 (see §3.4).

## 9. Links

- [`browser-infrastructure.md`](../browser-infrastructure.md)
- ADR [0015](../adr/0015-browser-storage-and-indexeddb-adapter.md),
  [0018](../adr/0018-indexeddb-failure-and-recovery-policy.md),
  [0094](../adr/0094-deferred-decisions-status-and-maximum-file-size.md),
  [0099](../adr/0099-os-backup-and-low-storage.md),
  [0206](../adr/0206-serve-installed-app-assets.md),
  [0225](../adr/0225-serve-installed-app-files-to-every-page.md),
  [0245](../adr/0245-raise-the-maximum-file-size.md)
- [ADR 0250](https://github.com/Kazo-ca/Os/blob/feature/base/doc/en/wasm/dev/adr/0250-storage-persistence-and-eviction.md) (branch `feature/base`)
- Issues [#520](https://github.com/Kazo-ca/Os/issues/520), [#379](https://github.com/Kazo-ca/Os/issues/379)
