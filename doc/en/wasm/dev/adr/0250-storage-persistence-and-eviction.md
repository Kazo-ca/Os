> [Version française](../../../../fr/wasm/dev/adr/0250-storage-persistence-and-eviction.md)

# ADR 0250: browser storage persistence and eviction (`navigator.storage.persist()`, quota, warning)

## Status

Proposed on 2026-11-11, for the Os-x86 feature
([Kazo-ca/Os-x86](https://github.com/Kazo-ca/Os-x86)), lot 1 of the
`feature/base` series. The questions asked to Benoit got their answers on
2026-11-11 (series 15 of the Os-x86 design); they are recorded at the end of
this ADR and the decision applies them. Source decisions: 149 (disk images in
OPFS, small images in IndexedDB) and 177 A ("`persist()` requested after the
first gesture in BoxedWine or `x86`; eviction detected = warning and export
offered; the disks count in the browser's space, shown"), 2026-11-11; Os-x86
trials E2 and E10 (OPFS 55 to 185 times faster per block than IndexedDB;
Firefox limits to 10 GiB without `persist()`; Safari erases after 7 days
without interaction).

- **Completes:** [ADR 0015](0015-browser-storage-and-indexeddb-adapter.md)
  (storage baseline; `estimate()` and `persist()` named as prerequisites),
  [ADR 0099](0099-os-backup-and-low-storage.md) (low space) and
  [ADR 0227](0227-factory-reset-erases-everything.md) (OPFS erased at the
  factory reset).
- **For:** the Os-x86 VM disks (up to several GiB in OPFS, outside Kazo's
  Files which refuse any file over 16 MiB, `FileContentStoragePolicy.cs:15`);
  and Kazo OS's own data (IndexedDB), which the browser can evict just as well.

## Context

- Kazo OS already reads `navigator.storage.estimate()` and `persisted()`
  (`Infrastructure/Kazo.Os.Infrastructure.Browser/wwwroot/storageManager.js:4-5`,
  `Storage/BrowserStorageManager.cs:33-49 GetStatusAsync`, :52-64
  `GetCapacityAsync`) at every capacity probe. `persist()` has a wrapper
  (`storageManager.js:17`, `RequestPersistenceAsync` :67-71) but **no
  caller** outside the tests; `IsPersisted` is read nowhere; **nothing
  detects an eviction**.
- The quota is **the whole origin's**: `StorageSpaceMonitor.cs:123-135`
  measures usage and quota for all of IndexedDB, Cache Storage and OPFS; the
  5 % reserve (256 MiB at most, `StorageSpaceContracts.cs:55-70`) refuses the
  writes of non-administrator users when reached
  (`FileSystemService.cs:828-848`). A 4 GiB VM disk in OPFS therefore shrinks
  the space Kazo believes it has for its Files, without Settings showing it.
- Browsers: Chromium grants ~60 % of the disk, Firefox the smaller of 10 % or
  10 GiB (50 % with `persist()`, which shows a prompt), Safari 60 %;
  `persist()` is only accepted on a user gesture and, in Chromium, by site
  engagement (installed PWA, bookmark); Safari may erase after 7 days without
  interaction; Chrome "Memory Saver" and quota eviction emit no event.
- OPFS belongs today to the Features (Kazo only uses it to erase it,
  `hostFactoryReset.js:59-71`, and as a test double, `hostFolder.js:1-15`);
  a Feature cannot call `navigator.storage` itself without JavaScript of its
  own, which AGENTS.md limits to the APIs C# cannot reach, and two concurrent
  `persist()` calls (OS and Feature) are useless.

## Decision

### 1. An SDK API: `KazoStorage`

```csharp
public static class KazoStorage
{
    public static ValueTask<KazoStorageEstimate> GetEstimateAsync(CancellationToken ct = default); // usage, quota, persisted, per origin
    public static ValueTask<bool> RequestPersistenceAsync(CancellationToken ct = default);          // persist(), on a gesture
    public static event EventHandler<KazoStorageChangedEventArgs>? Changed;
}
```

- `GetEstimateAsync`: `Usage`, `Quota`, `IsPersisted`, `IsLowSpace`
  (thresholds of ADR 0099), and the share attributed to the calling app when
  the OS knows it (§3). Permission: none (read-only, already given by `df`).
- `RequestPersistenceAsync`: calls `navigator.storage.persist()` **once per
  origin** (the OS keeps the result and does not ask again when `persisted()`
  is already true); returns true when granted. Reserved to **Features and
  system apps** (`manifest.feature.only` otherwise); a Feature calls it
  **after a user gesture** (first `x86 vm create`, first image download), as
  177 A asks. A call without a gesture returns false without an error (the
  browser refuses).
- The OS itself does not request persistence at boot (question 1): it offers
  it in Settings ▸ System ▸ Storage (§3) through a button "Protect the storage
  against erasure", which is a gesture.

### 2. Eviction detection

- The OS writes at every successful boot a **marker** in two places:
  `localStorage` (`kazoos.storage.marker`, already the place of
  `kazoos.osVersion`, ADR 0230) and an IndexedDB document. At the next boot,
  when the `localStorage` marker exists but the IndexedDB database is absent
  or empty (`StorageSpaceMonitor`, before the OS opens its databases), the OS
  declares an **eviction**: screen "The browser erased Kazo OS's data" on the
  detailed error page (ADR 0240), with the date of the last boot and a link
  to restoring a backup (ADR 0099), then goes on like a first installation.
  `syslog` and `kern.log` get `storage.evicted`.
- For a Feature's OPFS, the OS does not know what it kept there: the Feature
  writes **its** marker (an OPFS file listed in an OS IndexedDB document,
  `KazoStorage.RegisterMarkerAsync(path)`) and the OS, at the same check,
  compares: file absent → event `KazoStorage.Changed(Evicted, feature)` to
  the Feature at its next launch and a **notification** to the user ("The
  Os-x86 disks were erased by the browser"); the Feature offers the export of
  what is left (177 A).
- `IsPersisted` false after having been true (browser that withdraws the
  persistence): warning in Settings ▸ Storage, no screen.

### 3. Settings ▸ System ▸ Storage

- New page (or section of the existing "Memory" page, question 2): usage and
  quota of the origin, "protected" state (`persisted()`) with the button of
  §1, breakdown **by data origin**: Kazo's Files (IndexedDB), OS caches, and
  **per Feature** the space of its OPFS (the Feature declares it through
  `KazoStorage.ReportUsageAsync(bytes)`, which the OS remembers; without a
  declaration, "unknown"). One button per Feature opens its page (ADR 0249)
  to free space.
- The 5 % reserve (`FileSystemService.CheckReservedSpace`) keeps counting the
  whole origin: the page explains that the features' disks count in the
  Files' space. No per-Feature quota in V1 (Os-x86's V2).
- `df` (`IndexedDbStorageUsageProvider.cs:31-41`) adds one line per Feature
  that declared its usage (`/dev/feature/<id>`).

### 4. What does not change

- `estimate()` and `persisted()` stay probed as today; the factory reset
  still erases OPFS (ADR 0227); the Kazo backup (ADR 0099) does not include
  OPFS (the Os-x86 export is its own).

## Rejected options

- **A. `persist()` at OS boot for everyone**: refused without a gesture by
  the browsers; in Firefox, a prompt for every visitor of an OS that may have
  nothing to protect.
- **B. Let each Feature call `navigator.storage` in JavaScript**: two
  concurrent requests, no overview in Settings, and JavaScript where a C# API
  already exists (`BrowserStorageManager`).
- **C. Detect nothing, the user will notice**: a 4 GiB disk that vanishes
  without a word is exactly the risk 177 A refuses.
- **D. A per-Feature quota right now**: would require the OS to measure the
  OPFS (costly recursive walk); Os-x86's V2.

## Consequences

- SDK: `KazoStorage` (`GetEstimateAsync`, `RequestPersistenceAsync`,
  `RegisterMarkerAsync`, `ReportUsageAsync`, `Changed`), `KazoStorageEstimate`;
  `PublicAPI.Unshipped.txt`, `sdk-compat` fixture.
- Infrastructure: `BrowserStorageManager.RequestPersistenceAsync` finally
  called; eviction marker (`StorageEvictionDetector`, before the databases
  open); `storage.evicted` error page; notification; `kern.log`/`syslog`.
- Settings ▸ System ▸ Storage; `df`; docs
  [`system-administration.md`](../../framework/system-administration.md)
  (Storage section), [`pitfalls.md`](../pitfalls.md) (eviction),
  [`building-and-testing.md`](../building-and-testing.md) (tests under
  Firefox and Safari), `features.md`, `changes/unreleased` entry.
- Tests: `RequestPersistenceAsync` calls `persist()` once; ordinary app →
  exception; marker present and database absent → screen and log; Feature
  marker absent → `Changed(Evicted)` and notification; the storage page shows
  the declared usage; Playwright E2E: eviction simulated by erasing IndexedDB
  between two boots.

## Questions asked to Benoit

1. **Who triggers `persist()`?**
   - A) **(Recommended)** A Feature on a user gesture (177 A) and the
     Settings ▸ Storage button; never the OS alone at boot.
   - B) The OS too, at the first gesture of each session (first click):
     protects everyone's Kazo Files, but triggers the Firefox prompt for
     everyone.
2. **Where does the storage display live?**
   - A) **(Recommended)** A "Storage" page under System, next to "Memory".
   - B) A section of the existing "Memory" page.

Maintainer's answers (2026-11-11): 1 A, 2 A.
