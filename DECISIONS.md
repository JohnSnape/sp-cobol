# Visual COBOL for .NET: decisions log

Each entry: date, the decision, who made it, and why. **[USER]** marks the user's own calls.

---

### 2026-11-21: Target .NET IL by emitting C#, not raw IL
GnuCOBOL front end → new C# back end → Roslyn/MSBuild → .dll/.exe.
Why: `#line` directives give COBOL-source debugging in Visual Studio for free, generated code is
readable, and Roslyn optimizes better than hand-emitted IL.

### 2026-11-21: Base on GnuCOBOL 3.2, not Otterkit
Why: GnuCOBOL's front end is complete (10+ dialects, ~1,350 tests, NIST harness). Otterkit was
archived Feb 2024 and its code generator handles only 5 statements. A few Apache-2.0 Otterkit
pieces (project templates, VS Code grammar) may be reused with attribution.

### 2026-11-21: WinForms via a C# UI project calling a COBOL DLL
The .NET 6+ WinForms designer only serializes C#/VB, so forms stay in C# with one-line event
handlers that call COBOL. Business logic lives in COBOL.

### 2026-11-21 [USER]: Stay on .NET; no FreeDOS target
Considered running GnuCOBOL under FreeDOS (DJGPP) on an old laptop for the DOS look. Rejected:
it loses Visual Studio/.NET, and there is no maintained DOS port. The look is reproduced instead
by the console mode below.

### 2026-11-21 [USER]: First-class console mode for SCREEN SECTION
SCREEN SECTION (FOREGROUND-COLOR, BACKGROUND-COLOR, LINE/COL, ACCEPT/DISPLAY) runs on
`System.Console`, in place of libcob's curses layer. That gives the DOS-era text-screen look,
still compiled to IL and debugged in VS 2022. Expected to come before WinForms in the
build order.

### 2026-11-21 [USER]: Runtime data model = managed byte arrays
Each storage area is a `byte[]`; a field is (array, offset, size, attributes). REDEFINES and
reference modification are views over the same array. `SET ADDRESS OF` re-targets
(array, offset). Fully safe IL, GC-friendly, readable in debugger watch windows.
Cost: raw USAGE POINTER arithmetic is awkward; add a narrow unsafe escape hatch only if a
real program needs it.

### 2026-11-21 [USER]: Ultimate goal = run the user's own old COBOL programs
Priorities follow what those programs actually use (dialect, file types, screen handling).
The user's Apple IIe COBOL system covers some of them; this compiler is for the rest.

### 2026-11-21: The target programs are Wang VS COBOL, rewritten from memory
All of them were written on a Wang VS-45. No source survives. List and feature map are in
`PROGRAMS.md`. The dialect was verified against the user's own copy of the VS COBOL Reference
(800-1201-06): ANSI-74 base, COMP = packed decimal, BINARY = 2-byte, DISPLAY AND READ with
DISPLAY-WS screens.

### 2026-11-21 [USER]: Rewrite the programs in standard COBOL, not Wang syntax
The programs are rewritten in standard COBOL (GnuCOBOL default / COBOL 2014 dialect) with
SCREEN SECTION for screens, not Wang DISPLAY AND READ / DISPLAY-WS.
Consequences:
- **No grammar changes** to GnuCOBOL. The prebuilt `parser.c` / `scanner.c` in the tarball are
  used as-is, so bison/flex aren't needed, only a C compiler to build `cobc`.
- No `wang.conf`. Write `COMP-3` for packed decimal explicitly (Wang `COMP` meant packed).
- Modern constructs are allowed in the rewrites: EVALUATE, END-IF, inline PERFORM, reference
  modification, intrinsic FUNCTIONs (including RANDOM for Adventure).
- Wang screen ideas (PF keys, protected/dim fields, RANGE checks) are done with SCREEN SECTION
  attributes, `CRT STATUS` for function keys, and ordinary validation code.
- A Wang dialect stays possible later. The manuals and notes in PROGRAMS.md keep that door open.

