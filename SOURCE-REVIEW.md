# Visual COBOL for .NET: source review

Reviewed 2026-11-21. Goal: COBOL source edited in Visual Studio 2022, compiled to .NET IL,
with WinForms for the UI.

Downloads (untouched originals):

- `src-download/gnucobol-3.2/`: GnuCOBOL 3.2 release tarball (Jul 2023, latest stable)
- `otterkit-download/otterkit/`: current Otterkit repo (now only a native decNumber shim)
- `otterkit-download/otterkit-cobol/`: the actual Otterkit compiler, **archived Feb 2024**

---

## Verdict

**Build on GnuCOBOL's front end and write a new back end + a managed runtime.**
Otterkit is a source of ideas and a few Apache-2.0 pieces, not a base.

| | GnuCOBOL 3.2 | Otterkit (archived) |
|---|---|---|
| Language | C (compiler ~150K lines, runtime ~50K) | C# (~27K lines) |
| Front end | Mature: 10+ dialects, ~1,350 tests + NIST COBOL-85 harness | Parses COBOL 2023 syntax, no dialects |
| Code generator | Complete, emits C | Only DISPLAY, CALL, ACCEPT, STOP, IF |
| Runtime | Complete (files, sort, screen, report writer, XML/JSON) | Tiny; numerics via native decNumber P/Invoke |
| License | Compiler GPLv3, runtime LGPLv3 | Apache-2.0 |
| Status | Active project | Dead since Feb 2024 |

---

## GnuCOBOL: what we keep

### 1. The whole front end (`cobc/`), as is

| File | Lines | Job |
|---|---|---|
| `pplex.l`, `ppparse.y`, `replace.c` | ~5.5K | COPY / REPLACE / compiler directives, fixed/free format |
| `scanner.l`, `reserved.c` | ~8K | Tokenizer + per-dialect reserved words |
| `parser.y` | 20.5K | Full grammar (bison) |
| `typeck.c` | 15.3K | Semantic checks, lowers statements into runtime calls |
| `tree.c`, `tree.h`, `field.c` | ~14K | Syntax tree, data layout (offsets, sizes, REDEFINES, OCCURS) |
| `config.c`, `error.c`, `help.c` | ~2.5K | Options, diagnostics |
| `../config/*.conf`, `*.words` | n/a | Dialect definitions: IBM, MF, ACU, RM, Realia, COBOL-85/2002/2014, etc. |

`field.c` computing every field's byte offset and size is especially valuable. That layout
is exactly what a .NET runtime needs too, and it's the part that's hardest to get right.

### 2. The "runtime call" contract (big shortcut)

`typeck.c` does not leave statements abstract. It lowers them into calls by name:
`cob_move`, `cob_add`, `cob_inspect_*`, `cob_string_*`, `cob_unstring_*`, `cob_open`,
`cob_read`, `cob_write`, `cob_file_sort_*`, `cob_accept_*`, `cob_display`, and so on (~150 distinct
names). The C back end just prints them.

**So a managed runtime with the same function names and argument shapes plugs straight in.**
The new back end prints `Cob.Move(f1, f2)` instead of `cob_move (&f1, &f2)`, and libcob's C
sources become the line-by-line spec for each C# method.

### 3. Codegen structure (`codegen.c`, 14K lines): kept as a template, not as code

- 163 `output_*` functions, one per node type. The new emitter mirrors this list.
- **PERFORM already has a no-computed-goto mode** (`cb_flag_computed_goto` off). It records a
  return number and dispatches through a `switch`. That maps directly onto C# `goto` + `switch`.
  We force that mode and never emit GCC's `&&label` / `goto *ptr`.
- Paragraph labels come out flat at function level, so C# `goto` rules
  (no jumping into a nested block) are satisfied.

### 4. Test suites

- `tests/testsuite.src/*.at`: ~1,350 autotest cases. Each one is a COBOL program plus expected
  output, so they can be reused as-is against our compiler with a small C#/PowerShell harness.
- `tests/cobol85/`: harness for the NIST COBOL-85 suite (NC, SM, IC, SQ, RL, IX, ST, SG, OB, IF,
  RW, DB modules). The NIST source `newcob.val` is fetched separately. This is the scorecard.

### 5. libcob as a reference spec

`move.c` (MOVE rules between every pair of USAGEs), `numeric.c` (decimal arithmetic, rounding,
SIZE ERROR), `strings.c` (INSPECT/STRING/UNSTRING), `intrinsic.c` (~100 FUNCTIONs),
`reportio.c`. We port logic from these, method by method.

---

## GnuCOBOL: what we drop or replace

### Compiler side

| Item | Where | Replacement |
|---|---|---|
| C code generator | `codegen.c` | New `codegen_cs.c`, same tree walk, emits C# with `#line` directives |
| C-only optimizations | `codeoptim.c`, `codeoptim.def` | Drop; inline helpers go into the runtime |
| Inline C snippets | 9 `cb_build_direct()` calls in `typeck.c` / `parser.y` (`"break;"`, `COB_SPACES_ALPHABETIC`, packed-literal hex) | Small patch: emit C# equivalents |
| `memset` / `memcmp` / `memcpy` funcalls emitted by typeck | `typeck.c` | Runtime helpers `Cob.Fill`, `Cob.Compare`, `Cob.Copy` (Span-based) |
| GCC computed goto | `codegen.c` | Always use the switch fallback |
| Compile driver: C compiler/linker steps | `cobc.c` `process_compile`, `process_assemble`, `process_module*`, `process_link` | Hand the `.cs` to MSBuild/Roslyn. Keep `process_filename`, `preprocess`, `process_translate` |
| Dynamic CALL via `cob_resolve` / `dlopen` | `codegen.c` ~6943, `libcob/call.c` | Program registry: CALL "NAME" maps to a .NET type, found by attribute/reflection, cached |

