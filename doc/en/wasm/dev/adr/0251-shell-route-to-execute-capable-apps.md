> [Version française](../../../../fr/wasm/dev/adr/0251-shell-route-to-execute-capable-apps.md)

# ADR 0251: shell route to execute-capable apps

## Status

Proposed on 2026-11-11, for the Os-x86 feature
([Kazo-ca/Os-x86](https://github.com/Kazo-ca/Os-x86)), lot 1 of the
`feature/base` series; rewritten the same day after the maintainer's answer
to question 1 (D, outside the proposed options) and to the four precisions
asked next (252 to 255, A). The questions asked to Benoit got their answers;
they are recorded at the end of this ADR and the decision applies them.
Source decisions: 13 (an ELF typed in the terminal goes to Os-x86), 62 B
(`./setup.exe` goes to BoxedWine; "Os-x86 has no extension association",
64), 111 (a DOS `.exe` typed in the terminal goes directly to
Os-x86/FreeDOS), 162 A with Benoit's precision (the header decides when it
exists, the extension otherwise, a contradiction refuses; "except when it is
the Terminal application") and 184 A (child of the shell, a new instance per
launch, shell streams, the terminal waits; Ctrl+C closes the instance),
2026-11-11; the maintainer's answer 240 D, in his words (translated):
"Os-x86 provides a method to determine the application type. It is Os-x86
that interprets the magic and the extension. Then the shell asks the
applications that expose application-execution capabilities whether they
can open the application. I think we should add (optional) MimeType support
and also use it later for protocols (as for irc)."; precisions 252 A (format
detector declared by a Feature), 253 A (`execute` action of the
`FileHandlers`), 254 A (the user's default association, sole candidate,
"Open with" window) and 255 A (freedesktop and IANA MIME types, protocols
through `x-scheme-handler/<scheme>`), 2026-11-11; the shell's program host,
251 A (ADR 0252 §1).

- **Completes:** [ADR 0050](0050-shell-grammar-and-process-environment.md)
  and [ADR 0064](0064-posix-shell-scripts-jobs-and-app-commands.md)
  (scripts, apps launched by name), [ADR 0096](0096-added-intents-and-app-defined-intents.md)
  (`xdg-open`, associations, `x-scheme-handler/<scheme>`),
  [ADR 0121](0121-user-file-associations.md) (the user's default
  associations, `xdg-mime`), [ADR 0148](0148-sdk-contracts-and-os-provided-controls.md)
  (a type of an app instantiated by the OS outside any process) and
  [ADR 0260](0260-feature-app-type.md) §5 (only a Feature declares format
  detectors).
- **Replaces:** the first wording of this ADR (the shell read the `Magic`
  declared by a Feature and launched its handler; `FileHandler` gained
  `Executable`), rejected by 240 D (option F).
- **Does not change:** double-click, "Open with", `xdg-open` and
  `OpenFileAsync` (always the association), the confirmation of a
  default-app change (D61.10, 124.10), the shell's program host
  (ADR 0252 §1).
- **For:** `./setup.exe` (PE → BoxedWine), `./prog.exe` pure MZ (DOS → the
  Os-x86 `x86` command), `./a.out` (ELF → `x86`), `./script.bat` (→
  BoxedWine, never read as an `hsh` script); `xdg-open irc://…` for the
  Os-x86 agent.

## Context

- `./x` in the shell (`Platform/Kazo.Os.Platform.Core/Shell/ShellInterpreter.Scripts.cs:26-128`,
  `RunPathAsync`): directory → 126 (:42-47); missing → 127 (:50-55);
  **execute bit tested before any read** → "Permission denied", 126 (:57-66);
  read **as text** (:68); a NUL byte → "cannot execute binary file", 126
  (:78-83); `#!` (:86-125); otherwise an `hsh` script (:127). It **never**
  looks at the associations nor at an MZ, PE or ELF header. A downloaded
  `.exe` without the execute bit fails; a `.bat` with the bit runs as an `hsh`
  script.
- Command resolution order (`ShellInterpreter.SimpleCommands.cs:124-184`):
  aliases, builtins, functions, path, catalog commands (`Terminal` apps only,
  `ShellCommandResolver.cs:106`), `sh`/`bash`/`hsh`, then a window's short
  name, otherwise 127.
