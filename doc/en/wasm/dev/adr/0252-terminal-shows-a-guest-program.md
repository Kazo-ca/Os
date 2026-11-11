> [Version française](../../../../fr/wasm/dev/adr/0252-terminal-shows-a-guest-program.md)

# ADR 0252: the Terminal shows a guest program (per-cell attributes, F1 to F12, Escape chord, Ctrl+C to the program)

## Status

Proposed on 2026-11-11, for the Os-x86 feature
([Kazo-ca/Os-x86](https://github.com/Kazo-ca/Os-x86)), lot 1 of the
`feature/base` series. The questions asked to Benoit got their answers on
2026-11-11 (1 A, 2 A; the Terminal's dependency: B, §1; the shell's program
host `ProgramHost`: A, 251 A); the decision applies them. Source decisions:
154 B ("the Kazo terminal shows" the console of a Windows program and a
text-mode DOS program; Os-x86 opens no window), 162 A (Terminal app exception:
the Terminal runs a console program itself through Os-x86, with the header
read by it), 215 A (what is asked of the Terminal: a fixed-width "full screen"
surface, 80 columns and 25 rows or more, per-cell color attributes and cursor,
refreshed at ~30 Hz; key-by-key input with Ctrl+letter; Esc+letter / Esc+digit
chord translated to Alt+letter / F1-F12; Home, End, Delete, Page; size known
at opening; hot resize in V2) and 216 A (Ctrl+C goes to the guest program;
kill by Ctrl+Pause when Kazo has it, otherwise a Terminal button or `x86
kill`), 2026-11-11; maintainer's answer of 2026-11-11 on the Terminal's
dependency (B, §1), in his words (translated): "to be able to host terminal
sessions eventually. It will become the only system application that uses the
feature. It uses it the same way as BoxedWine"; Os-x86 trial E14 (Wine's `cmd`
and FreeDOS usable in such a grid under 100 ms; without Alt nor F-keys, EDIT
cannot quit through its menu, `nano` loses Meta, `mc` its F-keys).

- **Completes:** [ADR 0061](0061-xterm-terminal-emulator.md) (xterm.js as
  screen and keyboard), [ADR 0064](0064-posix-shell-scripts-jobs-and-app-commands.md)
  §6-7 (standard input, `nano`, full-screen commands), [ADR 0245](0245-feature-service-registry-and-async-resolution.md)
  (the Terminal resolves `Ix86` through the registry) and [ADR 0251](0251-shell-route-to-execute-capable-apps.md)
  (shell route outside the Terminal).
- **Does not change:** `IKazoTerminal`, the line discipline, the full raw
  mode (D27: Os-x86's V2).
- **For:** Kazo's Terminal app, which **calls Os-x86 through `Ix86`** as
  BoxedWine does (§1): a Windows console program, a text-mode DOS program, a
  Linux shell in the terminal tab; and every full-screen command handler,
  including the Os-x86 `x86` command launched by the ADR 0251 route (pure MZ,
  ELF) and `x86 shell`, which get the same extended contract (§2 to §5).

## Context

- The Terminal app (`Apps/System/Kazo.Os.Apps.Terminal/TerminalApp.cs:20`,
  `org.kazoos.terminal`, window app :26) is referenced by the host as a
  system app (`OS/Kazo.Os.Ecosystem/Kazo.Os.Ecosystem.csproj:53`,
  `IsSystemApp="true"`; `AppCatalog.cs:173`) and references only the SDK
  (`Kazo.Os.Apps.Terminal.csproj:18-20`). It starts the shell through
  `KazoShell.StartAsync(KazoShellOptions)` (`Shared/Kazo.Os.AppSdk/Shell/KazoShell.cs:31`,
  options `IKazoShell.cs:114-137`: directory, credential prompt, streams) and
  the shell knows its host app (`Platform/Kazo.Os.Platform.Core/Shell/ShellSession.cs:90`,
  used by `RunPathAsync`, `ShellInterpreter.Scripts.cs:30-36`). No system
  app consumes an interface outside the SDK today.
- The full-screen contract exists: `Shared/Kazo.Os.AppSdk/FullScreenTerminalContracts.cs`
  (86 lines). `IFullScreenTerminalSession` (:69-85) gives `Viewport`,
  `EnterAlternateScreenAsync`, `RenderAsync(TerminalScreenFrame)`,
  `ReadKeyAsync`, `LeaveAlternateScreenAsync`. A `TerminalScreenFrame`
  (:60-63) is a **list of strings** plus a cursor; colors only pass as SGR
  sequences inside the line (`TerminalAnsi.cs:116-157`, `FitToColumns` :159
  onward), an `ESC[0m` closing each row. The `htop`, `top`, `nano`, `watch`
  commands use it.
- `TerminalKey` (:4-34) has Character, Enter, Backspace, Delete, the arrows,
  Home, End, PageUp, PageDown, Resize and Escape: **neither Tab (arrives as
  Character `\t`), nor F1 to F12, nor Insert**. The decoder
  (`Shared/Kazo.Os.AppSdk.Terminal/TerminalInputDecoder.cs`) translates
  Ctrl+letter (:82-83) and an ESC prefix into Alt (:120-128), but **drops the
  F-key sequences** (:177-196: SS3 P/Q/R/S and CSI 15~…24~ yield nothing);
  an unknown sequence is ignored (:27). **F1 to F12 never reach a command.**
  Alt+letter does, except Alt+Q (close the window), Alt+Space under the lock
  and what the browser keeps.
- In full screen, **every key already goes to the command, Ctrl+C included**
  (`TerminalPanel.razor:350-379`, `DispatchKey` :356-360); outside full
  screen, Ctrl+C cancels the command (:362-366, :1132-1139, code 130). On the
  JavaScript side, Ctrl+C **with a selection** copies and is swallowed
  (`TerminalPanel.xterm.entry.js:217-226`); Ctrl+Shift+C always copies;
  Ctrl+V pastes (:229-231).
- xterm.js 6 draws (`TerminalPanel.xterm.entry.js:155`, ADR 0061); the grid
  is redrawn row by row at each `RenderAsync` (`TerminalPanel.razor:1290-1313`,
  smcup, rows, rmcup). The size (`UpdateViewport`,
  `TerminalFullScreenSession.cs:76-85`) is known at the start of the command
  and a resize sends `Resize`.
- Kazo takes its shortcuts before any app (`WindowManagerOverlays.razor.js:60-77`,
  capture on `document`): Alt+F3, F7 to F11 and Alt+Q, Alt+Tab, Alt+F4 under
  the lock (`WindowKeyBindings.cs:312-329`). A DOS or Linux program therefore
  cannot rely on Alt+F-key; the Escape chord of 215 A is the answer.

## Decision

### 1. The Terminal uses Os-x86 through `Ix86`, like BoxedWine

- The Terminal app **references the `Os-x86.Interface` contracts package** as
  an **optional** dependency: a shared library embedded in its package
  (ADR 0244, same copy rule as for any app), resolved when needed through
  `KazoFeatures.GetServiceAsync<Ix86>()` (ADR 0245), which returns `null`
  when the Feature is absent or disabled: the Terminal then behaves as today.
  The OS still knows neither Wine nor v86: only the Terminal app knows
  `Ix86`, and it calls it exactly like BoxedWine (ADR 0245 §2).
- The Terminal is **the only system app of Kazo that uses a Feature**
  (maintainer's answer, 2026-11-11): the list is kept here; another system
  app that would want a Feature asks for an ADR.
- **What the Terminal does itself** (162 A) when a file is run in one of its
  tabs: it reads the header (64 bytes at most, `KazoFile.OpenRead`), decides
  (MZ+PE **console** → run in the tab through `Ix86`; pure MZ → DOS in the
  tab; ELF → Linux in the tab; **graphical** PE/NE → the app associated with
  the extension, `OpenFileAsync` with the marked sender, ADR 0253; extension
  that contradicts the header → refusal, 126) and shows the program's console
  in its own full-screen session (§2 to §5). For that, `KazoShellOptions`
  gains `ProgramHost` (`IKazoShellProgramHost`:
  `TryRunAsync(path, header, arguments, session)`), which the Terminal
  provides when starting the shell: route 2 of ADR 0251 first asks the
  shell's host (`ShellSession.Host`) whether it takes the file; "no" (host
  without `ProgramHost`, `Ix86` absent, unknown header) → the rest of the ADR
  0251 route: type detection by the Feature's format detectors,
  execute-capable apps, the user's default association. Outside the Terminal
  (script run by another app, `hsh` without a program host), that route is the
  only one: a console PE goes to BoxedWine like a graphical PE, a pure MZ or
  an ELF to the `x86` command.
- **Hosting sessions**: the Terminal can open a tab that is an Os-x86
  session (Linux shell through a pty, DOS prompt) offered by `Ix86`, without
  going through the `x86 shell` command (which stays for scripts and
  pipelines); the tab shows the Feature's name and closes like a full-screen
  command (§4).
- Sender, permissions, cgroup: the launch is the Terminal app's for the
  session user (ADR 0253); the `x86.*` permissions asked are those of
  ADR 0246; stopping kills the launch cgroup (206 A).

### 2. Per-cell attributes in `TerminalScreenFrame`

- `TerminalScreenFrame` gains a cell form: `TerminalScreenFrame.FromCells(TerminalCell[,] cells, cursor, label)`
  with `TerminalCell(char Glyph, TerminalColor Foreground, TerminalColor Background, TerminalCellStyle Style)`;
  `TerminalColor` = the 16 ANSI colors (index 0 to 15; 256 colors and
  truecolor in Os-x86's V2, question 1); `Style` = `Bold`, `Underline`,
  `Inverse`, `Blink` (blink rendered as Inverse or ignored, per xterm.js).
  The string form stays (compatibility of the existing commands);
  `FitToColumns` converts one into the other.
- The panel renders a cell frame **by replacing the changed rows**
  (comparison with the previous frame), as SGR sequences to xterm.js; a
  ~30 Hz frame of 80×25 costs at most 25 rows per frame. The cadence belongs
  to the program (Os-x86 polls at 33 ms, 214 A); the panel merges two frames
  arrived during one rendering (the last one wins).
- A frame may exceed the window: `Viewport` stays the truth, the command
  draws 80×25 and the panel crops or centers (black background around, font
  unchanged); hot resize stays `Resize` as is (Os-x86's V2 to pass it to the
  program).

### 3. F1 to F12, Insert, Tab, and the Escape chord

- `TerminalKey` gains `F1` … `F12`, `Insert` and `Tab` (Tab also stays
  delivered as `Character "\t"` to the existing commands that expect it:
  both forms for one version, then `Tab` only, question 2). The decoder
  translates the SS3 P/Q/R/S, CSI 11~ to 24~ and CSI 2~ sequences; the CSI
  modifiers (Shift+F5…) fill `Shift`, `Control`, `Alt`.
- **Escape chord**: in full screen, when the program asked for it
  (`IFullScreenTerminalSession.Options.EscapeChord = true`), an Escape key
  followed **within 500 ms** by a letter yields `Character` with
  `Alt = true`, and by a digit `1` to `9`, `0`, `-`, `=` yields `F1` to
  `F12`; Escape followed by Escape yields a single Escape; Escape then
  nothing for 500 ms yields a single Escape. The decoder already does "ESC
  then byte = Alt" (:120-128); the chord extends the rule to digits and makes
  it explicit with a delay. The panel highlights "Esc" in its status bar
  while waiting.
- The keys Kazo keeps (Alt+Tab, Alt+F4, Alt+Q, Alt+F3/F7-F11) are not
  claimed: the program gets them through the chord.

### 4. Ctrl+C goes to the program; kill otherwise

- In full screen (already the case, :356-360): Ctrl+C is delivered to the
  program (`Character "c"` with `Control`); the guest program receives SIGINT
  through the pty (213 A) or a DOS Ctrl+C. The panel **no longer copies** on
  Ctrl+C in full screen, even with a selection: Ctrl+Shift+C copies (already
  the case), Ctrl+Insert too.
- **Killing the program**: Ctrl+Pause (`Pause` with `Control`, added to
  `TerminalKey` as `Break`, delivered to the panel and not to the program)
  closes the full-screen command as Ctrl+C does outside full screen
  (`CancelActiveCommand`, code 130); the Os-x86 command, or the Terminal
  through `Ix86` (§1), receives the cancellation of its token and kills the
  launch cgroup (206 A). A **"Stop"
  button** in the Terminal's tab bar, visible during a full-screen command,
  does the same (keyboards without Pause; mobile, ADR 0171). `x86 kill` from
  another tab stays possible.
- Outside full screen, nothing changes (Ctrl+C cancels the command).

### 5. Size and options at opening

- `IFullScreenTerminalSession.Options` (`EscapeChord`, requested
  `MinimumViewport`, `CellMode`): the command, or the Terminal for a program
  it runs itself (§1), sets them before `EnterAlternateScreenAsync`;
  `Viewport` is guaranteed known at that point
  (already the case). When the window is smaller than `MinimumViewport`
  (80×25), the panel says so ("Enlarge the window: 80×25 required") and the
  command decides (Os-x86 refuses or crops).

### 6. What does not change

- `IKazoTerminal`, canonical standard input, `nano`, `htop`: the string form
  and the existing key set stay valid; the Escape chord is **on request** (an
  existing command does not see it).
- The full raw mode (every key, including those Kazo keeps) and hot resize
  passed to the program: Os-x86's V2 (D27).

## Rejected options

- **A. Ask for the full raw mode from V1** (215 C): D27 deferred it to V2;
  the keys Kazo keeps cannot be delivered anyway without the keyboard lock
  (Chromium only).
- **B. Without the Escape chord** (215 B): EDIT cannot quit through its menu,
  `nano` without Meta, `mc` crippled (E14).
- **C. Ctrl+C closes the command and kills the cgroup** (216 B): no more
  gentle interruption of a looping program; programs expect SIGINT.
- **D. 256 colors or truecolor right now**: v86 delivers 16 VGA attributes;
  Wine's `cmd` through a pty uses 16; the rendering cost rises without a use.
- **E. The Terminal does not reference `Os-x86.Interface`** and lets the
  Feature's `x86` command alone hold the full-screen contract (first wording
  of this ADR): rejected by the maintainer on 2026-11-11 (§1); the Terminal
  must be able to host Os-x86 sessions and run a console program itself
  (162 A), which a handler launched by the shell does not cover; the OS stays
  without any knowledge of Wine or v86, only the Terminal app knows `Ix86`.
- **F. A Terminal hook in the shell by app name** (the shell tests
  `org.kazoos.terminal`): `ProgramHost` in `KazoShellOptions` does the same
  without the shell knowing an app.

## Consequences

- Terminal app (`Apps/System/Kazo.Os.Apps.Terminal`): references
  `Os-x86.Interface` (shared library, ADR 0244), `ProgramHost`, header read,
  execution through `Ix86`, "Os-x86 session" tab; manifest: the `x86.*`
  permissions of ADR 0246 declared like BoxedWine.
- SDK (`Kazo.Os.AppSdk`): `TerminalCell`, `TerminalColor`, `TerminalCellStyle`,
  `TerminalScreenFrame.FromCells`, `TerminalKey.F1`…`F12`, `Insert`, `Tab`,
  `Break`, `IFullScreenTerminalSession.Options`, `KazoShellOptions.ProgramHost`,
  `IKazoShellProgramHost`; `PublicAPI.Unshipped.txt`, `sdk-compat` fixture,
  minor SDK version. Shell: route 2 of ADR 0251 asks the host first.
- `Kazo.Os.AppSdk.Terminal`: decoder (F-keys, Insert, Escape chord with a
  delay), cell rendering by changed rows, "Stop" button, Ctrl+C without copy
  in full screen, chord status bar; `TerminalPanel.xterm.entry.js` (Ctrl+C
  with a selection only outside full screen).
- Docs: [`terminal-api.md`](../../framework/terminal-api.md),
  [`terminal-emulator.md`](../terminal-emulator.md) (full-screen commands,
  keys), `features.md`, `changes/unreleased` entry.
- Tests: `ProgramHost`: file taken by the host → no detection, no
  candidate; host without `ProgramHost` or `Ix86` null → ADR 0251 route
  unchanged; trimmed OS without Os-x86 → the Terminal starts and `./x.exe`
  gives the ADR 0251 message; decoding of each F-key and Insert; chord Esc+e → Alt+e, Esc+3 → F3,
  Esc+Esc → Esc, lone Esc after 500 ms; Ctrl+C delivered in full screen even
  with a selection; Ctrl+Pause and the button → 130; cell frame rendered,
  unchanged rows not rewritten; `htop` and `nano` unchanged.

## Questions asked to Benoit

1. **Palette of the per-cell attributes?**
   - A) **(Recommended)** 16 ANSI colors (VGA, `cmd`), bold, underline,
     inverse styles; 256 colors and truecolor in Os-x86's V2.
   - B) 256 colors right now (xterm.js renders them; a Linux shell uses them
     with `TERM=xterm-256color`).
2. **Tab as a named key?**
   - A) **(Recommended)** Add `TerminalKey.Tab` and deliver both forms for
     one version, then `Tab` only (ADR 0019, deprecation).
   - B) Keep `Character "\t"` only.

Maintainer's answers (2026-11-11): 1 A, 2 A; the shell's program host
(`KazoShellOptions.ProgramHost`, §1): A (251 A), consulted first.

Settled on 2026-11-11: does the Terminal depend on `Os-x86.Interface` → yes,
as an optional dependency, used like BoxedWine does (§1).
