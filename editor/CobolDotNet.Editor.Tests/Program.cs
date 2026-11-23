// Tests for the COBOL editor's logic: the line scanner (colors) and the
// sequence-number rules. Prints one line per test; exit code = failures.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CobolDotNet.Editor.Core;

static class Program
{
    static int failures, passes;

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { passes++; Console.WriteLine("PASS  " + name); }
        else { failures++; Console.WriteLine("FAIL  " + name + (detail.Length > 0 ? "\n      " + detail.Replace("\n", "\n      ") : "")); }
    }

    static void Same(string name, IList<string> want, IList<string> got)
        => Check(name, want.SequenceEqual(got),
                 "want:\n" + string.Join("\n", want) + "\ngot:\n" + string.Join("\n", got));

    /// <summary>Apply a plan the way the editor does: changes, then renumber.</summary>
    static (List<string> lines, int line, int col) Run(IList<string> lines, EditPlan plan)
    {
        var after = SequenceNumbers.Apply(lines.ToList(), plan.Changes);
        if (plan.Renumber) after = SequenceNumbers.Apply(after, SequenceNumbers.Renumber(after));
        return (after, plan.CaretLine, plan.CaretColumn);
    }

    static string Kinds(string line, bool fixedFormat = true)
        => string.Join(" ", CobolScanner.ScanLine(line, fixedFormat)
               .Select(t => t.Kind + ":" + line.Substring(t.Start, t.Length)));

    static int Main(string[] args)
    {
        string root = args.Length > 0 ? args[0] : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

        // ---------------- numbering basics ----------------
        Check("Number(0) is 000010", SequenceNumbers.Number(0) == "000010");
        Check("Number(63) is 000640", SequenceNumbers.Number(63) == "000640");

        // the samples, with columns 1-6 blanked, renumber back to themselves
        foreach (var f in Directory.GetFiles(Path.Combine(root, "samples"), "*.cbl"))
        {
            var orig = File.ReadAllLines(f).ToList();
            var blank = orig.Select(l => l.Length <= 6 ? "" : "      " + l.Substring(6)).ToList();
            var got = SequenceNumbers.Apply(blank, SequenceNumbers.Renumber(blank));
            Same("renumber " + Path.GetFileName(f), orig, got);
            Check("numbered " + Path.GetFileName(f), SequenceNumbers.IsNumbered(orig));
            Check("fixed format " + Path.GetFileName(f), SequenceNumbers.LooksFixed(orig));
        }
        Check("unnumbered is not numbered", !SequenceNumbers.IsNumbered(new[] { "       IDENTIFICATION DIVISION." }));
        Check("free format detected", !SequenceNumbers.LooksFixed(new[] {
            "IDENTIFICATION DIVISION.", "PROGRAM-ID. X.", "PROCEDURE DIVISION.", "DISPLAY \"HI\".", "STOP RUN." }));

        var doc = new List<string>
        {
            "000010 PROCEDURE DIVISION.",
            "000020 MAIN-PARAGRAPH.",
            "000030     PERFORM UNTIL WS-KEY = 2005",
            "000040         DISPLAY PAY-SCREEN",
            "000050     END-PERFORM",
            "000060     GOBACK.",
        };

        // ---------------- Enter ----------------
        var r = Run(doc, SequenceNumbers.PlanEnter(doc, 3, doc[3].Length, "\n"));
        Same("Enter at end of line: new numbered line, lines below +10", new[]
        {
            "000010 PROCEDURE DIVISION.",
            "000020 MAIN-PARAGRAPH.",
            "000030     PERFORM UNTIL WS-KEY = 2005",
            "000040         DISPLAY PAY-SCREEN",
            "000050         ",
            "000060     END-PERFORM",
            "000070     GOBACK.",
        }, r.lines);
        Check("Enter at end: caret under the code above", r.line == 4 && r.col == 15, $"caret {r.line},{r.col}");

        r = Run(doc, SequenceNumbers.PlanEnter(doc, 2, doc[2].IndexOf("UNTIL"), "\n"));
        Same("Enter mid-line splits; the rest keeps the indent", new[]
        {
            "000010 PROCEDURE DIVISION.",
            "000020 MAIN-PARAGRAPH.",
            "000030     PERFORM ",
            "000040     UNTIL WS-KEY = 2005",
            "000050         DISPLAY PAY-SCREEN",
            "000060     END-PERFORM",
            "000070     GOBACK.",
        }, r.lines);
        Check("split: caret before UNTIL", r.line == 3 && r.col == 11, $"caret {r.line},{r.col}");

        r = Run(doc, SequenceNumbers.PlanEnter(doc, 1, doc[1].Length, "\n"));
        Check("Enter after a paragraph name indents to area B",
              r.lines[2] == "000030     " && r.col == 11, $"'{r.lines[2]}' caret {r.col}");

        r = Run(doc, SequenceNumbers.PlanEnter(doc, 5, 7, "\n"));
        Same("Enter at column 8 opens a line above", new[]
        {
            "000010 PROCEDURE DIVISION.",
            "000020 MAIN-PARAGRAPH.",
            "000030     PERFORM UNTIL WS-KEY = 2005",
            "000040         DISPLAY PAY-SCREEN",
            "000050     END-PERFORM",
            "000060",
            "000070     GOBACK.",
        }, r.lines);
        Check("line above: caret stays with GOBACK's line", r.line == 6 && r.col == 7, $"caret {r.line},{r.col}");

        var blankDoc = new List<string> { "000010 A.", "000020", "000030 B." };
        r = Run(blankDoc, SequenceNumbers.PlanEnter(blankDoc, 1, 0, "\n"));
        Same("Enter on a blank line adds a line below", new[] { "000010 A.", "000020", "000030 ", "000040 B." }, r.lines);

        var comments = new List<string> { "000010*> Your first COBOL screen program.", "000020* old style" };
        r = Run(comments, SequenceNumbers.PlanEnter(comments, 0, comments[0].Length, "\n"));
        Check("Enter in a *> comment continues the comment", r.lines[1] == "000020*> " && r.col == 9,
              $"'{r.lines[1]}' caret {r.col}");
        r = Run(comments, SequenceNumbers.PlanEnter(comments, 1, comments[1].Length, "\n"));
        Check("Enter in a * comment continues the comment", r.lines[2] == "000030* " && r.col == 8,
              $"'{r.lines[2]}' caret {r.col}");

        Check("Enter in an unnumbered file is left to the editor",
              SequenceNumbers.PlanEnter(new[] { "       MOVE A TO B." }, 0, 10, "\n") == null);

        // ---------------- Backspace / Delete ----------------
        r = Run(doc, SequenceNumbers.PlanBackspace(doc, 4, 7));
        Same("Backspace at column 8 joins with the line above, lines below -10", new[]
        {
            "000010 PROCEDURE DIVISION.",
            "000020 MAIN-PARAGRAPH.",
            "000030     PERFORM UNTIL WS-KEY = 2005",
            "000040         DISPLAY PAY-SCREEN    END-PERFORM",
            "000050     GOBACK.",
        }, r.lines);
        Check("Backspace join: caret at the join", r.line == 3 && r.col == doc[3].Length, $"caret {r.line},{r.col}");

        r = Run(blankDoc, SequenceNumbers.PlanBackspace(blankDoc, 2, 7));
        Same("Backspace onto a blank line keeps code in its columns", new[] { "000010 A.", "000020 B." }, r.lines);

        Check("Backspace in mid-code is left to the editor", SequenceNumbers.PlanBackspace(doc, 2, 12) == null);
        r = Run(comments, SequenceNumbers.PlanBackspace(comments, 1, 7));
        Check("Backspace after '*' uncomments the line", r.lines[1] == "000020  old style", r.lines[1]);
        var first = SequenceNumbers.PlanBackspace(doc, 0, 7);
        Check("Backspace at the top does nothing", first != null && first.Changes.Count == 0);

        r = Run(doc, SequenceNumbers.PlanDelete(doc, 4, doc[4].Length));
        Same("Delete at end of line joins the next line", new[]
        {
            "000010 PROCEDURE DIVISION.",
            "000020 MAIN-PARAGRAPH.",
            "000030     PERFORM UNTIL WS-KEY = 2005",
            "000040         DISPLAY PAY-SCREEN",
            "000050     END-PERFORM    GOBACK.",
        }, r.lines);
        var del = SequenceNumbers.PlanDelete(doc, 2, 3);
        Check("Delete inside the number does nothing", del != null && del.Changes.Count == 0);
        Check("Delete in code is left to the editor", SequenceNumbers.PlanDelete(doc, 2, 12) == null);

        // ---------------- typing in columns 1-7 ----------------
        var star = SequenceNumbers.PlanTypeChar(doc, 2, 6, '*');
        r = Run(doc, star);
        Check("typing * in column 7 makes a comment", r.lines[2] == "000030*    PERFORM UNTIL WS-KEY = 2005" && !star.ThenDefault, r.lines[2]);
        var m = SequenceNumbers.PlanTypeChar(doc, 2, 2, 'M');
        Check("typing in the number moves to column 8 first", m.ThenDefault && m.CaretColumn == 7 && m.Changes.Count == 0);
        var shortLine = SequenceNumbers.PlanTypeChar(blankDoc, 1, 6, 'M');
        r = Run(blankDoc, shortLine);
        Check("typing on a blank line pads to column 8", r.lines[1] == "000020 " && shortLine.ThenDefault, r.lines[1]);
        Check("typing in code is left to the editor", SequenceNumbers.PlanTypeChar(doc, 2, 9, 'x') == null);

        // ---------------- Tab ----------------
        var tab = SequenceNumbers.PlanTab(doc, 2, 11);
        r = Run(doc, tab);
        Check("Tab at column 12 goes to column 16", r.lines[2] == "000030         PERFORM UNTIL WS-KEY = 2005" && r.col == 15, r.lines[2]);
        Check("Tab at column 13 goes to column 16", SequenceNumbers.PlanTab(doc, 2, 12).CaretColumn == 15);
        Check("Tab in the number goes to column 8", SequenceNumbers.PlanTab(doc, 2, 3).CaretColumn == 7);
        r = Run(blankDoc, SequenceNumbers.PlanTab(blankDoc, 1, 6));
        Check("Tab on a blank line pads to column 8", r.lines[1] == "000020 " && r.col == 7, $"'{r.lines[1]}'");

        // ---------------- Home ----------------
        Check("Home goes to the code", SequenceNumbers.PlanHome(doc, 2, 20).CaretColumn == 11);
        Check("Home again goes to column 8", SequenceNumbers.PlanHome(doc, 2, 11).CaretColumn == 7);

        // ---------------- pasted text ----------------
        var pasted = new List<string>
        {
            "000010 A.",
            "MOVE X TO Y",
            "       DISPLAY Y",
            "000010     STOP RUN.",
        };
        Same("Renumber fixes pasted lines", new[]
        {
            "000010 A.",
            "000020 MOVE X TO Y",
            "000030 DISPLAY Y",
            "000040     STOP RUN.",
        }, SequenceNumbers.Apply(pasted, SequenceNumbers.Renumber(pasted)));

        // ---------------- scanner ----------------
        Check("division header", Kinds("000140 IDENTIFICATION DIVISION.")
              == "Sequence:000140 Keyword:IDENTIFICATION Keyword:DIVISION Operator:.", Kinds("000140 IDENTIFICATION DIVISION."));
        Check("paragraph name", Kinds("000470 MAIN-PARAGRAPH.") == "Sequence:000470 ParagraphName:MAIN-PARAGRAPH Operator:.",
              Kinds("000470 MAIN-PARAGRAPH."));
        Check("section name", Kinds("000100 INIT SECTION.").Contains("ParagraphName:INIT"), Kinds("000100 INIT SECTION."));
        Check("a call is not a definition", !Kinds("000520             PERFORM CALCULATE-PAY").Contains("ParagraphName"));
        string pic = "000290 01  WS-PAY-SHOWN        PIC $$$,$$9.99.";
        Check("picture string", Kinds(pic) ==
              "Sequence:000290 Number:01 Identifier:WS-PAY-SHOWN Keyword:PIC Picture:$$$,$$9.99 Operator:.", Kinds(pic));
        string pic2 = "000240 01  WS-KEY              PIC IS 9(4).";
        Check("PIC IS", Kinds(pic2).Contains("Picture:9(4)"), Kinds(pic2));
        string str = "000440         VALUE \"Enter = calculate     Esc = quit\".";
        Check("string literal", Kinds(str).Contains("String:\"Enter = calculate     Esc = quit\""), Kinds(str));
        Check("doubled quote stays in the string", Kinds("000010     DISPLAY 'IT''S'.").Contains("String:'IT''S'"));
        Check("hex literal", Kinds("000010     MOVE X\"41\" TO A.").Contains("String:X\"41\""));
        Check("number with decimals", Kinds("000270 01  WS-RATE PIC 9(3)V99   VALUE 15.00.").Contains("Number:15.00 Operator:."));
        Check("signed number", Kinds("000010     MOVE -1.5 TO A.").Contains("Number:-1.5"));
        Check("hyphenated name is one word", Kinds("000010     COMPUTE A = WS-HOURS - 40").Contains("Identifier:WS-HOURS Operator:- Number:40"),
              Kinds("000010     COMPUTE A = WS-HOURS - 40"));
        Check("function name", Kinds("000010     MOVE FUNCTION CURRENT-DATE TO D.").Contains("Keyword:FUNCTION Function:CURRENT-DATE"));
        Check("comment line", Kinds("000010*> hello") == "Sequence:000010 Comment:*> hello");
        Check("inline comment", Kinds("000010     MOVE A TO B *> why").EndsWith("Comment:*> why"));
        string longLine = "000010     DISPLAY A" + new string(' ', 52) + "IDENTIFY";
        Check("columns 73+ are identification", Kinds(longLine).EndsWith("Identification:IDENTIFY"), Kinds(longLine));
        Check("free format has no sequence area", Kinds("MOVE A TO B.", false) == "Keyword:MOVE Identifier:A Keyword:TO Identifier:B Operator:.",
              Kinds("MOVE A TO B.", false));

        Console.WriteLine();
        Console.WriteLine($"{passes} passed, {failures} failed");
        return failures;
    }
}
