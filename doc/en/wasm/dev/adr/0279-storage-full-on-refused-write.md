> [Version française](../../../../fr/wasm/dev/adr/0279-storage-full-on-refused-write.md)

# ADR 0279: a write refused by the browser reports full storage

## Status

Accepted on 2026-11-16: Benoit chose "React to refused writes" (issue #493,
series `known-failures/base`).

- **Completes:** [ADR 0099](0099-os-backup-and-low-storage.md) §2 (low space
  and no space left). The measure by `navigator.storage.estimate()` stays; a
  refused write becomes a second signal.
- **Builds on:** [ADR 0273](0273-short-orphan-content-retention.md) (short retention
  of orphan content when space is low).

## Context

ADR 0099's "Low storage space" warning comes from
`navigator.storage.estimate()`. Since Chromium 153 (2026-11-16, Playwright
1243), that function no longer gives the real quota: it rounds it (4 GiB, then
3 GiB after a reload), even when the real quota is only 500 KB. With
`Storage.overrideQuotaForOrigin`, the DevTools protocol
(`Storage.getUsageAndQuota`) does see 500 KB, and IndexedDB does refuse a 2 MB
write with `QuotaExceededError`, but `estimate()` never goes down.

So the OS can no longer see the space go down: the warning never comes, and
`LowStorageSpaceTests` fails every time. The only reliable signal is the one a
real full disk gives: the refused write.

Benoit had three choices: react to refused writes (recommended), keep the
estimate alone (the test stays a known failure), or fake the estimate in the
test (the UI is tested, but the warning still does not work in a real Chrome).
He chose the first.

## Decision

1. **Signal.** When the persistent file provider answers `NoSpace` (a browser
   `QuotaExceededError`, ADR 0099 §2), `FileSystemService` calls
   `IStorageSpaceGuard.ReportWriteRefused()` instead of `RequestRefresh()`. A
   refusal by the root reserve (`CheckReservedSpace`) is not a browser refusal
   and does not call it.
2. **Contract.** `ReportWriteRefused()` is a default member of
   `IStorageSpaceGuard` that calls `RequestRefresh()`: existing implementations
   stay valid.
3. **Monitor.** `StorageSpaceMonitor.ReportWriteRefused()`:
   - holds the space as low for `RefusalHoldTime` (10 minutes), whatever the
     measure says: `IsLowSpace` stays true and ADR 0273's short retention
     applies;
   - first reclaims the orphan content (short retention), as for a low measure;
   - posts the usual warning, once per session and user; with no reliable
     measure, the message says the browser refused to save data
     (`Storage.Full.Message`) instead of giving the free MiB.
4. **Root reserve.** It is still computed from the measure. A refused write
   does not turn it on: the real limit is already reached, the reserve no
   longer protects anything.
5. **Test.** `LowStorageSpaceTests` sets the quota to 8 MiB before the first
   navigation (Chromium does not enforce a quota set after the origin first
   opened its storage), fills it with random bytes, then writes a file from the
   terminal: the write is refused ("No space left on device") and the warning
   must come. The rest of the test checks that
   the administrator still signs in again.

## Consequences

- The warning works again in Chromium, but when a write fails, not before:
  there is no early alert at 10 % free while Chromium rounds `estimate()`.
- If a browser gives a real quota again, the measure warns before the refusal,
  as before.
- `IStorageSpaceGuard` gains a public member (`PublicAPI.Unshipped.txt`).
