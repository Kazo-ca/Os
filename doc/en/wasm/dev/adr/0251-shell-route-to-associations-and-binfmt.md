> [Version française](../../../../fr/wasm/dev/adr/0251-shell-route-to-associations-and-binfmt.md)

# ADR 0251: shell route to the file association and header binfmt of a Feature

## Status

Proposed on 2026-11-11, for the Os-x86 feature
([Kazo-ca/Os-x86](https://github.com/Kazo-ca/Os-x86)), lot 1 of the
`feature/base` series. The recommendation of each question asked to Benoit
is applied while waiting for his answer. Source decisions: 13 (an ELF typed
in the terminal goes to Os-x86 through a binfmt), 62 B (`./setup.exe` goes
to BoxedWine through the `.exe` association; "Os-x86 has no extension
association", 64), 111 (a DOS `.exe` typed in the terminal goes directly to
Os-x86/FreeDOS), 162 A with Benoit's precision (the header decides when it
exists, the extension otherwise, a contradiction refuses; "except when it is
the Terminal application": the Terminal runs a console program itself
through Os-x86 when the console redirection is shipped, otherwise sends it to
BoxedWine) and 184 A (child of the shell, a new instance per launch, shell
streams, the terminal waits; Ctrl+C closes the instance), 2026-11-11.

- **Completes:** [ADR 0050](0050-shell-grammar-and-process-environment.md)
  and [ADR 0064](0064-posix-shell-scripts-jobs-and-app-commands.md)
  (scripts, apps launched by name), [ADR 0096](0096-added-intents-and-app-defined-intents.md)
  (`xdg-open`, associations) and [ADR 0243](0243-feature-app-type.md) §5
  (only a Feature registers a binfmt).
- **Does not change:** double-click and "Open with" (always the
  association), the confirmation of a default-app change (D61.10, 124.10).
- **For:** `./setup.exe` (Windows → BoxedWine), `./prog.exe` pure MZ (DOS →
  Os-x86), `./a.out` (ELF → Os-x86), `./script.bat` (→ BoxedWine, never read
  as an `hsh` script).

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
  is declared through `FileHandlers` of a window app
  (`AppManifestValidator.cs:318-321`).
- `IFullScreenTerminalSession` (ADR 0252) is only given to a **command** of a
  `Terminal` app; a window launched from the shell has no access to it.

## Decision

### 1. Two new declarations

- **Executable extension**: `FileHandler` gains `Executable = true`
  (`FileHandler(mediaType, [".exe", ".com", ".bat", ".cmd", ".msi"],
  ["open"], Executable: true)`). It tells the shell: "a file of this
  extension launched by its path is for me". Any window app may declare it
  (BoxedWine does); it has no effect outside the shell.
- **Executable format by header (binfmt)**: `FeatureDefinition.BinaryFormats`,
  **reserved to Features** (ADR 0243 §5), a list of `BinaryFormat`: `Magic`
  (bytes and offset, for example `7F 45 4C 46` at 0 for ELF, `4D 5A` at 0 for
  MZ), `Name` ("ELF", "MZ"), and `Handler`: the `Terminal` child of the
  Feature that executes (`org.kazoos.x86.cli`), with the arguments the shell
  passes to it (`--binfmt <name> --file <path> -- <args>`). Two Features with
  the same `Magic`: the older one wins, the second gets
  `catalog.binfmt.duplicate` (like commands).

### 2. The `./path` route, in order

Before the execute-bit test and before any reading as text:

1. **The file exists and is not a directory** (as today, 126/127).
2. **Header**: the shell reads the first bytes (64 at most, through
   `KazoFile.OpenRead`, without loading the file). **The shell's host
   first**: when the app that started the shell provided a `ProgramHost`
   (`KazoShellOptions`, ADR 0252 §1: the Terminal app, which runs a console
   program itself through `Ix86`) and it takes the file, the exit code is
   its own and the route stops there. Otherwise, when they match the
   `Magic` of a binfmt of an **installed and enabled** Feature: the shell
   launches that Feature's `Handler` like a catalog command (child of the
   shell, shell streams, `IFullScreenTerminalSession` available, ADR 0252),
   with the path and the arguments; the exit code is the one the handler
   returns. The handler applies its own rule (Os-x86: MZ+PE or MZ+NE → hands
   over to the app associated with the extension through route 3 below,
   `IAppIntentGateway.OpenFileAsync` with the marked sender, ADR 0253; pure MZ
   → FreeDOS in the terminal; ELF → Linux in the terminal). When the
   extension **contradicts** the header (`photo.png` with a PE header), the
   handler refuses with a message and code 126; never executed.
