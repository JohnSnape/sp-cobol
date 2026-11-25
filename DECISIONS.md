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

## File I/O: libcob's behavior and formats, minus its plain bugs (2026-11-24)

SEQUENTIAL, LINE SEQUENTIAL and RELATIVE files (runtime/FileIO.cs) follow GnuCOBOL 3.2's
libcob: the same FILE STATUS values for each case, the same AT END / INVALID KEY /
END-OF-PAGE exceptions, and **the same bytes on disk**, so data files move freely between
SP COBOL and GnuCOBOL programs. The regression suite checks this: each file test runs under
stock GnuCOBOL and ours, and every data file left behind must match byte for byte
(FILESEQ, FILEREL, FILELIN).

The rules were taken from fileio.c into a spec (with line references, and the unclear ones
confirmed by running stock programs). Where stock has a plain bug we do what it clearly
means to do instead. FILEFIX tests each one, with expected output written by hand:

| Stock 3.2 | SP COBOL |
|---|---|
| a TAB, form feed or ESC in a LINE SEQUENTIAL record is rejected with 71 (a macro reads past the byte it checks) | allowed, as the code intends |
| a quoted ASSIGN name (`"'a.dat'"`) keeps its first quote and fails with 30 | quotes removed |
| `$VAR` in the middle of a path swallows the next `\` | separators kept |
| LINE SEQUENTIAL files open in Windows text mode: Ctrl-Z ends the file, and LF-only files lose text when a long line is split | binary reads: CR LF and LF both end a line, Ctrl-Z is data (status 09 like any other control byte) |
| LINE SEQUENTIAL REWRITE of a shorter record overwrites the CR of the line end | the line end is kept |
| REWRITE / DELETE of an empty RELATIVE slot gives 00 (REWRITE even writes the data into the empty slot) | 23, record not found |
| the error message names the last environment variable it looked up (`'missing_dat'`) | it names the ASSIGN value (`'missing.dat'`) |

Kept as in stock, because they are behavior rather than bugs: CR LF line ends written on
Windows, the variable-record header `[hi][lo][00][00]`, the 8-byte relative slot header,
LINAGE padding with blank lines instead of form feeds, CLOSE REEL giving 07, the
"implicit CLOSE" warning at STOP RUN, and exit code 1 after a fatal file error.

Added for learners: after libcob's fatal file error message, one `hint:` line saying what
to check (for 35, the folder the program looked in). `COB_NO_HINTS=1` turns hints off.

The compiler side: `cob_file` handles become `CobFile` objects; the key table `k_NAME` is
an array (`(k_X + 1)->field` → `k_X[1].field`); the Default Error Handler is a paragraph
method like any other, so a failed I/O statement PERFORMs it.

ORGANIZATION INDEXED compiles (our cobc no longer refuses it), but every operation returns
91 until its file format is decided.

## INDEXED files: SP COBOL's own simple format [USER] (2026-11-24)

Options offered: our own simple format, an SQLite database per file, or VBISAM/C-ISAM
compatibility with GnuCOBOL. **The user chose our own simple format.** Stock GnuCOBOL keeps
indexed files in an external library and our reference build has none, so no format was
there to match anyway.

The format (runtime/IndexedFile.cs): one file; a header (`SPCOBIX1`, record size, key
offsets/lengths/duplicates); then one slot per record in write order (int64 length, 0 =
deleted; int64 sequence number; the record). OPEN scans the slots and builds a sorted
in-memory index per key, so there is no index file to fall out of step with the data.
Pure .NET, no extra DLLs, and simple enough to explain in the book. Costs: OPEN reads the
whole file (fine for the record counts beginners and the Wang programs use), and deleted
slots aren't reused until the file is copied.

Statuses as GnuCOBOL reports them: 02 duplicate alternate key (on WRITE/REWRITE, and on
READ when the next record has the same alternate key), 21 key sequence error (ACCESS
SEQUENTIAL WRITE, also after OPEN EXTEND), 22 duplicate key, 23 not found, 39 the file's
record size or key layout differs from the program's. FILEIDX tests all of these; its
expected output was worked out by hand, since stock has no indexed support to compare with.

## CALL, CANCEL and multi-program projects (2026-11-24)

- **Finding programs:** every program's entry method carries `[CobolProgram("NAME")]`.
  `CALL "NAME"` looks the name up among the loaded assemblies (then `NAME.dll` beside the
  program or on `COB_LIBRARY_PATH`), the way libcob finds a module; exact name first, then
  ignoring case. Not found: `ON EXCEPTION` runs, or without it the program stops with
  libcob's `module 'NAME' not found` plus a hint line.
- **The call itself:** codegen writes `call_NAME.Call(args)` instead of C's function-pointer
  cast. `Call` converts each argument to the callee's parameter type (CobPtr for BY
  REFERENCE / BY CONTENT, a number for BY VALUE) and passes defaults for arguments the
  caller left out, as C passes NULL. ON EXCEPTION uses a flag instead of C's `goto` into
  another block.
- **LINKAGE items are class fields.** In C# the paragraphs are separate methods, so the
  entry method stores its arguments into the class's fields for the LINKAGE items; BY VALUE
  arguments become a small native-binary buffer, as C's `&b_N`.
- **One class per program.** All programs in one source file share the outer class
  `<FIRST>_Program`; each later one (nested, or a second program in the file) gets its own
  nested class `N_<ID>_<n>`, the way C keeps each program's bookkeeping (`module`,
  `initialized`, decimals, ...) inside its own function. GLOBAL items of the outer program
  stay visible to nested ones.
- **RETURN-CODE and index items** are plain `int`s in C, and a cob_field may point at them
  (DISPLAY RETURN-CODE). In C# they are 4-byte storage read and written as `.S32`.
- **CANCEL** calls the program's internal entry with -1 (libcob's cancel path), so its
  WORKING-STORAGE starts fresh on the next CALL.
- **Projects with several .cbl files:** the SDK compiles one as the program (`-x`) and the
  others as subprograms: `CobolMainProgram` if set, else `Program.cbl`, else the only file,
  else the first file whose PROCEDURE DIVISION has no USING.
- **`-Wno-unfinished` in the SDK:** GnuCOBOL warns that BY VALUE and PIC N handling are
  "unfinished" in *its* runtime. Ours implements them (CALLS, NATIONAL), so the warning would
  only alarm a learner.
- Not done yet: `PROCEDURE DIVISION RETURNING` (stock 3.2 doesn't implement it either),
  `CALL ... RETURNING ADDRESS`, program pointers (`SET ptr TO ENTRY`), CALL STATIC.

Note for the book: `CALL ... ON EXCEPTION DISPLAY "x" NOT ON EXCEPTION ...` attaches the NOT
ON EXCEPTION to the DISPLAY, not the CALL (DISPLAY has its own exception phrases). Write
`END-DISPLAY`. Stock GnuCOBOL parses it the same way; it's standard COBOL.

## INSPECT and UNSTRING: libcob's algorithm, exactly (2026-11-24)

runtime/InspectUnstring.cs follows libcob/strings.c step for step, because INSPECT has
details where implementations differ and the Wang rewrites (Eliza, the converters) lean on
it: phrases are applied one at a time across the BEFORE/AFTER region, a position counted or
replaced by one phrase is marked so later phrases skip it, replacements go to a side buffer
copied back at the end (so BEFORE/AFTER INITIAL searches see the original text), and a
signed DISPLAY number is inspected without its overpunched sign. Test INSPSTR compares
against stock: competing phrases, LEADING/FIRST/TRAILING, CHARACTERS, CONVERTING with a
region, a signed item, and UNSTRING with COUNT/DELIMITER/POINTER/TALLYING, ALL, several
delimiters, ON OVERFLOW, numeric targets.

Kept as stock: UNSTRING into a `JUSTIFIED RIGHT` item fills it from the left (cobc doesn't
pass the justified flag for that target; MOVE does justify). Changing it would need codegen
work and would make results differ from GnuCOBOL; revisit if a program needs it.

## Intrinsic functions, COMP-1/COMP-2, DIVIDE REMAINDER, class tests (2026-11-24)

**Intrinsic functions** (runtime IntrinsicCore/Strings/Numeric/Dates.cs) port libcob's
intrinsic.c, aiming at identical printed results, because DISPLAY FUNCTION X prints
according to the result field's type: result fields are built exactly as libcob builds them
(4- or 8-byte binary, or display with GMP's mpz_sizeinbase width, which can be one too
big). The work was split three ways and each group proved against stock (FUNCSTR, FUNCNUM,
FUNCDATE):
- Numeric: libcob's own decimal steps (divide keeps 38 extra digits, truncating);
  transcendental functions in 160-digit BigInteger arithmetic rounded to 96 significant
  digits, as stock's mpf_get_str; RANDOM is GMP's Mersenne Twister with its seeding, so
  sequences match stock (seeds cut to 32 bits, as stock).
- Dates: integer dates from 1601-01-01, all FORMATTED-* forms, ACCEPT FROM DATE / DAY /
  DAY-OF-WEEK / TIME, command-line arguments and environment variables, COB_CURRENT_DATE.
- Not implemented in libcob 3.2 either (stop with "attempt to use non-implemented
  function", as stock): BOOLEAN-OF-INTEGER, INTEGER-OF-BOOLEAN, STANDARD-COMPARE,
  CHAR-NATIONAL, DISPLAY-OF, NATIONAL-OF, EXCEPTION-FILE-N, EXCEPTION-LOCATION-N.
- Stock quirks kept (they define what GnuCOBOL users see): TEST-NUMVAL-C rejects a
  multi-character currency string; EXCEPTION-FILE treats any 0x05xx code as a file error;
  short negative UTC offsets print as +00mm; COMBINED-DATETIME overflows for large days;
  SECONDS-PAST-MIDNIGHT ignores COB_CURRENT_DATE.
- Stock bug not kept: SECONDS-FROM-FORMATTED-TIME with decimals can pick up a leftover
  scale from another function's work decimal in stock; ours uses scale 0.
- Known limits: EXCEPTION-STATUS reads the current exception code (stock keeps the "last"
  one separately); EXCEPTION-STATEMENT is spaces; CONTENT-OF sees every pointer as NULL.

**COMP-1 / COMP-2** (Float.cs): double to decimal = exact value rounded to 96 significant
digits; decimal to double = truncated toward zero (GMP's mpf_get_d truncates); DISPLAY =
the Windows C runtime's "%.16G" / "%.8G" (three-digit exponent with one leading zero
removed, so E-05, E+20). FLOATS checks it, with FUNCTION RANDOM, against stock.

**Division by zero** now leaves the receiving field unchanged (it stored 0): the decimal is
marked NaN through the rest of the expression, and the store raises SIZE ERROR, as libcob.
**DIVIDE ... REMAINDER**: the remainder uses the quotient truncated to the quotient field's
decimals, even when the quotient is ROUNDED (DIVREM).

**Class tests** (ClassTests.cs): one fix over stock: libcob's NUMERIC test on COMP-1/COMP-2
is inverted (true only for NaN/infinity); here a finite float is NUMERIC.

## SORT and MERGE (2026-11-24)

runtime/Sort.cs follows libcob: an SD's records (from USING files or RELEASE) are held in
memory and come back in key order (GIVING files or RETURN). Keys compare in order, numeric
ones by value and the rest byte by byte or through the COLLATING SEQUENCE; equal keys keep
arrival order, so MERGE keeps the first USING file's records first. SORT-RETURN is 0, or
16 after an error, as libcob. GIVING to a LINE SEQUENTIAL file writes one record per line.
SORTS checks it against stock, including the five data files byte for byte.

In memory, not in work files: libcob spills to temporary files past its memory chunk; we
keep everything in memory, which suits the record counts these programs have.

Table SORT (`SORT table ON ... KEY`): stock uses C's qsort, which isn't stable, so the
order of elements with equal keys depends on the C runtime. Ours is stable (equal elements
keep their order). Everything else matches; SORTS uses distinct keys.

## DECLARATIVES (USE AFTER ERROR) (2026-11-24)

Worked in principle already (codegen PERFORMs the USE section after a failed I/O statement)
but never built: GnuCOBOL marks a USE section's own label with `flag_declarative_exit`
("this section ends with an implicit return"), and the C# translator had taken that flag
for a dummy exit label, so no method was started for the section. Now a USE section is a
method like any other. DECLS checks against stock: USE on a file and on an open mode
(INPUT, OUTPUT), routing of AT END / INVALID KEY / logic errors (a READ after end of file is
46 and goes to the USE procedure even with an AT END phrase, as stock), and a file without
FILE STATUS whose USE procedure keeps the program running. Not tested yet: USE GLOBAL
across nested programs, USE FOR DEBUGGING.

## Report Writer (2026-11-24)

runtime/ReportWriter.cs ports libcob's reportio.c step for step: INITIATE, GENERATE (detail
line or report name), TERMINATE, page heading / footing, PAGE LIMIT / HEADING / FIRST DETAIL
/ LAST DETAIL / FOOTING placement, LINE absolute and PLUS, NEXT GROUP, control breaks (the
highest changed level implies the lower ones; footings print the old control value), SUM
counters including rolled-forward sums of sums and FINAL, GROUP INDICATE, PRESENT WHEN,
COLUMN LEFT / CENTER / RIGHT (COB_COL_JUST_LRC), CODE IS, PAGE-COUNTER / LINE-COUNTER.

Representation: the C tables (cob_report, cob_report_line, _field, _control, _control_ref,
_sum, _sum_ctr) are classes CobReport, CobReportLine, ... whose constructors take the C
initializer's values in struct-member order. csharp.c's cs_decl turns
`static cob_report_field rf_40 = {NULL,&f_40,...};` into
`static CobReportField rf_40 = new CobReportField(null,f_40,...);`, so the pointer links are
object references. In C# mode codegen writes the cob_report initializer on one line and
leaves out the comments it puts inside initializers.

The report's MOVE / PRESENT WHEN code: C emits it once, inline at the first INITIATE /
GENERATE / TERMINATE, as a goto-subroutine (rwmove_N / rwfoot_N ... goto P_switch) that
later statements "perform". A paragraph is a method in C#, so a GENERATE in another
paragraph can't jump there. In C# mode typeck builds those statement trees once (in stock's
order, so they are the same trees) and emits them again inline at every GENERATE (both
parts) and TERMINATE (the footing part): the same statements run at the same moments.

Stock quirk kept: PRESENT WHEN on an elementary field never prints the field. cobc gives
the field COB_REPORT_PRESENT, and reportio.c prints such a field only when present_now is
set, which only PRESENT AFTER does; the WHEN code sets `suppress` instead. PRESENT WHEN on a
LINE works. REPORT2 shows both.

REPORT1 (page breaks, GROUP INDICATE, two control levels + FINAL, a SUM of SUMs, PAGE- /
LINE-COUNTER, GENERATE in another paragraph than INITIATE) and REPORT2 (report heading /
footing, control heading, NEXT GROUP PLUS and NEXT PAGE, PRESENT WHEN, a two-line detail
group, CENTER / RIGHT, two reports, summary reporting with GENERATE report-name) match
stock byte for byte, report files included.

Not supported: USE BEFORE REPORTING declaratives, and so SUPPRESS PRINTING, which only
appears in them. libcob returns to the generated code to run the declarative and is
re-entered with its number, resuming mid-loop; the C# translation stops with a CS-TODO
error. Untested: CODE IS, PAGE LIMIT etc. given by data items, OCCURS in report lines,
PRESENT AFTER / ABSENT AFTER, the runtime error paths (their messages lack stock's source
location).

## USE BEFORE REPORTING / SUPPRESS PRINTING (2026-11-24)

libcob's protocol: GENERATE / TERMINATE return the number of a USE BEFORE REPORTING section
when one is due; the generated code PERFORMs that section, re-runs the report's MOVEs, and
calls again with the number, and libcob resumes with a goto to the label just after the
point it returned from, often inside nested loops (PrintFooting, PrintHeading,
PrintFirstHeading, do_detail, PrintFootingFinal, PrintReportFooting).

In C#: typeck marks the start of the loop (`$B`) before the report MOVEs, and codegen writes
`for (;;) { moves; ctl = cob_report_generate (..., ctl); if (ctl == 0) break; switch (ctl)
{ case N: PERFORM section N } }`. The runtime can't jump into a loop, so cob_report_generate /
terminate find the control and line the number belongs to, skip ahead to it in the same
loops, and run the labelled code from there; whatever came before the label is skipped, as
with libcob's goto. SUPPRESS PRINTING now works (it only appears in these declaratives).
REPORT3 checks every resume point against stock, including stock's habit of running a
control-heading declarative again on a GENERATE with no control break.

**Fix over stock 3.2 (report declaratives):** a USE BEFORE REPORTING section for a DETAIL
line makes libcob return from two checks that have no resume point, so on the call back it
starts GENERATE over: the detail is added into the SUM counters twice (every total doubled),
control-heading declaratives run again, and the page heading can print twice. We resume
right after those checks instead. REPORT3's expected output is therefore checked by hand,
not taken from stock: totals 300.50 / 300.00 / 650.75 where stock prints 601.00 / 600.00 /
1,301.50.

## USE GLOBAL across nested programs (2026-11-24)

A nested program's I/O error goes to the outer program's USE GLOBAL section: the nested
program calls the outer program's internal function with the section's number. In C that
function jumps to the section and returns from it ("Implicit GLOBAL DECLARATIVE return").
In C# the paragraphs are methods, so the global entry dispatch PERFORMs the section through
the dispatcher and returns. One extra step: C keeps the PERFORM stack local to each call, but
in C# it is a class field that the internal function resets, so the dispatch remembers the
outer program's position and pushes the declarative on top of it; otherwise the outer
program's PERFORM in progress (the one that CALLed the nested program) was overwritten and
never returned. USEGLOB checks against stock: a GLOBAL file used by the outer and a nested
program, a nested program's own file by open mode, and the CALLs inside a PERFORMed
paragraph.

## PROCEDURE DIVISION RETURNING: not implemented [USER] (2026-11-24)

The user's call: don't implement it, since GnuCOBOL 3.2 doesn't either (cobc warns "program
RETURNING is not implemented"). SP COBOL behaves the same as stock. A called program passes a
result back through RETURN-CODE (CALL ... RETURNING receives it) or a BY REFERENCE argument.

## Watch window: COBOL names (2026-11-24)

- Each program class gets one static property per data item, named after the COBOL name in
  upper case with every non-alphanumeric as `_` (WS-PAY -> `WS_PAY`; a leading digit gets `_`).
  Typing `WS_PAY` in the Watch window shows the DISPLAY form of the value, and the Type column
  shows its PICTURE and USAGE (`PIC S9(7)V99 COMP-3`, `group`, `... OCCURS 3`). Groups expand
  to their items, tables to `(1)..(n)` and then the items of each occurrence.
  Runtime: `CobVar` / `CobData` in DebugView.cs; compiler: `cs_output_debug_view` in codegen.c.
- `COBOL` lists every 01/77 item; `COBOL["ws-pay"]` finds any item by its COBOL name,
  case-insensitively. `MONTH_ENTRY[2]` is occurrence 2 of a table.
- The properties read storage live (base + offset each time), so LINKAGE items show
  "(no data ...)" until the program is CALLed rather than stale values.
- An item inside a table shows its first occurrence; use the table item's `[n]` for others.
- Duplicate names (qualified with OF/IN in COBOL): only the first one gets a property; the
  others are still reachable by expanding their group.
- Left out: FILLER (elementary), 66/88/78 levels, special registers (flag_internal_register),
  COB-CRT-STATUS, EXTERNAL, LOCAL-STORAGE (its base isn't a class member), ANY LENGTH.
  The FD's synthetic "NAME Record" item (level 0) is skipped; its 01 records are shown.
- Why properties rather than a custom debugger engine/expression evaluator: works in any VS
  (and VS Code / Rider) with no extension, and the Watch window already evaluates C# members.

## #line was one COBOL line late (fixed 2026-11-24)

- csharp.c wrote `#line N` before codegen's `/* Line: N ... */` comment, so the comment was
  line N and the statement's code N+1: a breakpoint on a line stopped before the line ABOVE
  it had run, and stepping highlighted the line after the one executing. Found by the
  watch-window test. The directive now follows the comment.
