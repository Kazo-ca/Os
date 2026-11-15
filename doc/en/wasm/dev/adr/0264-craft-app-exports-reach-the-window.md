> [Version française](../../../../fr/wasm/dev/adr/0264-craft-app-exports-reach-the-window.md)

# ADR 0264: a Craft app's exports reach its window

## Status

Proposed on 2026-11-13. Benoit chose this approach for Kazo Pdf (question Q1 of the
[Kazo Pdf plan](https://github.com/Kazo-ca/Os-App-PDF/blob/pdf/base/doc/en/plan.md), answer A).
Series `craft-exports/base`.

- **Builds on:** [ADR 0259](0259-shared-craft-app-host-module.md) (the shared Craft
  app module, `Kazo.Os.AppSdk.Craft`, and its file picker).
- **Concerns:** Kazo Pdf first, then any Craft app that exports the same way.

## Context

- A Craft app (WordCraft, GridCraft, PdfCraft…) runs in an iframe. It hands its saves
  to the window through the `save` event of its host mode, and the module already
  intercepts its file picker (`rfd`) and the `rfd-output` link that `rfd` shows for a
  save outside host mode (`file-picker.js`).
- PdfCraft 0.21.1 does otherwise for all its exports: Export (Word, images, text…),
  Print to PDF, Reduce size, signed copy, created PDF, OCR result, form data (FDF,
  XFDF, CSV), measurements, area snapshot. Its `download()` function makes a `Blob`,
  its `blob:` address with `URL.createObjectURL`, an `<a download>` detached from the
  document, activates it with `click()`, then revokes the address at once.
- The browser then downloads the file into the real computer's Downloads folder. It
  never reaches the Kazo OS file system, and the user does not see the OS save
  dialog.
- The address is revoked in the same event loop turn as the click: reading the file
  later with `fetch(href)` fails. The `Blob` itself must be kept.
- Some exports are named `.pdf` (Print to PDF, Reduce size, signed copy). So the
  window cannot tell an export by its extension: taken for a save, it would bind the
  window to the new file, or even rewrite the open document.

## Decision

1. **The module intercepts the page's downloads.** In the iframe, `file-picker.js`
   replaces, for that page only:
   - `URL.createObjectURL`, which notes each `Blob` under its address while it is
     not revoked (the `Blob` is then held until it is handed over);
   - `HTMLAnchorElement.prototype.click`: a link with a `download` attribute and a
     known `blob:` address (or a `data:` one) is not followed. Its bytes go to the
     window in a new `export { name, bytes }` message.

   Any other link keeps the browser's behaviour. The `rfd-output` link stays a save
   (`save` with `saveAs`), as today.

2. **Only when the window takes them.** `CraftFrame` gains the `Exporting` event
   (`Func<FrameSave, Task<bool>>`, with `FrameSave.SaveAs` false). The proxy
   intercepts only when the window subscribed to it before `StartAsync`. A window
   written before this version therefore keeps the browser download, instead of
   receiving an export it would take for a save.

3. **The window saves it as a copy.** `DocumentSession.DecideCopy(name)` returns an
   `AskForCopy` decision in the document's folder, whatever the format. The window
   shows the OS save dialog ("Save a copy"), writes the file, and its binding does
   not change. When the user cancels or the write fails, only a message says so:
   the document is not touched.

4. **Version.** This adds to the module's public API (`CraftFrame.Exporting`,
   `CraftFrame.OnFrameExport`, `DocumentSession.DecideCopy`) and to the frame
   protocol (`export`). An app that uses it asks for the OS version that brings it
   (`sdkCompatibility.minimumVersion`), the next one after 3.3.3.

5. **Upstream.** Benoit publishes the issue prepared for PdfCraft: in host mode,
   hand exports to the host through `save`, like saves. The interception still
   holds for an app that does not.

## Consequences

- Kazo Pdf's exports reach the Kazo OS file system, through the OS dialog, and no
  longer leave the browser.
- Kazo Pdf asks for the OS version that carries this ADR: its Store release waits
  for that version.
- The other Craft apps have nothing to change. They benefit by subscribing to
  `Exporting`, if they export through a download link.
- The module replaces two functions of the iframe page (`createObjectURL` and the
  links' `click`), as it already replaces the file inputs' `click`. These
  replacements are undone when the frame stops.
