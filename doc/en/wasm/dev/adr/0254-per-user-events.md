> [Version française](../../../../fr/wasm/dev/adr/0254-per-user-events.md)

# ADR 0254: per-user events, visible by the user and the administrators, attached to a VM or a program

## Status

Proposed on 2026-11-11, for the Os-x86 feature
([Kazo-ca/Os-x86](https://github.com/Kazo-ca/Os-x86)), lot 1 of the
`feature/base` series. The recommendation of each question asked to Benoit
is applied while waiting for his answer. Source decisions: 137 ("the user's
personal events, such as a granted permission or an update of their VM, are
visible by them and by the administrators; system events without personal
data go to the general log") and Os-x86 guidelines §16 ("update, permission
granted, file conflict resolved … attached to a VM or a program; neither a
universal access to every user's details, nor a log reserved to
administrators"), 2026-11-11. The personal journal is V1; only a complete
viewer may wait.

- **Completes:** [ADR 0045](0045-logs-as-files.md) (logs as files,
  `/var/log`), [ADR 0100](0100-sensitive-settings-and-protected-write-audit.md)
  (`audit.log`), [ADR 0247](0247-feature-declared-roles.md) §4 and
  [ADR 0253](0253-marked-sender-on-launch-and-open.md) (sender).
- **For:** the Os-x86 journal (network permission granted or refused to a
  program, VM started, stopped, updated, image downloaded, console opened,
  admin rule applied) and BoxedWine's (program added, installed,
  uninstalled).

## Context

- Kazo OS logs are files (ADR 0045): `/var/log/syslog` and
  `/var/log/apps/<appId>.log` readable by everyone (0644,
  `Diagnostics/LogFileTarget.cs:18-19, 52-60`), `auth.log` and `audit.log`
  reserved (0600, :21-22). An app writes to **its** file through
  `IAppExecutionContext.Logging.Log(severity, message, properties)`
  (`AppGatewayContracts.cs:259-262`), with the app identifier and the pid,
  **without a user identifier** (`Execution/AppLoggingGateway.cs:66-74`).
- There is **no per-user log**: what BoxedWine would write ("Alice launched
  setup.exe") is readable by Bob in `/var/log/apps/org.kazoos.boxedwine.log`,
  and the Error Log Viewer, a system app with `filesystem.system.read`,
  reads everything for everyone
  (`Apps/System/Kazo.Os.Apps.ErrorLogViewer/LogFileReader.cs:42-64`).
- Notifications (`AppNotificationGateway.cs:37-61`) are per user but in
  memory, 100 per user, lost at reload (`InMemoryNotificationQueue.cs:6-9`):
  not a journal.
- Os-x86 events carry personal data (a program's name, a network
  destination, a VM's name) and a **subject** (a VM, a program, a bottle)
  the user wants to find again ("what happened with my Windows VM
  yesterday?").

## Decision

### 1. A per-user events journal

- File `/var/log/users/<guid>/events.log` (stable user identifier,
  ADR 0169; never the login name), owned by the user, mode **0600**; the
  `/var/log/users/<guid>/` folder is created at the first write,
  `system:system 0711` so that other users cannot list the folders. An
  administrator reads every folder with `sudo` or the viewer's elevation
  (§3), as for `auth.log` (ADR 0045).
- Rotation like the other logs (`LogRotationPolicy`, 1 MiB, 5 archives);
  same bounded channel and batched flush (`LogFileWriter`).
- Account deletion (ADR 0169): the folder is deleted with the account (the
  "account deletion event for Features" ADR, Os-x86's V2, warns the Feature
  first).

### 2. Writing an event: `KazoEvents`

```csharp
public static class KazoEvents
{
    public static void Write(KazoUserEvent e);   // into the journal of the session user
}
public sealed record KazoUserEvent(
    string Kind,                 // "x86.network.allowed", "x86.vm.started", "boxedwine.program.added"
    string Message,              // readable text, localized by the app
    KazoEventSubject? Subject = null,    // (Type, Id, DisplayName): "vm", "program", "prefix", "file"
    KazoEventSeverity Severity = Info,   // Info, Warning, Error
    IReadOnlyDictionary<string, string>? Properties = null);
```

- Permission: **`events.write`**, a new **normal** catalog capability (an
  app only writes to the journal of the user it runs for; nothing else is
  reached). The OS adds the time, the app identifier (from the context,
  never from the caller), the pid and, when it exists, the sender of
  ADR 0253. Line format: that of `LogLineFormat` (ADR 0045), with `kind`,
  `subject.type`, `subject.id`, `subject.name` and `sender` in the
  properties block.
- The OS itself writes user events for its own personal actions: dangerous
  permission granted or refused for that user (ADR 0049 §5, in addition to
  `auth.log`), role assigned or revoked (ADR 0247 §4), app installed for
  oneself (ADR 0208), synchronization conflict resolved (ADR 0101), sensitive
  Feature operation authenticated (ADR 0247 §4, `Subject` = what the Feature
  passed).
- **System events without personal data** stay in `syslog` and
  `apps/<appId>.log` (ADR 0045); an app chooses one or the other by the
  nature of the event (the guide says: "if the event names a file, a program
  or a destination of the user, it is a user event").

### 3. Reading: the user and the administrators

- **"My events" tab** in the Error Log Viewer (V1, minimal): chronological
  list of the session user's journal, filter by app, by severity and by
  **subject** (a VM, a program), search; an administrator sees a user
  selector (reading `/var/log/users/<other>/events.log` after the OS
  authentication, one ticket per viewer session, logged in `auth.log`
  `events.read-other user=`). The complete viewer (grouping by subject, link
  to the VM page in Settings ▸ Features, export) is a later work item.
- An app reads **its own** user events through `KazoEvents.ReadAsync(filter)`
  (permission `events.write` as well: it only sees its lines, filtered by
  the OS on the app identifier): BoxedWine shows a program's "History";
  Os-x86's rules page shows a bottle's past decisions.
- `cat ~/.events` does not exist: the file is under `/var/log/users/`,
  readable through `cat /var/log/users/<my guid>/events.log` with the
  ordinary file rights.

### 4. Attachment to a subject

- `KazoEventSubject(Type, Id, DisplayName)`: `Type` free but recommended
  (`vm`, `program`, `prefix`, `file`, `app`), stable `Id` (VM GUID, Os-x86
  program identifier: path + hash), `DisplayName` at the time of the event
  (the name may change). The viewer groups by `(app, Type, Id)`.
- A notification (ADR 0197) may carry the same subject
  (`NotificationAction.LaunchApp` with `--subject=`): the "X awaits your
  answer" bubble leads to the subject's page; outside this ADR.

## Rejected options

- **A. Everything in `/var/log/apps/<appId>.log`**: readable by everyone;
  137 and §16 forbid it for personal events.
- **B. A file in the home folder (`~/.local/state/kazoos/events.log`)**: any
  app with `filesystem.user-home.write` (normal) could erase or forge it
  (same root as issue #433); `/var/log` is protected by the audit of
  ADR 0100.
- **C. Notifications as the journal**: in memory, 100 per user, lost at
  reload.
- **D. One log per app and per user** (`/var/log/users/<guid>/<appId>.log`):
  more files for nothing; the filter by app is enough, and the OS also has
  its own user events.
- **E. Visible by the user only**: 137 wants the administrators too (after
  authentication).

## Consequences

- SDK: `KazoEvents` (`Write`, `ReadAsync`), `KazoUserEvent`,
  `KazoEventSubject`, `KazoEventSeverity`; `events.write` capability
  (normal, catalog, ADR 0049 §1: classification sentence);
  `PublicAPI.Unshipped.txt`, `sdk-compat` fixture.
- Platform: `LogFileTarget.ForUser(guid)` (0600, folder 0711),
  `AppUserEventsGateway`, events written by the OS (permissions, roles,
  installation, conflicts, Feature authentication), deletion with the
  account.
- Error Log Viewer: "My events" tab, user selector for an administrator with
  authentication.
- Docs: [`app-log-files.md`](../../framework/app-log-files.md) ("User
  events" section), [`logging.md`](../logging.md),
  [`capabilities.md`](../../framework/capabilities.md), `features.md`,
  `changes/unreleased` entry.
- Tests: `Write` writes to the session user's file with the context's app;
  another user cannot read the file (0600, folder 0711); `ReadAsync` returns
  only the app's lines; the administrator reads after authentication and
  `auth.log` says so; rotation; account deletion deletes the folder; the OS
  writes the event of a granted permission.

## Questions asked to Benoit

1. **Location of a user's journal?**
   - A) **(Recommended)** `/var/log/users/<guid>/events.log`, 0600, folder
     0711, protected by the `/var` audit (ADR 0100).
   - B) `~/.local/state/kazoos/events.log` in the home folder: synchronized
     with the user's files, but erasable by any app with a normal permission.
2. **The `events.write` capability: normal?**
   - A) **(Recommended)** Normal: an app only writes to the journal of the
     user it runs for, and its own lines.
   - B) No capability at all (like `Logging` today): simpler, but Settings ▸
     Capabilities would not show which apps log.

Applied while waiting: 1 A, 2 A.