### 2026-11-21 [USER]: Primary audience = new programmers learning COBOL
The compiler's first job is to be something a beginner can install and learn COBOL with.
Rewriting the user's Wang programs comes second, and they double as teaching samples.
Consequences for design:
- **Error messages are a feature.** Plain-English diagnostics that point at the COBOL line,
  ideally with a hint ("did you forget the period?", "PIC 9 can't hold a minus sign").
- **Install and first run must be trivial.** One installer, a "New COBOL Console App" project
  template, and F5 runs it. No MSYS2 or environment variables for the end user. MSYS2 is only
  our build tool for `cobc`.
- **Debugging in COBOL terms**: step by COBOL line, and watch data items by their COBOL names
  showing their PIC-formatted values.
- **Samples and a short tutorial** ship with it. The Wang program rewrites (Life, Eliza, check
  writer, payroll...) are good graded examples.
- Console mode (SCREEN SECTION) stays first, since that's where beginners start. WinForms comes later.

### 2026-11-21: Spike 1 technique: a line filter over GnuCOBOL's C codegen
`-fcsharp` routes all of codegen.c's output through `compiler/cobc/csharp.c`, which turns
declarations and statements into C#. It proved the pipeline fast, with byte-identical output
on HELLO and MATHTEST. It's a stepping stone, not the final emitter (see SPIKE1.md).

### 2026-11-21: Runtime written fresh in C#, not translated from libcob
Same function *names* as libcob (so generated code maps 1:1), but new implementations built
on one exact numeric form, (BigInteger, scale). This keeps the runtime out of LGPL-derivative
territory and lets the project choose its own runtime license. The compiler (modified cobc)
stays GPLv3.

### 2026-11-21: Working name "CobolDotNet" (placeholder)
The runtime namespace and assembly are `CobolDotNet.Runtime`. "Visual COBOL" is a
Rocket/Micro Focus product name and must not be used. **The final product name is the user's call.**

### 2026-11-21: Next: paragraphs as C# methods (spike 2)
C# forbids `goto` into a block, so GnuCOBOL's switch-based PERFORM return can't work when
a PERFORM sits inside IF/EVALUATE/loops (confirmed: CS0159 on PERFTEST.cbl). Plan: each
paragraph/section is a method, PERFORM is a call, PERFORM THRU is a range loop, and GO TO
returns the next paragraph id to a dispatcher. Bonus for learners: the debugger's call stack
shows paragraph names.

### 2026-11-21: Spike 2 done: paragraphs are methods, control flow via a dispatcher
Implemented as planned (SPIKE2.md). Each SECTION/paragraph/ENTRY becomes `static int NAME()`
returning the next paragraph id; -2 means "PERFORM range finished" and -3 means "leave the
program". GnuCOBOL's perform frame stack and its "implicit PERFORM return" checks are kept
as-is, so PERFORM THRU semantics come from GnuCOBOL. Labels local to a paragraph stay plain
gotos. EXIT SECTION's label becomes its own method.

### 2026-11-21: Regression rule: every sample must match stock GnuCOBOL byte for byte
`tools/run-tests.ps1` compiles each `samples/*.cbl` with stock GnuCOBOL (the reference) and
with ours, then diffs output and exit code. The generated C# must build with warnings as
errors. New runtime features land with a sample that exercises them.

### 2026-11-21 [USER]: Wang VS-45 system emulator = possible future project
A full Wang VS-45 emulation (system + text-only workstation screen) is a "someday" goal,
**out of scope** for this compiler. Notes in PROGRAMS.md and the manuals in
`H:\New folder\Wang VS` would seed it.

