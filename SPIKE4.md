# Spike 4: a Visual Studio project for COBOL (2026-11-22)

**Result: works from the command line and with Visual Studio 2022's own MSBuild.** A beginner's
project file is now just:

```xml
<Project Sdk="CobolDotNet.Sdk/0.1.0">
  <PropertyGroup>
    <ScreenMode>Both</ScreenMode>   <!-- Both | Console | Forms -->
  </PropertyGroup>
</Project>
```

plus `Program.cbl`. Building it produces **PayDemo.exe** (DOS-style text screen) and
**PayDemo-Win.exe** (the same screen in a window). The debug info points at `Program.cbl`
lines.

Regression suite: 8 tests. The 7 from spike 3 pass, and the template's own program (WELCOME)
is a new screen test.

## Pieces

| Piece | Where | What it does |
|---|---|---|
| `CobolDotNet.Sdk` | `sdk/CobolDotNet.Sdk/` → `dist/feed/*.nupkg` | an MSBuild SDK (NuGet package) holding cobc.exe + its DLLs + config/copybooks + the runtime DLLs + the build logic |
| `Sdk.props` / `Sdk.targets` | `sdk/CobolDotNet.Sdk/Sdk/` | wrap Microsoft.NET.Sdk; pick TargetFramework, UseWindowsForms and OutputType from ScreenMode |
| `CobolCompile` task | `Sdk/CobolCompile.cs` (inline, RoslynCodeTaskFactory) | runs `cobc -C -x -fcsharp` on each `.cbl`/`.cob` (copybooks `.cpy`); incremental; turns cobc messages into VS errors with hints; finds the main program's class |
| `CobolScreenCheck` task | `Sdk/CobolScreenCheck.cs` | the "this control forfeits the DOS screens" build error (decision of 2026-11-22) |
| Project template | `templates/cobol-screen-app/` | "COBOL Screen App", `dotnet new cobol-screen`, with a **Screens** choice: Both / DOS-style console only / Windows only |
| Packaging | `tools/package-sdk.ps1` | stages everything and packs into `dist/feed`; clears the stale NuGet cache copy |

## What a beginner gets

- **New project dialog:** "COBOL Screen App", with a *Screens* dropdown (`ide.host.json`),
  defaulting to **Both**. The choice is made before any fields are laid out, as decided.
- **F5:** the run-profile list offers *"PayDemo (DOS-style screen)"* and *"PayDemo (Windows)"*.
  The Windows profile runs the same exe with `COB_SCREEN=forms` (the run-time switch), so
  debugging works in both.
- **Errors in COBOL terms**, in the Error List, double-clickable to the `.cbl` line:
  ```
  Program.cbl(63): COBOL error COB1001: 'WS-PAYY' is not defined  -- Every data name must be
      declared in the DATA DIVISION (usually WORKING-STORAGE SECTION). Check the spelling, too.
  Program.cbl(52): COBOL error COB1001: 'END-IFF' is not defined  -- Check the spelling of the
      scope terminator: END-IF, END-PERFORM, END-EVALUATE, ...
  ```
  Hints cover the common beginner mistakes:
  - missing period, column 7/8 layout, past column 72
  - undefined name, unknown paragraph
  - reserved word used as a name
  - missing END-xxx
- **Forfeit guard:** a ListBox (or any control other than Label/TextBox/CheckBox/Button/Panel)
  in a form of a Both/Console project gives:
  ```
  Form1.Designer.cs(6): COBOL error COB2001: 'listBox1' is a ListBox, which has no DOS-screen
      equivalent. This project also builds a DOS-style screen (ScreenMode = Both). Either remove
      the ListBox, or make this a Windows-only app by setting <ScreenMode>Forms</ScreenMode> ...
  ```
  With `ScreenMode=Forms` the same project builds. Verified.
- **Debugging in COBOL:** each `/* Line: N */` comment that codegen writes becomes
  `#line N "full\path\Program.cbl"`, and the plumbing (dispatcher, initialization, method
  headers) is `#line hidden`. The PDB check shows each paragraph method mapped to its COBOL
  lines:
  ```
  MAIN_PARAGRAPH   Program.cbl: lines 49,48,50 ... 58
  CALCULATE_PAY    Program.cbl: lines 57 ... 70
  ```
- **Nothing else to install:** cobc.exe ships inside the package with its 5 MSYS2 DLLs. No
  MSYS2 or environment variables on the user's PC.

## How ScreenMode=Both makes two exes

The project's normal build makes `NAME.exe`. Then target `CobolBuildWindowsVersion` builds the
same project again with `CobolVariant=Win`. That pass sets OutputType WinExe, assembly name
`NAME-Win`, its own `obj\...\win\` folder, the same `OutDir`, and a generated `WinHost.cs`
(`FormsHost.Run(<main class>.Main, ...)`). Both passes share one restore and one
TargetFramework (`net9.0-windows`), so nothing is restored twice. `Clean` cleans both.

## Verified

- `dotnet new cobol-screen -n PayDemo` → `dotnet build` → both exes. The console exe under the
  script renderer computes 40 h @ 15.00 + 5 h overtime = **$712.50**.
- The same project built with
  `C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe -restore`
  (.NET Framework MSBuild, inline tasks included).
- Error mapping, hints, and the COB2001 check (and that Forms mode lifts it).
- PDB sequence points → `Program.cbl`.

**Not verified:** the Visual Studio IDE itself:
- that the template shows in the New Project dialog;
- F9 breakpoints in the `.cbl` editor;
- that F5 hits them.

## Known gaps (next)

- **Watch window:** data items show as `f_17` etc. Watching `WS-PAY` by its COBOL name, showing
  its PIC-formatted value, needs a debugger display layer (next VS spike).
- ~~**COBOL editor:** `.cbl` opens as plain text.~~ Done 2026-11-22: colors, column guides
  and automatic sequence numbers. See EDITOR.md.
- **The designer direction** (lay out a form in the VS designer → SCREEN SECTION copybook) and
  the designer-time "Keep it / Remove it" dialog: the build-time half of that warning exists now.
- **Multiple programs:** every `.cbl` is compiled with `-x`, and the main one is chosen by
  `CobolMainProgram`, else `Program.cbl`, else the only file. CALL between programs isn't in the
  runtime yet.
- ~~**Local feed:**~~ Done 2026-11-23: the template's `nuget.config` reads `%LOCALAPPDATA%SP COBOLeed`,
  which `toolsinstall.ps1` fills (and `package-sdk.ps1` on the dev PC).
- **Version bumps:** NuGet caches `CobolDotNet.Sdk/0.1.0` per user. `package-sdk.ps1` clears that
  cache entry, so re-packing the same version works on this PC.
- `dotnet new install` was run once for this PC (`dotnet new uninstall "D:\CALUDE CODE\visual-cobol\templates\cobol-screen-app"` removes it).
