# SP COBOL

COBOL for .NET, made for people **learning COBOL**. Write standard COBOL (fixed format, with
SCREEN SECTION screens) in Visual Studio. Build it into an ordinary .NET program that runs as a
DOS-style text screen, as a Windows window, or both from the same source.

> **Preview (0.1).** Results are checked against stock GnuCOBOL 3.2 (screen output and
> data files, byte for byte) by a regression suite. Working:
> - files: sequential, line sequential, relative, indexed; DECLARATIVES (USE AFTER ERROR,
>   incl. USE GLOBAL for nested programs)
> - CALL / CANCEL, nested programs, several .cbl files in one project
> - INSPECT, UNSTRING, STRING; SORT and MERGE; Report Writer (incl. USE BEFORE REPORTING)
> - the intrinsic functions (UPPER-CASE, NUMVAL, CURRENT-DATE, SQRT, ...); COMP-1/COMP-2
> - XML GENERATE and JSON GENERATE (no extra libraries needed)
> - SCREEN SECTION as a DOS-style console screen or a Windows window
>
> Not supported, as in GnuCOBOL 3.2: PROCEDURE DIVISION RETURNING (pass results back through
> RETURN-CODE or a BY REFERENCE argument) and XML PARSE.

## What's here

| Folder | What it is |
|---|---|
| `compiler/` | GnuCOBOL 3.2's `cobc` with a new C# back end (`-fcsharp`). GPLv3. |
| `runtime/` | The .NET runtime the generated C# calls: data items, MOVE/DISPLAY/ACCEPT, SCREEN SECTION for console and Windows Forms. |
| `sdk/` | `CobolDotNet.Sdk`, an MSBuild SDK (NuGet package) that compiles `.cbl` files as part of a normal .NET build, with beginner-friendly error messages. |
| `templates/` | The "COBOL Screen App" project template (`dotnet new cobol-screen`). |
| `editor/` | A Visual Studio 2022/2026 extension: COBOL syntax colors, column guides, and sequence numbers (000010, 000020, ...) kept automatically as you insert and delete lines. |
| `samples/` | Test programs, with the output they must produce. |
| `tools/` | Build, packaging and test scripts. |

## Trying it

You need Windows and the [.NET 9 SDK](https://dot.net). Visual Studio 2022 or 2026 is
optional, and gets you the COBOL editor. Close Visual Studio, then run this in PowerShell:

```
powershell -ExecutionPolicy Bypass -c "irm https://raw.githubusercontent.com/JohnSnape/sp-cobol/main/tools/install.ps1 | iex"
```

The installer ([tools/install.ps1](tools/install.ps1)) downloads the latest release and:

- puts the compiler package in `%LOCALAPPDATA%\SP COBOL\feed`, the folder every new COBOL
  project reads;
- installs the project template (`dotnet new cobol-screen`; in Visual Studio it's
  "COBOL Screen App" under New Project);
- starts the installer for the Visual Studio editor extension, if Visual Studio is present.
  See [EDITOR.md](EDITOR.md) for what each key does.

Then make and run a program:

```
dotnet new cobol-screen -n PayDemo
cd PayDemo
dotnet build
```

That builds `PayDemo.exe` (DOS-style text screen) and `PayDemo-Win.exe` (the same screen in a
window). If you'd rather not run a script, download the files from the
[Releases](../../releases) page and run `install.ps1 -From <that folder>`.

## Debugging

Press F5 in Visual Studio. Breakpoints and stepping work on the `.cbl` lines. In the Watch
window, type a data item's name with `_` for `-`:

| Watch | Shows |
|---|---|
| `WS_PAY` | `0712.50`, type `PIC 9(4)V99` |
| `CUSTOMER` | the whole group; expand it for `CUST-NAME`, `CUST-CITY`, ... |
| `MONTH_ENTRY[2]` | occurrence 2 of a table (expand `MONTH_ENTRY` to see them all) |
| `COBOL` | every 01 / 77 item of the program |
| `COBOL["ws-pay"]` | any item by its COBOL name |

An item inside a table (`MONTH_SALES`) shows its first occurrence; use the table's `[n]`
for the others.

## Building from source

See [BUILD.md](BUILD.md). The compiler needs MSYS2 (UCRT64) and the GnuCOBOL 3.2 source with
`compiler/gnucobol-3.2-csharp.patch` applied. The runtime, SDK and editor need the .NET 9 SDK,
and the editor needs Visual Studio with the "Visual Studio extension development" workload.

## Design notes

How it got here and why, spike by spike: [SOURCE-REVIEW.md](SOURCE-REVIEW.md),
[SPIKE1.md](SPIKE1.md) to [SPIKE4.md](SPIKE4.md), [EDITOR.md](EDITOR.md), and the running
decision log [DECISIONS.md](DECISIONS.md).

## License

The compiler (`compiler/`) is GPLv3, as part of GnuCOBOL. Everything else is MIT. Programs you
write with SP COBOL are yours. See [LICENSE](LICENSE).