### 2026-11-22 [USER]: One screen definition, two renderers (DOS-style console and Windows Forms)
SCREEN SECTION is the single source of truth for a screen. The runtime draws it through a
renderer interface: a **console renderer** (DOS look, System.Console) and a **Forms renderer**
(Label/TextBox/CheckBox/Button on a character-cell grid, e.g. 80x25 cells). Program logic is the
same for both: `ACCEPT screen` waits until Enter or a function key, then returns with the fields
filled in, so beginners never need event-driven code to get a Windows version.
Control mapping: literal → Label; `USING`/`TO` field → TextBox (MaxLength from PIC, numeric
validation, edit mask); `SECURE` → password TextBox; 1-char "X" field → CheckBox; colors →
control colors; F-keys/`CRT STATUS` → Buttons returning that key code.
Reverse direction (later, with the VS integration): the user may lay out a screen in VS's
WinForms designer with a restricted "COBOL Screen" toolbox and grid snap; the build reads
`InitializeComponent` and writes a SCREEN SECTION copybook.

### 2026-11-22 [USER]: The Screen App template builds BOTH versions by default
Project property `ScreenMode = Both | Console | Forms`, default **Both**: one build produces
`PROG.exe` (console/DOS look) and `PROG-Win.exe` (window). A run-time switch may also pick one.

### 2026-11-22 [USER]: Warn before a non-DOS control forfeits the DOS screens
Two templates: **COBOL Screen App** (convertible controls only, builds both) and **COBOL Windows
App** (full toolbox, C# form calling COBOL, Windows only). If a user adds a control with no DOS
equivalent (ListBox, DataGridView, menu, picture, mouse-only events, free pixel layout) to a
Screen App form, they must be told *before* it's accepted that this gives up the DOS-style
screen. Planned layers:
1. **Designer:** the restricted toolbox only offers convertible controls. Dropping anything
   else (from the full toolbox or pasted) pops a dialog: "A ListBox has no DOS-screen
   equivalent. Keeping it turns this project into a Windows-only app (ScreenMode = Forms).
   Keep it / Remove it." Choosing Keep sets ScreenMode = Forms explicitly, so it's a deliberate act.
2. **Build:** if ScreenMode is Both or Console and a form contains a non-convertible control,
   the build fails with a plain-English error naming the control and both ways out (remove it,
   or set ScreenMode = Forms). Never a silent drop.
3. **Console renderer** never guesses: it can only draw what SCREEN SECTION can express, so the
   source of truth can't contain an unconvertible item in the first place.

### 2026-11-22: Spike 3 done: the runtime owns the screen; renderers only present and edit
All SCREEN SECTION meaning (positions, colors, BLANK/ERASE, REQUIRED, numeric checks, CRT STATUS)
lives in `Screen/ScreenIO.cs` over an 80x25 `ScreenGrid`. `IScreenRenderer` only shows the grid
and edits one ACCEPT's fields. Console, Forms and a scripted test renderer share it. The Forms
renderer paints literals on the cell grid and uses real TextBoxes for input. Labels as real
controls come with the designer pipeline. WinForms lives in a separate assembly
(`CobolDotNet.Forms`), so console programs never load it. See SPIKE3.md.

### 2026-11-22: Deliberate differences from GnuCOBOL screen I/O (for beginners)
- Unspecified colors inherit the last BLANK SCREEN's colors (GnuCOBOL: terminal default).
- The first key typed in a field replaces it, like a Windows text box (GnuCOBOL: overwrite in place).
- Numeric fields are checked on leaving them, with plain-English messages, and re-formatted
  through their PICTURE.
These are screen-only behaviors; plain DISPLAY/ACCEPT still match stock byte for byte.

### 2026-11-22: Screen tests use approved output, not stock
The stock reference build has no curses, so screen samples (`NAME.keys`) are checked against
our own reviewed output from the scripted renderer. Every screen sample also builds the
Windows version, because the default ScreenMode is Both.

### 2026-11-22: Visual Studio integration = an MSBuild SDK package + a dotnet-new template
`CobolDotNet.Sdk` (NuGet, PackageType MSBuildSdk) wraps Microsoft.NET.Sdk and carries cobc.exe
(+5 MSYS2 DLLs, config, copybooks), the runtime DLLs and the build logic. A project is a `.csproj`
with `Sdk="CobolDotNet.Sdk/0.1.0"`, so VS loads it with the normal C# project system (no custom
project system to write). `.cbl` files ride along as `None` items and are compiled by an inline
`CobolCompile` task, which works in VS's .NET Framework MSBuild and in `dotnet build`.
The template "COBOL Screen App" asks for Screens (Both / Console / Forms) at creation time.
See SPIKE4.md.

