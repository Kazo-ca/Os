> [Version française](../../../../fr/wasm/dev/adr/0263-windows-that-stay-on-their-screen.md)

# ADR 0263: windows that stay on their screen (Craft apps in an iframe)

## Status

Proposed on 2026-11-13, waiting for Benoit's answers to questions 1 and 2
("Questions" section). The text follows the recommended choices (A and A). Branch
`screens/single-screen-apps`. Issue [#482](https://github.com/Kazo-ca/Os/issues/482).

- **Builds on:** [ADR 0178](0178-extended-screens-in-display-surfaces.md)
  (extended screens, window portal), [ADR 0179](0179-per-screen-taskbars-and-other-screen-menus.md)
  ("Other screen" menu, Start menu of each screen), [ADR 0183](0183-drag-a-window-between-screens.md)
  (drag a window from one screen to another) and [ADR 0234](0234-window-styles-inspired-by-winforms.md)
  (WinForms-like window options).
- **Concerns:** the shared Craft module ([ADR 0259](0259-shared-craft-app-host-module.md))
  and Kazo Word.

## Context

- Benoit noticed that Kazo Word misbehaves when its window goes to another Kazo OS
  screen, that is another browser window.
- To change screen, the portal (`WindowPortal.js`) moves the window's element into
  the document of the other browser window. Its Blazor components are not
  recreated, but the browser always reloads an `<iframe>` removed from its
  document (same cause as [ADR 0244](0244-stable-window-order-in-the-page.md),
  within one page).
- A Craft app (WordCraft, GridCraft…) runs in an iframe, behind a proxy that talks
  to it through a `MessagePort`. After the reload, the app starts again empty, the
  port is lost, and so are the open document and its unsaved state.
- A window changes screen by four paths: the "Other screen ▸ Screen N / New
  screen" menu, the "previous / next screen" shortcuts, dragging between screens,
  and opening an app from the Start menu of a secondary screen. Closing a screen
  brings its windows back to screen 1.
- The real fix, according to Benoit, is to compile our own WebAssembly build of
  each Craft app with Kazo OS support, without iframe or proxy, in a future update.
  Until then, the screen change must be blocked and the user told why.

## Decision

1. **A new window option.** `WindowOptions.CanChangeScreen`
   (`window.canChangeScreen` in `manifest.json`, written only when `false`), true
   by default. An app declares it in its manifest, or changes it at run time with
   `IAppWindowGateway.SetOptionsAsync`, like the other ADR 0234 options. The
   window manager carries it in `WindowFrame.CanChangeScreen`
   (`UserCanChangeScreen`).

2. **The window manager enforces it.**
   - `WindowRuntime` refuses `MoveWindowToDisplayCommand` (no event) when a
     window of the modal family cannot change screen.
   - A window created with this option opens on screen 1, even from the Start
     menu of a secondary screen.
   - Closing a screen still brings its windows back to screen 1: the OS does it,
     not the user.
   - An app that turns the option off while its window is already on a secondary
     screen leaves it there: bringing it back would reload the iframe too.

3. **Warning.** For such a window, the "Other screen" submenu only offers a
   greyed-out entry: "Not available yet for this app: known issue, a fix is in
   progress". A screen shortcut, or a window dropped on another screen, does not
   move it: it stays where it is, no outline is drawn on the other screen during
   the drag, and a notification shows the same text.

4. **Who sets it.** Every app that shows a Craft frame declares it in its
   manifest: Kazo Word first (version 0.1.1), then the apps ported with the Craft
   module ([ADR 0259](0259-shared-craft-app-host-module.md)), whose documentation
   and port guide ask for it. The module does not change it by itself:
   `CraftFrame` does not know the window, and a manifest declaration applies from
   the opening, before the iframe exists. An older OS refuses a manifest with an
   unknown field: the app therefore requires the OS version that brings this
   option (`sdkCompatibility.minimumVersion`).

5. **Way out.** The option stays in the SDK after Benoit's fix: an app that
   depends on its document (an iframe, a running player) may need it. The Craft
   apps will remove it from their manifest when they no longer use an iframe.

## Questions asked to Benoit

1. **How does the OS block the screen change?** A (recommended): an option
   declared by the app (this ADR). B: automatic detection of any iframe in the
   window, which fixes Kazo Word 0.1.0 without updating it but also affects the
   Browser and depends on the DOM. C: both.
2. **Which warning?** A (recommended): the greyed-out entry with the text, and a
   notification when the window is dropped on another screen. B: the menu stays
   enabled and a click shows the notification. C: the entry is hidden, with no
   message.

## Consequences

- Craft apps no longer lose their document when the user tries to change their
  screen. In return, they stay on screen 1 until Benoit's fix is in place.
- Kazo Word 0.1.0, already published, stays affected until the OS version that
  brings this option and Kazo Word 0.1.1.
- Public API addition (`WindowOptions.CanChangeScreen`,
  `WindowFrame.CanChangeScreen`): a minor SDK version.
- `bug` issues: Kazo-ca/Os#482, Os-App-Word#19, Os-App-Spreadsheets#4, and #2 in
  Os-App-PDF, Os-App-Fig, Os-App-Photo, Os-App-Design, Os-App-Deck and
  Os-App-Vector.