- In-VS test: editor\vs-test\watch-test.ps1 (DTE only, no keystrokes) breaks on a line of
  samples\WATCH.cbl and checks eight Watch expressions.

## XML GENERATE and JSON GENERATE (2026-11-25)

- Runtime MlGenerate.cs is a port of libcob mlio.c. Stock GnuCOBOL passes the tree to
  libxml2 (xmlTextWriter) and cJSON; we write the same text ourselves, so no native library
  ships with SP COBOL. Copied from those libraries: libxml2's escaping (`&amp; &lt; &gt; &quot;`,
  `&#13;`; in attributes also `&#10; &#9;`), `<x/>` for an empty element, namespace
  declarations after the attributes, `<?xml version="1.0"?>` + newline; cJSON's unformatted
  print and string escapes (`\" \ \t \n \uXXXX`).
- Stock's own behaviour is kept, quirks included: only the first occurrence of an OCCURS item
  is generated (cobc warns "not implemented"); attribute values are the raw bytes
  (`BALANCE="001234u"` for -123.45); non-printable data becomes a `hex.NAME` element and
  XML-CODE 417; on overflow COUNT IN includes the trailing newline (213 vs 212).
  COMP-1/COMP-2 items stop the program, as in libcob ("TO-DO: Implement!").
- Reference: a second stock build, D:\vcobol-stock-ml (libxml2 + cJSON from MSYS2; GnuCOBOL 3.2
  needs `-include libxml/parser.h` with libxml2 2.14+). run-tests.ps1 now uses it for new
  expected outputs (tools/stock-cobc-ml.sh). MLGEN / MLGEN2 are byte-identical to it.
