> [Version française](../../../fr/wasm/framework/app-log-files.md)

# App logs (`/var/log`): API and files produced by an app

This document describes the log files that an app, a command or a site produces, the APIs that write them, and what was removed from the SDK. The implementation (`LogFileWriter`, rotation, channel, Error Log Viewer) and the system logs (`syslog`, `kern.log`, `auth.log`, `audit.log`) are in [`dev/logging.md`](../dev/logging.md).

## Files produced by an app

| File | Content | Mode |
|---|---|---|
| `/var/log/apps/<appId>.log` | The app's `IAppExecutionContext.Logging` | `system:system 0644` |
| `/var/log/<daemon>/<file>.log` | `IAppLoggingGateway.AppendToLogFile` of a **system app** only, line written as is (ADR 0080 §3); for example `/var/log/kazoweb/error.log` of `kazowebd` ([`static-sites.md`](../dev/static-sites.md)) | `system:system 0644` |
| `/var/log/kazoweb/<appId>.log` | Debug log of a **site** (`Web` app) that enables it with `builder.Logging.EnableDebugLog()`: `ILogger<T>`, requests, exceptions, through `IAppLoggingGateway.AppendToSiteDebugLog` (ADR 0088 §5, [`asp-kazo-site-data.md`](asp-kazo-site-data.md)) | `system:system 0644` |

## Writing to a log

In an app, `IAppExecutionContext.Logging` (type `IAppLoggingGateway`) writes to `/var/log/apps/<appId>.log`. A system app writes a line as is to a file of its daemon with `AppendToLogFile(relativePath, line)`. A site that enables the debug log (`builder.Logging.EnableDebugLog()`) writes with `AppendToSiteDebugLog(line)`.

## User events (ADR 0254)

A user event says what happened to something **of the user's**: a permission
granted to a program, a virtual machine started, a file conflict resolved. It
goes to that user's journal, `/var/log/users/<id>/events.log`, readable by them
and, after the OS's authentication, by an administrator. Rule of the guide:
**if the event names a file, a program or a destination of the user's, it is a
user event**; otherwise it goes to the app's log (`Logging`), readable by everyone.

| File | Content | Mode |
|---|---|---|
| `/var/log/users/<id>/events.log` | `KazoEvents.Write` of the apps and the OS's events for that user; `<id>` is the stable account identifier (ADR 0169), never its login name | `<user>:<group> 0600`, directories `/var/log/users` and `/var/log/users/<id>` as `system:system 0711` |

```csharp
using Kazo.Os.AppSdk.UserEvents;

// Manifest: Capabilities = [AppCapabilities.EventsWrite] ("events.write", normal).
KazoEvents.Write(new KazoUserEvent(
    "x86.vm.started",                                  // kind, one dotted token
    L["VmStarted", vm.Name],                           // text already localized by the app
    new KazoEventSubject("vm", vm.Id, vm.Name),        // subject: type, stable id, current name
    KazoEventSeverity.Info,
    new Dictionary<string, string> { ["memory"] = "64 MiB" }));

// The history of one machine: only this app's lines.
IReadOnlyList<KazoUserEventEntry> history = await KazoEvents.ReadAsync(
    new KazoUserEventFilter { SubjectType = "vm", SubjectId = vm.Id, Limit = 200 });
```

- **The OS fills in** the time, the app (taken from its own context, never from
  the call), the process and, when it knows one, the sender (the app that
  launched this one or had it open a file, ADR 0253). A line has the format of
  every log (ADR 0045), with `kind`, `subject.type`, `subject.id`,
  `subject.name` and `sender` in the property block:
  `2026-11-18T09:30:00.000Z INFO  org.example.vm[42]: Windows 98 started {kind=x86.vm.started, memory=64 MiB, subject.id=4d2c, subject.name=Windows 98, subject.type=vm}`.
