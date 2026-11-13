> [Version française](../../../../fr/wasm/dev/adr/0265-direct-link-survives-update-restart.md)

# ADR 0265: the direct link survives the restart for an update

## Status

Proposed on 2026-11-13. Series `app-link-reload/base`. The questions are asked
to Benoit in the project thread (see [Questions](#questions-asked-to-benoit));
the code follows the recommendations while waiting for his answers.

- **Partly supersedes:** [ADR 0258](0258-direct-app-launch-link.md) §2
  ("Reloading the page, or restarting it for an update, therefore does not run
  the install again" and "nothing is written to storage"). The rest of ADR 0258
  does not change.
- **Builds on:** [ADR 0239](0239-start-on-the-latest-version.md) (update at
  boot, launch gate), [ADR 0223](0223-restart-onto-the-new-version.md) (restart
  onto the new version) and [ADR 0195](0195-start-menu-power-button.md)
  ("Restart" of the Start menu).

## Context

Benoit, 2026-11-13 (translated):

> When we use the link to try an application, the page sometimes refreshes for
> an update (for example: we visited it on 3.3.3 without creating an account,
> then we visit the link when the site is on 3.3.4): the page refreshes for the
> update and we lose the query string.

The link `?autolaunch=<appId>` (ADR 0258) is read once at boot, kept in memory,
then removed from the address. Any reload of the tab **after** that read loses
it. The browser often keeps the previous version of Kazo OS (service worker
cache), and several paths reload the tab:

| Path | Where | Was the link already read? |
| --- | --- | --- |
| Update at boot (ADR 0239 §1) | `App.razor`, `UpdateAtBootAsync` | No: the read comes after, the address is reloaded as is. But when `version.json` answers after 5 s, or the tab already tried that version, the old version starts and reads the link itself. |
| The app does not open, a newer version waits (ADR 0239 §2) | `HostUpdateLaunchGate`, "Restart" dialog | **Yes**: the link is what launches the app. The restart lands on an address without the parameter: the app is not launched. |
| New version activated by another tab (ADR 0223) | `hostUpdate.js`, `OnActivatedElsewhere` | Yes, when the other tab updates during the install. |
| "Restart to update" of the notification, "Restart" of the Start menu | `BrowserHostRestart` | Yes. |
| Boot failure retried (`index.html` boot guard) | `location.reload()` | No: .NET has not started yet. |

Benoit's most likely case is the second row: the old version starts, reads and
removes the link, opens the guest session, installs the app; the launch gate
then refuses the app because a newer version waits, and "Restart" reloads an
address without `?autolaunch=`.

## Decision

### 1. The link read is also kept by the tab

When the host reads a valid link from the address, it keeps it in memory
(`EcosystemHostState.PendingAppLaunch`) **and** in the tab's `sessionStorage`,
under the key `kazoos.appLaunchLink` (value: the app id). The address still
loses the parameter (ADR 0258 §2).

`sessionStorage` survives reloads of the tab, not its closing, and is not
shared with other tabs: only the tab the link opened can run it again
(question Q1).

### 2. At boot, the address first, then the tab

At boot, at the same moment as before (after the update at boot, before the
first screen):

1. a link in the address wins, and replaces the tab's;
2. otherwise the link kept by the tab is read again; its id goes through
   `AppLaunchLink.IsValidAppId` again, and a malformed value is erased;
3. the rest does not change: guest start or the sign-in screen's notice, then
   install and launch, or the Store at the app's page.

### 3. Erased once the link has finished

The kept link is erased when its handling ends: app launched, Store opened,
error notified, or **Cancel**. A reload **after** that runs nothing (as in
ADR 0258).

One exception: when the app could not open because a newer Kazo OS waits
(ADR 0239 §2), the link stays kept. "Restart" reloads the tab onto the new
version, which runs the link again: the app, already installed, is launched
without a new download. "Later" erases the link (question Q2).

### 4. What does not change

- The link still carries only an app id of the signed official catalog.
- A manual reload (F5) **during** the handling runs it again; after, it does
  nothing.
- Closing the tab forgets the link.

### 5. Limit

The version **already running** reads the link and reloads the tab. A cached
copy of Kazo OS 3.3.3 or older therefore does not keep the link: the fix
protects updates starting from the version that contains it.

## Implementation

- `Kazo.Os.Platform.Core.AppLinks.AppLaunchLink.TryFromAppId(appId, out link)`:
  reads a kept id again (same rules as `TryParse`).
- `IAppLaunchLinkStore` (host `Kazo.Os.Ecosystem.Host`): `ReadAsync`,
  `SaveAsync`, `ClearAsync`. `BrowserAppLaunchLinkStore` calls `sessionStorage`
  directly through Blazor's JS interop (no new JS file) and never throws: a
  refused storage (strict private browsing) falls back to the behaviour of
  ADR 0258.
- `App.razor`: reads the address then the tab (§2), erases at the end of the
  link's handling unless the update dialog opened (§3), erases on "Later".
- Tests: `AppLaunchLinkTests` (`TryFromAppId`), tests of the address-then-tab
  order, and an end-to-end test of the trimmed publish: the tab is reloaded
  during the install the link started, and the app opens anyway.

## Questions asked to Benoit

Asked in the project thread on 2026-11-13.

- **Q1. Where to keep the link between two loads?** A the tab's
  `sessionStorage` (recommended: survives reloads, forgotten on close, not
  shared); B `localStorage` with a lifetime (survives closing, but an app could
  open later when the user no longer remembers the link); C leave the parameter
  in the address until the link has finished (simple, but an address copied
  meanwhile runs the link elsewhere).
- **Q2. The app is refused because a newer version waits.** A keep the link
  until the restart and open it on the new version (recommended); B forget it:
  the user opens the link again.

## Work items of the series `app-link-reload/base`

| # | Branch | Content |
| --- | --- | --- |
| 0 | `app-link-reload/0-adr-keep-link-across-restart` | This ADR, indexes, memory. |
| 1 | `app-link-reload/1-keep-link-across-restart` | Code, unit and end-to-end tests, doc `dev/app-launch-link.md`, change entry, `features.md`. |

## Task list

- [x] Find every path that reloads the tab.
- [x] Write this ADR (fr + en) and update the indexes.
- [x] Ask Benoit the questions in the thread.
- [ ] Work item 1: keep the link in the tab, tests, doc.
