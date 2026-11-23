# SP COBOL

COBOL for .NET, made for people **learning COBOL**. Write standard COBOL (fixed format, with
SCREEN SECTION screens) in Visual Studio. Build it into an ordinary .NET program that runs as a
DOS-style text screen, as a Windows window, or both from the same source.

> **Preview (0.1).** It works end to end for the sample programs, but large parts of COBOL
> (files, CALL, INSPECT, most intrinsic functions) are not in the runtime yet.

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

From the [Releases](../../releases) page:

- **`CobolDotNet.Editor.vsix`**: close Visual Studio, double-click it, install. Then open any
  `.cbl`, `.cob` or `.cpy` file. See [EDITOR.md](EDITOR.md) for what each key does.
- **`CobolDotNet.Sdk.0.1.0.nupkg`**: the compiler and build support. Put it in a folder, then
  point the template's `nuget.config` at that folder (an installer that does this is planned).

Then:

```
dotnet new install templates\cobol-screen-app
dotnet new cobol-screen -n PayDemo
cd PayDemo
dotnet build
```

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
