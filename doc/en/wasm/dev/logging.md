> [Version française](../../../fr/wasm/dev/logging.md)

# File logs (`/var/log`)

The log files an app produces and the APIs that write them are in [`app-log-files.md`](../framework/app-log-files.md).

Implementation of [ADR 0045](adr/0045-logs-as-files.md) and section 5.3 of the
[overhaul plan](plans/ecosystem-rebuild-plan.md).

## Role

Kazo OS logs are real text files. `cat`, `tail`, `grep` and Error Log Viewer
read them with ordinary file permissions.

| File | Content | Mode |
|---|---|---|
| `/var/log/syslog` | Platform `ILogger<T>`, startup (`kernel`), host failures (`ecosystem-host`), lost lines (`logging`) | `system:system 0644` |
| `/var/log/kern.log` | Kernel log (`KernelLog`): OOM killer, browser memory guard; also copied to `syslog` and read by `dmesg` from `/dev/kmsg` (ADR 0126 §3, [`oom-killer.md`](oom-killer.md)) | `system:system 0644` |
| `/var/log/auth.log` | `IAuditLog` (`session.login`, `session.elevate`, `capability.*` from ADR 0049, `feature.authenticate`, `feature.role-denied`, `role.grant` and `role.revoke` from ADR 0247, `events.read-other` from ADR 0254…) | `system:system 0600` |
| `/var/log/audit.log` | Audit of creations, writes, renames, copies, deletions and `chmod` under `/etc`, `/bin` and `/var` (except `/var/log`), in `auditd` format (ADR 0100 §3, [`sensitive-settings.md`](sensitive-settings.md)); `ausearch` filters it | `system:system 0600` |
| `/var/log/users/<id>/events.log` | A user's event journal (ADR 0254): `KazoEvents` of the apps and the OS's events for that user (`kazoos.permission.*`, `kazoos.role.*`, `kazoos.app.installed`, `kazoos.sync.conflict-resolved`); `<id>` is the stable account identifier ([`app-log-files.md`](../framework/app-log-files.md#user-events-adr-0254)) | `<user>:<group> 0600`, directories `system:system 0711` |

## Architecture

```text
ILogger<T> ──► KazoOsDiagnosticLoggerProvider ─┐
IAppLoggingGateway ──► AppLoggingGateway ────────┼─► LogFileWriter ─► IFileSystemService ─► /var/log/…
IAuditLog ──► FileAuditLog ──────────────────────┘        │ (bounded channel, grouped flush, rotation)
HostExceptionReporter ───────────────────────────┘        ▼
                                     IDiagnosticSink (in-memory buffer of recent entries)
```

- `LogFileTarget`: `Syslog`, `AuthLog`, `AuditLog` (ADR 0100), `ForApp(appId)`, with path and mode.
- `LogFileWriter` (singleton, `Platform.Core/Diagnostics/`):
  - `Enqueue(target, line)` and `Write(target, severity, source, message, pid)`,
    synchronous, never blocking. Channel of 10,000 lines; beyond that, the
    oldest are lost and a `WARN logging: <n> entries dropped` line is written
    to `syslog` at the next flush.
  - Out of space (`NoSpace`, ADR 0099 §2): the refused batch is abandoned and
    its lines counted (`PendingNoSpaceLoss`); at the first successful flush,
    a single `WARN logging: <n> lines lost (No space left on device)` line is
    written to `syslog` ([`system-backup.md`](system-backup.md)).
  - Flush every 1 s of simulated time, as soon as 100 lines are pending, at
    logout (`SessionLoggedOutEvent`) and at `DisposeAsync`. Only one flush at a
    time per attached filesystem; only one write per file and per flush.
  - `AttachAsync(IFileSystemService)` is called by `EcosystemBootCoordinator`
    right after `EnsureRootAsync`. Before that, lines wait in the channel.
    The writer itself creates `/var/log` and `/var/log/apps` if they are
    missing.
  - Attaching a **different** filesystem abandons the previous one: its token
    is cancelled and later flushes no longer wait for it. On
    `Kazo.Os.Server`, each circuit mounts its own; a reload can cut the old
    circuit in the middle of a flush (JS call with no response), and the
    startup of the new circuit stayed blocked behind it, about one time in
    five (found by `build/verify-hosts.sh`). The lines of the abandoned batch
    remain only in the in-memory buffer.
  - A write failure is never logged to a file: it is noted once per file
    (category `logging`) in the in-memory buffer.
- `FileAuditLog` replaces `BoundedAuditLog` in the composition; `Entries` is
  still served by a 512-entry buffer.
- **User journals (ADR 0254).** `LogFileTarget.ForUser(id, group)` targets
  `/var/log/users/<id>/events.log` with an owner (`LogFileOwner`) and a
  directory mode (`DirectoryMode`, `0711`). The writer creates the file in the
  user's name, like `install -o` (only the creation uses that context; appends
  and rotation are still done by the system), and creates the directories below
  `/var/log` with that mode. `ReadLinesAsync(path)` and `DeleteDirectoryAsync(directory)`
  (a directory below `/var/log/users` only) take the flush gate, write the
  queue, then read or delete before releasing it: no flush or rotation moves a
  file meanwhile.
  - `IUserEventLog` / `FileUserEventLog` (singleton). `Write(owner, author, event)`,
    for apps, checks the event (`Validate`: one-token kind, `kazoos.*` kinds
    refused, bounded sizes, OS keys refused) and limits the rate per app and per
    user (200 in 30 s, a fixed window like journald; the extra events are
    counted and a `user-events` line in `syslog` says so).
    `TryWriteSystem(...)`, for the OS, shortens fields that are too long (`…`),
    never throws and writes a failure to `syslog`. Both redact the properties,
    add `kind`, `subject.*` and `sender`, and format the line with
    `LogLineFormat`. `ReadAsync(id, filter)` (60 reads per journal in 30 s) and
    `DeleteAsync(id)`, which never throws and ignores any event written later
    for that account.
  - `AppUserEventsGateway` (one per app instance, `AppExecutionContextFactory.UserEvents`)
    checks `events.write`, takes the app and the process from the context and
    the sender from `Intent.SenderAppId`, and reads back only the app's lines.
    In a worker: `RemoteUserEventsGateway`, operations `uev.write` (posted
    without an answer, checked in the app first) and `uev.read`.
  - The OS's events (`OsUserEvents`, texts in the session language):
    `DangerousCapabilityGate` (permission granted or refused,
    `org.kazoos.permissions`), `FeatureRoleService` (role granted or revoked,
    `org.kazoos.accounts`), `AppManagementGateway` (app installed for oneself only,
    `org.kazoos.packages`), `FileSystemSyncService` (conflict resolved, by hand
    or automatically, for the user of the pass, `org.kazoos.sync`),
    `AppFeatureAuthenticationGateway` (operation of a feature authenticated, when
    the ticket is consumed, with the subject passed to `AuthenticateAsync`,
    `org.kazoos.authentication`). Each keeps its `auth.log` or `syslog` entry
    too, and goes through `TryWriteSystem`: the journal never breaks the
    operation already done.
  - `AppUserAccountsGateway.ReadEventsAsync(login, filter)` (ADR 0254 §3): the
    signed-in user's own journal without asking; another account after an
    administrator in the OS's window, once per gateway (so per process), and
    one `events.read-other` entry per account read in `auth.log`.
  - `LocalAccountService.DeleteAsync` and `GuestAccountService.ReplaceAsync`
    delete the directory of the deleted account, even when the home stays.
- `HostExceptionReporter` (ADR 0240) writes one `CRIT ecosystem-host` line per
  unhandled failure, with `correlationId`, `phase`, `exceptionType`, `module`,
  `source`, `message` and `stack`. Message and stack go through
  `SensitiveTextRedactor` first (address passwords, `token=`, `password=`,
  `Bearer`, IRC `PASS`... replaced by `***redacted***`); the error page shows
  the same values ([ADR 0240](adr/0240-detailed-error-page.md)).
- `SimulationClockDriver` (`Platform.Core/Time/`): the production adapter
  planned by ADR 0012, which did not exist. It advances `ManualSimulationClock`
  from a platform timer (at most 50 ticks per beat, the rest is dropped when
  the tab is throttled). Without it, the periodic flush, the taskbar clock and
  the process shutdown delays never ran. It is started by
  `EcosystemBootCoordinator`. `ManualSimulationClock` now protects its
  callback queue with a lock.

## Removals

- `IPersistentDiagnosticRepository` and `IndexedDbDiagnosticRepository` are no
  longer registered; the IndexedDB audit was already not wired. The classes
  and their stores remain, and the IndexedDB schema version does not change.
- The `OS/Kazo.Os.Ecosystem` host no longer removes its `ILoggerProvider`:
  all its dependencies are singletons. The server keeps its composition (the
  `ILogger` bridge remains removed there, no filesystem is attached to it).

## Error Log Viewer

- Manifest: `filesystem.system.read` and `filesystem.system.write` (instead of
  `diagnostics.*`).
- `LogFileReader` lists `syslog` then `apps/*.log` (and the `.1` to `.5`
  archives if the "Archives" box is checked), reads and parses with
  `LogLineFormat.TryParse`, and counts foreign lines. It never reads
  `auth.log` (`sudo cat` for that).
- `audit.log` and its archives (ADR 0100) are listed for an administrator or
  an elevated session only (`LogSource.RequiresRoot`) and are not read by the
  "all logs" view. Each `type=PATH` line is parsed by `AuditLineFormat`: a
  denial becomes a warning, a successful write an information entry, with the
  app as source.
- Live tracking through `AppContext.Watch` on `/var/log` and `/var/log/apps`.
- "My events" tab (ADR 0254 §3, `UserEventsPanel`): the journal of the
  signed-in user, `/var/log/users/<AppContext.UserId>/events.log` and its
  archives, read as an ordinary file the user owns
  (`LogFileReader.ReadUserEventsAsync`). Filters by app, severity, subject
  (grouped by app, type and identifier) and text; newest first. No live
  tracking (the `0711` directory cannot be listed): "Refresh" reads again.
  An administrator (or a session under `sudo`) sees a user picker: another
  account is read through `KazoUserAccounts.ReadEventsAsync`, which asks an
  administrator once for this window (`ErrorLogViewerWindow.UserEvents.cs`).
- "Truncate" empties the selected log (never an archive). For an ordinary user
  it is refused, the window offers administrator approval (ADR 0042), then a
  second click succeeds.
- **Temporary deviation from ADR 0045 §6:** the reader goes through
  `IAppFileSystemGateway` and not `KazoFile`/`KazoFileSystemWatcher`, which
  arrive with the `rebuild/1-file-api` branch (ADR 0043). The switch to
  `Kazo*` will happen once that API is merged; the behavior does not change.

## Contract tests

- `Tests/Kazo.Os.Platform.Core.Tests/Diagnostics/LogLineFormatTests.cs`: format,
  exact ADR example, escapes, properties, audit, rejection of foreign lines.
- `.../LogFileWriterTests.cs`: creation and modes, periodic and threshold
  flush, one write per file, rotation and archives, giant batch, line loss,
  non-recursive failures, logout, shutdown, read permissions, new attachment
  that does not wait for a flush blocked on the previous one.
- `.../LogProducersTests.cs`: `ILogger`, `AppLoggingGateway`, `FileAuditLog`.
- `.../UserEventLogTests.cs` (ADR 0254): writing to the user's file with the
  context's app, owner, `0600` and `0711` modes, another user neither reads nor
  lists, reads limited to the app's lines, refusal without `events.write`,
  malformed events, rotation, deletion, reading a line back, the `KazoEvents`
  facade. The OS's events in `DangerousCapabilityApprovalTests`,
  `FeatureRoleServiceTests`, `AppPackageGatewayTests`,
  `FileSystemSyncDeletionAndConflictTests`; deletion with the account in
  `LocalAccountServiceTests`; worker in `WireCodecUserEventsTests`.
- `Tests/Kazo.Os.Platform.Core.Tests/Time/SimulationClockDriverTests.cs`.
- `Tests/Kazo.Os.Apps.ErrorLogViewer.Tests/`: manifest and `LogFileReader` on
  a real `AppFileSystemGateway`.

## Task list

- [x] `LogLineFormat` (`Format`, `FormatAudit`, `TryParse`).
- [x] `LogFileTarget`, `LogRotationPolicy` (1 MiB × 5).
- [x] `LogFileWriter`: bounded channel, grouped flush, rotation, no recursion.
- [x] `ILogger` → `syslog`, `IAppLoggingGateway` → `apps/<appId>.log`, `IAuditLog` → `auth.log`.
- [x] `HostExceptionReporter` on `syslog`.
- [x] Attach at startup and `kernel: boot` line; `/var/log/apps` created by `FileSystemSeeder`.
- [x] `SimulationClockDriver` so that simulated time advances.
- [x] Removal of `IAppDiagnosticsGateway` and the IndexedDB repositories from the composition.
- [x] Error Log Viewer reads the files.
- [x] Contract tests.
- [x] Lines lost for lack of space, counted then reported (`v3/7`, ADR 0099 §2).
- [x] User journals (ADR 0254): `LogFileTarget.ForUser`, `FileUserEventLog`, `KazoEvents`, `events.write`, worker.
- [x] Events written by the OS: permissions, roles, install for oneself, synchronization conflicts.
- [x] Journal deleted with the account.
- [x] Error Log Viewer: "My events" tab.
- [ ] Move Error Log Viewer to `KazoFile` after the ADR 0043 merge.
