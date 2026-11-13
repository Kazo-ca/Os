> [Version française](../../../../fr/wasm/dev/adr/0259-shared-craft-app-host-module.md)

# ADR 0259: a shared SDK module for the Craft apps (`Kazo.Os.AppSdk.Craft`)

## Status

Accepted on 2026-11-13. Benoit took recommendation A of both questions (see
[Questions](#questions-asked-to-benoit)): the shared code of the Craft apps is
a module shipped and served by Kazo OS, and it holds the iframe, the proxy and
the document logic. Series `craft-host/base`. First numbered 0247; renumbered 0259 on 2026-11-13
(Benoit's choice), because the `feature/base` series already used 0247.

- **Builds on:** [ADR 0019](0019-sdk-versioning-and-compatibility.md) (SDK
  versions), [ADR 0055](0055-base-apps-on-public-sdk-only.md) (an SDK library
  does not depend on Platform Core),
  [ADR 0176](0176-app-repository-template.md) (app repository, OS submodule),
  [ADR 0182](0182-install-apps-from-github.md) (installed app packages),
  [ADR 0221](0221-pack-all-static-web-assets-of-an-app.md) (an app's static
  resources in its package; those of referenced libraries are not there,
  because the SDK comes with the OS),
  [ADR 0242](0242-framework-api-for-installed-apps.md) (the API an installed
  app finds in the published OS) and
  [ADR 0243](0243-serve-installed-app-pages.md) (an installed app's page in an
  iframe).
- **Takes Kazo Word's decision 8** ("shared library") and section 6 of the
  [Craft app port guide](../craft-app-port-guide.md).
- **Replaces no ADR.**

## Context

Kazo Word (repository `Kazo-ca/Os-App-Word`) shows WordCraft, a Rust app
compiled to WebAssembly, in an iframe of its window. Three parts do it (Kazo
Word integration plan, sections 2 and 5):

| Part | Files in Kazo Word | Role |
| --- | --- | --- |
| OS side (JS) | `wwwroot/js/word-host.js` | creates the iframe, hands it a `MessagePort`, sends commands, replays reserved shortcuts, follows the theme |
| In the iframe (JS) | `wwwroot/host/word-proxy.js`, `file-picker.js`, `reserved-keys.js`, `settings-store.js` | starts the Craft app, relays its events, catches its file picker, sends shortcuts back, keeps its settings in memory |
| C# | `WordFrame`, `FrameStartup`, `FrameMessages`, `FrameSave`, `FrameCommandResult`, `ReservedKeys`, `WordCraftLanguage`, `PickRequest`, `WebStorageFile`, `DocumentSession`, `SaveDecision`, `OpenDocument`, `OpenRequest` | decides everything: bound files, changes, title, closing, per-user settings |

Seven more Craft apps follow (Kazo Spreadsheets, Pdf, Fig, Photo, Design,
Deck, Vector; port guide). They all speak the same upstream contract:

- `craft-web-host` (repository `craft-libs`): messages `<app>:ready`, `open`,
  `save`, `dirty`, `failed` (and `host`, `saved`, `handoff`), as
  `CustomEvent`s on the canvas when the page marks it `data-host="page"`,
  contract version `"1"`;
- `craft-web-api`: `window.<app>.request(method, params)`;
- the `rfd` file picker (the same `input type=file`).

Kazo Word's code is therefore shared, except for the app id (`wordcraft`), the
canvas id, the build path and the list of document formats. Copying it into
each app would mean eight copies to fix one by one (Alt+F4, theme, `rfd`,
settings).

Size is not the point: about 35 KB of JS and 90 KB of C# source per app, next
to a 60 MB `.wasm` for WordCraft. The point is maintenance.

Three OS constraints decide the shape:

1. An installed app cannot carry a second assembly: the package only holds the
   assemblies its manifest declares (issue
   [#419](https://github.com/Kazo-ca/Os/issues/419)).
2. `KazoOsPack` does not put the `_content/` files of referenced libraries in
   the package (`PackagedWebAssets`, ADR 0221): the SDK comes with the OS.
3. Installed apps are loaded into `AssemblyLoadContext.Default`: two apps that
   brought two versions of the same library would conflict.

## Decision

### 1. An SDK library shipped with the OS

New library `Shared/Kazo.Os.AppSdk.Craft` (`Microsoft.NET.Sdk.Razor`,
namespace `Kazo.Os.AppSdk.Craft`, NuGet package `Kazo.Os.AppSdk.Craft`
published with the other SDK packages, `PublicAPI.Shipped.txt` and
`PublicAPI.Unshipped.txt`).

- It depends only on `Kazo.Os.App.Abstractions`, `Kazo.Os.AppSdk` and
  `Kazo.Os.AppSdk.Blazor`, as every SDK library (ADR 0055): no Platform Core,
  no MudBlazor.
- `Kazo.Os.Platform.Blazor` references it: every OS host (the
  `Kazo.Os.Ecosystem` PWA, the OS served by `Kazo.Os.Server`, `test/test`)
  therefore publishes it in `_framework` and serves its files at
  `_content/Kazo.Os.AppSdk.Craft/`. Its name starts with `Kazo.Os.`: the
  trimmed publish keeps it whole (`_KazoOsRootKazoOsAssemblies`, ADR 0242).
- A Craft app references it to compile (NuGet package, or project through the
  OS submodule, ADR 0176) and does not carry it: its package stays a single
  assembly, and its `_content/` only holds its own files (the Craft app's
  build, its iframe page, its icons).
- No code of a particular Craft app in the OS: no app id, no document format,
  no Rust build. Aucun jeu sur ce repository.

### 2. What the module holds (Q2)

Everything that does not depend on the app, taken from Kazo Word and made
generic:

| Part | In the module | Stays in the app |
| --- | --- | --- |
| OS side (JS) | `craft-host.js` (was `word-host.js`) | — |
| In the iframe (JS) | `craft-proxy.js` (was `word-proxy.js`), `file-picker.js`, `reserved-keys.js`, `settings-store.js` | `host/index.html` (a few lines, see §3) |
| Frame (C#) | `CraftFrame` (was `WordFrame`), `CraftFrameOptions`, `FrameStartup`, `FrameMessages`, `FrameSave`, `FrameCommandResult`, `ReservedKeys`, `CraftLanguage` (was `WordCraftLanguage`) | — |
| Documents (C#) | `DocumentSession`, `SaveDecision`, `OpenDocument`, `OpenRequest`, a `CraftDocumentFormat` type (extension, media type, default, copy, opens) | its list of formats |
| Files and settings (C#) | `PickRequest`, `WebStorageFile` (per-user settings, folder `~/.config/<appId>/`) | — |
| Window | — | its Razor window (thin: it puts together the frame, the session and its strings) |

The module provides no base window component: a public Razor component is
harder to evolve without breaking installed apps. The exact type names are set
by the library work item and frozen by `PublicAPI.Unshipped.txt`; this table
gives their role.

### 3. The iframe page stays in the app

Each app keeps its `wwwroot/host/index.html` page, served by ADR 0243 at
`_content/<assembly>/host/index.html`. It has no inline script and describes
the app with attributes of the canvas:

```html
<canvas id="gridcraft_canvas" data-host="page"
        data-craft-app="gridcraft"
        data-craft-build="../gridcraft/gridcraft.json"></canvas>
<script type="module" src="../../Kazo.Os.AppSdk.Craft/craft-proxy.js"></script>
```

- `data-craft-app` gives the message prefix (`gridcraft:ready`) and the
  command object (`window.gridcraft`).
- `data-craft-build` gives the build's provenance file (name of the JS glue
  and of the `.wasm`), relative to the page.
- The canvas id stays the one the Craft app looks for (WordCraft:
  `wordcraft_canvas`); the proxy takes the canvas that carries
  `data-craft-app`.
- `CraftFrameOptions` gives the OS side the address of that page
  (`_content/<assembly>/host/index.html`).

The OS side and the proxy always come from the same OS: their messages
(`init`, `ready`, `failed`, `open`, `save`, `dirty`, `focus`, `command`,
`result`, `pick`, `picked`, `key`, `setting`) stay internal to the module and
may change from one OS version to the next without breaking an app.

### 4. Compatibility

- **With the Craft apps:** the module speaks version `"1"` of the
  `craft-web-host` contract and treats a version it does not know as `"1"`,
  as the contract asks. Optional fields (`id`, `saveAs`, `file`,
  `appVersion`) are read when present. A new version of the upstream contract
  is handled in a new OS version, without dropping the previous one: an
  installed app keeps the Craft build it brought.
- **With installed apps:** the public API follows the SDK's SemVer
  (ADR 0019): an addition needs a minor version, a removal or a change of
  meaning a major version. The SDK compatibility fixture
  (`Tests/fixtures/sdk-compat`) covers the module.
- **Minimum OS version:** an app that uses the module sets
  `sdkCompatibility.minimumVersion` to the first OS version that holds it.
  Benoit chooses that number at release time (3.4.0 is already planned for
  the preview SDK, issue #456).

### 5. The apps

- **Kazo Word ships as it is**, with its copy (PR Os-App-Word#12 and its Store
  publication do not depend on this series). It moves to the module in a step
  of its own, after the OS that holds it is released (`TODO` issue in
  `Kazo-ca/Os-App-Word`).
- **Kazo Spreadsheets** uses the module from its work item 1. Until the OS
  that holds it is released, it builds against the OS submodule on
  `craft-host/base`; its Store publication waits for that release.
- The other Craft apps follow the port guide, updated by this series.

### 6. Work items of the `craft-host/base` series

| No. | Branch | Content |
| --- | --- | --- |
| 1 | `craft-host/1-adr` | this ADR (fr and en), indexes, memory, `features.md` |
| 2 | `craft-host/2-library` | C# library (frame, documents, files, settings) taken from Kazo Word, xUnit tests, `PublicAPI.Unshipped.txt`, reference from `Kazo.Os.Platform.Blazor`, change entry |
| 3 | `craft-host/3-scripts` | `craft-host.js`, `craft-proxy.js` and its modules, driven by `data-craft-app`; `node --test` tests with a fake Craft app |
| 4 | `craft-host/4-e2e-and-docs` | Playwright journey with a fake Craft app (iframe page, open, save, changes, command, picker, settings) in the trimmed OS; doc `framework/craft-apps.md` (fr and en); port guide updated |

Work items 2 and 3 can run in parallel: the boundary between them is the
`create` function of `craft-host.js` and the messages of `CraftFrame`, already
set by Kazo Word.

## Consequences

- A fix to the frame (shortcuts, theme, `rfd`, settings) reaches every
  installed Craft app with the OS update, without rebuilding or republishing
  the apps.
- A Craft app comes down to its window, its list of formats, its iframe page,
  its build and its manifest.
- The module becomes a public API: it must stay compatible, and a bug in the
  module hits every Craft app at once. Work item 4's tests run in the trimmed
  OS to catch it before a release.
- The Craft apps depend on a more recent OS version than they would need
  without the module.
- The OS grows by a few tens of KB (one assembly and five JS modules).

## Questions asked to Benoit

Asked in the thread "Partager le proxy des apps Craft dans Kazo OS" on
2026-11-13, answer "je suis d'accord avec tes recommandations" (I agree with
your recommendations).

**Q1. Where does the shared code of the Craft apps live?**

- **A (taken): a Kazo OS module, shipped and served by the OS**, like the
  SDK. Avoids #419, installed apps get fixes without being rebuilt; in
  exchange, a minimum OS version and a public API to keep compatible.
- B: a NuGet package carried by each app. Needs #419 solved, the packages'
  `_content/` carried, and the version conflict between two apps.
- C: shared sources compiled into each app (source package or submodule). No
  OS version, but every fix means republishing the eight apps.
- D: stay copied and align the copies by hand.

**Q2. What goes in the module?**

- **A (taken): the iframe and the proxy, plus the document logic** (frame,
  picker, shortcuts, settings, `DocumentSession`, save rule); each app keeps
  its Razor window.
- B: only the iframe and the proxy.
- C: everything, including a base window component.

## Task list

- [x] Assess what is shared (Kazo Word, `craft-web-host`, `craft-web-api`)
      and ask Benoit the questions.
- [x] Write the ADR (fr and en), indexes, memory, `features.md`.
- [ ] Work item 2: C# library.
- [ ] Work item 3: scripts.
- [ ] Work item 4: end-to-end journey, framework doc, port guide.
- [ ] `TODO` issue in `Kazo-ca/Os-App-Word`: move Kazo Word to the module.
