> [Version française](../../../../fr/wasm/dev/adr/0238-an-app-failure-stays-in-its-window.md)

# ADR 0238: an app's failure stays in its window

## Status

Accepted on 2026-11-08; Benoit chose option A ("2. A") in the thread "Réparer
le plantage d'IRC sans service worker". Branch
`apps/failure-in-window-and-newer-worker`.

- **Goes with:** [ADR 0237](0237-activate-the-worker-of-a-newer-page.md) (the
  cause of the IRC crash) and [ADR 0239](0239-start-on-the-latest-version.md).
- **Similar to:** [ADR 0127](0127-priority-and-not-responding-dialog.md)
  §3 (the "not responding" dialog covers the affected window only).
- **Does not change:** `HostErrorBoundary` stays the host's barrier for its own
  screens (sign-in, desktop, taskbar); what its error page shows is ADR 0240.

## Context

On 2026-11-08, the IRC app could not import
`_content/Kazo.Irc/Components/MessagePane.razor.js` (404, see ADR 0237). The
`JSException` thrown in `MessagePane.OnAfterRenderAsync` went up to the OS's
only `ErrorBoundary`, the host's (`App.razor`), which replaced the **whole**
screen with "An unexpected host failure interrupted the current surface". A
third-party app could therefore stop the whole system with a simple error,
which no real OS allows: a program that crashes closes on its own.

## Options asked to Benoit

- **A** Only the app's window shows ""App" ran into a problem" with its
  buttons; the rest of the OS keeps running. **Chosen.**
- **B** Close the window automatically and post a notification.
- **C** Keep the current behavior (the whole OS stops).

## Decision

1. **One barrier per window.** `WindowAppRenderer` (desktop, mobile, remote
   rendering of ADR 0129) and `DesktopSurfaceHost` (desktop surface app) put
   `AppErrorBoundary` (`Platform.Blazor/Windows`) around the app's entry point,
   inside the cascading values (context, menus, window gateway). An exception
   thrown by an app component while it renders, in a lifecycle method
   (`OnAfterRenderAsync` included) or in an event handler stops there.
2. **What the window shows.** The window's content is replaced by a panel:
   ""IRC" ran into a problem.", "An error stopped this window. The rest of Kazo
   OS keeps running.", then the exception's type and message (selectable
   text). The title bar, the other windows and the taskbar do not change.
3. **Two buttons.** *Try again* creates the app's components again in the same
   window and process (`ErrorBoundaryBase.Recover`). *Close the app* stops its
   process with the reason `Fault` (`AppLifecycleOrchestrator.StopAsync`),
   which closes its windows.
4. **Log.** The failure is written with the app's id and the full exception
   (`ILogger<AppErrorBoundary>`, so `/var/log/syslog` and the browser console).

## Rejected options

- **B (automatic close):** the user loses the window without seeing the error
  and cannot try again.
- **C:** a third-party app stops the whole system.

## Consequences

- An app that crashes now affects its window only; its services and other
  windows keep running until *Close the app*.
- *Try again* does the same thing again: if the cause remains (a missing file),
  the panel comes back.
- Exceptions thrown outside rendering (a task started without `await`, a timer)
  do not go through a Blazor barrier; they already stayed outside
  `HostErrorBoundary`.

## Tasks

- [x] `AppErrorBoundary.razor` (+ `.razor.cs`, `.razor.css`), texts
  `AppFailure.*` in English and French.
- [x] `WindowAppRenderer` and `DesktopSurfaceHost` use it.
- [x] Tests: `AppErrorBoundaryTests` (working app, crashing app, no process to
  close, in French).
- [x] Docs: [`process-priority-and-unresponsive-windows.md`](../process-priority-and-unresponsive-windows.md), indexes, `features.md`.
