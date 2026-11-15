> [Version française](../../../../fr/wasm/dev/plans/large-file-save-profile.md)

# Measurement: saving and opening a large file in the real OS

Measured on 2026-11-14 on `main` (version 3.3.4) for issue
[#538](https://github.com/Kazo-ca/Os/issues/538), which follows
[#520](https://github.com/Kazo-ca/Os/issues/520) ("reading or writing a 50 MB file takes
about 40 s"). **No product code is changed.** This document says where the time goes,
layer by layer, and what to fix.

## 1. Summary

1. **The 40 s of issue #520 come from a host built in Debug.** In the Release publish
   (the one of the site and the Store), saving 52.5 MiB takes **4.1 to 4.9 s** and opening
   it **3.8 to 4.1 s**. The same journey on a Debug host takes 51 to 55 s and 48 to 51 s.
   The tests of Kazo Photo, where the measurement of #520 comes from, start the host in
   Debug.
2. **Almost all the time is in the storage layer**: 96% in Release, 99% in Debug, in
   `IndexedDbFileContentRepository`. The passage between the iframe and .NET and the
   copies of `KazoFile` account for 0.16 s.
3. **Three items make up this time**: the SHA-256 computed in .NET, base64, and the JSON
   passage to IndexedDB. They are exactly the ones the "binary content" work item of the
   `storage/base` series removes. That work item therefore fixes #520 by itself.
4. **Memory rises by about 780 MiB** at the first save of 52.5 MiB and does not come back
   down. The write keeps several whole copies of the file.
5. **`head -c N /dev/urandom` never ends**, whatever the size. It is a bug of the `head`
   command, unrelated to storage.

## 2. Method

- Headless Chromium, 4 cores, fresh profile, random bytes (the worst case: nothing
  compresses or deduplicates).
- **The real OS**: the Release publish of `build/publish-trimmed.sh` (the same as the
  site), then the `OS/Kazo.Os.Ecosystem` host started by `dotnet run -c Debug`.
- **The path of a Craft app**: the test window `Tests/fixtures/craft-app`, installed by
  `kazopkg`, which uses the `Kazo.Os.AppSdk.Craft` module as Kazo Photo does. The fake
  Craft app of the iframe emits a `save` of N bytes; opening goes through `xdg-open`.
- **Temporary timers** in each layer (`craft-host.js`, `CraftFrame`, the window,
  `KazoFile`, `KazoFileSystemCore`, `FileSystemService`, the IndexedDB provider,
  `IndexedDbFileContentRepository`), written to the console with the system time. They
  are removed: nothing of them remains in the repository.
- Memory: sum of the resident memory of all Chromium processes.

The bench (timer patch, Playwright journey, scripts, raw readings) is kept in the project
files, folder `kazofile-profile/`. It is not in the repository.

## 3. Results

### 3.1 End to end

Save: from the app's `save` event to the `saved` it receives. Open: from the `xdg-open`
command to the bytes received by the app (window and iframe included).

| Size | Release, save | Release, open | Debug, save | Debug, open |
| --- | --- | --- | --- | --- |
| 5 MiB | 0.45 to 0.62 s | 0.72 to 0.76 s | 5.3 to 5.4 s | 5.6 to 5.9 s |
| 16 MiB | 1.3 to 1.7 s | 1.5 to 1.6 s | not measured | not measured |
| 52.5 MiB | 4.1 to 4.9 s (4 measurements) | 3.8 to 4.1 s (4 measurements) | 51 to 55 s | 48 to 51 s |

Time follows size: about 80 to 90 ms per MiB in Release, 1 s per MiB in Debug. Debug is
about 12 times slower.

### 3.2 Saving 52.5 MiB, layer by layer

| Layer | Release | Debug |
| --- | --- | --- |
| Iframe to .NET (`craft-host.js`, Blazor's `byte[]`) | 0.08 s | 0.03 s |
| `KazoFile.WriteAllBytesAsync`: copy of the array, `stat` | 0.08 s | 0.14 s |
| Gateway, permissions, path resolution | 0.01 s | 0.05 s |
| Splitting into 256 KiB chunks | 0.08 s | 0.14 s |
| SHA-256 of the file, in .NET | 1.5 s | 20 s |
| Base64 of the chunks | 0.35 s | 7.6 s |
| JavaScript call in JSON and IndexedDB transaction (4 batches) | 2.0 s | 23 s |
| Metadata, change event, return to the app | 0.01 s | 0.02 s |
| **Total** | **4.1 s** | **51 s** |

### 3.3 Opening 52.5 MiB, layer by layer

| Layer | Release | Debug |
| --- | --- | --- |
| Starting the window, loading the iframe | 0.35 s | 0.7 s |
| JavaScript call, IndexedDB and JSON reading (4 batches) | 0.95 s | 13 s |
| Base64 decoding and buffer | 0.69 s | 17 s |
| Verification SHA-256, in .NET | 1.6 s | 20 s |
| Copies of `KazoFile.ReadAllBytesAsync` | 0.12 s | 0.09 s |
| .NET to the iframe (`open`) | 0.08 s | 0.12 s |
| **Total** | **3.8 s** | **51 s** |

### 3.4 Memory

Release, memory of all Chromium processes, with one Craft window open:

| Moment | Memory |
| --- | --- |
| Before | 1,930 MiB |
| Peak of the first save of 52.5 MiB | 2,708 MiB |
| Peak of the second | 2,833 MiB |
| Peak of the second open | 3,061 MiB |

The first save adds about 780 MiB, 15 times the file. WebAssembly memory is not given
back to the system: what was reached stays. The whole copies seen in the code during a
write:

- the `byte[]` received from the iframe;
- the copy of `KazoFile.WriteAllBytesAsync` (`bytes.Clone()`);
- the list of all chunks (`IndexedDbFileContentRepository.WriteAsync`);
- the buffer of `IncrementalHash`: the whole hashing time falls in the final call, which
  indicates the bytes are kept until then;
- per 16 MiB batch: the base64 strings (2 bytes per character in .NET, so 43 MiB), their
  JSON, then the same strings on the JavaScript side.

### 3.5 `/proc/diskstats`

Issue #538 proposed comparing the total time with the transaction time of
`/proc/diskstats`. That counter cannot serve as a layer timer: its time includes the JSON
serialization of the call, and it adds up the transactions running at the same time. An
open of 3.8 s counts 5.3 s of reading there. The timers of §3.2 and §3.3 replace that
comparison.

### 3.6 `head -c 1048576 /dev/urandom > a.bin`

`head -c 16 /dev/urandom` does not end either: after 45 s, nothing is written and memory
rose by 800 MiB. `head -c 16 /etc/hostname` ends in 0.5 s.

Cause: `HeadCommand` reads the **whole** file (`CommandIO.ReadTextAsync`, so
`ReadToEndAsync`), then cuts the text. `/dev/urandom`, `/dev/random` and `/dev/zero` are
endless: the read never stops and memory rises until the tab stops. Yet the documentation
of `DevFileSystemProvider` gives `head -c 16 /dev/urandom` as an example. Two other limits
of the same code: `-c` counts characters and not bytes, and the output goes through text,
so it does not produce a faithful binary file.

This bug belongs to the terminal. It touches neither `KazoFile` nor IndexedDB.

## 4. What it changes

- **Issue #520 describes a development cost, not a production one.** A Store app runs in
  the Release publish: about 4 s for 50 MB, plus the work of the app itself (2.8 s for
  PhotoCraft). In Debug, every Craft app developer waits 50 s: it is still worth fixing,
  for tests and comfort.
- **The storage analysis said 90% of the time was elsewhere.** It compared its Release
  bench (3.4 s) with the Debug measurement of #520 (43 s). Measured in the same
  configuration, storage is almost all the time. The sentence is to be corrected in the
  analysis ([PR #536](https://github.com/Kazo-ca/Os/pull/536), §1 point 4 and §3.4), in
  ADR 0272 and in the series plan ([PR #539](https://github.com/Kazo-ca/Os/pull/539)).
- **Nothing to change in `craft-host.js`, `CraftFrame` or `KazoFile` for speed**: 0.16 s
  out of 4.1 s.

## 5. Proposals

1. **Start the "binary content" work item of `storage/base` (ADR 0272) as the fix of
   #520.** It removes the managed SHA-256 (WebCrypto: 0.09 s), base64 and JSON (bytes
   passed by `[JSImport]` and `MemoryView`). The analysis measured the storage layer at
   0.40 s for a write and 0.36 s for a read with that path. End-to-end estimate for
   52.5 MiB in Release: about 0.6 s instead of 4.1 s. The gain in Debug should be even
   larger, because the work moves from the .NET interpreter to the browser; to be
   measured when the work item is done.
2. **Keep the "streamed write" work item** (ADR 0275 §4). The memory peak is confirmed.
   Work item 1 already removes the base64 and JSON copies; the `Clone` of `KazoFile`, the
   list of chunks and the hash buffer remain.
3. **Measure performance on the Release publish.** A measurement taken on a Debug host is
   about 12 times too pessimistic for everything that computes in .NET. The journeys of
   the app repositories (`tests/e2e/host.mjs`) start the host in Debug by default: for a
   performance figure, pass the Release configuration or a published OS. The pitfall is
   added to [`../pitfalls.md`](../pitfalls.md).
4. **Fix `head` on an endless file** in a separate issue: read at most N bytes of the
   stream for `-c N`, and stop after N lines for `-n N` on a file, as is already done for
   standard input. Question asked to Benoit.

## 6. Limits

- One machine, one browser (headless Chromium). Proportions matter more than absolute
  values.
- The fake Craft app does no work on the bytes. The time of a real app (reading or
  producing a `.psd`) comes on top.
- The measurement of #520 was not redone with Kazo Photo itself: its script is not
  versioned. The cause rests on two facts: its harness starts the host in Debug by
  default, and the same journey on the Debug host of Os gives durations of the same order.
- The estimate of point 1 of §5 adds two different measurements (this document and the
  bench of the analysis); it is not a measurement.

## 7. Links

- Issues [#538](https://github.com/Kazo-ca/Os/issues/538),
  [#520](https://github.com/Kazo-ca/Os/issues/520),
  [#379](https://github.com/Kazo-ca/Os/issues/379)
- Storage analysis: [PR #536](https://github.com/Kazo-ca/Os/pull/536); plan of the
  `storage/base` series and ADRs 0272 to 0275: [PR #539](https://github.com/Kazo-ca/Os/pull/539)
- ADR [0245](../adr/0245-raise-the-maximum-file-size.md) (maximum file size, 16 MiB
  batches), [0259](../adr/0259-shared-craft-app-host-module.md) (module of the Craft apps)
- [`../browser-infrastructure.md`](../browser-infrastructure.md),
  [`../pitfalls.md`](../pitfalls.md)

## Tasks of this document

- [x] Measure a save and an open of 50 MB in the real OS, layer by layer.
- [x] Compare with `/proc/diskstats`.
- [x] Measure memory.
- [x] Find why `head -c … /dev/urandom` does not end.
- [x] Propose the fix.
- [ ] Redo the measurement after the "binary content" work item.
