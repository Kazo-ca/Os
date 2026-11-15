> [Version française](../../../../fr/wasm/dev/adr/0244-stable-window-order-in-the-page.md)

# ADR 0244: keep the window order stable in the page

## Status

Accepted on 2026-11-13. Benoit chose option A to question 14 of the Kazo Word
thread: fix Kazo OS rather than work around the problem in the app. Branch
`windows/stable-dom-order`.

- **Complements:** [ADR 0178](0178-extended-screens-in-display-surfaces.md) §3
  (windows moved from one screen to another by a portal, without recreating
  their components) and [ADR 0171](0171-mobile-file-explorer-and-full-screen-dialogs.md)
  §3 (one surface on mobile, the owner stays rendered under its dialog).

## Context

`WindowRuntime.Windows` gives the windows from back to front (sorted by
`ZOrder`). `DesktopArea` rendered them in that order, in a `@key` list. When a
window comes to the front, its `ZOrder` grows: the list changes order and
Blazor **moves** the window's element in the page. A browser reloads an
`<iframe>` moved in the document. An app that lives in an iframe therefore
loses its state on every click on another window: Kazo Word loses its
document, the OS browser reloads its page.

The visual stacking does not depend on the order in the page: each window gets
its `z-index` from the `--window-z` variable (`WindowHost.razor.js`,
`projectWindowGeometry`).

On mobile, `SingleSurfaceArea` rendered only the window on screen (and its
owners under a dialog): switching apps removed the other window from the page,
and its iframe started over on return.

## Decision

1. **Opening order in the page.** The window areas (`DesktopArea`,
   `SingleSurfaceArea`) render their windows in the order they appeared, kept
   by a `WindowDocumentOrder` (Windowing Core): a new window is added at the
   end, a closed window leaves the list, a window that comes to the front
   **does not move**. The `Windows` parameter keeps its contract (back to
   front); only the render order changes.
2. **`ZOrder` only drives `z-index`.** The visible stacking, clicks, previews
   and peek (ADR 0199) do not change.
3. **Mobile.** `SingleSurfaceArea` renders every window, in the same stable
   order; the ones not on screen stay in the page, hidden (`hidden`, inert),
   like a minimized desktop window. Switching apps no longer reloads an iframe.
   The memory cost becomes the desktop's, where minimized windows stay
   rendered.
4. **Keyboard and accessibility.** The reading and Tab order between windows
   becomes the opening order. A window that receives the keyboard focus (Tab or
   Shift+Tab from another window) **comes to the front and becomes active**,
   as on a real desktop: the focus is never in a window hidden behind another.
   A click or Tab into the iframe of a background window also brings it to the
   front: the page then gets no click and no `focusin`, only its own blur, which
   `trackFocus` (`WindowHost.razor.js`) turns into a `focusin` on the iframe.
   A window blocked by a modal dialog stays inert and does not receive the
   focus. Alt+Q, Alt+\` and the taskbar still give the focus to the activated
   window (ADR 0105 §4).

## Questions asked to Benoit (answered on 2026-11-13: A and A)

- **Q1 (keyboard)**: **A, the window that receives the focus comes to the
  front** (recommended, chosen); B, change nothing; C, trap Tab in the active
  window.
- **Q2 (mobile)**: **A, also fix mobile in the same PR** (recommended, chosen);
  B, desktop only, a `TODO` issue for mobile.

## Consequences

- An iframe (Kazo Word, the OS browser, iframe apps) keeps its page and its
  state when its window comes to the front or goes behind, on desktop and on
  mobile. It still reloads when its window changes screen (ADR 0178 §3): that
  is a real move to another document.
- Tests can no longer assume that the last window in the page is the front
  one: they read `is-focused` or `data-window-z`.
- No change to the app SDK. Windowing Core gains a public type
  (`WindowDocumentOrder`).

## Tasks

- [x] ADR (FR + EN) and index.
- [x] `WindowDocumentOrder` and its unit tests.
- [x] `DesktopArea` and `SingleSurfaceArea` render in the stable order.
- [x] A window that receives the keyboard focus comes to the front.
- [x] E2E test: an iframe does not reload when its window comes to the front.
- [x] Docs (`window-portal.md`, `features.md`) and change entry.
