> [Version française](../../../../fr/wasm/dev/adr/0237-activate-the-worker-of-a-newer-page.md)

# ADR 0237: activate the service worker of a newer page

## Status

Accepted on 2026-11-08; Benoit chose option B ("1. B") among the options asked
in the thread "Réparer le plantage d'IRC sans service worker". Branch
`apps/failure-in-window-and-newer-worker`.

- **Complements:** [ADR 0225](0225-serve-installed-app-files-to-every-page.md)
  (taking control of a page with no controller). Replaces its consequence "when
  the page runs another release than the worker, the files are not served
  before the restart".
- **Builds on:** [ADR 0223](0223-restart-onto-the-new-version.md) (the other
  tabs restart cleanly onto the new version).
- **Goes with:** [ADR 0238](0238-an-app-failure-stays-in-its-window.md) (an
  app's failure stays in its window).

## Context

Benoit (2026-11-08, `https://os.kazo.ca`, version 3.2.1): the IRC app,
installed from the Kazo OS Store, took the whole OS down when it started. The
console:

- "the service worker does not serve this page, installed apps keep their
  wwwroot/ files until the next restart";
- `_content/Kazo.Irc/Components/MessagePane.razor.js`: 404;
- `JSException: Failed to fetch dynamically imported module` in
  `MessagePane.OnAfterRenderAsync`, then the host's fatal error.

The page had no controller (Ctrl+F5, or the first load of a new tab served by
the network). The active worker came from an older deployment: the "version"
ADR 0225 compares is the one of `service-worker-assets.js`, a fingerprint of the
files, which changes with **every** deployment of `main` (two on 2026-11-08:
3.2.1 and #425), not only with every release. ADR 0225 then refuses to take the
page and nobody serves the installed apps' `wwwroot/` files.

The new worker does exist: `index.html` calls `register()` at every load, the
browser downloads the new script and installs it. It stays waiting as long as
another tab, screen or detached window of the site keeps the old one.

## Options asked to Benoit

- **A** The old worker takes the page but serves it only the installed apps'
  files (independent of the release); everything else goes to the network.
- **B** Activate the new worker at once; the other tabs and screens restart
  onto the new version. **Chosen.**
- **C** Change nothing; the app shows "restart Kazo OS".

## Decision

1. **When the active worker refuses the page** because it caches another
   release (answer to `KAZOOS_CLAIM_FOR_INSTALLED_APPS`),
   `installedAppAssets.js` looks for the worker of the page's release: the
   waiting one, else the installing one, else it calls `registration.update()`.
2. **Once that worker is installed** (its release cache filled and checked),
   the page sends it `KAZOOS_ACTIVATE_FOR_INSTALLED_APPS` with its version.
   `installedAppAssets.sw.js` calls `skipWaiting()` only when that is the
   release it caches (the same `mayClaim` check as ADR 0225): a worker of
   another release never activates for this reason.
3. **Its `activate` takes every page of the site** (`clients.claim()`, already
   there) and deletes the old release's cache. The asking page sets
   `window.kazoOsClaimForInstalledApps` to `pending` before asking: `index.html`
   does not reload it (it already runs that release). The pages of the older
   release restart cleanly onto the new one (`hostUpdate.js`,
   `OnActivatedElsewhere`, ADR 0223); a page the OS does not handle yet is
   reloaded.
4. **Install longer than the delay** (10 s,
   `BrowserInstalledAppAssetCache.ServiceWorkerTimeout`): the launch goes on
   without the files, as before; the page waits for the install in the
   background (at most 3 min), then asks for the activation. The next launch
   finds the page controlled (ADR 0225 §3).

## Rejected options

- **A (old worker in "installed apps only" mode for that page):** no restart of
  the other tabs, but the worker must remember, across worker restarts, which
  pages run another release.
- **C ("restart" message):** the app stays unusable until a restart, at every
  deployment.

## Consequences

- After a deployment, a page loaded from the network serves the installed apps'
  files as soon as the new worker is installed.
- The other tabs, screens and detached windows still on the old release restart
  onto the new one when an installed app is launched in the newer page (the
  user did not ask for it at that moment; that is the price of option B).
- If the new worker does not install (offline, a file refused by the integrity
  check), nothing changes from ADR 0225: a warning in the console and in
  `/var/log/syslog`, and ADR 0238 keeps the app's failure in its window.

## Tasks

- [x] `installedAppAssets.js`: `activateNewerWorker`, `waitUntilInstalled`,
  `askToActivate`.
- [x] `installedAppAssets.sw.js`: message `KAZOOS_ACTIVATE_FOR_INSTALLED_APPS`.
- [x] Checked in Chromium (Playwright, `service-worker.published.js` served on
  `127.0.0.2`, a second release simulated by another `service-worker-assets.js`).
- [x] Docs: [`app-installation.md`](../app-installation.md), indexes, `features.md`, memory.
