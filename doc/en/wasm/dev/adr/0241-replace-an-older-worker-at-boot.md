> [Version française](../../../../fr/wasm/dev/adr/0241-replace-an-older-worker-at-boot.md)

# ADR 0241: replace an older service worker at boot

## Status

Accepted on 2026-11-09, on Benoit's report in the thread "Réparer le plantage
d'IRC sans service worker": "when I press F5, I am on 3.2.0, but with Ctrl+F5 I
am on 3.2.1. Then, with F5, I go back to 3.2.0 (the first version seen in this
browser on this domain)". Branch `service-worker/replace-older-worker-at-boot`.

- **Completes:** [ADR 0237](0237-activate-the-worker-of-a-newer-page.md)
  (activate the worker of a newer page, when an installed app is launched) and
  [ADR 0239](0239-start-on-the-latest-version.md) (start on the latest version).

## Context

The service worker of a version serves every file of that version, page
included. When a new version is deployed, the browser downloads its worker,
which stays **waiting**: it only activates once no tab or window of the site is
open any more (or on "Restart to update", ADR 0223). F5 does not release the
tab: the old worker keeps control and serves the old version again, for as long
as the user keeps a tab open.

ADR 0239 fixes this from the versions that contain it: the page served by the
old worker reads `version.json` at boot and restarts onto the new version. But
a page of an older version (3.2.0) does not have that code. Ctrl+F5 bypasses the
old worker and loads the latest version, yet nothing replaces that worker:
ADR 0237 only does it when an installed app is launched. The next F5 therefore
falls back to the old version.

## Decision

1. **When the OS starts**, once the ADR 0239 check has passed (the page runs
   the latest version), the host calls
   `BrowserInstalledAppAssets.AdoptAtBootAsync` in the background: the boot does
   not wait for it.
2. **Page not controlled while a worker is active** (Ctrl+F5): the page asks
   the active worker to take it (ADR 0225). If it caches another version, the
   worker of the page's version is activated (ADR 0237); the tabs of the old
   version restart onto it. The next F5 stays on the latest version.
3. **Nothing to do** on a development host (no version cache), on a first
   visit (`index.html` registers the worker, which takes the page when it
   activates) or when a worker already controls the page.
4. **Failure**: it is logged (`boot: the service worker of this version could
   not take this page`) and nothing is shown; "Restart to update" stays
   available.

## Consequences

- A single Ctrl+F5 is enough to leave an old version, even when that version
  does not contain ADR 0239.
- The other tabs of the old version restart onto the new one (Benoit's choice B
  for ADR 0237).
- `BrowserInstalledAppAssets.AdoptAtBootAsync` is public in
  Infrastructure.Browser; `installedAppAssets.js` exports `adoptAtBoot`.

## Tasks

- [x] `installedAppAssets.js`: `adoptAtBoot`, which reuses `prepare` outside a
  development host.
- [x] `BrowserInstalledAppAssets.AdoptAtBootAsync`; background call in
  `App.razor` after `UpdateAtBootAsync`.
- [x] Checked in Chromium: one version served, then a new one, Ctrl+F5, then F5
  stays on the new version.