- A window launched by its name (:259-309) is a **child of the shell**
  (`ParentPid`), with the shell streams (:263-278, `AttachedAppConsole`,
  `AppLifecycleOrchestrator.cs:548-558`, text line by line, no raw mode); the
  terminal waits (:289); code 0 at close, 1 on crash, 130 after Ctrl+C which
  **kills the window** (:291-295, `InterruptAsync` :1345-1358); a
  single-instance app already open returns 0 right away without streams
  (:282-284).
- Associations resolve **by extension** only (or the MIME type given by the
  caller): `Intents/FileAssociationResolver.cs:198-214, 386-393`; a handler's
  `Priority` is never read; a sole candidate is chosen without asking;
  without a handler, `xdg-open` returns 3 (`XdgOpenCommand.cs:89`). A handler
  is declared through `FileHandlers`
  (`FileHandlerManifest(MediaType, Extensions, Actions, Priority)`,
  `AppManifest.cs:224`) of a window app (`AppManifestValidator.cs:318-321`).
  The OS "Open with" window exists (`DialogCoordinator.OpenWithAsync`,
  `Platform/Kazo.Os.Platform.Blazor/Dialogs/DialogCoordinator.cs:240`).
- **Kazo has no extension → MIME type table**: `xdg-mime query filetype`
  (`Apps/Commands/Kazo.Os.Commands/XdgMime/XdgMimeCommand.cs:84-118`) takes
  the `MediaType` of the first `FileHandler` of an installed app that
  declares the extension, otherwise `application/octet-stream`; it does not
  read the file.
- **Kazo never calls the class of an installed app without launching it**:
  the entry point is constructed at launch, in a process
  (`AppLifecycleOrchestrator.cs:250-253`, `CreateAppInstance`). The only
  exception is an embeddable component (ADR 0148): a type named in the
  manifest (`AppComponentManifest(Contract, Assembly, Type)`,
  `AppManifest.cs:250`), lazily loaded with the entry-point assembly and
  instantiated by the OS outside any app process
  (`Platform/Kazo.Os.Platform.Blazor/Components/KazoComponentHost.cs:245-265`,
  `descriptor.Assembly.GetType(component.Type)`).
- `IFullScreenTerminalSession` (ADR 0252) is only given to a **command** of a
  `Terminal` app; a window launched from the shell has no access to it.

## Decision

### 1. Three declarations

- **Format detector** (252 A): `FeatureDefinition.FormatDetectors`,
  **reserved to Features** (ADR 0260 §5), a list of `FormatDetector`
  (`MediaTypes`: the MIME types the Feature recognizes; `Type`: a type of the
  Feature's entry-point assembly that implements the SDK's
  `IFormatDetector`). `DetectAsync(header of 64 bytes at most, extension)`
  returns a `FormatDetection`: the MIME type (§3) and the detail
  (`PeConsole`, `PeGraphical`, `Ne`, `Mz`, `Elf`, `DosBatch`), or `Mismatch`
  with a reason when the extension contradicts the header (`photo.png` with
  a PE header), or null ("not mine"). The OS instantiates it like an
  ADR 0148 component (type named in the manifest, lazily loaded), **without
  any app context nor gateway**: it receives the bytes and the extension,
  nothing else, and can do nothing else. Os-x86 declares the five types of
  §3; it is Os-x86 that interprets the magic and the extension (240 D).
- **`execute` action** (253 A): the `execute` action joins the existing
  `FileHandlers` (`FileHandler("application/vnd.microsoft.portable-executable",
  [".exe"], ["open", "execute"])`); it tells the shell: "a program of this
  type launched by its path is for me". The validator accepts it on a window
  app and, new, on a command of a `Terminal` app whose actions are limited to
  `execute`. BoxedWine declares `execute` for PE (`.exe`), NE and the DOS
  batch (`.bat`, `.cmd`); the Os-x86 `x86` command for pure MZ and ELF. It
  has no effect outside the shell.
