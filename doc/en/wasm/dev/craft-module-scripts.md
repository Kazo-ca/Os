> [Version française](../../../fr/wasm/dev/craft-module-scripts.md)

# Craft module: the scripts (OS side and iframe proxy)

Work item 3 of the `craft-host/base` series
([ADR 0247](adr/0247-shared-craft-app-host-module.md)). Kazo Word's scripts
(`word-host.js`, `word-proxy.js` and its modules) become those of the
`Kazo.Os.AppSdk.Craft` module, with nothing specific to one app: the app id
comes from the iframe page. The C# library (`CraftFrame`, work item 2) calls
them; the end-to-end journey and the guide for app authors come with work
item 4.

## Purpose

A Craft app (a Rust app compiled to WebAssembly by the ArtCraft team) runs in
an iframe of its Kazo OS window. Two scripts connect them:

- **`craft-host.js`**, OS side: creates the iframe, gives it a `MessagePort`
  on each load, passes the proxy's messages to the window's C#, replays on the
  OS page the shortcuts reserved for the window manager, follows the OS theme.
  It decides nothing.
- **`craft-proxy.js`**, in the iframe: starts the app in page host mode
  (`data-host="page"`), relays its `craft-web-host` events to the window and
  the window's messages to the app, runs the page commands (`craft-web-api`).
  Its modules: `file-picker.js` (the `rfd` picker goes through the window),
  `reserved-keys.js` (OS shortcuts), `settings-store.js` (the app's
  `localStorage` holds the Kazo user's settings).

## Files

In `wasm2/KazoOs/Shared/Kazo.Os.AppSdk.Craft/wwwroot/`, served at
`_content/Kazo.Os.AppSdk.Craft/`:

| File | Role | Taken from Kazo Word |
| --- | --- | --- |
| `craft-host.js` | OS side: `create`, `frameUrl`, `readColorScheme`, `replayKey` | `js/word-host.js` |
| `craft-proxy.js` | proxy: `readAppDescription`, `startProxy`, `startInPage`, `runCommand`, `prepareApp`, `runApp` | `host/word-proxy.js` |
| `file-picker.js` | intercepts the `rfd` file picker and its downloads | `host/file-picker.js` |
| `reserved-keys.js` | sends the reserved shortcuts back to the window | `host/reserved-keys.js` |
| `settings-store.js` | `MemoryStorage` in place of `localStorage` | `host/settings-store.js` |
| `craft-page.css` | the canvas fills the iframe page | `host/word-proxy.css` |

## The iframe page (in the app)

Each app keeps its `wwwroot/host/index.html` page, with no inline script, which
describes the app with attributes of its canvas:

```html
<link rel="stylesheet" href="../../Kazo.Os.AppSdk.Craft/craft-page.css">
<script type="module" src="../../Kazo.Os.AppSdk.Craft/craft-proxy.js"></script>
...
<canvas id="gridcraft_canvas" data-host="page"
        data-craft-app="gridcraft"
        data-craft-build="../gridcraft/gridcraft.json"></canvas>
```

- `data-craft-app`: prefix of the messages (`gridcraft:ready`) and command
  object (`window.gridcraft`). Checked form: `^[a-z][a-z0-9_]{0,63}$`.
- `data-craft-build`: the build's provenance file (`{ "glue": "...js",
  "wasm": "..._bg.wasm" }`), relative to the page.
- The canvas id stays the one the app looks for; the proxy takes the canvas
  that carries `data-craft-app`.
- An element the app writes its start error into can carry
  `data-craft-loading` (styled by `craft-page.css`).

A page that describes no app (no canvas, missing or malformed attribute) still
answers `failed` to the window as soon as `init` arrives, with the reason.

## Boundary with the C# (`CraftFrame`)

Agreed with work item 2. `CraftFrame` imports
`./_content/Kazo.Os.AppSdk.Craft/craft-host.js` and calls
`create(container, dotnetRef, options)`:

| Option | Meaning |
| --- | --- |
| `page` | address of the iframe page, relative to the OS document's base (`_content/<assembly>/host/index.html`); required |
| `parameters` | text → text dictionary added to the address (an empty value stays a flag, e.g. `webgl=`) |
| `title`, `language`, `reservedKeys`, `settings` | as in Kazo Word |

The returned object has `focus`, `open(name, bytes)`, `request(method, params,
timeoutMs)`, `answerPick(id, files)`, `colorScheme`, `dispose`. The module
calls back `OnFrameMessage(type, value)` and `OnFrameSave(name, saveAs,
bytes)`. `OnFrameMessage` types: `ready`, `failed`, `dirty`, `key`, `pick`,
`setting`, `scheme`, and `error` (new, see below). The iframe has the class
`craft-frame-iframe`.

## What each app supplies (not the scripts)

The scripts know neither an app's commands nor its formats; everything that
differs from one Craft app to another goes through the window's C#. Example of
GridCraft 0.19.1 next to WordCraft:

- theme and language: other page commands (`ui.set { dark }`,
  `app.language.set { language }`); the window sends them with `request`;
- Save As: `gridcraft:save` always has `saveAs: false`; the window reserves
  F12 and Ctrl+Shift+S (`reservedKeys`, target `app`) and decides itself;
  Ctrl+S stays with the app (test `craft-proxy-frame.test.mjs`); the name
  received carries the extension of the chosen format;
- `dirty` covers every document of the page; the list of formats it opens is
  the app's.

## Internal messages (window ↔ proxy)

`init`, `ready`, `failed`, `open`, `save`, `saved`, `dirty`, `focus`,
`command`, `result`, `pick`, `picked`, `key`, `setting`. They stay internal to
the module (ADR 0247, §3): the OS side and the proxy always come from the same
OS.

## What changes from Kazo Word

- **App id as a parameter**: read on the canvas; another app's events on the
  canvas are ignored.
- **Optional `craft-web-host` fields**:
  - a `save` that carries an `id` passes it on; when `OnFrameSave` returns
    `true` (file written), the module answers `<app>:saved { id }` to the app,
    which then clears its changes. Without an `id`, or when the C# returns
    nothing, nothing is sent (Kazo Word's behaviour);
  - a `failed` received **after** `ready` is about a document or a save: it
    reaches the C# as `error` with `{ error, id }`; before `ready`, it is the
    start failure (`failed`);
  - an unknown `version` is read as `"1"`.
- **Page address** given by the C# (`page`, `parameters`) instead of a fixed
  path and the `author` and `webgl` options.
- **Not handled yet**: `host`, `handoff` and `handoff-done` (the app behaves
  as with the earlier bridges), and opening by `file` (a `Blob` read in
  slices); `open` always sends `bytes`.

## Tests

`wasm2/KazoOs/Tests/Kazo.Os.AppSdk.Craft.Scripts.Tests/`: an xUnit project
that runs each file of `scripts/*.test.mjs` with `node --test` (skipped when
`node` is missing; GitHub's machines have it), in the `NonBrowser` suite. A
fake Craft app (`fakecraft`) stands in for WordCraft.

| File | Covers |
| --- | --- |
| `craft-proxy.test.mjs` | `ready`, `failed`, queue before the port, origin and source of `init`, `focus`, `dirty`, `open`, `save` |
| `craft-proxy-commands.test.mjs` | `command` → `window.<app>.request` → `result`, errors, timeout |
| `craft-proxy-frame.test.mjs` | reserved shortcuts, settings, start after `init`, `picked` |
| `craft-proxy-contract.test.mjs` | app id, `id` of `save` and `failed`, `saved`, page description, page without an app |
| `craft-host.test.mjs` | iframe address, `init`, `failed` / `error`, `saved` after `OnFrameSave`, commands, closing |

Run by hand: `node --test wasm2/KazoOs/Tests/Kazo.Os.AppSdk.Craft.Scripts.Tests/scripts/*.test.mjs`.
The real picker interception in a browser is covered by work item 4's
Playwright journey.

## Key decisions

- The scripts ship without the `.csproj`: the library's comes with work
  item 2, which serves `wwwroot/` as it is.
- The proxy only starts inside an iframe (`window.parent !== window`), as a
  host page; imported by the tests, it does nothing.
- No Craft app name in the module's code (WordCraft only appears in examples
  in comments). Aucun jeu sur ce repository.

## Task list

- [x] Take `word-host.js`, `word-proxy.js` and its three modules into the module.
- [x] App id and build read on the canvas (`data-craft-app`, `data-craft-build`).
- [x] Boundary with `CraftFrame` agreed with work item 2 (`page`, `parameters`, `error`, `saved`).
- [x] Optional `craft-web-host` fields `id`, `saved`.
- [x] `node --test` tests with a fake app, run by xUnit.
- [x] Documentation in fr and en, indexes.
