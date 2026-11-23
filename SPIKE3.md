# Spike 3: ACCEPT, SCREEN SECTION, and one program built two ways (2026-11-22)

**Result: works.** One COBOL program with a SCREEN SECTION compiles to one C# file, and that
file builds into both:

- **a console exe:** the DOS look. A blue BLANK SCREEN fills the window, fields show prompt
  underscores, and you Tab between fields;
- **a Windows exe:** the same layout in a window, with real TextBoxes for the input fields and
  a button bar (Enter, Esc, and F1-F10 when the program has CRT STATUS).

There's no code change between the two. Only the startup differs:

```csharp
static class WinHost {
    static void Main(string[] a) =>
        CobolDotNet.Forms.FormsHost.Run(CobolPrograms.SCREENS_Program.Main, a, "SCREENS");
}
```

Regression suite: **7 passed, 0 failed.**
- **HELLO, MATHTEST, PERFORMS, PERFORMS2, PERFTEST:** unchanged.
- **ACCEPTIN:** new; plain ACCEPT from stdin, byte-identical to stock GnuCOBOL.
- **SCREENS:** new screen test. Both its console and Windows builds must compile.

## Design: the runtime owns the screen

```
COBOL  ──>  cob_screen_display / cob_screen_accept / cob_display_field / cob_accept_field
                 │   (positions, colors, BLANK SCREEN, ERASE, REQUIRED, number checks: once, here)
                 ▼
            ScreenGrid (80x25 cells)  +  AcceptRequest (input fields, focus, message, Check)
                 │
     ┌───────────┼──────────────────┐
ConsoleRenderer  FormsRenderer      ScriptRenderer
 (System.Console) (WinForms window)  (tests: keys from a file, screens printed as text)
```

`IScreenRenderer` has just four jobs:
- show the grid;
- let the user edit the inputs, returning the key that ended the ACCEPT;
- ring the bell;
- shut down.

All COBOL meaning lives in `Screen/ScreenIO.cs`, so a program can't do something on one
renderer that the other can't show. This is what makes **ScreenMode = Both** (the default)
safe.

- Positioning follows GnuCOBOL's `screenio.c` rules: relative LINE/COL, `COL PLUS n`, AT LLCC,
  and LINE/COL 0 meaning "where the last DISPLAY/ACCEPT ended".
- CRT STATUS codes are libcob's: Enter 0, F1-F64 1001-1064, PgUp/PgDn 2001/2002, Esc 2005.
- The renderer is chosen by `Cob.ScreenRenderer`, set by the Windows host. Otherwise the env
  var `COB_SCREEN` picks: `console` (default), `forms` (run-time switch, needs
  CobolDotNet.Forms.dll), or `script`.

## Beginner-friendly behavior (deliberate)

- **Numbers are checked when you leave a field**, in plain English:
  - "Only numbers can be typed here."
  - "That number is too big for this field (at most 5 digits before the point)."
  - "This field can't hold a negative number."
  - "This field holds only 2 digits after the decimal point."

  Valid input is re-formatted through the PICTURE (`1,234.5` → ` 1,234.50`).
- **REQUIRED** gives "Please fill in this field." and puts the cursor back in it.
- **The first key typed in a field replaces its old contents**, like a Windows text box. Arrow
  keys or Home first let you edit in place. Insert toggles insert mode.
- **Unspecified colors inherit the screen's colors** (from the last BLANK SCREEN), so a DISPLAY
  without colors doesn't show up as black bars on a blue screen. *This differs from GnuCOBOL*,
  which uses the terminal default.
- **Windows version, end of program:** if something was displayed since the last ACCEPT, the
  window stays open with "The program has finished. Press Enter or close the window."
  (Otherwise the last screen would flash and vanish.) Closing the window while the program
  runs ends it, like closing a console.
- **Plain DISPLAY in the Windows version** goes into the window as terminal-style lines, since a
  window has no console. In the console version it goes to stdout until the program first uses
  screen I/O, then onto the screen (as GnuCOBOL does).
- A runtime error in the Windows version shows a message box instead of vanishing silently.

## Compiler changes

- `csharp.c`: `cob_screen` declarations → `CobScreen` objects. `COB_SET_SCREEN`,
  `cob_screen_*`, `cob_display_field` / `cob_accept_field` (variadic → `params CobField[]`)
  already translated through the existing filter.
- `typeck.c`: no "runtime is not configured to support SCREEN SECTION" warning in `-fcsharp`
  mode (that warning reflects the stock build having no curses, which our runtime doesn't need).
  `typeck.c` joined the overlay; `save-compiler-overlay.sh` now copies it too.

## Runtime

- `Screen/CobScreen.cs`: screen item and flags (libcob values), `CobKey` codes, `COB_SET_SCREEN`.
- `Screen/Renderer.cs`: `ScreenGrid`, `ScreenInput`, `AcceptRequest`, `IScreenRenderer`.
- `Screen/ScreenIO.cs`: the engine. Also handles plain `cob_accept` from stdin; at end of input
  the field is blanked, as stock does.
- `Screen/FieldEditor.cs`: the keyboard editing rules, shared by the console and test renderers.
- `Screen/ConsoleRenderer.cs`, `Screen/ScriptRenderer.cs`.
- New project `runtime/CobolDotNet.Forms` (net9.0-windows): `FormsRenderer`, `FormsHost`. It's a
  separate assembly, so console programs never load WinForms.
- `CobModule.crt_status` / `cursor_pos` are now `CobField`.

## Tests

`tools/run-tests.ps1` now supports:
- **`NAME.in`:** standard input for both stock and ours (ACCEPTIN compares with stock).
- **`NAME.keys`:** a screen test. Stock GnuCOBOL here is built without curses, so there's no
  stock reference. Ours runs under `COB_SCREEN=script`, and the first run's output becomes
  `samples/expected/NAME.out` after review. The script also builds the Windows version.

Key-script syntax is plain text plus `{ENTER} {TAB} {BTAB} {ESC} {UP} {DOWN} {LEFT} {RIGHT}
{HOME} {END} {BS} {DEL} {INS} {PGUP} {PGDN} {F1}..{F24}`.

## Verified by hand

- The console version in a real console window: blue screen, layout, re-formatted amount,
  masked password.
- The Windows version: real TextBoxes, Tab, numeric re-formatting, masked password, F3 into CRT
  STATUS, the end-of-program prompt.

The last Forms fix (AUTO skip into a field that's already full) builds but **wasn't
re-checked on screen**.

## Known gaps (next)

- **Forms literals are painted text on the cell grid, not Label controls.** It looks the same,
  but real Labels belong to the designer direction: the VS designer with a restricted toolbox →
  SCREEN SECTION copybook, plus the "this control forfeits the DOS screen" warning (see
  DECISIONS.md).
- Not handled yet:
  - ACCEPT ... TIMEOUT
  - SCROLL
  - CONTROL / COLOR variables
  - CURSOR IS
  - OCCURS on input fields
  - BELL / BLINK (stored, not shown)
  - GRAPHICS line drawing
- `UPPER` / `LOWER` work in the runtime, but GnuCOBOL's default dialect rejects them in
  SCREEN SECTION.
- The Windows window is a fixed 80x25. Console windows smaller than 80x25 clip.
- The MSBuild side of "ScreenMode = Both | Console | Forms" (two exes from one project) is
  done by hand in the test script. It becomes real with the VS project template.
