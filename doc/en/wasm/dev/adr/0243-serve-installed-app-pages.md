> [Version française](../../../../fr/wasm/dev/adr/0243-serve-installed-app-pages.md)

# ADR 0243: serve an installed app's page loaded in an iframe

## Status

Accepted on 2026-11-13. Benoit chose option A of question 10 of the
[Kazo Word plan verification](https://github.com/Kazo-ca/Os-App-Word/pull/2)
(`doc/en/plan-verification.md`) and asked to start it right away.
Branch `fix/installed-app-iframe-page`.

- **Complements:** [ADR 0206](0206-serve-installed-app-assets.md) (serve the
  `wwwroot/` files of installed apps), which only served subresources (scripts,
  images, workers), not page loads.
- **Builds on:** [ADR 0131](0131-cross-origin-isolation.md) §2 (COOP/COEP
  headers added by the service worker) and [ADR 0225](0225-serve-installed-app-files-to-every-page.md)
  (the worker takes a page it does not control).

## Context

- An app can show an HTML page of its `wwwroot/` in an iframe: Kazo Word wraps
  WordCraft, a complete web application, in
  `_content/Kazo.Word/host/index.html?author=…`. The page reads its parameters
  (`?author=`, `?webgl`) from its own address.
- Loading an iframe is a **navigation** (`mode: 'navigate'`). The published
  service worker answered every navigation outside the release manifest with
  `index.html`, **before** looking at the installed apps' cache: the iframe
  started a second Kazo OS.
- On a development host, the "installed apps only" worker (ADR 0206 §4) already
  serves these navigations: the page worked in development and broke once
  published.

## Decision

1. **The installed apps' cache first, navigation included.** In
   `fetchShellOrAsset` of `service-worker.published.js`, a same-origin `GET`
   request under `_content/` (`installedAppAssets.handles`) that the
   `kazoos-installed-apps` cache holds receives that file, whether it is a page
   load or a subresource. The query is ignored to find the file (key without
   query, ADR 0206 §2); the iframe keeps its full address, and the page reads
   `location.search`.
2. **Headers.** The response goes through `withIsolationHeaders` like every
   same-origin response: COOP, COEP and `Cross-Origin-Resource-Policy` when the
   OS is isolated (ADR 0131). An isolated page can therefore load the iframe, and
   the iframe's page is isolated in turn (`SharedArrayBuffer`, WebGL in a
   worker).
3. **Fallback unchanged.** An address under `_content/` that no installed app
   holds (not copied yet, app removed) follows the usual path: the release's
   file if it exists, otherwise `index.html` for a navigation.
4. **Offline.** The file comes from the cache: the iframe's page opens without
   network, like the app's other files.
5. **Development.** `service-worker.js` and the "installed apps only" mode do not
   change: `installedAppAssets.respond` already serves every request `handles`
   accepts, navigation included.
6. **Any navigation.** The rule does not look at whether the navigation comes
   from an iframe or the address bar: opening `_content/<assembly>/page.html` in
   a tab also shows the page, as in development and as for a file of an app
   built into the OS.

## Consequences

- An installed app can show its own HTML pages in an iframe, with parameters in
  the address, in development as once published, online as offline. Generic: no
  list of apps or pages.
- The iframe's page runs in the OS origin, like the app's code (ADR 0182,
  Consequences): no new trust boundary.
- The fix only acts from the OS version that contains it (next release: 3.4.0
  or 3.3.1, as Benoit chooses).
- Test: `TrimmedPublishTests.InstalledAppPages.cs` copies a page into the
  installed apps' cache and opens it in an iframe, on the published address
  (online then offline) and on the development address.

## Tasks

- [x] Serve the file from the installed apps' cache before the fallback to
      `index.html` (`service-worker.published.js`).
- [x] Trimmed publish E2E tests (two addresses, offline included);
      `EXPECTED_TESTS` goes to 10 in `build/publish-trimmed.sh`.
- [x] ADR 0206: link to this ADR; pitfall added to `pitfalls.md`.
- [x] Change entry, `features.md`, memory.
