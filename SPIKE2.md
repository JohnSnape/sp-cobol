# Spike 2: paragraphs as C# methods (2026-11-21)

**Result: works.** PERFORM now works anywhere: inside IF, EVALUATE and loops; with
TIMES / UNTIL / VARYING / AFTER; THRU ranges; nested; SECTIONs; GO TO; GO TO DEPENDING ON;
EXIT PARAGRAPH / SECTION / PERFORM [CYCLE]; NEXT SENTENCE; GOBACK from deep inside nested
PERFORMs. The regression suite runs 5 programs, and output plus exit code are
**byte-identical to stock GnuCOBOL 3.2**, built with C# warnings treated as errors.

```
powershell -File tools\run-tests.ps1
PASS  HELLO
PASS  MATHTEST
PASS  PERFORMS      <- 12 kinds of control flow
PASS  PERFORMS2     <- the edge cases
PASS  PERFTEST      <- spike 1's failure (PERFORM inside IF)
5 passed, 0 failed
```

## How it maps

| COBOL | Generated C# |
|---|---|
| paragraph / SECTION / ENTRY | `static int NAME ()`, named after the COBOL label (`100-MAIN` → `_100_MAIN`) |
| fall-through into the next paragraph | `return <next id>;` |
| `PERFORM A THRU B` | `frame_ptr++; perform_through = B; if (PROG__run (A) != 0) return -3; frame_ptr--;` |
| end of B (implicit PERFORM return) | `if (frame_ptr.perform_through == B) return -2;` |
| `GO TO X` | `return X;` (the dispatcher continues at X) |
| `GOBACK` / `EXIT PROGRAM` | `return -3;` (passes up through every active PERFORM) |
| `EXIT SECTION` | `return <section-exit id>;` (that label becomes its own small method) |
| EXIT PARAGRAPH, EXIT PERFORM, EVALUATE ends, NEXT SENTENCE | ordinary `goto` inside the method |
| `PROG__run (id)` | loop: call the method for id, continue at what it returns, stop on -2 / -3 |

Example: generated from PERFORMS.cbl:

```csharp
/* PARAGRAPH OUTER-PARA */
static int OUTER_PARA ()
{
  cob_add_int (f_19, 1, 0);
  cob_display (0, 1, 2, c_19, f_19);
  /* PERFORM INNER-PARA */
  frame_ptr++;
  frame_ptr.perform_through = 13;
  if (PERFORMS__run (13) != 0) return -3;
  frame_ptr--;
  cob_display (0, 1, 1, c_20);
  /* Implicit PERFORM return */
  if (frame_ptr.perform_through == 12)
    return -2;	/* end of PERFORM range */
  return 13;
}
```

**For learners:** a runtime error's stack trace now reads like the program:

```
at PERFORMS_Program.START_HERE()
at PERFORMS_Program.PERFORMS__run(Int32 p)
at PERFORMS_Program.PERFORMS_(Int32 entry)
```

## Changes

- `csharp.c`: body diversion to a side file, one method per label, dispatcher generation,
  method naming (unique, digit-leading names get `_`), unary `*p` → `p[0]`, raw lines allowed
  after indentation.
- `codegen.c`: hooks in `output_label`, `output_perform_call`, `output_perform_exit`,
  `output_goto_1`, `output_goto_entry`, GOBACK/EXIT PROGRAM, entry dispatch, PROCEDURE DIVISION
  loop. GnuCOBOL's inline C optimizer helpers are skipped (the runtime has them).
- Runtime: `cob_get_numdisp`.
- `tools/run-tests.ps1`: regression suite. Stock outputs are cached in `samples/expected/`.

## Known gaps (next)

- **Semantics differ from GnuCOBOL in one corner:** GnuCOBOL checks only the *top* PERFORM
  frame at a paragraph end. With overlapping PERFORM ranges entered by GO TO, the two can
  disagree. That's legal but bad COBOL; the plan is to match it later if a real program needs it.
- DECLARATIVES / USE procedures, ALTER, PERFORM via DEBUGGING sections: not tried (their perform
  calls are emitted outside paragraph methods).
- RECURSIVE programs: working storage is class-static. Fine for ordinary programs.
- Still missing in the runtime: files, ACCEPT, SCREEN SECTION, CALL, INSPECT, UNSTRING,
  intrinsic FUNCTIONs, SORT.