3. **Extension declared executable** by an installed and enabled window app,
   and no binfmt recognized the header: the shell launches the associated app
   (`FileAssociationResolver` resolution, default handler or sole candidate;
   several candidates → the OS "Open with" window, never a question in the
   terminal, D61.5) **like a window launched by its name**: child of the
   shell, **a new instance per launch** (a `SingleInstancePerUser` app is
   launched anyway as a separate instance for this launch, or refused with
   126 when it does not allow it, question 2), shell streams, the terminal
   waits for the close; code 0/1/130 as today (named limit: it is not the
   program's code, 110); **Ctrl+C closes the instance** (184 A) as long as
   the "Ctrl+C passed" ADR of lot 3 does not exist. The sender (ADR 0253) is
   the Terminal app and the user. No execute bit required; the file is not
   read as text.
4. Otherwise, **as today**: execute bit, text, `#!`, `hsh` script.

- **Without a handler** for an extension no app declares executable
  (BoxedWine absent): the shell does not fall through to step 4 for `.exe`
  (reading a binary as a script is what must be avoided): message "No
  application opens .exe files" with the suggestion of ADR 0248 §2 when the
  Store catalog offers one, code 126. The list of extensions "known as
  executable" comes from the Store catalog (declared formats, Os-x86's
  lot 2); in V1, without a catalog, step 4 applies and a `.exe` returns
  "cannot execute binary file" as today.
- The same path through `xdg-open ./setup.exe` stays the association open
  without waiting (unchanged).

### 3. The Terminal app exception (162, Benoit's precision)

- "The Terminal runs the console program itself through Os-x86": it is the
  Terminal app, through `Ix86` (ADR 0252 §1), that takes the file at step 2
  when it hosts the shell; it decides for a **console** MZ+PE (run in the
  tab) and hands every **graphical** program over to the associated app
  (route 3, BoxedWine). The shell needs to know nothing about Wine nor DOS:
  the rule lives in the Terminal (through `Ix86`) and in the handler.
- **Outside the Terminal** (shell started by another app without a
  `ProgramHost`, script), the Feature's binfmt handler stays the route: the
  same rule, held by the Feature's `x86` command.
- On double-click, `xdg-open` and `OpenFileAsync`, **nothing changes**: the
  file goes to the association (BoxedWine), which calls Os-x86 through
  `Ix86`.

### 4. What the shell logs

`/var/log/syslog`, source `shell`: `exec route=host app=org.kazoos.terminal
path=…`, `route=binfmt format=ELF handler=org.kazoos.x86.cli path=…` or
`route=association app=… ext=.exe`,
and the refusals (`route=refused reason=header-extension-mismatch`).

## Rejected options

- **A. Every associated extension is executable** (like Windows' `cmd`, where
  `photo.png` opens the image): `./notes.txt` would launch the editor and the
  terminal would wait; contrary to POSIX shell usage.
- **B. A generic binfmt in the OS** (the shell itself recognizes MZ, PE, ELF,
  original 13 A): the OS would hold rules about formats it does not run; the
  162 precision (console in the terminal, DOS without BoxedWine) lives in
  the Terminal app and in the Feature's handler (ADR 0252 §1), not in the
  shell.
- **C. The binfmt before the association, always, with `.exe` → Os-x86**:
  62 B decided for BoxedWine; the handler delegates.
- **D. A single BoxedWine instance that receives successive launches**
  (184 B): the terminal would return 0 right away without streams (current
  code :282-284); contradicts 163.
- **E. Read the whole file to detect**: 64 bytes are enough; a 4 GiB VM disk
  must not be read to learn that it is not an ELF.

## Consequences

- `FileHandler.Executable`, `FeatureDefinition.BinaryFormats`, `BinaryFormat`
  (SDK, `PublicAPI.Unshipped.txt`); validator (well-formed `Magic`, binfmt
  reserved to Features); `AppCatalog` (binfmt registry,
  `catalog.binfmt.duplicate`).
- `ShellInterpreter.RunPathAsync`: header read, shell's host first
  (`ProgramHost`, ADR 0252 §1), binfmt route, association route (window launched as a child with a separate instance), message
  without a handler; `ShellCommandResolver` for the handler; log.
- Docs: [`shell-commands.md`](../shell-commands.md),
  [`file-associations-api.md`](../../framework/file-associations-api.md),
  [`app-model.md`](../../framework/app-model.md), `features.md`,
  `changes/unreleased` entry.
- Tests: `./x` ELF without the bit → handler launched with the path; host
  with a `ProgramHost` that takes the file → no handler, the host's code; PE
  `.exe` → handler then association (child, waits, 0 at close, 130 on
  Ctrl+C); `.bat` → association, never `hsh`; `photo.png` with a PE header →
  126 and log; without BoxedWine → message 126; directory and missing
  unchanged; `xdg-open` unchanged; ordinary app with `BinaryFormats` →
  refused.

## Questions asked to Benoit

1. **Who reads the header and applies "header first"?**
   - A) **(Recommended)** The shell recognizes the `Magic` declared by the
     Feature and launches its handler; the fine rule (PE, NE, pure MZ,
     console or graphical, delegation to BoxedWine) lives in the Os-x86
     handler (72.2: central service of Os-x86) and in the Terminal app
     (ADR 0252 §1).
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

Applied while waiting: 1 A, 2 A, 3 A.
