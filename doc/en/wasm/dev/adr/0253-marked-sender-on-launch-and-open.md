> [Version française](../../../../fr/wasm/dev/adr/0253-marked-sender-on-launch-and-open.md)

# ADR 0253: OS-marked sender on `LaunchAsync` and file open, for the journal only

## Status

Proposed on 2026-11-11, for the Os-x86 feature
([Kazo-ca/Os-x86](https://github.com/Kazo-ca/Os-x86)), lot 1 of the
`feature/base` series. The questions asked to Benoit got their answers on
2026-11-11 (series 15 of the Os-x86 design); they are recorded at the end of
this ADR and the decision applies them. Source decisions: 52 ("the OS marks
the sender (small ADR)"), 108 ("no new request and no permission ceiling based
on the launcher") and 186 A ("the marked sender serves the journal only"),
2026-11-11.

- **Completes:** [ADR 0096](0096-added-intents-and-app-defined-intents.md)
  (intents, `AppIntentDelivery.SenderAppId` for app-defined intents),
  [ADR 0245](0245-feature-service-registry-and-async-resolution.md) (identity
  of a service's caller) and [ADR 0254](0254-per-user-events.md) (per-user
  journal).
- **For:** the Os-x86 journal (who asked for a program launch: the terminal,
  BoxedWine, another app, a shortcut) and BoxedWine's.

## Context

- `AppIntentDispatcher` (`Platform/Kazo.Os.Platform.Core/Intents/AppIntentDispatcher.cs`)
  builds for `LaunchAsync` (:218) and the file open (:241-242) an
  `AppLaunchRequest(appId, principal, arguments)` **without a sender**; only
  `apps.launch` is checked on the caller (:206-207, :212-213, :225). The
  other intents (Reveal :380-382, Edit :419-421, OpenUri :473-475,
  ShowSettings :503-505) already pass `AppIntentDelivery(…, SenderAppId:
  request.CallerAppId)`.
- The OS **knows** the caller: `AppIntentRequest(RequestId, CallerAppId,
  UserId, Intent)` (`Shared/Kazo.Os.App.Abstractions/AppIntents.cs:221-225`),
  `CallerAppId` set by the OS when building the context
  (`AppExecutionContextFactory.cs:255`, `AppIntentGateway.cs:15-29`). The
  receiver reads `IAppExecutionContext.Intent` (:144).
- A `.desktop` shortcut launches an app with any arguments
  (`KazoDesktopEntry.cs:29, 55-75`; launch by the explorer,
  `ExplorerFileView.Desktop.cs:475`): the launched app does not know whether
  the user or a file launched it.
- A notification action launches an app with arguments without checking the
  app that posted the notification (`NotificationActionRunner.cs:37-44`).
- The Os-x86 design once planned a permission ceiling based on the launching
  app (D21, D52); Benoit discarded it (108): the launch origin changes
  neither the bottle nor the program's permissions. What remains is the need
  to **log** who asked for what.

## Decision

### 1. The OS marks the sender

- `AppLaunchRequest` and the delivery of a launch or of a file open gain a
  `Sender`: `KazoIntentSender(AppId, UserId, Kind)` with `Kind` ∈ `App` (an
  SDK call), `Shell` (a terminal's shell, `AppId` = the Terminal app),
  `Desktop` (double-click, Start menu, `.desktop` shortcut, "Open with":
  `AppId` = the app that held the view, explorer or desktop), `Notification`
  (`AppId` = the app that posted the notification), `System` (automatic
  start, service).
- The `Sender` is **set by the OS**, never supplied by the caller: the
  dispatcher takes it from `AppIntentRequest.CallerAppId` and the context; a
  caller cannot pass itself off as another.
- The receiver reads it in `IAppExecutionContext.Intent?.Sender` (window and
  service) and in `TerminalExecutionContext.Sender` (command); an app
  launched without an intent (desktop start) has `Sender.Kind = Desktop`. A
  service provided by a Feature (ADR 0245) gets it through
  `IFeatureServiceContext.Caller` (`AppId`, `UserId`), same structure.

### 2. For the journal only

- The `Sender` **enters no permission decision**: neither in
  `DangerousCapabilityEvaluator`, nor in Os-x86's choice of a bottle, nor in
  the network permission window (108, 186 A). An app that used it to
  restrict would make a rule of its own, outside the OS.
- The OS writes the `Sender` to `/var/log/syslog` at every launch
  (`apps: launched <app> by <sender.kind>:<sender.appId> user=<guid>`) and
  into the user's event (ADR 0254) when the receiver writes one.
- Nothing is hidden: every launched app knows its sender; it is not sensitive
  data (an app identifier, not a secret).

### 3. What does not change

- `apps.launch` stays the only permission checked; the candidate order of an
  association and the confirmation of a default-app change are unchanged;
  `AppIntentDelivery.SenderAppId` of app-defined intents stays and becomes an
  alias of `Sender.AppId`.

## Rejected options

- **A. A permission ceiling based on the sender** (original D21, D52):
  discarded by Benoit (108); and every installed app has the same rights as
  the OS in the page (`KazoAppPackages.cs:20-23`), a ceiling would be in
  good faith only.
- **B. Let the caller put its identity in the arguments** (`--from=…`):
  forgeable; the OS already has `CallerAppId`.
- **C. Mark nothing**: the Os-x86 journal could not tell a launch by the
  user in the terminal from a launch by an app; 52 asks for it.

## Consequences

- `AppLaunchRequest.Sender`, `KazoIntentSender`, `IAppExecutionContext.Intent.Sender`,
  `TerminalExecutionContext.Sender`, `IFeatureServiceContext.Caller` (SDK,
  `PublicAPI.Unshipped.txt`, `sdk-compat` fixture); `AppIntentDispatcher`
  (`DispatchLaunchAsync`, `DispatchOpenFileAsync`), the shell
  (`ShellInterpreter.SimpleCommands.cs:259-309`, ADR 0251), the explorer and
  the Start menu, `NotificationActionRunner` (sender = the app that posted),
  `AppLifecycleOrchestrator` (log).
- Docs: [`app-intents.md`](../../framework/app-intents.md), `features.md`,
  `changes/unreleased` entry.
- Tests: `LaunchAsync` from an app → `Sender.Kind = App` and its id; `./x.exe`
  → `Shell` and the Terminal app; double-click → `Desktop`; notification
  action → `Notification` and the posting app; a `--from=` argument has no
  effect; log written.

## Question asked to Benoit

1. **The `.desktop` shortcut and the notification action: which sender?**
   - A) **(Recommended)** `Desktop` with the app that held the view
     (explorer, desktop) for the shortcut; `Notification` with the posting
     app for the action: that is what the OS knows for sure.
   - B) `Desktop` and `Notification` without an `AppId` (the OS only):
     simpler, less information in the journal.

Maintainer's answer (2026-11-11): 1 A.
