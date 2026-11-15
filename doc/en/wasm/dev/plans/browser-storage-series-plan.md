> [Version française](../../../../fr/wasm/dev/plans/browser-storage-series-plan.md)

# Browser storage: plan of the `storage/base` series

**Status:** series opened on 2026-11-15, after Benoit's decisions of
2026-11-14 on the [storage analysis](browser-storage-optimization-analysis.md)
(§7). Integration branch `storage/base`, created from `main`. ADRs
[0272](../adr/0272-binary-file-content.md),
[0273](../adr/0273-short-orphan-content-retention.md),
[0274](../adr/0274-single-copy-of-app-files.md) and
[0275](../adr/0275-partial-read-and-file-address.md) are written (work item 0).
**No code has started.** Benoit answered the questions on 2026-11-15 (§6);
none is left.

## 1. Purpose

Make Kazo OS take less space in the browser and read and write its files
faster, without changing what an app sees:

- file content in binary instead of base64 (25% less space, writes about
  8 times faster, reads about 5 times);
- the space of a rewritten or deleted file reclaimed within one day at most, within minutes when space is short;
- a single copy of the files of an installed app (a 64 MiB app goes from
  about 236 MiB to 128 MiB, previous version included);
- reading a part of a large file without loading all of it, and a file
  address the browser reads by itself
  ([#379](https://github.com/Kazo-ca/Os/issues/379)).

## 2. Benoit's decisions (2026-11-14)

| Topic | Decision |
| --- | --- |
| Where to start | Two `TODO` issues outside the series, right away: [#537](https://github.com/Kazo-ca/Os/issues/537) (reclaim space) and [#538](https://github.com/Kazo-ca/Os/issues/538) (profile [#520](https://github.com/Kazo-ca/Os/issues/520)). The rest in the `storage/base` series, ADRs before code. |
| Files of an installed app | The file system stays the only source; the service worker serves `_content/…` from the file store; the copy in Cache Storage goes away. |
| Orphaned content | Short retention; at once when space is low. On 2026-11-15, in the thread of #537: **1 day**, and 5 minutes when space is low. |

## 3. Before the series: two issues with their own thread

| Issue | Content | Link with the series |
| --- | --- | --- |
| [#537](https://github.com/Kazo-ca/Os/issues/537) | Plug the cleanup pass in, make it read keys only, short retention | Applies [ADR 0273](../adr/0273-short-orphan-content-retention.md), written here, and brings it to `main` with its code. The series relies on a pass that runs (work items 1 and 4). |
| [#538](https://github.com/Kazo-ca/Os/issues/538) | Measure a 50 MB save and open in the real OS, layer by layer | Work item 1 waits for its result. It confirms or corrects work item 6 (streamed write), and may change the order of the work items. |

## 4. Work items

One `storage/<n>-<topic>` branch per work item, created from `storage/base`,
and a PR to `storage/base`.

| No. | Branch | Content | ADR | Depends on |
| --- | --- | --- | --- | --- |
| 0 | `storage/0-adrs-and-plan` | This plan and ADRs 0272 to 0275 | | |
| 1 | `storage/1-binary-file-content` | Chunks as `Uint8Array` with their hash, JavaScript module of the content repository (`[JSImport]`, `MemoryView`), SHA-256 by WebCrypto, per-chunk check, database at version 5, **conversion of the old content all at once at the first boot with a waiting screen**, backup and diagnostic snapshot | [0272](../adr/0272-binary-file-content.md) | #538 |
| 2 | (removed) | Merged into work item 1 on 2026-11-15: Benoit chose the one-time conversion at the first boot, which must ship with binary content | | |
| 3 | `storage/3-partial-read` | Range read in the content repository, on-demand read stream for files over 16 MiB | [0275](../adr/0275-partial-read-and-file-address.md) §1, 2 | 1 |
| 4 | `storage/4-single-copy-of-app-files` | `servedFiles` store, publication without copy, chunk reader of the service worker (stream, `Range`), end of the `kazoos-installed-apps` cache | [0274](../adr/0274-single-copy-of-app-files.md) | 1, #537 |
| 5 | `storage/5-file-address` | `KazoFile.CreateUrlAsync`, `_kazo/files/` route, `/` disk and network drives, apps in a Web Worker | [0275](../adr/0275-partial-read-and-file-address.md) §3 | 4 |
| 6 | `storage/6-streamed-write` | Writing without keeping the whole file in .NET memory | [0275](../adr/0275-partial-read-and-file-address.md) §4, to confirm | 1, #538 |
| 7 | `storage/7-docs-and-version` | `features.md`, `browser-infrastructure.md`, memory, change entries checked, API folded, final measurements in Chromium, Firefox and Safari | | all |

**Order and parallel work.** Work item 1 first: it changes the form of the
chunks and converts the old content, so all the others only have the binary
form to read. Then 3 and 4 can move at the same time (they touch the content
repository and the service worker). Work item 5 follows 4, whose chunk reader
it reuses. Work item 6 starts when #538 has given its result.

```text
#537 ──────────────┐
#538 ──► 1 ────────┼──► 3
         │         └──► 4 ──► 5
         └──► 6 (after #538)
                              7 (at the end)
```

**What each work item delivers**, besides the code: its tests (unit tests, and
E2E when the service worker or the published OS is touched), its docs in both
languages, its change entry (`wasm2/KazoOs/changes/unreleased/<guid>.json`),
and a before-and-after measurement when it promises a gain.

## 5. Outside the series

- **ADR 0250** (storage persistence, eviction detection, Settings ▸ Storage
  page): decided, in the `feature/base` series (branch
  `feature/8-storage-persistence-and-eviction`). It stays there. The
  Settings ▸ Storage page can later show the reclaimable space (ADR 0273).
- **Keeping the previous version of an app or not**: unchanged. Two versions
  already share their identical files.
- Leads kept for later, only if a measured need asks for them: per-chunk
  compression (lead 7), per-chunk deduplication (8), OPFS for large objects
  (9), streamed backup (11), lighter caching of the release (12).
- Craft apps receiving a file address instead of the bytes
  (`Kazo.Os.AppSdk.Craft` module): to look at after work item 5 and #538.

## 6. Benoit's answers (2026-11-15)

| No. | Question | Choices | Recommended | Decision |
| --- | --- | --- | --- | --- |
| 1 | How to convert the content already stored in base64? (ADR 0272 §3) | A. in the background, in small batches; B. all at once at the first boot, with a waiting screen; C. never | A | **B** |
| 2 | What to check at each read of a file? (ADR 0272 §4) | A. each chunk by its hash; B. the whole file, as today; C. nothing at read time | A | **A** |
| 3 | How long to keep orphaned content? (ADR 0273 §1, asked in the thread of #537) | 1 day and 5 minutes when space is low; keep 30 days; another duration | 1 day | **1 day**, 5 minutes when space is low |

## 7. Risks

- **First boot after the update.** The OS waits for the end of the
  conversion (ADR 0272 §3): in the order of 20 seconds per GiB, to measure.
  The waiting screen must say what is going on and resume after a close.
- **No way back after work item 1.** The database goes to version 5: a
  previous version of the OS no longer opens it (ADR 0272 §6).
- **Service worker.** Work items 4 and 5 change what the worker serves. The
  known pitfalls (page loaded with Ctrl+F5, worker of another release, app in
  an iframe, origin isolation) each have an E2E test to keep green (ADR 0225,
  0237, 0241, 0243).
- **Measurements made in Chromium only.** Firefox and Safari are measured by
  work item 1 before it is merged.
- **The gain does not fix #520 by itself**: the storage layer accounts for
  only about 3.4 s of 43 s there. What follows depends on #538.

## 8. Rules of the series

- ADRs before code; a work item does not start before work item 0 is merged.
- Green local build and tests before asking for a merge, E2E suites one at a
  time; `build/publish-trimmed.sh` and `TrimmedPublish.E2E` for work items 4
  and 5; SDK compatibility fixture for work item 5.
- One thread per work item. An agent merges its PRs into `storage/base` once
  checks are green; only Benoit decides when `storage/base` goes to `main`.
- No game in this repository ("Aucun jeu sur ce repository").