- **Refusal at launch** (253 A, as the code is): the candidate app is not
  asked before being launched, because Kazo only instantiates an app's class
  at launch (context). It receives the program at its start
  (`ProgramLaunch(Path, MediaType, Detail, Arguments)`, in
  `AppLaunchRequest`) and may **refuse it before its first window or its
  first output** (`AppLaunchResult.Refused(reason)`, new in the SDK): the
  shell logs the refusal and moves to the next candidate (§2, step 5); with
  no candidate left, 126. An app that answers nothing accepts.

### 2. The route of `./path`, in order

Before the execute-bit test and before any read as text:

1. **The file exists and is not a directory** (as today, 126/127).
2. **The shell's host** (251 A): if the app that started the shell provided
   a `ProgramHost` (`KazoShellOptions`, `IKazoShellProgramHost.TryRunAsync`,
   ADR 0252 §1: the Terminal app, which runs a console program itself through
   `Ix86`) and it takes the file, the exit code is its own and the route
   stops there.
3. **Type detection** (252 A): the shell reads the first bytes (64 at most,
   `KazoFile.OpenRead`, without loading the file) and asks the detectors of
   the **installed and enabled** Features, in installation order; the first
   that answers decides. `Mismatch` → refusal with its message, 126, log;
   never executed. Without a detector, or none answering: the type comes
   from the `FileHandlers` of the installed apps as for `xdg-mime query
   filetype` (first app that declares the extension with a `MediaType`),
   otherwise the extension alone.
4. **Execute-capable apps** (253 A): the installed and enabled apps of which
   a `FileHandler` declares `execute` for this MIME type, or for the
   extension when there is no type (`FileAssociationResolver`, `execute`
   action).
5. **Choice of the candidate** (254 A): the user's default association for
   this MIME type and the `execute` action (ADR 0121, Settings ▸
   Associations, `xdg-mime default <app> <type>`); otherwise the sole
   candidate; otherwise the OS "Open with" window
   (`DialogCoordinator.OpenWithAsync`, candidates limited to those of step 4),
   **never a question in the terminal** (D61.5). `Priority` is not used. A
   refusal at launch (§1) removes the candidate and resumes here.
6. **Launch**: like a window launched by its name (child of the shell, **a
   new instance per launch**, a `SingleInstancePerUser` app being launched as
   a separate instance for this launch or refused with 126 when it does not
   allow it, 241 A; shell streams; the terminal waits for the close; code
   0/1/130 as today, named limit: it is not the program's code, 110;
   **Ctrl+C closes the instance**, 184 A, as long as the "Ctrl+C forwarded"
   ADR of lot 3 does not exist); a candidate command (`x86`) is launched like
   a catalog command, with `IFullScreenTerminalSession` (ADR 0252). The sender
   (ADR 0253) is the shell's host app and the user; the program is passed
   through `ProgramLaunch` with the detail of step 3. No execute bit
   required; the file is not read as text. **Without a candidate** (242 A):
   "No application opens .exe files" with the suggestion of ADR 0248 §2 when
   the Store catalog lists a format (Os-x86 lot 2), code 126; without a
   catalog, step 7 applies and a `.exe` gives "cannot execute binary file" as
   today.
7. Otherwise, **as today**: execute bit, text, `#!`, `hsh` script.

The same path through `xdg-open ./setup.exe` stays the opening by
association (`open` action) without waiting (unchanged).

### 3. MIME types and protocols (255 A)

| Format | MIME type | Source | `execute` declared by |
|---|---|---|---|
| PE (Windows, console or graphical) | `application/vnd.microsoft.portable-executable` | IANA | BoxedWine |
| NE (16-bit Windows) | `application/vnd.microsoft.portable-executable`, detail `Ne` | IANA | BoxedWine |
| Pure MZ (DOS) | `application/x-dosexec` | freedesktop | `x86` command |
| ELF (Linux) | `application/x-executable` | freedesktop | `x86` command |
| DOS batch (`.bat`, `.cmd`) | `application/x-bat` | freedesktop | BoxedWine |
| Windows installer (`.msi`) | `application/x-msi` | freedesktop | BoxedWine |

- The types are set by this ADR; a Feature that recognizes others names them
  in `MediaTypes` with a freedesktop or IANA type.
