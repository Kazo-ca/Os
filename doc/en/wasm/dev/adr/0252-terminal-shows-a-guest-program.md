> [Version française](../../../../fr/wasm/dev/adr/0252-terminal-shows-a-guest-program.md)

# ADR 0252: the Terminal shows a guest program (per-cell attributes, F1 to F12, Escape chord, Ctrl+C to the program)

## Status

Proposed on 2026-11-11, for the Os-x86 feature
([Kazo-ca/Os-x86](https://github.com/Kazo-ca/Os-x86)), lot 1 of the
`feature/base` series. The recommendation of each question asked to Benoit
is applied while waiting for his answer. Source decisions: 154 B ("the Kazo
terminal shows" the console of a Windows program and a text-mode DOS
program; Os-x86 opens no window), 162 (Terminal app exception), 215 A (what
is asked of the Terminal: a fixed-width "full screen" surface, 80 columns
and 25 rows or more, per-cell color attributes and cursor, refreshed at
~30 Hz; key-by-key input with Ctrl+letter; Esc+letter / Esc+digit chord
translated to Alt+letter / F1-F12; Home, End, Delete, Page; size known at
opening; hot resize in V2) and 216 A (Ctrl+C goes to the guest program;
kill by Ctrl+Pause when Kazo has it, otherwise a Terminal button or
`x86 kill`), 2026-11-11; Os-x86 trial E14 (Wine's `cmd` and FreeDOS usable
in such a grid under 100 ms; without Alt nor F-keys, EDIT cannot quit
through its menu, `nano` loses Meta, `mc` its F-keys).

- **Completes:** [ADR 0061](0061-xterm-terminal-emulator.md) (xterm.js as
  screen and keyboard) and [ADR 0064](0064-posix-shell-scripts-jobs-and-app-commands.md)
  §6-7 (standard input, `nano`, full-screen commands).
- **Does not change:** `IKazoTerminal`, the line discipline, the full raw
  mode (D27: Os-x86's V2).
- **For:** the Os-x86 binfmt handler (ADR 0251) and `x86 shell`: a Linux
  shell, Wine's `cmd`, a text-mode DOS program in the terminal tab. The
  Terminal **does not need** `Os-x86.Interface`: the Feature's command is
  what uses the contract.

## Context

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

### 1. Per-cell attributes in `TerminalScreenFrame`

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

### 2. F1 to F12, Insert, Tab, and the Escape chord

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

### 3. Ctrl+C goes to the program; kill otherwise

- In full screen (already the case, :356-360): Ctrl+C is delivered to the
  program (`Character "c"` with `Control`); the guest program receives SIGINT
  through the pty (213 A) or a DOS Ctrl+C. The panel **no longer copies** on
  Ctrl+C in full screen, even with a selection: Ctrl+Shift+C copies (already
  the case), Ctrl+Insert too.
- **Killing the program**: Ctrl+Pause (`Pause` with `Control`, added to
  `TerminalKey` as `Break`, delivered to the panel and not to the program)
  closes the full-screen command as Ctrl+C does outside full screen
  (`CancelActiveCommand`, code 130); the Os-x86 command receives the
  cancellation of its token and kills the launch cgroup (206 A). A **"Stop"
  button** in the Terminal's tab bar, visible during a full-screen command,
  does the same (keyboards without Pause; mobile, ADR 0171). `x86 kill` from
  another tab stays possible.
- Outside full screen, nothing changes (Ctrl+C cancels the command).

### 4. Size and options at opening

- `IFullScreenTerminalSession.Options` (`EscapeChord`, requested
  `MinimumViewport`, `CellMode`): the command sets them before
  `EnterAlternateScreenAsync`; `Viewport` is guaranteed known at that point
  (already the case). When the window is smaller than `MinimumViewport`
  (80×25), the panel says so ("Enlarge the window: 80×25 required") and the
  command decides (Os-x86 refuses or crops).

### 5. What does not change

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
- **E. The Terminal references `Os-x86.Interface`** (Os-x86 plan, ADR 10 in
  its first wording): useless since the Feature's command holds the
  full-screen contract; and the OS must know neither Wine nor v86.

## Consequences

- SDK (`Kazo.Os.AppSdk`): `TerminalCell`, `TerminalColor`, `TerminalCellStyle`,
  `TerminalScreenFrame.FromCells`, `TerminalKey.F1`…`F12`, `Insert`, `Tab`,
  `Break`, `IFullScreenTerminalSession.Options`; `PublicAPI.Unshipped.txt`,
  `sdk-compat` fixture, minor SDK version.
- `Kazo.Os.AppSdk.Terminal`: decoder (F-keys, Insert, Escape chord with a
  delay), cell rendering by changed rows, "Stop" button, Ctrl+C without copy
  in full screen, chord status bar; `TerminalPanel.xterm.entry.js` (Ctrl+C
  with a selection only outside full screen).
- Docs: [`terminal-api.md`](../../framework/terminal-api.md),
  [`terminal-emulator.md`](../terminal-emulator.md) (full-screen commands,
  keys), `features.md`, `changes/unreleased` entry.
- Tests: decoding of each F-key and Insert; chord Esc+e → Alt+e, Esc+3 → F3,
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

Applied while waiting: 1 A, 2 A.
