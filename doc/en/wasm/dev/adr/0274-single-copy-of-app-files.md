> [Version française](../../../../fr/wasm/dev/adr/0274-single-copy-of-app-files.md)

# ADR 0274: a single copy of the files of an installed app

## Status

Proposed on 2026-11-15, `storage/base` series
([plan](../plans/browser-storage-series-plan.md)). On 2026-11-14 Benoit chose
option A of the storage analysis (§7, decision 2): the file system stays the
only source, the service worker serves `_content/…` from the file store and
the copy in Cache Storage goes away. This ADR says how. No open question.

- **Supersedes:** in [ADR 0206](0206-serve-installed-app-assets.md), the copy
  of the files into the `kazoos-installed-apps` cache; in
  [ADR 0225](0225-serve-installed-app-files-to-every-page.md) §2, the
  housekeeping of that cache. The `_content/<assembly>/<path>` addresses, the
  worker taking control of the page (ADR 0225 §1, 0237, 0241) and the page of
  an app in an iframe (ADR 0243) do not change.
- **Builds on:** [ADR 0098](0098-sdk-versions-packages-and-manifest-checks.md)
  §5 (SHA-256 of each file in the manifest),
  [ADR 0221](0221-pack-all-static-web-assets-of-an-app.md) (all static assets
  in the package), [ADR 0272](0272-binary-file-content.md) (form of the
  chunks) and [ADR 0273](0273-short-orphan-content-retention.md) (references
  of content).
- **Analysis:** [`browser-storage-optimization-analysis.md`](../plans/browser-storage-optimization-analysis.md),
  finding C8, lead 6.

## Context

- An installed app has its files in
  `/usr/lib/kazoos/apps/<appId>/<version>/` (or under
  `~/.local/share/kazoos/apps/`). The installer keeps the current version and
  the previous one.
- At the first launch in each session, `InstalledAppAssetPublisher` reads
  again every `wwwroot` file declared in the manifest, checks its SHA-256, and
  **copies it** into the `kazoos-installed-apps` cache of Cache Storage. The
  service worker (`installedAppAssets.sw.js`) answers the
  `_content/<assembly>/<path>` addresses from that cache.
- A 64 MiB app therefore takes about 236 MiB: two versions in base64 in the
  file system, plus the copy in the cache. The copy is made again in every
  session (reading, checking and writing all the bytes).
- The content of a file is addressed by the SHA-256 of the whole file
  (`contentHash`): the same value as the SHA-256 of the manifest.

## Decision

### 1. The file system is the only source

The bytes of an app file live only in `fsContent`. The `kazoos-installed-apps`
cache no longer receives any file; it is deleted at the first boot of the
release that brings this ADR.

### 2. A table of served addresses

A new `servedFiles` store of the `kazoos` database (version 5, ADR 0272 §6)
maps each served address to content:

```text
{ url, contentHash, length, contentType, appId }
```

- Key: `url`, relative to the base of the page (`_content/Kazo.Sample/x.js`).
- Indexes: `contentHash` (for the cleanup pass) and `appId`.
- The table is shared by every tab, as the cache was.

### 3. Publishing an app no longer writes bytes

At the first launch of an app in the session, `InstalledAppAssetPublisher`
writes one row of the table for each declared file, **without reading the
content**: it compares the `contentHash` of the file entry with the SHA-256 of
the manifest. If they differ, or if the file is missing, the address is not
served and a line goes to `/var/log/syslog`, as today for a damaged file. The
first publication also removes the rows no installed app declares any more
(rule of ADR 0225 §2, unchanged).

The check stays as strong: the content served is the one whose hash is in the
manifest, since content is addressed by that hash.

### 4. A served address is a reference

The cleanup pass (ADR 0273 §6) does not delete content that a `servedFiles`
row still carries. An app removed or updated while another window runs it
therefore keeps its files until the next publication, which removes its rows;
the content then becomes orphaned and follows the ordinary retention.

### 5. The service worker reads the file store

For a `GET` request under `_content/`:

1. the worker looks the address up in `servedFiles`; with no row, the request
   goes to the network, as today (files of built-in apps);
2. it answers with a stream (`ReadableStream`) that reads the
   `[contentHash, number]` chunks of `fsContent` in batches, without loading
   the whole file;
3. headers: `Content-Type` and `Content-Length` from the table,
   `Cross-Origin-Resource-Policy: same-origin`, `Cache-Control: no-cache`,
   `ETag` equal to the `contentHash`, and the isolation headers of the page in
   "installed apps only" mode (unchanged);
4. a `Range` request gets a `206` answer with only the chunks asked for. This
   reader is also the one of the file address
   ([ADR 0275](0275-partial-read-and-file-address.md) §3).

The worker reads both chunk forms (base64 and binary, ADR 0272 §3).

**Database connection.** The worker opens `kazoos` with no version number, and
closes its connection on `versionchange` and after each answer: a connection
kept open by the worker would block the upgrade of the database by the page
(`onblocked` makes the open fail).

**Worker version.** A worker from before this ADR serves from the cache, which
the page no longer fills. Publication therefore asks, as today, that the
worker controlling the page be the one of its release (`prepare`, ADR 0225 §1
and 0237); otherwise it tries again at the next launch.

### 6. What does not change

- The addresses, and so the apps: no app has anything to change.
- The previous version of an app stays kept by the installer. Two versions
  already share the content of their identical files (per-file deduplication).
- The link of the app's scoped style sheet (ADR 0221 §2).
- Permissions: the worker only serves the addresses of the table, which only
  publication writes, for the files declared by the manifest of an installed
  app. This is what the cache already exposed.

## Consequences

- A 64 MiB app takes 128 MiB with the previous version (64 MiB without),
  instead of about 236 MiB, once content is binary.
- The first launch of an app in the session no longer copies anything: a
  60 MB app starts without reading and writing its 60 MB again.
- Serving from IndexedDB is a little slower than from Cache Storage (0.19 s
  against 0.08 s to read 52 MiB in binary, 0.56 s as long as content is in
  base64). The work item measures the start of a Craft app before and after.
- Editing a file of an installed app by hand stops it being served at the next
  session (its hash no longer matches the manifest), as today.
- The service worker gains code that reads IndexedDB; it is shared by the two
  service-worker files of the host, like `installedAppAssets.sw.js`.

## Rejected options

- **B. Served files only in Cache Storage**, without extracting them into the
  file system: rejected by Benoit; an app would no longer be a folder of files
  as on a real machine, and `ls`, backup and sync would no longer see it.
- **Keep the copy but make it only once** (not in every session): saves time,
  not space.
- **A table in Cache Storage** rather than in IndexedDB: no store to add, but
  the table would not be in the same transaction as the files and the cleanup
  pass could not check it while deleting.

## Tasks

- [ ] `servedFiles` store (database at version 5), repository, tests.
- [ ] `InstalledAppAssetPublisher`: rows instead of copies, hash comparison,
  housekeeping of the rows.
- [ ] Chunk reader of the service worker (stream, `Range`, both forms), shared
  by the two service-worker files.
- [ ] The cleanup pass counts `servedFiles` as a reference.
- [ ] Removal of the `kazoos-installed-apps` cache and of
  `IInstalledAppAssetCache.PutAsync`.
- [ ] E2E tests: app installed in the published OS (`TrimmedPublish.E2E`),
  page loaded with Ctrl+F5, app in an iframe, worker of an app, two tabs.
- [ ] Measurement of the start of a Craft app before and after.
- [ ] Docs (FR and EN): `browser-infrastructure.md`, app packages, memory,
  `features.md`, change entry.