- **Protocols**: nothing new. A scheme is declared through the existing
  `x-scheme-handler/<scheme>` (ADR 0096 §3, ADR 0121); `open-url` of the
  Os-x86 agent goes through `xdg-open`, which resolves `irc://…` as today.

### 4. The Terminal app exception (162, Benoit's precision)

- "The Terminal runs the console program itself through Os-x86": it is the
  Terminal app, through `Ix86` (ADR 0252 §1), that takes the file at step 2
  when it hosts the shell; it decides for a **console** PE (run in the tab),
  a pure MZ (DOS in the tab) and an ELF (Linux in the tab), and passes a
  **graphical** program to the rest of the route (BoxedWine). The shell has
  nothing to know about Wine or DOS: the rule is in the Terminal (through
  `Ix86`) and in the Os-x86 detector.
- **Outside the Terminal** (shell started by another app without
  `ProgramHost`, script), steps 3 to 6 are the only route: a console PE goes
  to BoxedWine like a graphical PE (BoxedWine shows it in its own window), a
  pure MZ or an ELF to the `x86` command in the shell streams.
- On double-click, `xdg-open` and `OpenFileAsync`, **nothing changes**: the
  file goes to the `open` association (BoxedWine), which calls Os-x86 through
  `Ix86`.

### 5. What the shell logs

`/var/log/syslog`, source `shell`: `exec route=host app=org.kazoos.terminal
path=…`, `route=execute app=ca.kazo.boxedwine
type=application/vnd.microsoft.portable-executable detail=pe-graphical
path=…`, `route=execute app=ca.kazo.x86.cli type=application/x-executable
path=…`, and the refusals (`route=refused reason=header-extension-mismatch`,
`reason=no-handler`, `reason=refused-by-app app=…`).

## Rejected options

- **A. Every associated extension is executable** (like Windows' `cmd`, where
  `photo.png` opens the image): `./notes.txt` would launch the editor and the
  terminal would wait; contrary to POSIX shell usage.
- **B. A generic binfmt in the OS** (the shell itself recognizes MZ, PE, ELF,
  original 13 A): the OS would hold rules about formats it does not run; the
  162 precision (console in the terminal, DOS without BoxedWine) lives in
  the Terminal app and in the Feature's detector, not in the shell.
- **C. The header before the association, always, with `.exe` → Os-x86**:
  62 B decided for BoxedWine; BoxedWine declares `execute` for PE.
- **D. A single BoxedWine instance that receives successive launches**
  (184 B): the terminal would return 0 right away without streams (current
  code :282-284); contradicts 163.
- **E. Read the whole file to detect**: 64 bytes are enough; a 4 GiB VM disk
  must not be read to learn that it is not an ELF.
- **F. The `Magic` read by the shell and one handler per Feature** (first
  wording of this ADR, question 1 A): rejected by 240 D; the shell knows no
  magic byte, the Feature interprets the header and the extension, and the
  shell asks declared apps.
- **G. The shell resolves `Ix86` to detect** (252 B): the shell would depend
  on an interface of a named Feature; a declared detector works for every
  Feature.
- **H. Declarative only, without a detector** (253 B) nor **a declaration
  apart from the `FileHandlers`** (253 C): the extension alone does not tell
  a PE from a pure MZ; a separate declaration would duplicate the
  associations (`Priority`, Settings ▸ Associations) the `FileHandlers`
  already have.
- **I. `Priority` or "Open with" every time** (254 B, C): the user's
  preference already exists (ADR 0121); a window at every launch would
  contradict shell usage.
- **J. A separate `protocols` field** (255 B): `x-scheme-handler/<scheme>`
  already does it for the browser and the IRC app.
- **K. `CanExecuteAsync` on the app before launching it**: Kazo never calls
  the class of an installed app outside its launch (context); a type named
  in the manifest, like a component, would allow it, but without an app
  context it would know nothing more than the detector; the refusal at
  launch (§1) is enough.

## Consequences

