> [Version française](../../../../fr/wasm/dev/adr/0245-raise-the-maximum-file-size.md)

# ADR 0245: raise the maximum file size to 128 MiB

## Status

Accepted on 2026-11-13. Benoit chose option A of Kazo Word question 17 (raise
the OS limit rather than split the `.wasm` inside the app), then **128 MiB**
among 64, 128 and 256 MiB. Branch `files/raise-max-file-size`, version 3.4.0.

- **Supersedes:** point 2 of [ADR 0094](0094-deferred-decisions-status-and-maximum-file-size.md)
  for the maximum size only (16 MiB). The 256 KiB chunks, SHA-256,
  deduplication and 30-day retention stay those of ADR 0094.
- **Builds on:** [ADR 0221](0221-pack-all-static-web-assets-of-an-app.md) (an
  app package is at most 64 MiB compressed and 128 MiB extracted, `AppPackage`)
  and [ADR 0099](0099-os-backup-and-low-storage.md) (`FileTooLarge`, `EFBIG`).

## Context

- The Kazo OS file system refused any file larger than 16 MiB
  (`FileContentStoragePolicy.DefaultMaxFileSizeBytes`, checked by
  `IndexedDbFileContentRepository.WriteAsync`).
- An app package may hold 128 MiB once extracted. In the published OS 3.3.1,
  `sudo kazopkg install Kazo-ca/Os-App-Word` fails with `FileTooLarge`: the
  WordCraft `.wasm` is 60 MB (22 MB gzipped).
- ADR 0094 requires a new ADR to change this value, because it touches the
  format stored in IndexedDB and the chunk protocol of the sync (ADR 0030).
- A write kept the whole file in memory, base64-encoded it and sent it to
  IndexedDB in **one** transaction; a read fetched every chunk in one request.
  For 128 MiB that would be more than 170 MB of base64 (twice that as .NET
  strings) in a single JavaScript call.

## Decision

1. **Maximum size.** A file is at most **128 MiB**
   (`DefaultMaxFileSizeBytes = 128 MiB`), the largest extracted size of a
   package: any file a Store package may contain can be installed. Beyond it,
   the write still fails with `FileSystemErrorCode.FileTooLarge` (`EFBIG`,
   `File too large`) without writing anything.

2. **Stored format unchanged.** Chunks stay 256 KiB, keyed by
   `[SHA-256 of the content, index]`, base64-encoded and deduplicated. A
   128 MiB file is 512 chunks. No IndexedDB migration: an existing database
   stays readable, and an earlier version also reads a file larger than 16 MiB
   written by 3.4.0 (it reads every chunk of a hash, with no limit); it only
   refuses to write one.

3. **Batched write.** The content is first read and hashed whole (the chunk key
   is the hash of the whole file), then written in batches of **64 chunks
   (16 MiB)** per transaction (`FileContentStoragePolicy.ChunksPerTransaction`).
   Only the current batch is base64-encoded and crosses the interop. A file of
   at most 16 MiB keeps a single transaction, as before.

   If the write stops between two batches, no entry points to that hash yet:
   the chunks already written are orphans and the cleanup removes them after
   30 days, like any unreferenced chunk. A new write of the same content adds
   the missing chunks (`addIfAbsent`). The file entry is written only after the
   last batch, as today.

4. **Batched read.** A file of at most 64 chunks is read with one query on the
   `contentHash` index, as before. A larger file is read in batches of 64
   `get` on the `[hash, index]` keys. Each chunk is checked (hash, consecutive
   index), then the length and the SHA-256 of the whole content.

5. **What does not change.** The other 16 MiB limits are separate limits and
   stay:
   - the input and result of a `KazoBackground` task (ADR 0128 §4);
   - the first answer of a read from a worker, the rest arrives in chunks
     (ADR 0070);
   - Kazo Paint images and wallpapers (ADR 0103, ADR 0202).

   The sync with the server (ADR 0030) already splits content into 256 KiB
   chunks and has no maximum file size: nothing changes there.

## Consequences

- Kazo Word installs in the published OS from 3.4.0 on; its package requires
  that version at least.
- A 128 MiB file takes about 171 MB in IndexedDB (base64). The browser quota
  and the low-space warning (ADR 0099) still apply.
- Reading a large file still loads it whole in memory (`MemoryStream`), and the
  orphan-chunk cleanup still reads the whole `fsContent` store in one request.
  The file address served on demand (issue
  [#379](https://github.com/Kazo-ca/Os/issues/379)) will handle partial reads
  of large files.
- The SDK message for `FileTooLarge` says "128 MiB at most".
- The public constant `FileContentStoragePolicy.DefaultMaxFileSizeBytes`
  changes value (`PublicAPI.Unshipped.txt`).

## Tasks

- [x] `DefaultMaxFileSizeBytes` at 128 MiB, `ChunksPerTransaction` at 64.
- [x] Batched write and read in `IndexedDbFileContentRepository`.
- [x] Tests: policy, refusal beyond the limit with nothing written, write and
  read of more than one batch, missing chunk refused.
- [x] Docs (FR and EN): SDK, Linux commands, backup, Kazo Paint, indexes,
  `features.md`, memory.
- [ ] Version 3.4.0.
