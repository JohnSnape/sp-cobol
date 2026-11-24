// MSBuild task: COBOL -> C# with cobc -fcsharp.
// Turns cobc's messages ("PROG.cbl:12: error: ...") into Visual Studio errors
// (Error List entries you can double-click), adding a plain-English hint for
// the mistakes beginners make most.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

public class CobolCompile : Task
{
    [Required] public ITaskItem[] Sources { get; set; }
    public ITaskItem[] Copybooks { get; set; }
    [Required] public string Compiler { get; set; }
    [Required] public string ToolsDir { get; set; }
    [Required] public string OutputDir { get; set; }
    [Required] public string ProjectDir { get; set; }
    public string SourceFormat { get; set; }
    public string MainProgram { get; set; }

    [Output] public ITaskItem[] Generated { get; set; }
    [Output] public string MainClass { get; set; }

    static readonly Regex Diag = new Regex(@"^(?<file>.+?):(?<line>\d+):\s*(?<kind>error|warning|note):\s*(?<msg>.*)$");
    static readonly Regex ClassName = new Regex(@"public static partial class (?<c>\w+)_Program\b");

    // cobc message (start) -> hint for a beginner
    static readonly (string, string)[] Hints =
    {
        ("'END-", "Check the spelling of the scope terminator: END-IF, END-PERFORM, END-EVALUATE, END-COMPUTE, END-READ, END-STRING ..."),
        ("syntax error, unexpected", "Check this line and the one above it: a missing period, a misspelled COBOL word, or code starting before column 8 are the usual causes."),
        ("is not defined", "Every data name must be declared in the DATA DIVISION (usually WORKING-STORAGE SECTION). Check the spelling, too."),
        ("is not a procedure name", "PERFORM and GO TO need a paragraph or SECTION name that exists in the PROCEDURE DIVISION."),
        ("invalid indicator", "In fixed format, column 7 is only for '*' (comment), '-' (continuation) or 'D'. Code starts in column 8."),
        ("source text exceeds", "In fixed format, code must end by column 72. Split the line, or set <CobolSourceFormat>free</CobolSourceFormat> in the project."),
        ("unexpected end of file", "The program ended early: is a period, END-IF or END-PERFORM missing?"),
        ("PROGRAM-ID", "Every program starts with IDENTIFICATION DIVISION. and PROGRAM-ID. name."),
        ("is a reserved word", "That name is a COBOL reserved word; pick another name, for example add a prefix like WS-."),
        ("not numeric", "Arithmetic needs numeric items (PIC 9...)."),
    };

    public override bool Execute()
    {
        Directory.CreateDirectory(OutputDir);
        if (!File.Exists(Compiler))
        {
            Log.LogError(null, "COB0001", null, null, 0, 0, 0, 0,
                "The COBOL compiler wasn't found at {0}. Reinstall CobolDotNet.Sdk.", Compiler);
            return false;
        }

        var copyDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ProjectDir };
        DateTime newestCopy = DateTime.MinValue;
        foreach (var cb in Copybooks ?? Array.Empty<ITaskItem>())
        {
            var p = Path.GetFullPath(Path.Combine(ProjectDir, cb.ItemSpec));
            copyDirs.Add(Path.GetDirectoryName(p));
            var t = File.GetLastWriteTimeUtc(p);
            if (t > newestCopy) newestCopy = t;
        }
        var compilerTime = File.GetLastWriteTimeUtc(Compiler);

        var generated = new List<ITaskItem>();
        string mainClass = null, firstClass = null;
        bool ok = true;
        string mainName = ChooseMain();

        foreach (var src in Sources)
        {
            string full = Path.GetFullPath(Path.Combine(ProjectDir, src.ItemSpec));
            string name = Path.GetFileNameWithoutExtension(full);
            string cs = Path.Combine(OutputDir, name + ".cs");
            copyDirs.Add(Path.GetDirectoryName(full));
            // the main program is an executable (-x); the others are CALLed subprograms
            bool isMain = name.Equals(mainName, StringComparison.OrdinalIgnoreCase);

            bool upToDate = File.Exists(cs)
                && File.GetLastWriteTimeUtc(cs) >= File.GetLastWriteTimeUtc(full)
                && File.GetLastWriteTimeUtc(cs) >= newestCopy
                && File.GetLastWriteTimeUtc(cs) >= compilerTime
                && File.ReadAllText(cs).Contains("static void Main (") == isMain;
            if (!upToDate)
            {
                if (File.Exists(cs)) File.Delete(cs);
                if (!RunCobc(full, copyDirs, isMain) || !File.Exists(cs)) { ok = false; continue; }
            }
            generated.Add(new TaskItem(cs));

            var m = ClassName.Match(File.ReadAllText(cs));
            if (m.Success)
            {
                string cls = "CobolPrograms." + m.Groups["c"].Value + "_Program";
                firstClass ??= cls;
                if (isMain && mainClass == null) mainClass = cls;
            }
        }