- SDK (`PublicAPI.Unshipped.txt`): `FeatureDefinition.FormatDetectors`,
  `FormatDetector`, `IFormatDetector`, `FormatDetection`; `execute` action
  of the `FileHandlers`; `ProgramLaunch` in `AppLaunchRequest` and
  `AppLaunchResult.Refused`. `FileHandler.Executable` and
  `FeatureDefinition.BinaryFormats` of the first wording do not exist.
- Validator: detectors reserved to Features (`manifest.feature.only`,
  ADR 0260 §5), well-formed `MediaTypes`, detector type in the entry-point
  assembly (like a component); `FileHandlers` with the sole `execute` action
  allowed on a command of a `Terminal` app.
- `AppCatalog`: registry of the detectors in installation order;
  `FileAssociationResolver`: `execute` action, resolution by MIME type then
  extension; `xdg-mime query filetype` consults the detectors before the
  `FileHandlers`, to give the same type as the shell.
- `ShellInterpreter.RunPathAsync`: shell's host first (`ProgramHost`,
  ADR 0252 §1), detection, candidates, default association, "Open with"
  window, launch of a window or a command as a child with a separate
  instance, refusal at launch, message without a candidate; log.
- Docs: [`shell-commands.md`](../shell-commands.md),
  [`file-associations-api.md`](../../framework/file-associations-api.md),
  [`app-model.md`](../../framework/app-model.md), `features.md`,
  `changes/unreleased` entry.
- Tests: `./a.out` ELF without the bit → Os-x86 detector, `x86` command
  launched with `ProgramLaunch`; host with `ProgramHost` that takes the file
  → no detection nor candidate, the host's code; PE `.exe` → BoxedWine
  (child, waits, 0 at close, 130 on Ctrl+C); two candidates → default
  association, otherwise "Open with"; refusal at launch → next candidate
  then 126; `.bat` → BoxedWine, never `hsh`; `photo.png` with a PE header →
  126 and log; without a detector → type from the `FileHandlers`; without
  BoxedWine → message 126; directory and missing unchanged; `xdg-open`
  unchanged; ordinary app with `FormatDetectors` → refused; `xdg-mime query
  filetype ./a.out` → `application/x-executable`.

## Questions asked to Benoit

1. **Who reads the header and applies "header first"?**
   - A) **(Originally recommended)** The shell recognizes the `Magic`
     declared by the Feature and launches its handler; the fine rule (PE,
     NE, pure MZ, console or graphical, delegation to BoxedWine) is in the
     Os-x86 handler and in the Terminal app (ADR 0252 §1).
   - B) The shell applies the whole rule (PE → association, pure MZ → DOS,
     ELF → Linux) from a table declared by the Feature: more logic in the OS,
     less in the Feature.
2. **A `SingleInstancePerUser` associated app already open, launched by
   `./x.exe`?**
   - A) **(Recommended)** The shell launches a separate instance for this
     launch (184 A: "BoxedWine is not single-instance"; an app that is gets a
     second instance with the streams).
   - B) Refusal with 126 "already open": the app must be closed first.
3. **Without any handler for `.exe` (BoxedWine absent), in V1?**
   - A) **(Recommended)** Message "No application opens .exe files" and 126
     as soon as the Store catalog lists a format (lot 2); before that,
     "cannot execute binary file" as today.
   - B) Always "cannot execute binary file" (nothing new).

Maintainer's answers (2026-11-11): 1 **D**, outside the options, in his
words (quoted in the status): Os-x86 determines the type (magic and
extension), the shell asks the execute-capable apps, optional MIME types
reused for protocols; 2 A; 3 A.

Precisions asked after 240 D, maintainer's answers on 2026-11-11 ("I agree
with your recommendations"):

- 252 A: a format detector declared by a Feature (`FormatDetectors`, §1)
  rather than the shell resolving `Ix86`.
- 253 A: the `execute` action of the existing `FileHandlers` rather than a
  separate declaration or the declarative alone; the app's confirmation
  happens at launch, since Kazo does not call an app without launching it
  (§1, context).
- 254 A: the user's default association, otherwise the sole candidate,
  otherwise the "Open with" window, never a question in the terminal;
  `Priority` unused.
- 255 A: MIME types set by the ADR from freedesktop or the IANA (§3);
  protocols through the existing `x-scheme-handler/<scheme>`.
