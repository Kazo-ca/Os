> [Version française](../../../../fr/wasm/dev/adr/0240-detailed-error-page.md)

# ADR 0240: a detailed error page, with secrets masked

## Status

Proposed on 2026-11-08, with the recommendations of "Questions asked to
Benoit" applied while waiting for his answer. Original request: "Could we show
more information when an error happens? The application is open source after
all, what kind of sensitive information could we show?"

- **Complements:** [ADR 0045](0045-logs-as-files.md) (the `CRIT` line of
  `HostExceptionReporter` in `/var/log/syslog` gets the message and the stack).
- **Does not change:** the page stays the one of `HostErrorBoundary` in
  `App.razor`; a crashing app still takes the whole OS down (out of scope here).

## Context

- When a component throws an unhandled exception, `HostErrorBoundary` replaces
  the whole OS with "Kazo OS encountered an error", the sentence "The failure
  was recorded without exposing sensitive details" and "Correlation:". The
  stack was only visible in development.
- **The correlation was always empty.** `ErrorBoundaryBase` calls
  `OnErrorAsync`, then renders the error content without waiting for it. The
  ID was only assigned after the log write (an `await`), and nothing rendered
  the page again.
- The log only kept the exception type: no diagnosis afterwards.
- The code of Kazo OS is public: a stack trace reveals nothing. What can be
  private is **the user's content** that ends up in a message: an address with
  a password (`wss://me:password@…`), a token (`?access_token=…`), a settings
  value, an IRC `PASS` command. On one's own screen this is no risk; it becomes
  one when the person takes a screenshot or pastes the text in a public issue.

## Decision

1. **`HostFailureDetails`** (`OS/Kazo.Os.Ecosystem.Host/`) describes a failure
   synchronously: correlation ID, time, phase, exception type, message, origin
   module and method, and the full text (`Exception.ToString()`, inner
   exceptions included).
   - The **source** is the first stack line outside the .NET and Blazor
     framework (`System.`, `Microsoft.`, `Mono.`, `Interop.`, `MudBlazor.`),
     preferring an app (`Kazo.Irc.Components.MessagePane.OnAfterRenderAsync`)
     to a Kazo OS line (`Kazo.Os.*`). An async state machine line
     (`<Method>d__5.MoveNext`) becomes `Method` again. The stack is read as
     text: trimming forbids `StackFrame.GetMethod`.
   - The **module** is the longest loaded assembly name prefixing the source
     (`Kazo.Irc`).
2. **`HostErrorBoundary.Failure`** is set **before the first `await`** of
   `OnErrorAsync` (`HostExceptionReporter.Describe`), then the log is written
   (`HostExceptionReporter.ReportAsync(HostFailureDetails)`). The page shows
   the correlation on its first render. `CorrelationId` stays and reads
   `Failure`.
3. **`SensitiveTextRedactor`** (`Platform.Core/Diagnostics/`) replaces with
   `***redacted***`, on screen, in the copied text and in the log:
   - the password of an address (`scheme://user:***redacted***@host`);
   - the value of a sensitive `key=value` or `key: value` pair, JSON quotes
     included (`password`, `passwd`, `pwd`, `passphrase`, `secret`,
     `client_secret`, `token`, `access_token`, `refresh_token`, `id_token`,
     `apikey`, `api_key`, `api-key`, `authorization`, `credential(s)`,
     `cookie`, `sig`, `signature`);
   - the `Bearer` or `Basic` value of a header;
   - the IRC `PASS` and `IDENTIFY` commands.
   **File paths stay visible**: they explain most failures and only name the
   person's own files.
4. **`HostFailurePanel`** is the body of the page:
   - always visible: module, source, exception, message, local time,
     correlation (selected with one click);
   - folded: "Technical details", the full stack;
   - **"Copy details"** (short header then the stack, ready to paste in an
     issue; the component's own JS module `HostFailurePanel.razor.js`, so it
     does not depend on the OS clipboard, which may be the failing part) and
     **"Reload"** (full page reload);
   - one sentence asks to read the text before publishing it.
   "The failure was recorded without exposing sensitive details" becomes "The
   failure was recorded in /var/log/syslog. Passwords and tokens are masked."
5. The `CRIT` line of `syslog` gets the `message`, `stack`, `source` and
   `module` properties, masked the same way (`LogLineFormat` keeps the escaped
   line breaks on one line).

## Consequences

- An app failure can be diagnosed from a screenshot or a copied text, without
  opening the browser console.
- Masking relies on known shapes: a secret with no recognizable shape (a bare
  password inside a message) stays visible. The sentence under the buttons says
  so.
- `Kazo.Os.Ecosystem.Host` gives its internals to `Kazo.Os.Ecosystem.Tests`
  (stack reading tested directly).

## Questions asked to Benoit

Asked on 2026-11-08 in the thread "Afficher plus de détails sur la page
d'erreur"; the recommendations are applied.

1. **What to show?** A (recommended): visible summary, folded stack, "Copy
   details" and "Reload". B: everything unfolded. C: type and message only.
2. **Mask secrets?** A (recommended): known shapes masked on screen and in the
   copy, paths visible. B: mask nothing. C: mask only in the copy.
3. **The `/var/log/syslog` log?** A (recommended): also write the masked
   message and stack there. B: change only the page.

## Task list

- [x] `HostFailureDetails` and reading of the source and module.
- [x] `SensitiveTextRedactor` (Platform.Core) and its tests.
- [x] `HostExceptionReporter.Describe` and `ReportAsync(HostFailureDetails)`;
  message and stack in `syslog`.
- [x] `HostErrorBoundary.Failure` set before rendering (empty correlation
  fixed).
- [x] `HostFailurePanel` (summary, folded details, copy, reload), English and
  French texts.
- [x] Render, report and masking tests; FR and EN docs.
