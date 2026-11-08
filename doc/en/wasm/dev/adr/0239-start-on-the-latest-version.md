> [Version française](../../../../fr/wasm/dev/adr/0239-start-on-the-latest-version.md)

# ADR 0239: start on the latest version

## Status

Accepted on 2026-11-08, asked by Benoit in the thread "Réparer le plantage
d'IRC sans service worker": "At start, an application (and the OS) must make
sure it is on the latest version, otherwise we block with a message to restart.
When the OS starts, we restart showing `Mise à jour`". Branch
`apps/failure-in-window-and-newer-worker`.

- **Complements:** [ADR 0174](0174-announce-a-new-version-to-open-sessions.md)
  (reading `version.json`), [ADR 0223](0223-restart-onto-the-new-version.md)
  (restart onto the new version) and [ADR 0236](0236-build-number-and-version-status-at-sign-in.md)
  (build number, checked at boot).
- **Goes with:** [ADR 0237](0237-activate-the-worker-of-a-newer-page.md) and
  [ADR 0238](0238-an-app-failure-stays-in-its-window.md).

## Context

The service worker serves a whole copy of one version (page included). After a
deployment, a normal reload restarts on the old copy; the OS only shows
"Restart to update", every 5 minutes or as soon as the sign-in screen opens
(ADR 0236). An app opened from that copy can load files of another version (the
IRC crash of 2026-11-08). Benoit wants the OS and every app to start only on
the latest version.

## Decision

1. **When the OS starts.** Before the first screen, the host reads
   `version.json` (at most 5 s, `HostBootUpdateCheck.DefaultTimeout`). When
   another version or build is served (same rule as ADR 0236), the boot screen
   shows "Updating Kazo OS" ("Mise à jour de Kazo OS"), the new service worker
   downloads the version (ADR 0223), then the OS restarts onto it
   (`BrowserHostRestart`). With no answer in time (offline, slow server) or a
   failed download, the OS starts the version it has, with the usual notice.
2. **No loop.** Before restarting, the host writes the target (`3.3.0+812`) in
   the tab's `sessionStorage` (`hostRestart.js`, `claimBootUpdate`). When the
   restart did not land on that version (a deployment in progress), the tab
   does not try again for the same target and starts normally.
3. **When an app starts.** `AppLifecycleOrchestrator.LaunchAsync` asks the
   `IAppLaunchGate` barrier (Platform.Core) before loading the app, for
   interactive launches of `Window` and `Web` apps that are not running yet.
   The browser host provides `HostUpdateLaunchGate`: it reads `version.json`
   again when its last answer is older than 30 s (waiting 3 s at most); when the
   OS is outdated, the launch is refused (`AppLaunchStatus.UpdateRequired`, code
   `lifecycle.os.update-required`, "A new version of Kazo OS is available.
   Restart to open "IRC".") and the host shows the "Update required" dialog with
   *Restart to update* and *Later*.
4. **What is not blocked.** An app already open (it gets the focus), services
   and terminal commands: they are part of the running system. Offline, nothing
   is blocked.

## Consequences

- A deployment no longer lets a tab start on the old copy: the first start
  after the deployment goes through "Updating Kazo OS".
- The boot waits for `version.json` (one request, a few milliseconds normally,
  5 s at most).
- Every app opening reads `version.json` again at most every 30 s.
- `AppLaunchStatus` gains `UpdateRequired`; `IAppLaunchGate` and
  `AppLaunchRefusal` are public in Platform.Core (not in the app SDK).

## Tasks

- [x] `IAppLaunchGate`, `AppLaunchRefusal`, `AppLaunchStatus.UpdateRequired`,
  call in `AppLifecycleOrchestrator.LaunchAsync`.
- [x] `HostBootUpdateCheck`, `HostUpdateLaunchGate` (Platform.Blazor),
  registered in `KazoOsBrowserHostExtensions`.
- [x] `App.razor`: "Updating Kazo OS" at boot, "Update required" dialog;
  `hostRestart.js`: `claimBootUpdate`.
- [x] Tests: `HostUpdateAtStartTests`, `AppLifecycleOrchestratorTests.LaunchGate`.
- [x] Checked in Chromium: two publishes, the second served after the first,
  the boot shows "Updating Kazo OS" and restarts onto the second.