### 2026-11-22: Debugging maps to COBOL with #line
codegen's per-statement `Line:` comments become C# `#line N "file.cbl"`. Plumbing is
`#line hidden`. The PDB then steps COBOL lines, and the call stack shows paragraph names.

### 2026-11-22: ScreenMode=Both builds the Windows twin as a second pass of the same project
Same TargetFramework (`net9.0-windows`), one restore, a separate obj folder, the same output
folder: `NAME.exe` and `NAME-Win.exe` side by side. The F5 "Windows" profile runs NAME.exe with
`COB_SCREEN=forms` instead, because launchSettings can't point at the twin reliably.

### 2026-11-22: The DOS-forfeit guard is a build error first (COB2001)
Any WinForms control other than Label/TextBox/CheckBox/Button/Panel in a `*.Designer.cs` of a
Both/Console project fails the build. The message names the control and the two ways out. The
designer-time "Keep it / Remove it" dialog needs a VSIX and comes with the designer work.

### 2026-11-22: Source format defaults to fixed [USER]
`<CobolSourceFormat>fixed</CobolSourceFormat>` (columns 8-72) by default, matching the Wang
originals and most books. `free` is one property away. Asked whether beginners should start in
free format instead; **the user chose to keep fixed** (2026-11-22). The column-7/72 hints in
`CobolCompile.cs` and the planned column guides in the editor carry the teaching load.

### 2026-11-22: Sequence numbers in columns 1-6 [USER]
Fixed-format source carries a sequence number in columns 1-6, starting at **000010 and going up
by 10**, the way COBOL is normally written. The template's `Program.cbl` and every sample are
numbered this way. `tools/renumber.ps1 FILE...` (options `-Start`, `-Step`) renumbers a file
in place without touching columns 7 onward or the line endings. The compiler still ignores
columns 1-6, so a gap or a duplicate number is never an error.

For the COBOL editor (VSIX, still to come), numbering stays by tens with no in-between
numbers. **[USER, as the Wang editor did]:** inserting a line gives it the previous line's
number + 10, and every line after it moves up by 10. So the file is always 000010, 000020, ...
in order. Deleting a line closes the gap the same way. (An earlier draft used midpoint numbers
like 000025. The user's recollection of the Wang editor replaced it.) In effect the editor
renumbers from the change down, the same result `renumber.ps1` gives for the whole file.

### 2026-11-22: The COBOL editor is a MEF VSIX, not a TextMate grammar
A TextMate grammar would give colors only. The sequence-number rules need editor command
handlers, so the extension is a classic MEF VSIX:
- a classifier for the colors;
- an adornment layer for the column guides;
- `IChainedCommandHandler`s for the keys;
- a small AsyncPackage for one menu command.

It targets 17.0 APIs, so one VSIX serves VS 2022 and VS 2026. The logic (scanner, numbering
plans) has no VS types and is unit-tested headless. See EDITOR.md.

### 2026-11-22: Editor behaviors around the sequence area
These go beyond the user's numbering rule. Each follows from "numbers are the editor's job":
- Each keystroke, with its renumbering, is one undo step. Edits that don't come from a key
  (Find and Replace, automation) are renumbered right after.
- The number area can't be typed over or deleted into. Typing there goes to column 8, and
  Backspace at column 8 joins with the line above.
- Tab inserts spaces to columns 8, 12, 16, ..., the area A/B stops. Home goes to the code,
  then to column 8.
- Automatic numbering only applies to files that already have numbers. Older unnumbered
  sources aren't changed until the user picks Edit > Renumber COBOL Lines.
- Keywords come from cobc's own reserved-word list, so the editor and compiler agree on what
  a reserved word is.

## Public release on GitHub: name, license, what's left out [USER] (2026-11-23)

- **Name: SP COBOL [USER].** The repo is `sp-cobol`. Shown names (the editor in Extensions,
  the README) say SP COBOL; internal identifiers (namespaces, `CobolDotNet.Sdk`) keep the old
  working name for now, since renaming them breaks existing projects and the installed editor.
- **License: MIT for our own code, GPLv3 for the compiler [USER].** The compiler is modified
  GnuCOBOL and must stay GPLv3. The runtime, SDK, editor, templates and tools are MIT. The
  runtime matches libcob's function names and constant values (needed so cobc's output can
  call it) but its code is written fresh, so MIT is open to it. User programs are not covered
  by either license.
