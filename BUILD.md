# Build notes

## Toolchain (installed 2026-11-21)

- **MSYS2** at `C:\msys64` (winget `MSYS2.MSYS2`), UCRT64 environment.
  Packages: `make diffutils mingw-w64-ucrt-x86_64-gcc mingw-w64-ucrt-x86_64-gmp`. gcc 16.2.0.
- .NET SDKs 9.0 and 10.0; Visual Studio 2022 Professional (already present).

## Stock GnuCOBOL 3.2 (reference build, unmodified)

Built in `D:\vcobol-build\gnucobol-3.2`, installed to `D:\vcobol-stock`.
These are **outside** the project folder on purpose: libtool's generated wrapper code breaks on
the space in `D:\CALUDE CODE`.

```bash
# MSYS2 UCRT64 shell
cd /d/vcobol-build
tar -xf "/d/CALUDE CODE/visual-cobol/src-download/gnucobol-3.2.tar.xz"
cd gnucobol-3.2
./configure --prefix=/d/vcobol-stock --without-db --with-curses=no \
            --without-xml2 --with-json=no CFLAGS='-O2 -std=gnu17'
make -j8 && make install
```

Gotchas hit:

1. **gcc 15+ defaults to C23**, which rejects GnuCOBOL 3.2's old-style function pointers
   (`common.c:766`, `call.c:184`). Fix: `CFLAGS='-std=gnu17'`.
2. **Spaces in the build path** break libtool wrappers (`lt-cobcrun.c: stray '\'`). Build in
   `D:\vcobol-build`.
3. **MSYS paths baked into a native exe.** `cobc` looks for `/d/vcobol-stock/share/...`, which
   Windows can't open. Override with the env vars in `tools/stock-cobc.sh`.

Disabled on purpose: BDB/ISAM, curses, libxml2, JSON. Our .NET runtime replaces all four.

## Our compiler (cobc with `-fcsharp`)

Source of truth: `compiler/cobc/` (full modified files + new `csharp.c`), diff vs. 3.2 in
`compiler/gnucobol-3.2-csharp.patch`. Build tree `D:\vcobol-build\gnucobol-cs`, installed
`cobc.exe` in `D:\vcobol-cs\bin`.

```bash
bash tools/build-cobc-cs.sh              # copy overlay into build tree, make, install cobc.exe
bash tools/cobnet.sh samples/X.cbl OUT   # X.cbl -> OUT/X.cs
bash tools/save-compiler-overlay.sh      # after editing in the build tree: copy back + refresh patch
```

**Regression suite** (run after any compiler or runtime change). Each program runs in its own empty folder under stock GnuCOBOL and ours; the screen output, the exit code, and every data file the program leaves behind must match (stock's files are kept in `samplesexpectedNAME`):

```
powershell -ExecutionPolicy Bypass -File tools\run-tests.ps1          # all samples
powershell -ExecutionPolicy Bypass -File tools\run-tests.ps1 PERFORMS # one
```

Expected outputs come from stock GnuCOBOL and are cached in `samples/expected/`.
`NAME.in` beside a sample is fed to it as stdin. `NAME.keys` makes it a **screen test**: it runs
with `COB_SCREEN=script` (keystrokes from the file, screens printed as text), its expected output
is our own reviewed first run, and its Windows (Forms) build must compile too.

Runtime projects: `runtime/CobolDotNet.Runtime` (net9.0, console + screen engine) and
`runtime/CobolDotNet.Forms` (net9.0-windows, the Forms renderer + `FormsHost`). Choose a renderer
at run time with `COB_SCREEN=console|forms|script`.

Gotcha: stock `cobc` rejects SCREEN SECTION ("runtime is not configured to support") because it
was built without curses. Ours skips that check in `-fcsharp` mode (`typeck.c`).

Then `dotnet build` / `dotnet run` in a project that references
`runtime/CobolDotNet.Runtime` (see `spike/Hello/Hello.csproj`). Run `dotnet` from PowerShell,
because it isn't on the MSYS2 PATH.

Gotcha: changing `flag.def` changes help text, and make then wants `help2man`/`makeinfo` to
rebuild `cobc.1` and `gnucobol.info`. The build script touches those files to skip that.

## Smoke test

`samples/HELLO.cbl` checks COMP-3, ROUNDED, a check-protected edit picture, EVALUATE, inline
PERFORM, STRING and ref-mod. Verified 2026-11-21 with output `Tax ... $*****94.44`.
`cobc -C` translates it to 297 lines of C calling `cob_decimal_*` and `cob_display`. That's the
shape our C# back end replaces.

## Visual Studio SDK and template (spike 4)

```
bash tools/build-cobc-cs.sh                                   # if the compiler changed
powershell -ExecutionPolicy Bypass -File tools\package-sdk.ps1 # -> dist\feed\CobolDotNet.Sdk.<ver>.nupkg
powershell -ExecutionPolicy Bypass -File tools\package-templates.ps1 # -> dist\SpCobol.Templates.<ver>.nupkg
dotnet new install "D:\CALUDE CODE\visual-cobol\templates\cobol-screen-app"   # once per PC
dotnet new cobol-screen -n MyApp [--ScreenMode Both|Console|Forms]
cd MyApp && dotnet build     # or open MyApp.csproj in VS 2022
```

`package-sdk.ps1` stages `sdk\CobolDotNet.Sdk\stage\` (runtime DLLs, cobc.exe + libcob-4,
libgmp-10, libintl-8, libiconv-2, libwinpthread-1, config, copy, license texts), packs it, and
deletes `%USERPROFILE%\.nuget\packages\coboldotnet.sdk\<ver>` so the next restore sees the new
build. The template's `nuget.config` points at `dist\feed`.

Project properties: `ScreenMode` (Both), `CobolSourceFormat` (fixed|free), `CobolMainProgram`
(file name of the main .cbl), `CobolCompiler` (override cobc.exe).

## Sequence numbers

Fixed-format sources are numbered 000010, 000020, ... in columns 1-6 (decision of 2026-11-22):

```
powershell -ExecutionPolicy Bypass -File tools\renumber.ps1 samples\NEW.cbl [more files]
```

## COBOL editor extension (VSIX)

```
powershell -ExecutionPolicy Bypass -File tools\build-editor.ps1           # tests + dist\CobolDotNet.Editor.vsix
powershell -ExecutionPolicy Bypass -File tools\build-editor.ps1 -Deploy   # also into the VS 2022 experimental instance
powershell -ExecutionPolicy Bypass -File editor\vs-test\run-vs-test.ps1   # 22 checks inside VS (opens a VS window)
powershell -ExecutionPolicy Bypass -File tools\make-editor-words.ps1      # after the compiler's word list changes
```

Install for real by double-clicking `dist\CobolDotNet.Editor.vsix`. Details in EDITOR.md.
