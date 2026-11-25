# Language reference sources

The GitHub Pages site in `docs/` is generated from this folder. Don't edit `docs/` by hand.

```
powershell -ExecutionPolicy Bypass -File tools\check-doc-examples.ps1   # compile + run every example
powershell -ExecutionPolicy Bypass -File tools\build-docs.ps1           # write docs\
```

`check-doc-examples.ps1` needs the same tools as the regression suite (MSYS2 UCRT64, our
`cobc` in `D:\vcobol-cs`, the stock reference build in `D:\vcobol-stock-ml`; see BUILD.md).
`build-docs.ps1` needs only Windows PowerShell. Commit `docs/` after building, since GitHub
Pages serves it as it is.

## Layout

| Path | What it is |
|---|---|
| `nav.txt` | Page order: `# Section` lines, then page names. The sidebar, the overview's page list, and previous/next links all follow it. |
| `pages/NAME.html` | One page (published as `docs/NAME.html`). |
| `examples/X.cbl` | An example program. Insert it into a page with `<!-- example X -->`. |
| `examples/X.in`, `X.keys`, `X.sub/`, `X.expect` | Standard input, screen keystrokes, called programs, exceptions (see the header of `tools/check-doc-examples.ps1`). |
| `examples/copy/*.cpy` | Copybooks, available to every example. |
| `examples/output/X.out` | What SP COBOL printed for X, written by `check-doc-examples.ps1`. Shown under the example. |
| `assets/` | CSS and JavaScript, copied to `docs/assets/`. |

## A page

```
title: ADD
summary: Add numbers together and store the sum.
keywords: TO GIVING ROUNDED SIZE ERROR END-ADD
badge: differs
---
<h2>Syntax</h2>
<pre class="syntax">...</pre>
...
```

- `title` is the page heading. `summary` is the line under it, the overview card, and the
  search result text: one sentence. `keywords` are extra search words (phrases, related
  words a learner might type). `badge` is optional: `not-supported`, `differs` (SP COBOL
  deliberately behaves differently from GnuCOBOL 3.2) or `obsolete`.
- The body is HTML. Escape `&`, `<` and `>` as `&amp;`, `&lt;`, `&gt;`. The page title is
  added for you; start with `<h2>`.
- Usual order of sections: **Syntax**, **What it does** (with `<h3>` subsections), **Example**
  (one or more), **SP COBOL notes** (only when there is something to say), **See also**.
- Callouts: `<div class="note">`, `<div class="warn">` (a difference from GnuCOBOL, or a trap
  for learners), `<div class="bad">` (not supported). Wide tables go in
  `<div class="table-wrap">`.
- Links to other pages use their names: `<a href="move.html">MOVE</a>`.

### Syntax notation

Inside `<pre class="syntax">`:

- UPPERCASE words are COBOL words. Lowercase words (`identifier-1`, `literal-2`) are the parts
  you supply; the build shows them in italics.
- `[ ]` optional, `{ a | b }` choose one, `...` may repeat.
- A line starting with `Format` is a label for one form of the statement.

## An example

- Fixed format. Sequence numbers in columns 1-6, starting at `000010` and going up by 10
  (`tools\renumber.ps1 FILE` fixes them). Column 7 is the indicator (`*` for a comment line).
  Division, section and paragraph names and level 01/77 start in column 8; statements in
  column 12. Nothing past column 72.
- The file name is the PROGRAM-ID, in upper case letters and digits only (`ADD1.cbl` has
  `PROGRAM-ID. ADD1.`). Names must be unique across all examples.
- Short: show the feature, not a whole application. DISPLAY enough that the output explains
  itself.
- Output must be the same on every run: no clock times, dates or unseeded random numbers.
- Every example is run under SP COBOL and under stock GnuCOBOL 3.2. Output, exit code and
  data files must match, unless `X.expect` says otherwise.
