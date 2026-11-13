> [Version française](../../../../fr/wasm/dev/adr/0257-offer-a-store-app-for-an-unsupported-file.md)

# ADR 0257: offer a Store app to open a file no app opens

## Status

Accepted on 2026-11-13. Benoit answered "ok" to questions 1 to 4: the
recommendations (choice A) are kept. Series `open-with-store/base`.

- **Builds on:** [ADR 0121](0121-user-file-associations.md) (association
  resolution and "Open with"), [ADR 0188](0188-hack-store-and-app-catalog.md)
  (`catalog.json` format and the Store), [ADR 0212](0212-hack-store-app-build.md)
  (Store v1 and installing from a catalog) and
  [ADR 0189](0189-app-sources-trust-and-per-user-install.md)
  (trust and install for me or for everyone).
- **Repositories touched:** Kazo-ca/Os and Kazo-ca/Os-AppMarketPlace (catalog tool).

## Context

- In Kazo OS 3.3.2, opening `NewFile.docx` with no app installed shows
  "No application is registered to open this file." (the File Explorer's
  `Error.NoHandler`). Yet Kazo Word 0.1.0 is in the official catalog and
  declares `.docx` in the `fileHandlers` of its manifest.
- `FileAssociationResolver` only looks at installed apps. The `NoHandler`
  result goes as is to the File Explorer, the desktop and `xdg-open`.
- `catalog.json` does not carry file types: the Os-AppMarketPlace tool
  (`CatalogVersion`) copies the capabilities, SDK range, icon and screenshots
  from the manifest, but not `fileHandlers`. On the OS side,
  `AppCatalogVersion` has no field for them.
- The Store can already open on an app's page (`--app=<appId>`) and install
  it there, with the capability screen and the administrator prompt of
  ADR 0189.

## Decision

1. **The catalog announces file types (question 2, choice A).** Each version in
   `catalog.json` gets an optional `fileHandlers` field, copied from the package
   manifest by the Os-AppMarketPlace tool: for each entry, `extensions`,
   `mediaType` and `actions`. The schema stays at version 1: a catalog without
   the field is still valid and simply offers no app. `AppCatalogVersion` gets
   `FileHandlers` (empty list by default). The field is part of `catalog.json`,
   already signed with the catalog: no new signature.

2. **Search in the catalogs.** When resolution finds no installed app, the OS
   searches the catalogs already downloaded (no extra network request) for apps
   whose **newest version compatible with the OS SDK** declares the file's
   extension (or, failing that, its media type) for the requested action.
   Blocked or already installed apps are left out. The official catalog comes
   first, then the `[catalogs]` order.

3. **In the OS open path (question 4, choice A).** The `NoHandler` result of
   `OpenFileAsync` carries the list of offered apps (`SuggestedCatalogApps`: id,
   name, catalog). The File Explorer, the desktop and apps that call
   `KazoApps.OpenFileAsync` receive it; `xdg-open` (`open`) writes it under its
   error message with the `kazopkg install` command to run. The SDK addition is
   backward compatible (new optional property, `PublicAPI.Unshipped.txt`).

4. **The File Explorer dialog (question 1, choice A).** Under "No application
   can open this file", the File Explorer lists the offered apps (icon, name,
   short description) with a "View in the Store" button. The "Open with" dialog
   shows the same section under the installed apps. The button launches the
   Store with `--app=<appId>`: installing goes through the Store and keeps its
   usual consent (capabilities, scope, administrator). With nothing to offer,
   the dialog stays as it is today.

5. **Open the file after installing (question 3, choice A).** The Store is
   launched with `--open-after-install=<path>`. When the install succeeds, the
   Store reopens the file with `KazoApps.OpenFileAsync`; resolution then finds
   the new app. If the user closes the Store or cancels, nothing happens.

6. **Offline.** With no cached catalog, the list is empty and the dialog stays
   as it is today. No suggestion blocks opening or waits for the network.

## Consequences

- Os-AppMarketPlace: `CatalogVersion.FileHandlers`, tool tests, then a catalog
  republish so Kazo Word (and Webamp, IRC) announce their types.
- Os: reading the field (`AppCatalogDocument`), catalog search, richer
  `NoHandler`, File Explorer dialog, Store argument, `xdg-open`, docs
  (`user-file-associations.md`, `file-explorer.md`, `app-installation.md`, SDK
  reference) and change entry.
- The error dialog becomes more useful without changing the case where nothing
  is found.

## Work items of the `open-with-store/base` series

| No. | Branch | Repository | Content |
| --- | --- | --- | --- |
| 0 | `open-with-store/0-adr` | Os | This ADR. |
| 1 | `open-with-store/1-catalog-file-types` | Os-AppMarketPlace | `fileHandlers` in `catalog.json`, tests, republish. |
| 2 | `open-with-store/2-suggest-catalog-apps` | Os | Read the field, search, richer `NoHandler`, SDK, `xdg-open`. |
| 3 | `open-with-store/3-explorer-and-store` | Os | File Explorer and "Open with" dialogs, Store `--open-after-install`, E2E, docs, change entry. |

## Questions asked to Benoit (2026-11-13, answer "ok": choice A everywhere)

1. Where to offer the app: A ✨ error dialog and "Open with", button to the
   Store; B install directly from the File Explorer; C a plain "Search the
   Store" button.
2. How the catalog announces types: A ✨ optional `fileHandlers` field in
   `catalog.json`; B the OS downloads each manifest; C separate
   `file-types.json` file.
3. After installing: A ✨ the file opens by itself; B the user double-clicks
   again.
4. Scope: A ✨ the OS open path (File Explorer, desktop, `open`,
   `KazoApps.OpenFileAsync`); B File Explorer only.