        Generated = generated.ToArray();
        MainClass = mainClass ?? firstClass;
        return ok && !Log.HasLoggedErrors;
    }

    static readonly Regex UsingClause = new Regex(@"PROCEDURE\s+DIVISION\s+USING", RegexOptions.IgnoreCase);

    /// <summary>
    /// The file that is the program (the rest are subprograms it CALLs):
    /// CobolMainProgram if set, else Program.cbl, else the only file, else the first
    /// whose PROCEDURE DIVISION has no USING (a main program can't take parameters).
    /// </summary>
    string ChooseMain()
    {
        if (!string.IsNullOrEmpty(MainProgram)) return Path.GetFileNameWithoutExtension(MainProgram);
        var names = Sources.Select(s => Path.GetFullPath(Path.Combine(ProjectDir, s.ItemSpec))).ToList();
        var program = names.FirstOrDefault(n => Path.GetFileNameWithoutExtension(n).Equals("Program", StringComparison.OrdinalIgnoreCase));
        if (program != null) return "Program";
        if (names.Count == 1) return Path.GetFileNameWithoutExtension(names[0]);
        foreach (var n in names)
        {
            try { if (!UsingClause.IsMatch(File.ReadAllText(n))) return Path.GetFileNameWithoutExtension(n); }
            catch (IOException) { }
        }
        return names.Count > 0 ? Path.GetFileNameWithoutExtension(names[0]) : null;
    }

    bool RunCobc(string source, IEnumerable<string> copyDirs, bool isMain)
    {
        // -Wno-unfinished: GnuCOBOL flags BY VALUE / PIC N as "unfinished" in ITS runtime;
        // ours implements them (tests CALLS, NATIONAL), so the warning would only mislead
        var args = new StringBuilder(isMain ? "-C -x -fcsharp -Wno-unfinished" : "-C -fcsharp -Wno-unfinished");
        if (string.Equals(SourceFormat, "free", StringComparison.OrdinalIgnoreCase)) args.Append(" -free");
        foreach (var d in copyDirs) args.Append(" -I \"").Append(d.TrimEnd('\\')).Append('"');
        args.Append(" \"").Append(source).Append('"');

        var psi = new ProcessStartInfo(Compiler, args.ToString())
        {
            WorkingDirectory = OutputDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.GetEncoding(28591),
            StandardErrorEncoding = Encoding.GetEncoding(28591),
        };
        // cobc.exe and its DLLs ship together; config and copybooks live beside them
        psi.Environment["PATH"] = Path.Combine(ToolsDir, "bin") + ";" + Environment.GetEnvironmentVariable("PATH");
        psi.Environment["COB_CONFIG_DIR"] = Path.Combine(ToolsDir, "config");
        psi.Environment["COB_COPY_DIR"] = Path.Combine(ToolsDir, "copy");
        psi.Environment["LANG"] = "C";

        Log.LogMessage(MessageImportance.Low, "cobc {0}", psi.Arguments);
        var lines = new List<string>();
        using (var p = new Process { StartInfo = psi })
        {
            p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (lines) lines.Add(e.Data); };
            p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (lines) lines.Add(e.Data); };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.WaitForExit();
            bool reported = Report(lines, source);
            if (p.ExitCode != 0 && !reported)
                Log.LogError(null, "COB0001", null, source, 0, 0, 0, 0,
                    "The COBOL compiler stopped (exit code {0}): {1}", p.ExitCode, string.Join(" ", lines));
            return p.ExitCode == 0;
        }
    }

    /// <summary>Log cobc's diagnostics; true if any error was logged.</summary>
    bool Report(List<string> lines, string source)
    {
        bool anyError = false;
        foreach (var raw in lines)
        {
            var m = Diag.Match(raw.Trim());
            if (!m.Success) continue;
            string file = m.Groups["file"].Value;
            if (file == "cobc" || file.StartsWith("cobc")) file = source;
            if (!Path.IsPathRooted(file)) file = Path.Combine(Path.GetDirectoryName(source), file);
            int line = int.Parse(m.Groups["line"].Value);
            string msg = m.Groups["msg"].Value;
            string hint = Hints.FirstOrDefault(h => msg.IndexOf(h.Item1, StringComparison.OrdinalIgnoreCase) >= 0).Item2;
            string text = hint == null ? msg : msg + "  -- " + hint;
            switch (m.Groups["kind"].Value)
            {
                case "error":
                    Log.LogError("COBOL", "COB1001", null, file, line, 0, 0, 0, text);
                    anyError = true;
                    break;
                case "warning":
                    Log.LogWarning("COBOL", "COB1002", null, file, line, 0, 0, 0, text);
                    break;
                default:
                    Log.LogMessage(MessageImportance.High, "{0}({1}): {2}", file, line, text);
                    break;
            }
        }
        return anyError;
    }
}
