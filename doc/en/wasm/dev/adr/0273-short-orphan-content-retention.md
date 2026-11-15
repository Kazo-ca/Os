> [Version française](../../../../fr/wasm/dev/adr/0273-short-orphan-content-retention.md)

# ADR 0273: short retention of orphaned content

## Status

Proposed on 2026-11-15. On 2026-11-14 Benoit decided that retention is short
(a few hours to a day) and that orphaned content goes at once when space is
low; this ADR sets the values and the safety rules. One question is still
asked to Benoit (at the end); the decision applies its recommendation in the
meantime. This is the ADR that issue
[#537](https://github.com/Kazo-ca/Os/issues/537) asks for: the code of #537
applies it and brings it to `main` with it.

- **Supersedes:** in point 2 of [ADR 0094](0094-deferred-decisions-status-and-maximum-file-size.md),
  the 30-day retention only ("a chunk that is no longer referenced is kept
  30 days before it is deleted"). The analysis and issue #537 name ADR 0099:
  the value is in fact in ADR 0094; ADR 0099 only defines low space.
- **Builds on:** [ADR 0099](0099-os-backup-and-low-storage.md) (low-space
  thresholds) and [ADR 0018](0018-indexeddb-failure-and-recovery-policy.md).
- **Analysis:** [`browser-storage-optimization-analysis.md`](../plans/browser-storage-optimization-analysis.md),
  findings C1, C2, C3 and C10, lead 1.

## Context

- Content is **orphaned** when no file entry (`fsEntries`) carries its
  `contentHash` any more: the file was rewritten or deleted for good (a file
  in the trash keeps its entry, so its content).
- The cleanup pass exists but no host calls it, and it starts by loading all
  of `fsContent` in memory. On the published site, every boot adds orphans.
- Retention does not serve the user: nothing in the OS can bring orphaned
  content back. It only protects a **write in progress**, whose chunks are
  already written while the file entry is not yet (ADR 0245 §3), including
  from another tab.
- With 30 days, a 50 MB document saved ten times takes 500 MB for a month.

## Decision

### 1. Durations

| Situation | Orphaned content is deleted after |
| --- | --- |
| Normal space | **6 hours** |
| Low space (thresholds of ADR 0099) | **5 minutes** |

- `FileContentStoragePolicy.DefaultOrphanRetention` becomes 6 hours; a second
  value, `DefaultLowSpaceOrphanRetention`, is 5 minutes.
- The 5 minutes are not a wanted wait: they are the margin that lets a write
  in progress finish. "At once" means the pass starts as soon as space gets
  low, without waiting for the next hour.

### 2. Age of content

- The age of content is the `createdUtcMs` timestamp of its **first chunk**.
  The pass therefore reads only one chunk per candidate content, never the
  whole store.
- A write that finds content already present (`addIfAbsent` rewrites nothing)
  **refreshes the timestamp of the first chunk**. Without this, saving again
  content orphaned for 5 h 59 would leave it one minute from deletion before
  its entry is written.

### 3. Two guarantees, whatever the duration

1. **Deletion.** The pass checks again, in the transaction that deletes, that
   no entry references the content (already the case:
   `deleteAllByIndexIfUnreferenced`).
2. **Write.** The transaction that writes a file entry checks that all the
   chunks of its content are present. If some are missing, the write rewrites
   the missing chunks and tries once more, then fails with an I/O error.

With these two checks, an entry cannot point to deleted content, even if the
clock jumps or two tabs work together. The retention duration is then only a
comfort: it avoids rewriting.

### 4. When the pass runs

- After boot, in the background, once the desktop is shown.
- Every hour while the OS runs.
- As soon as space gets low (`StorageSpaceMonitor`), with the 5-minute
  duration.
- After the trash is emptied.

A pass handles a bounded number of contents (64 today) and runs again as long
as it finds some. It reads only keys, plus the first chunk of each candidate.
It does not run when the tab is hidden, and only one tab runs it at a time.

### 5. Files the system rewrites itself

Logs (`LogFileWriter`) and settings are rewritten whole, often. When the
system rewrites one of its files, it deletes the old content **in the same
operation**, if it is no longer referenced, without waiting for the pass.
Guarantee 2 of §3 covers the case where another file was about to reuse that
content.

### 6. Other references

Content is referenced by a file entry or by a served address of an installed
app ([ADR 0274](0274-single-copy-of-app-files.md) §4). The pass checks both.

## Consequences

- The space of a rewritten or deleted file comes back in 6 hours at most, in
  a few minutes when space is short. The case of 500 MB for a month is gone.
- The chunks left by an interrupted write go after the same duration, instead
  of 30 days (ADR 0245 §3).
- No format change: `createdUtcMs` already exists. An old chunk without a
  timestamp is treated as old enough, since guarantee 1 protects it when it is
  referenced.
- The cleanup pass becomes the place of the conversion to binary
  ([ADR 0272](0272-binary-file-content.md) §3).
- The Settings ▸ Storage page of ADR 0250 (`feature/base` series) can later
  show the reclaimable space and start the pass; that is not in this ADR.

## Rejected options

- **No retention** (delete at rewrite, always): a write in progress in another
  tab would rewrite its chunks every time; guarantee 2 would make the result
  correct but slow.
- **Keep 30 days and only plug the pass in**: finding C3 remains.
- **Count references per chunk**: useful for per-chunk deduplication
  (analysis, lead 8), not needed here.

## Question asked to Benoit

1. **How long should orphaned content be kept when space is not short?**
   A. 1 hour; B. 6 hours (recommended); C. 24 hours. In all three cases,
   5 minutes when space is low.

## Tasks

Done by issue #537:

- [ ] A pass that reads only the keys and the first chunk of the candidates.
- [ ] Durations of 6 hours and 5 minutes in `FileContentStoragePolicy`.
- [ ] Refresh the timestamp of the first chunk when present content is
  written again.
- [ ] Check that the chunks are present in the transaction of the entry.
- [ ] Triggers: boot, every hour, low space, trash emptied.
- [ ] Immediate deletion for files rewritten by the system.
- [ ] Tests: a file rewritten several times does not make `fsContent` grow; a
  write in progress during a pass; two tabs.
- [ ] Docs (FR and EN): `browser-infrastructure.md`, memory, change entry.