### Runtime side (libcob), with its external C libraries

| libcob piece | C dependency | .NET replacement |
|---|---|---|
| Decimal math, `numeric.c`, `intrinsic.c` (~400 `mpz_` calls) | GMP / MPIR | `System.Decimal` (28 digits, fast path) + `BigInteger` with scale for 31–38 digit ARITHMETIC |
| Indexed files, `fileio.c` | Berkeley DB / VBISAM / D-ISAM | Our own ISAM. Options: B-tree file of our own, or SQLite under the hood. **Biggest single runtime job.** |
| Sequential, relative, line-sequential files | none | `FileStream`, direct port |
| Screen section, `screenio.c` | ncurses / PDCurses | **First-class console mode** on `System.Console` (cursor positioning, 16 colors, `ReadKey` for ACCEPT), for the DOS-era look. WinForms programs won't use SCREEN SECTION. |
| XML/JSON GENERATE, PARSE, `mlio.c` | libxml2, cJSON / json-c | `System.Xml`, `System.Text.Json` |
| CALL loading, `call.c` | dlfcn | Reflection + registry (above) |
| EXTFH, CGI hooks | n/a | Drop for v1 |
| Threading helpers (`cob_threadstart` etc.) | n/a | Drop for v1 |

### Key runtime design decision (to settle before coding)

GnuCOBOL's whole data model is `cob_field { size, data pointer, attr }` over raw storage blocks.
REDEFINES, reference modification, `SET ADDRESS OF`, and LINKAGE items all depend on
"a pointer into a byte block". Two ways to carry that into .NET:

1. **Unsafe native memory**: storage from `NativeMemory.Alloc`, fields hold `byte*`. Porting
   libcob is close to mechanical, and POINTER and ADDRESS OF work naturally. The IL is
   unverifiable, which is fine for desktop apps.
2. **Managed `byte[]` + offset**: fields hold (array, offset, size). It's fully safe and GC-friendly, and
   debugger watch windows are nicer. `SET ADDRESS OF` becomes a re-target of (array, offset), and
   raw USAGE POINTER arithmetic gets awkward.

**Decided 2026-11-21 (user): option 2, managed `byte[]` + offset.** USAGE POINTER gets a narrow
unsafe escape hatch only if a real program needs it. See `DECISIONS.md`.

---

## Otterkit: what's worth taking

All Apache-2.0, so it can be copied with attribution.

- **Project templates** (`Libraries/Otterkit.Templates`): `dotnet new` templates for a COBOL
  Application and Library. A good starting shape for our `.cobproj` templates.
- **Build approach** (`src/Otterkit.cs` ~318–365): writes generated C# into a hidden
  `.otterkit/Artifacts/OtterkitExport.csproj` and runs `dotnet publish`. That confirms the
  emit-C# route works. Ours would be an MSBuild target instead, so F5 in Visual Studio works.
- **VS Code extension repo** (`otterkit/otterkit-vscode`, not downloaded): a TextMate grammar we
  could adapt for the VS 2022 VSIX syntax coloring.
- **Unicode case-fold table + exception index generators** (`src/Tools/`): small utilities.

## Otterkit: what to ignore

- **Code generator**: 5 statements, with no MOVE, COMPUTE, PERFORM, or files.
- **Numeric storage**: keeps a literal `+`/`-` byte in front of numbers instead of the standard
  trailing overpunch or separate sign. It's not compatible with real COBOL data files or REDEFINES.
- **Decimal math**: P/Invokes a native decNumber C library, which defeats "pure IL".
- **Analyzer**: COBOL 2023 only. GnuCOBOL's front end is far more complete and battle-tested.

---

## Licensing reminder

- Our modified `cobc` must stay **GPLv3** if distributed. Programs it compiles are unaffected.
- A C# runtime **ported from libcob** is a derivative of LGPLv3 code. It can ship as a separate
  DLL linked by any program, but its own source stays LGPL. If you want the runtime under your
  own license, write it from the COBOL standard rather than translating libcob line by line.
- Otterkit pieces: Apache-2.0, keep the NOTICE.

---

## Suggested first spike

1. Build stock GnuCOBOL 3.2 on Windows (MSYS2 or the MSVC build files) to prove the front end
   builds.
2. Add `-fcsharp` (or similar) so `process_translate` calls a new `codegen_cs.c`.
3. Cover: WORKING-STORAGE layout, MOVE, ADD/COMPUTE, DISPLAY, IF, PERFORM paragraph, STOP RUN.
4. Write a minimal `GnuCobol.Runtime` C# library with `Move`, `Add`, `Display` and the field model.
5. Hello-world plus a payroll-style arithmetic test, stepped through in VS with `#line`.
