> [Version française](../../../fr/wasm/dev/craft-app-port-guide.md)

# Porting a Craft app to Kazo OS

Internal guide, written on 2026-11-13 from the port of **Kazo Word** (WordCraft, repository
[`Kazo-ca/Os-App-Word`](https://github.com/Kazo-ca/Os-App-Word)). It says how to do the same
for the other apps of the ArtCraft team's Craft suite (GitHub organisation `storytold`): the
method, the order of the work items, the pitfalls already paid for and what to check for each
app before starting.

Kazo Word's details stay in its repository, to read with this guide:
[`plan.md`](https://github.com/Kazo-ca/Os-App-Word/blob/main/doc/en/plan.md) (goal and decisions),
[`plan-verification.md`](https://github.com/Kazo-ca/Os-App-Word/blob/main/doc/en/plan-verification.md)
(cross-check against WordCraft's code) and
[`integration-plan.md`](https://github.com/Kazo-ca/Os-App-Word/blob/word/base/doc/en/integration-plan.md)
(files, contracts, results of each work item, on the `word/base` branch).

## 1. The apps to port

| Kazo repository | Name in Kazo OS | Upstream app | Upstream web build (2026-11-13) |
| --- | --- | --- | --- |
| `Kazo-ca/Os-App-Word` | Kazo Word | [WordCraft](https://github.com/storytold/wordcraft) | `apps/wordcraft-web`, `craft-web-host` and `craft-web-api`. **Done** (work items 1 to 4), Store in progress. |
| [`Kazo-ca/Os-App-Spreadsheets`](https://github.com/Kazo-ca/Os-App-Spreadsheets) ([TODO #1](https://github.com/Kazo-ca/Os-App-Spreadsheets/issues/1)) | Kazo Spreadsheets | [GridCraft](https://github.com/storytold/gridcraft) | `apps/gridcraft-web`, `craft-web-host` and `craft-web-api`. **Next**, as soon as Kazo Word is published in the Store. |
| [`Kazo-ca/Os-App-PDF`](https://github.com/Kazo-ca/Os-App-PDF) ([TODO #1](https://github.com/Kazo-ca/Os-App-PDF/issues/1)) | Kazo Pdf | [PdfCraft](https://github.com/storytold/pdfcraft) | `apps/pdfcraft-web`, `craft-web-host` and `craft-web-api`. |
| [`Kazo-ca/Os-App-Fig`](https://github.com/Kazo-ca/Os-App-Fig) ([TODO #1](https://github.com/Kazo-ca/Os-App-Fig/issues/1)) | Kazo Fig | [FigCraft](https://github.com/storytold/figcraft) | `apps/figcraft-web`, `craft-web-host` and `craft-web-api`. |
| [`Kazo-ca/Os-App-Deck`](https://github.com/Kazo-ca/Os-App-Deck) ([TODO #1](https://github.com/Kazo-ca/Os-App-Deck/issues/1)) | Kazo Deck | [DeckCraft](https://github.com/storytold/deckcraft) | `apps/deckcraft-web`, `craft-web-host` and `craft-web-api`. |
| [`Kazo-ca/Os-App-Vector`](https://github.com/Kazo-ca/Os-App-Vector) ([TODO #1](https://github.com/Kazo-ca/Os-App-Vector/issues/1)) | Kazo Vector | [VectorCraft](https://github.com/storytold/vectorcraft) | `apps/vectorcraft-web`, `craft-web-api` only: **no host mode**. |
| [`Kazo-ca/Os-App-Photo`](https://github.com/Kazo-ca/Os-App-Photo) ([TODO #1](https://github.com/Kazo-ca/Os-App-Photo/issues/1)) | Kazo Photo | [PhotoCraft](https://github.com/storytold/photocraft) | `apps/photocraft-web`, **neither `craft-web-host` nor `craft-web-api`**. |
| [`Kazo-ca/Os-App-Design`](https://github.com/Kazo-ca/Os-App-Design) ([TODO #1](https://github.com/Kazo-ca/Os-App-Design/issues/1)) | Kazo Design | [DesignCraft](https://github.com/storytold/designcraft) | `apps/designcraft-web`, **neither `craft-web-host` nor `craft-web-api`**. |

The "web build" column comes from the `Cargo.toml` of `apps/<app>-web` on each upstream
repository's `main` branch, read on 2026-11-13. It changes fast (about one release a week):
check it again at step 0. The seven Kazo repositories and the `Kazo-ca/<app>craft` forks exist since 2026-11-13; each repository has its `TODO` issue #1 that follows this guide. All these apps are licensed `MIT OR Apache-2.0`, written in Rust
with egui and wgpu (WebGPU, WebGL 2 fallback), in alpha.

An app without `craft-web-host` cannot be hosted like Kazo Word: it can neither receive a file
from the page around it nor hand one back. That mode must come first, upstream (issue and PR
in `storytold/craft-libs` and in the app, opened by Benoit) or in our fork, before work item 2
(section 4, step 0).

## 2. The architecture, the same for every app

```
┌─ Kazo <App> window (Blazor, C#) ─────────────────────────────────┐
│ <App>Window.razor  ⇄  js/<app>-host.js   (OS document)            │
│        │                    ⇅  MessagePort (one per window)       │
│        │        ┌─ iframe  _content/Kazo.<App>/host/index.html ─┐ │
│        │        │ host/<app>-proxy.js                           │ │
│        │        │      ⇅  CustomEvent on the canvas             │ │
│        │        │ <App>Craft (.wasm), data-host="page"          │ │
│        │        └───────────────────────────────────────────────┘ │
│  KazoFile, OS dialogs, title, closing                             │
└───────────────────────────────────────────────────────────────────┘
```

- **One iframe per window.** Craft apps hard-code their canvas id (`wordcraft_canvas`,
  `gridcraft_canvas`…): one instance per HTML document. The iframe gives one page per window,
  so several windows.
- **The window (C#) decides everything**: which file is bound to which document, where to
  write, the title, the "modified" state, closing. All testable logic is in C#.
- **`js/<app>-host.js`** (in the OS document, loaded by the component) creates the iframe,
  holds the `MessagePort` and calls C#. It decides nothing.
- **`host/<app>-proxy.js`** (in the iframe) talks to the Craft app: the canvas's `<app>:*`
  events in `data-host="page"` mode, `window.<app>.request` (`craft-web-api`), the file
  picker, keys, `localStorage`. It decides nothing either.
- One `MessagePort` per window, handed to the iframe when it loads: two windows never mix.

Kazo Word's port messages are in section 5 of the
[integration plan](https://github.com/Kazo-ca/Os-App-Word/blob/word/base/doc/en/integration-plan.md):
`init`, `ready`, `failed`, `open`, `save`, `dirty`, `pick`/`picked`, `command`/`result`, `key`,
`setting`, `focus`. They do not depend on WordCraft: reuse them as they are.

## 3. What Kazo OS already provides

These fixes were made for Kazo Word; a new Craft app has nothing to ask the OS for them.
**Minimum version: Kazo OS 3.3.2.**

| Need | OS answer | Version |
| --- | --- | --- |
| Serve the iframe page of an **installed** app (the published service worker answered with the OS `index.html`) | [ADR 0243](adr/0243-serve-installed-app-pages.md) | 3.3.1 |
| A window brought to the front no longer reloads its iframe (stable window order in the page, stacking by `z-index`) | [ADR 0244](adr/0244-stable-window-order-in-the-page.md) | 3.3.1 |
| A file over 16 MiB (WordCraft's `.wasm` is 60 MB): 128 MiB per file | [ADR 0245](adr/0245-raise-the-maximum-file-size.md), replaces the limit of [ADR 0094](adr/0094-deferred-decisions-status-and-maximum-file-size.md) | 3.3.2 |
| `.kazoapp` package: 64 MiB compressed, 128 MiB extracted, 2000 files | `AppPackage` (`MaximumArchiveBytes`, `MaximumExtractedBytes`, `MaximumFiles`); installation: [`app-installation.md`](app-installation.md) | — |
| App repository from the template, OS submodule, Store by a tag | [ADR 0176](adr/0176-app-repository-template.md), [`app-repository.md`](../framework/app-repository.md) | — |
| Signing only in `Os-AppMarketPlace` | [ADR 0219](adr/0219-app-authors-signing-tool.md) | — |

If an app goes over one of these limits (a `.wasm` over 128 MiB, a package over 64 MiB), it is
a new decision for the OS: an ADR in `Kazo-ca/Os`, in a separate thread, then an OS version the
app requires.

## 4. The steps

One `<app>/base` series in the app's repository (for example `spreadsheets/base`), one branch
`<app>/<n>-<topic>` and one PR to `<app>/base` per work item. The plan is written and merged
before the code. Benoit merges `<app>/base` into `main`.

### Step 0: check the upstream app and write the plan

Before any code, read the upstream app and record in `doc/fr/plan.md` (and `doc/en/plan.md`):

- **Web build**: does `apps/<app>-web` exist? Does it depend on `craft-web-host` and
  `craft-web-api`? Which `craft-libs` revision (pinned in its `Cargo.toml`)?
- **Host-mode messages**: read `apps/<app>-web/src/host.rs` (or its equivalent) and the
  `index.html` page: canvas id, event prefix (`gridcraft:open`…), what `save` sends (`name`,
  `saveAs`, `bytes`, `id`), when `dirty` changes.
- **Commands**: does `window.<app>.request` exist? Do `ui.inspect`, `ui.theme`, `ui.language`,
  `file.new`, `file.save`, `file.rename`, `ui.backstage` exist under those names?
- **File picker**: where does the app open the browser picker (rfd), and for what (document,
  picture, fonts, data)? Every use must go through the OS dialog.
- **Formats**: extensions opened, saved, exported (copies do not change the document's
  binding), MIME types, and which to associate with the app (`open`, `edit`).
- **Build**: Rust version, Trunk, `craft-i18n` and `craft-fonts` revisions, upstream release
  workflow (in `storytold/craft-common-actions`), official `<app>-web-<version>.zip` published
  or not.
- **Size**: `.wasm` and package, against the limits of section 3.
- **Open upstream issues** about the host mode or the web (one issue per problem upstream; we
  join existing issues).
- **Trademark**: the ArtCraft name and logo are trademarks; a **modified** build must remove
  them, and the Store listing says "based on <App>Craft by the ArtCraft team", with no logo.

Then ask Benoit, in the thread, the questions this reading opens (with choices, context,
implications and a recommendation). For an app without `craft-web-host` (today PhotoCraft,
DesignCraft, VectorCraft), the first question is: add the host mode upstream (Benoit opens the
issue and the PR, prepared by Claude), or carry it in our fork only, meanwhile?

### Work item 1: solution and iframe (`<app>/1-solution`)

The repository is created from `Os-AppTemplate` (thread "Créer des dépôts GitHub depuis mon
PC": a cloud session cannot create a repository).

- Rename the template's solution (`Kazo.<App>`, `Kazo.<App>.Os`, `Kazo.<App>.Tests`),
  `[AppManifest]`: id `ca.kazo.<app>`, publisher `pub.kazo`, category, **several instances**,
  default and minimum window size (the Craft app's minimum).
- `tools/vendor-<app>.sh`: copies a build (official zip pinned by SHA-256 at first) into
  `wwwroot/<app>/` with a provenance `<app>.json` (version, source, names and SHA-256). The
  build lives **in the repository**: the Store builds with `dotnet build -t:KazoOsPack` only
  and fetches only the `external/KazoOs` submodule; it has neither Rust nor Trunk.
- `host/index.html` (canvas `data-host="page"`, no inline script), `host/<app>-proxy.js`
  (startup, `ready`, `failed`), `js/<app>-host.js`, a window with three states: loading,
  app, error (60 s timeout, "Retry").
- **OS submodule on 3.3.2 or later** from the start.
- Tests: manifest, `<app>.json` matches the files, proxy with a fake Craft app (`ready`,
  `failed`, timeout), a Playwright journey waiting for `ready`; the document survives its
  window going to the front and back.
- **Done when**: the app shows and takes keyboard input with WebGPU and WebGL 2 (`--webgl` adds
  `?webgl` to the iframe), two windows run together, and **the app installs in a published
  Kazo OS** (`sudo kazopkg install Kazo-ca/Os-App-<App>`). Record the package size and memory
  per window.

### Work item 2: open and save (`<app>/2-open-save`)

- File associations and "Open with", file requested at launch, intents.
- `DocumentSession`: each app document name bound to a path. Kazo Word's save rule, to reuse:

| Case | Decision |
| --- | --- |
| `name` bound to a path and `saveAs` false | Rewrites that path. |
| `saveAs` true, or `name` bound to nothing | OS save dialog, then bind to the chosen path (and `file.rename` in the app if the name changes). |
| Copy format (PDF, PNG, text…) | Save dialog, the binding does not change. |

- Craft apps send `save` **without confirmation** and clear "modified" at once
  (craft-libs #611): the window keeps its own state. "Modified" = the app says so, **or** a
  save did not complete. A cancelled dialog or a refused write leaves the window "modified".
- A Craft page can hold **several documents** (File › New); `dirty` only covers the shown
  document. The close guard asks `ui.inspect` and saves each document before closing.
- Startup: requested file sent by `open`, otherwise a blank document; close the app's start
  screen if it has one.
- **Done when**: no case loses changes unless the user chose so, including when the write
  fails.

### Work item 3: complete proxy (`<app>/3-proxy`)

- **File picker**: the proxy catches rfd's `input type=file` (click, or the `#rfd-overlay`
  card) and sends `pick`; the window shows the OS dialog and answers `picked`. A document picked
  with File › Open opens in a **new window** (in place only for an untouched blank document).
- **OS shortcuts**: an iframe keeps every key; the proxy listens to `keydown` in capture and
  forwards the OS's reserved shortcuts, plus `Ctrl+N` (new window).
- **Theme and language**: `ui.theme` on every OS theme change, `ui.language` on the app
  language closest to the Kazo culture.
- **Per-Kazo-user settings**: replace `localStorage` in the iframe with an in-memory copy
  loaded from `~/.config/ca.kazo.<app>/web-storage.json`, written back at most 500 ms after the
  first unsaved write, one write at a time.
- **Done when**: no browser dialog opens from the app, and OS shortcuts work with the caret in
  the document.

### Work item 4: build from source (`<app>/4-build-from-source`)

- Fork `Kazo-ca/<app>craft` (created by Benoit) as submodule `external/<app>craft`. An upstream
  release commit is often on the `release` branch, which the fork does not copy: keep it with a
  `kazo/v<version>` branch in the fork.
- `tools/build-<app>.sh` builds like the upstream release (`craft-i18n` and `craft-fonts`
  revisions read from the submodule, exact Trunk version, `wasm32-unknown-unknown` target,
  **today's stable Rust**), then `vendor-<app>.sh --dir`.
- Workflow `build-<app>.yml`: run by hand, it builds, pushes to a branch, runs CI and opens
  the PR; on a PR touching the submodule, it only builds.
- An xUnit test checks that the commit in `<app>.json` is the submodule's in the git index.
- **Done when**: upgrading = moving the submodule commit and running the workflow.

### Work item 5: Store (`<app>/5-store`)

- Icon without the ArtCraft logo, fr and en listing ("based on <App>Craft by the ArtCraft
  team", known limits), screenshots, `THIRD-PARTY-NOTICES.md`, licences in the package.
- Minimum OS version in the manifest; Playwright journey in a published OS.
- Tag through the "Tag a release" workflow (a cloud session cannot push tags), PR to the
  `Os-AppMarketPlace` catalog (signing there only), a row in `features.md`.
- **Done when**: the app installs from the Store and opens a file by double-click.

**Order**: 1, then 2, 3 and 4 in parallel (C#, proxy, build: three threads), then 5.

## 5. Pitfalls already met

| Pitfall | Answer |
| --- | --- |
| A `[JSInvokable]` callback (proxy message) does not run in the app's context: `KazoFile` refuses. | Every handler that touches the SDK does `using var s = KazoExecutionContext.Enter(AppContext);`. |
| The `cancel` event of rfd's file input makes WordCraft 0.18.1 panic (`removeChild`). | Cancel with a `change` event carrying no file. |
| rfd calls `input.click()` right after adding its card. | Replace `HTMLInputElement.prototype.click` in the iframe's realm. |
| `localStorage` is an own property of the iframe's window; settings are shared by every Kazo user (same origin). | In-memory copy, fed and written back by the window (work item 3). Added fonts (IndexedDB) stay shared. |
| Two overlapping writes of the settings file restored old values. | One write at a time (queue). |
| A `.wasm` built with an old Rust imports more functions than the official zip. | Today's stable Rust, like the upstream release. The `.wasm` is never byte-identical: compare behaviour. |
| `Alt+F4` is bound by the OS only in full screen (keyboard lock). | Outside full screen, close = `Alt+Q`; the test journey uses it. |
| "System fonts" asks for the browser's local-fonts permission. | Known limit, stated in the listing. |
| Printing goes through the browser's print dialog. | Accepted exception for Kazo Word. |
| File Explorer drops a file uploaded before its view exists ("insert a picture" journey flaky). | The test waits for the folder's first listing. |
| The browser picker silently opens in a test. | Playwright journeys fail if a browser picker opens. |
| A pushed file over 50 MB triggers a GitHub warning (refused over 100 MB). | WordCraft's `.wasm` (60 MB) passes; a `.wasm` over 100 MB needs another decision (LFS or download), to ask Benoit. |

## 6. What stays shared between the apps

- **The Kazo code** (window, `DocumentSession`, proxy, picker, keys, settings) is the same but
  for a few names. Kazo Word's decision 8 (shared library) is taken before the second app:
  extract that code into a common library, or copy it and keep it aligned. To ask Benoit at
  the start of Kazo Spreadsheets.
- **The `.wasm`** is not shared: two wasm modules do not share Rust code. Only data loaded
  separately (translations, spelling, fonts: about 9 MB of 60 in WordCraft) could be, if
  upstream moves it out of the `.wasm`.
- **The upstream track** (one issue per problem, texts prepared by Claude in English, posted by
  Benoit with no agent mention) serves every app: save confirmation (craft-libs #611),
  `open-request` (craft-libs #610), reserved keys, settings handed to the host.

## 7. Checklist for a new app

- [ ] Repository `Kazo-ca/Os-App-<App>` created from `Os-AppTemplate` and added to the project.
- [ ] Fork `Kazo-ca/<app>craft` created (before work item 4).
- [ ] Step 0: upstream app read, fr and en plan merged, questions asked to Benoit.
- [ ] Host mode present upstream or added (apps without `craft-web-host`).
- [ ] Work item 1: iframe, two windows, installation in a published Kazo OS (3.3.2 or later).
- [ ] Work item 2: open, save, close guard.
- [ ] Work item 3: picker, shortcuts, theme, language, per-user settings.
- [ ] Work item 4: build from the fork, workflow.
- [ ] Work item 5: listing, tag, catalog, `features.md`.