- **Public repo, leaving out [USER]:** the OCR text of Wang's manuals (Wang's copyright), the
  owner's list of lost programs, and the downloaded reference sources (GnuCOBOL tarball,
  Otterkit, spikes). The GnuCOBOL changes ship as `compiler/gnucobol-3.2-csharp.patch` plus
  the changed files, which with GnuCOBOL 3.2's public source is the complete corresponding
  source for the cobc.exe in the SDK package.

## How other people install it: a per-user feed folder + install.ps1 (2026-11-23)

- New projects' `nuget.config` reads `%LOCALAPPDATA%\SP COBOL\feed` (NuGet expands the
  variable), so the template works on any PC with no hand edits.
- `tools\install.ps1` downloads the latest GitHub release, copies the SDK package there,
  installs the templates as a NuGet template package (`SpCobol.Templates`), and starts
  VSIXInstaller. It touches only the user profile, apart from the VS extension installer.
- Not nuget.org yet: publishing there needs an account and API key (the owner's call), and
  would remove the feed folder step entirely.

## DISPLAY of PIC N shows characters, unlike stock GnuCOBOL (2026-11-24)

GnuCOBOL 3.2 keeps national (PIC N) data as UTF-16 big-endian and its DISPLAY writes those
bytes raw, so `N"ABC"` prints as NUL A NUL B NUL C. GnuCOBOL itself warns that national
support is "unfinished". For a learner that output is useless, so our DISPLAY (console and
screen) decodes the field and prints the characters (anything above U+00FF as `?`, since
console output is Latin-1). Storage is unchanged, so MOVEs and comparisons still match
stock. This is the one place the suite expects different output from stock GnuCOBOL:
`samples/expected/NATIONAL.out` is written by hand.

## Pointer casts in generated code become typed CobPtr views (2026-11-24)

codegen writes `*(short *)(p)`, `*(cob_u8_ptr)(p)` and the like when a binary (COMP) field
or a single byte is read or written in place: a COMP subscript, a PIC 9 initialized to
zero, a COMP field in arithmetic. The C# filter now turns these into `(p).S16`,
`(p)[0]`, ... (`ref` properties on CobPtr, so they can also be assigned), and
`COB_BSWAP_16/32/64` are runtime functions. Before this, any program with a COMP subscript
or a PIC 9 item without VALUE failed to build.

## cobc's inline optimizer helpers live in the runtime (2026-11-24)

For COMP, COMP-5 and COMP-3 items used as counters, subscripts or in simple ADD/SUBTRACT/IF,
cobc's codeoptim.c writes small C functions into each program (cob_cmpswp_u16,
cob_addswp_s32, cob_setswp_u24, cob_add_packed_int, ...; about 130 of them). In C# mode
they are not emitted. The runtime has all of them instead (BinaryOps.cs), built on four
generic load/store routines, so their behavior is defined in one place. Byte order follows
the names (swp = big-endian COMP; plain = native COMP-5); arithmetic wraps at the field
width like the C. DISPLAY of a COMP-5 item follows libcob too: raw value, padded to what
the size holds (5 digits for 2 bytes, 10 for 4, 20 for 8). Samples BINOPS and TABLES2
check all of this against stock GnuCOBOL.
