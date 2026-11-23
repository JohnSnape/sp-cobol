# Spike 1: COBOL → C# → .NET IL (2026-11-21)

**Result: works.** Two test programs compile through our modified `cobc` to C#, build to IL
with `dotnet build` (0 warnings, 0 errors), and produce output **byte-identical** to stock
GnuCOBOL 3.2, CRLF line endings included.

| Program | Covers | Result |
|---|---|---|
| `samples/HELLO.cbl` | COMP-3, COMPUTE ROUNDED, `$***,**9.99`, EVALUATE, inline PERFORM VARYING, STRING, ref-mod | identical |
| `samples/MATHTEST.cbl` | signed packed / zoned / binary, division ROUNDED vs truncated, CR, DB, floating `$`, `-ZZZ9`, `+9999`, `ZZZZ.ZZ` zero, `99/99/99`, ON SIZE ERROR / NOT ON SIZE ERROR, ADD truncation, numeric→alnum, text→numeric, JUSTIFIED RIGHT, IF numeric and alphanumeric | identical (after one fix: CR/DB symbol) |
| `samples/PERFTEST.cbl` | PERFORM paragraph from inside IF | **fails as predicted**, see below |

## Pipeline

```
PROG.cbl --(cobc -C -x -fcsharp)--> PROG.cs --(dotnet build)--> PROG.dll (IL)
                                       |
                          references runtime/CobolDotNet.Runtime
```

- `tools/build-cobc-cs.sh` builds the modified `cobc` from `compiler/cobc/` into `D:\vcobol-cs`.
- `tools/cobnet.sh PROG.cbl OUTDIR` writes `OUTDIR/PROG.cs`.
- `spike/Hello`, `spike/MathTest`: .NET projects that compile the generated file.

## What was built

**Compiler** (`compiler/cobc/`, diff in `compiler/gnucobol-3.2-csharp.patch`, ~300 lines):

- New flag `-fcsharp`.
- `csharp.c` (new, #included by codegen.c): all codegen output goes through a line filter.
  Declarations become typed C# statics. Statement lines get a token rewrite (`->`→`.`,
  `&x`→`x` or `ref x`, casts, `3LL`→`3L`, `NULL`→`null`, octal escapes→`\u00XX`).
  `#define` literals become `const`. Anything unmapped becomes `#error CS-TODO`.
- `codegen.c`: output routines go through the filter. Prototypes and the C version check are
  skipped. There's a C# `Main`. Computed goto is forced off. Three `if (int)` tests are
  rewritten as explicit comparisons, which is valid C as well.
- `cobc.c`: merges `.c.h` + `.c.l.h` + `.c` into one `.cs`, in that order, so static
  initializers run in dependency order.

**Runtime** (`runtime/CobolDotNet.Runtime`, .NET 9, about 1,000 lines):

- `CobPtr` = (byte[], offset), the managed "pointer" (decision: managed byte arrays).
  `CobField` = (size, CobPtr, attr).
- All numeric work goes through one exact form, `(BigInteger value, int scale)`: decode any
  USAGE, compute, encode. Rounding is half away from zero. Size errors use libcob's exception
  codes, because the generated code tests them directly.
- The edited-picture formatter is written from the COBOL rules.
- **The runtime is written fresh, not translated from libcob**, so it isn't an LGPL derivative.
  Only the *names* (`cob_move`, `cob_display` ...) mirror libcob's API.

## Findings

1. **PERFORM from inside a block cannot work with the current approach.** GnuCOBOL's
   no-computed-goto PERFORM returns through a `switch` that jumps back *into* the block holding
   the PERFORM (`l_7:` inside the IF). C allows that; C# doesn't
   (`CS0159: No such label 'l_7' within the scope of the goto`).
   **→ Spike 2: a structural emitter where each paragraph/section is a C# method,**
   PERFORM is a call, PERFORM THRU is a range loop, and GO TO returns a "next paragraph"
   code to a dispatcher loop. This is the standard design for COBOL→JVM/.NET translators,
   and it also makes the VS call stack show paragraph names (good for learners).
2. The line filter was the right *spike* tool: it got two non-trivial programs running with
   little code. It's not the long-term emitter. Expressions and declarations can stay
   filter-based for a while; procedure structure can't.
3. GnuCOBOL's diagnostics are already beginner-friendly (`duplicate JUSTIFIED clause`,
   `invalid MOVE statement` with source context), and we get them for free.
4. Known small gaps noted for later: C string literals emitted with `llit` (unescaped
   backslashes); RETURN-CODE as a field (`&b_2`); recursion (program-local statics); nested
   programs; files, ACCEPT, SCREEN SECTION, CALL.