- DISPLAY now writes a line feed inside data as CR LF on Windows, as stock's C runtime does
  (stdout in text mode). Seen with the XML declaration's newline.
- cobc's "runtime is not configured to support XML/JSON" warnings are off in C# mode.

## XML PARSE: a compile error [follows the USER's RETURNING call] (2026-11-25)

- GnuCOBOL 3.2 doesn't implement XML PARSE: its runtime sends START-OF-DOCUMENT and then stops
  with "XML PARSE is not implemented". Same rule as PROCEDURE DIVISION RETURNING (user,
  2026-11-24: don't implement what GnuCOBOL doesn't), and a learner gets the message when
  compiling instead of from a program that half runs: "XML PARSE is not implemented
  (GnuCOBOL 3.2 doesn't implement it either)".

## The 27 problems found by the language reference examples (2026-11-25)

Writing and running the reference site's examples turned up 27 places where SP COBOL
differed from GnuCOBOL 3.2. Each now has a suite test (samples\NAME.cbl); 23 match stock
byte for byte, 2 are screen tests checked against libcob's screenio.c, and 3 are deliberate.

Fixed to match GnuCOBOL 3.2:
- Conditions: arithmetic in a condition (C's comma list -> `cob_seq (() => {...})` lambda,
  CONDEXPR); constant conditions (`if (1)` -> `true`, LITCOND, EVALANY); figurative
  constants and ALL literals repeat across the other operand (cob_cmp_all, FIGCMP).
- MULTIPLY ... BY / DIVIDE ... INTO without GIVING (cob_mul / cob_div, MULDIV);
  ROUNDED MODE IS (all eight modes, libcob cob_decimal_do_round incl. its NEAREST-EVEN
  check and PROHIBITED, RNDMODE); MOVE to several targets from a computed source
  (cob_put/get_indirect_field, MOVEMULT); STOP "literal" (waits for Enter, STOPLIT);
  STRING WITH POINTER starting outside the target raises ON OVERFLOW (STRPTR).
- Data: alphanumeric-edited moves (X / B 0, ALNUMED); P scaling in store, ADD and DISPLAY
  (PSCALE); DISPLAY of SIGN TRAILING SEPARATE puts the sign last (SIGNSEP); DECIMAL-POINT IS
  COMMA and CURRENCY SIGN in edited pictures (DPCOMMA, CURRSIGN); user CLASS tests (USERCLAS);
  ALPHABET as PROGRAM COLLATING SEQUENCE (COLLSEQ) and on SORT (SORTCOLL); EXTERNAL items
  (one area per name, EXTERNL); USAGE POINTER (`*(unsigned char **)p` -> `p.Ptr`, an id in a
  registry of addresses; NULL -> CobPtr.Null, POINTERS); SPECIAL-NAMES switches with
  COB_SWITCH_n (SWITCHES).
- Program structure: LOCAL-STORAGE (LOCALST); RECURSIVE programs (RECURSE): C keeps the
  module, frame stack, parameter list and decimals in function locals, our C# in class
  fields, so a recursive program snapshots them (and the entry wrapper the LINKAGE bases)
  and restores them at exit; ALTER (the altered GO TO returns the paragraph id to the
  dispatcher, ALTERGO); USE FOR DEBUGGING with COB_SET_DEBUG=Y (DEBUGDCL, checked both ways).
- Files and reports: RELATIVE READ NEXT with a record number too big for the key: status 14,
  record still read, key set to 0, position kept (RELKEY14); a report ASSIGN TO DISPLAY
  pads its last page (stdout honours BEFORE/AFTER ADVANCING, RPTDISP); RD CODE IS builds and,
  as in GnuCOBOL 3.2, writes an empty report (RPTCODE).
- Screens: a numeric field with an implied point (PIC 9(3)V99) keeps what is typed
  (SCRNUMV); SPECIAL-NAMES CURSOR IS starts the ACCEPT on the field under it and gets the
  position back (CURSORIS; we return the field's start, curses the exact column).

Deliberately different (hand-checked expected output):
- RELPREV: after READ NEXT gave record 7, GnuCOBOL's READ PREVIOUS skips record 5; and after
  a random WRITE its READ NEXT is at end of file. The standard says WRITE doesn't move the
  file position, and PREVIOUS is the record before: we give 5, then 2, and read from the start.
- RELWRT24: a sequential WRITE whose record number doesn't fit the RELATIVE KEY: GnuCOBOL
  writes the record, says 00 and leaves the key at 0; we say 24 (the standard's code for
  it) and write nothing.
- BOUNDS: a subscript past the end of the table. GnuCOBOL without -debug reads or writes the
  bytes after the item (padding, or the next item); our items are separate byte arrays, so
  we stop with one message and a hint, as GnuCOBOL does with -debug. Uncaught runtime
  errors now print `libcob: error:` + hint instead of a .NET stack trace (exit code 1);
  under the debugger Visual Studio still stops on the statement first.
- (later the same day) ALPHABET x IS EBCDIC / NATIVE: the built-in translation tables were
  multi-line C arrays too; now one C# line each, like a listed alphabet (EBCDCOLL, found by
  the docs session).