- **Refused** (`ArgumentException`): an empty kind or one with whitespace (128
  characters at most), a `kazoos.*` kind (kept for the OS's events), an empty
  message (2,048 at most), a subject without a type or an id (256 per field),
  more than 16 properties (key of 64, value of 512), and a property with a key
  of the OS (`KazoUserEventProperties`).
- **Rate**, like journald: at most 200 events per app and per user in 30 s;
  beyond that, events are dropped without an error and `/var/log/syslog` says
  how many (`user-events: suppressed N events of <app>`, without their
  content). A journal is read back at most 60 times in 30 s; beyond that,
  `ReadAsync` throws `InvalidOperationException`.
- **Without `events.write`**, `Write` and `ReadAsync` throw
  `KazoUnauthorizedAccessException`. In a worker, `Write` is checked in the app
  before it is posted without an answer: a refusal by the OS after that check
  only goes to the process's log.
- `KazoEvents.ReadAsync` returns the events **of this app only**, oldest first,
  archives included; `KazoUserEventFilter` filters by kind (or a prefix ending
  in `.`), subject, minimum severity, date, text and count (1,000 by default,
  5,000 at most).
- `KazoUserEventEntry.TryParse(line)` reads back a line of the file;
  `cat /var/log/users/<my id>/events.log` works with ordinary rights.
- **Same rotation** as the other logs (1 MiB, 5 archives); the directory is
  deleted with the account.
- The "My events" tab of the Error Log Viewer shows the whole journal of the
  signed-in user, all apps together ([`logging.md`](../dev/logging.md#error-log-viewer)).
- **Reading another account's journal**:
  `KazoUserAccounts.ReadEventsAsync(login, filter)` returns all its events. The
  signed-in user's own journal needs nothing; another account's needs an
  administrator in the OS's window, once per process (the ticket stays valid
  until the process ends), and each account read is written to `auth.log`
  (`events.read-other user=<login>`).
- **Sensitive operation of a feature**:
  `KazoFeatureAuthentication.AuthenticateAsync(operation, scope, subject)`; when
  the feature consumes the ticket, the OS writes
  `kazoos.feature.operation-authenticated` to the user's journal with that
  subject ([`system-administration.md`](system-administration.md#roles-of-the-features-adr-0247-adjusted-by-adr-0283)).
- **Limits.** The file belongs to the user: an app of that user holding
  `filesystem.system.read` or `filesystem.system.write` (dangerous, approved by
  an administrator) can read all of it or change it through the file API; "an
  app sees only its own lines" holds for `KazoEvents`. The message, the subject
  and the kind are not redacted; only property values go through the
  redaction of sensitive keys.

## Line format

- `LogLineFormat` (`Shared/Kazo.Os.Simulation.Abstractions/Diagnostics/`):
  `Format`, `FormatAudit` and `TryParse`. An entry fits on one line:
  `2026-10-10T03:11:42.123Z WARN  org.kazoos.text-editor[42]: message {clé=valeur}`.
  The message escapes `\`, `\n`, `\r`, `\t` and also `{` (as `\{`), so that a
  message is never read back as a property block. Inside that block, keys and
  values additionally escape `,`, `=` and `}`. Spaces in a source become `_`.

## Rotation

- `LogRotationPolicy`: `MaxFileBytes` = 1 MiB, `MaxArchives` = 5. Rotation like
  `logrotate` (`.5` deleted, `.4`→`.5` … `<f>`→`.1`, new empty `<f>`).
  An empty file never rotates: a batch larger than 1 MiB is written whole.

## Delays and losses

The lines of a log are flushed in batches: every 1 s of simulated time, as soon as 100 lines are pending, on disconnection and at shutdown. Beyond 10,000 pending lines, the oldest are lost; a `WARN logging` line reports it at the next flush.

## What was removed

- `IAppDiagnosticsGateway`, `AppDiagnosticsGateway` and `IAppExecutionContext.Diagnostics`
  are removed. Reading the logs means reading files.
- `diagnostics.read` and `diagnostics.clear` are removed from the catalog by
  ADR 0049 ([`capabilities.md`](capabilities.md)).
