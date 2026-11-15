> [Version française](../../../../fr/wasm/dev/adr/0275-partial-read-and-file-address.md)

# ADR 0275: partial read and file address for apps

## Status

Proposed on 2026-11-15, `storage/base` series
([plan](../plans/browser-storage-series-plan.md)). This is the ADR that issue
[#379](https://github.com/Kazo-ca/Os/issues/379) asks for (Benoit's decision
of 2026-11-06: an OS API for later), and leads 4 then 5 of the storage
analysis, kept by Benoit on 2026-11-14. §4 (streamed write) gives a direction;
it will be confirmed or corrected from the result of issue
[#538](https://github.com/Kazo-ca/Os/issues/538). No open question: the names
of the API are a proposal to review in the PR.

- **Completes:** [ADR 0245](0245-raise-the-maximum-file-size.md) (whose
  consequences announce this partial read) and
  [ADR 0070](0070-worker-hosted-processes.md) (reading by chunks from a
  worker).
- **Builds on:** [ADR 0272](0272-binary-file-content.md) §4 (a chunk checks by
  itself), [ADR 0274](0274-single-copy-of-app-files.md) §5 (chunk reader of
  the service worker) and
  [ADR 0181](0181-host-folder-mounted-as-a-network-drive.md) (folder of the
  computer mounted as a network drive).
- **Analysis:** [`browser-storage-optimization-analysis.md`](../plans/browser-storage-optimization-analysis.md),
  finding C6, leads 4 and 5.

## Context

- Reading a file loads all of it: the content repository returns a full
  `MemoryStream` and `KazoFile` makes its own copies. Writing keeps the list
  of all the chunks. Issue #520 notes memory going from 1391 to 2588 MiB for a
  52 MB file.
- An app that plays a sound or a video must read the whole file with
  `KazoFile.ReadAllBytesAsync`, then make a `Blob` of it. For a playlist,
  every track stays in memory. Seeking in a track needs the whole file.
- A file of a network drive (ADR 0181) goes through .NET memory while the
  browser can read it without a copy (`FileSystemFileHandle.getFile()`).
- The service worker already answers addresses under the base of the page
  (ADR 0206, 0243), and ADR 0274 teaches it to read the chunks of `fsContent`.

## Decision

### 1. Range read in the content repository

The content repository reads a `[start, length]` range of content: it loads
only the chunks that cover it, in batches, and checks each chunk by its hash
(ADR 0272 §4). Content that still has chunks in the old form is read whole, as
today, until it is converted.

### 2. A read stream that loads on demand

`KazoFile.OpenReadAsync` returns, for a file over 16 MiB (one batch), a stream
that loads the chunks when they are read and keeps only a window of them in
memory. It stays seekable (`Seek`). Under 16 MiB, nothing changes: the file is
read at once. `ReadAllBytesAsync` still returns the whole file; it makes one
copy fewer.

The public API does not change for this point. From a worker, reading already
goes by chunks (ADR 0070).

### 3. File address

An app asks the OS for an address for a file it may read, and gives it to the
browser (`<audio>`, `<video>`, `<img>`, `fetch`).

```csharp
public static class KazoFile
{
    /// Address of the file, valid until the returned object is disposed.
    public static ValueTask<KazoFileUrl> CreateUrlAsync(string path, CancellationToken cancellationToken = default);
}

public sealed class KazoFileUrl : IAsyncDisposable
{
    public string Url { get; }   // for example _kazo/files/<token>/track.mp3
}
```

- **Rights.** `CreateUrlAsync` checks read access to the path with the
  ordinary permissions and capabilities of the app. No new capability: the app
  could already read these bytes.
- **Address.** `_kazo/files/<token>/<name>` under the base of the page. The
  token is random (128 bits) and cannot be guessed; the file name follows so
  the browser sees the extension.
- **Resolved by the page.** The service worker keeps no table: for a request,
  it asks the page of the OS what the token stands for (`postMessage`). The
  page answers from the **current** state of the file, checking the rights
  again:
  - `/` disk: `{contentHash, length, contentType}`; the worker serves the
    chunks with its reader (ADR 0274 §5), with `Range`;
  - network drive: the `File` returned by `getFile()`, which the worker serves
    with `slice`, without a copy;
  - another provider with no partial read: a `Blob` the page read in full.

  A token that is unknown, disposed, or whose app may no longer read the file
  gets `404`. A worker restarted by the browser simply asks again.
- **File changed while it is read.** The answer carries an `ETag` equal to the
  `contentHash`. A `Range` request with `If-Range` for replaced content gets
  the new file in full (`200`), as HTTP wants. An answer in progress goes on
  with the old content, which the orphan retention keeps for at least
  5 minutes ([ADR 0273](0273-short-orphan-content-retention.md)).
- **Lifetime.** The address is valid until `DisposeAsync`. All the addresses
  of an app instance are released when it ends. Nothing is written to the
  database: an address does not outlive the page.
- **Apps in a Web Worker.** `CreateUrlAsync` goes through the main thread,
  like the file service; the address is then usable from the worker.
- **Isolation.** Answers carry `Cross-Origin-Resource-Policy: same-origin` and
  the isolation headers the worker already sets.
- **With no service worker** controlling the page, `CreateUrlAsync` returns a
  `blob:` address after reading the whole file: the app does not have two
  paths to write.

### 4. Streamed write (direction, to confirm after #538)

The key of content is the SHA-256 of the whole file, known only at the end:
chunks cannot be written at their key as they come. Direction kept: the bytes
of a write pile up **on the browser side** (a `Blob` built batch by batch),
outside .NET memory; at close, the browser hashes it all and writes the
chunks. .NET memory then carries only one batch at a time.

The profiling of issue #538 will say whether the memory peak really comes from
there. If not, this paragraph is replaced by a new ADR before the work item.

## Consequences

- Reading the start of a large file, or seeking in a track, no longer loads
  the whole file.
- Webamp, and any app for sound, video or heavy images, can play a file
  without copying it in memory. An issue will be opened in Os-App-Webamp when
  the API exists.
- Craft apps (iframe) can receive an address instead of the bytes to open a
  large document; that change of the `Kazo.Os.AppSdk.Craft` module is not in
  this ADR.
- Additions to the public API: `KazoFile.CreateUrlAsync`, `KazoFileUrl`
  (`PublicAPI.Unshipped.txt`, so a minor version of the SDK).
- Cloud drives (ADR 0267, `cloud-drives/base` series) first go through the
  "another provider" case; a partial read through their API can come later.
- An address gives access to the file to any code of the same origin that
  knows the token, as long as it is not released.

## Rejected options

- **A `blob:` address only**: simple, but the file is read whole in memory,
  which issue #379 wants to avoid.
- **A table of addresses in IndexedDB**, read by the worker: would outlive the
  page, but would need cleaning the addresses of dead sessions and checking
  rights again somewhere other than in the OS.
- **Freeze the content when the address is created**: the content would have
  to be pinned against the cleanup pass; `ETag` and `If-Range` give the right
  behaviour without that.
- **OPFS** for partial reads (0.1 ms): see ADR 0272, rejected options.

## Tasks

- [ ] Range read in `IndexedDbFileContentRepository`, tests.
- [ ] On-demand read stream for files over 16 MiB; memory measured before and
  after.
- [ ] `_kazo/files/` route of the service worker, resolved by the page,
  `Range`, `ETag`, `If-Range`.
- [ ] `KazoFile.CreateUrlAsync`, `KazoFileUrl`, release with the instance;
  apps in a Web Worker; `blob:` fallback.
- [ ] `/` disk and network drives.
- [ ] Streamed write, after #538.
- [ ] `PublicAPI.Unshipped.txt`, SDK compatibility fixture, framework docs (FR
  and EN), `features.md`, change entry.
- [ ] Issue in Os-App-Webamp.
