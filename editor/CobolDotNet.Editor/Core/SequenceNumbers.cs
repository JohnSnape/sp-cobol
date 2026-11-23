// Sequence numbers in columns 1-6, kept the way the Wang editor kept them
// (decision of 2026-11-22): 000010, 000020, ... by tens, always in order.
// Inserting a line gives it the previous number + 10 and moves every line below
// up by 10; deleting a line closes the gap the same way.
//
// Everything here works on plain strings, so the unit tests can drive it.
// The editor turns an EditPlan into one Visual Studio edit, then runs Renumber,
// both inside a single undo step.
using System;
using System.Collections.Generic;

namespace CobolDotNet.Editor.Core
{
    /// <summary>Replace the text from (Line, Column) up to (EndLine, EndColumn).</summary>
    public struct TextChange
    {
        public int Line, Column, EndLine, EndColumn;
        public string Text;

        public TextChange(int line, int column, int endLine, int endColumn, string text)
        {
            Line = line; Column = column; EndLine = endLine; EndColumn = endColumn; Text = text;
        }

        public static TextChange InLine(int line, int column, int length, string text)
            => new TextChange(line, column, line, column + length, text);

        public override string ToString() => $"({Line},{Column})-({EndLine},{EndColumn}) \"{Text}\"";
    }

    /// <summary>What the editor should do for one keystroke.</summary>
    public sealed class EditPlan
    {
        public readonly List<TextChange> Changes = new List<TextChange>();
        /// <summary>Where the caret goes afterwards (positions after the changes).</summary>
        public int CaretLine, CaretColumn;
        /// <summary>After the changes and the caret move, let the editor's own command run too.</summary>
        public bool ThenDefault;
        /// <summary>Renumber the file afterwards (lines were added or removed).</summary>
        public bool Renumber;
    }

    public static class SequenceNumbers
    {
        public const int Start = 10;
        public const int Step = 10;

        public static string Number(int lineIndex)
            => Math.Min(999999, Start + lineIndex * Step).ToString("000000");

        public static bool HasNumber(string line)
        {
            if (line == null || line.Length < 6) return false;
            for (int i = 0; i < 6; i++) if (!char.IsDigit(line[i])) return false;
            return true;
        }

        /// <summary>A file is "numbered" when its first non-empty line starts with 6 digits.
        /// Only numbered files get automatic numbering.</summary>
        public static bool IsNumbered(IReadOnlyList<string> lines)
        {
            for (int i = 0; i < lines.Count; i++)
                if (lines[i].Length > 0) return HasNumber(lines[i]);
            return false;
        }

        /// <summary>
        /// Fixed format unless most lines plainly aren't (code in columns 1-7).
        /// Checks at most the first 200 non-empty lines.
        /// </summary>
        public static bool LooksFixed(IReadOnlyList<string> lines)
        {
            int seen = 0, fixedLike = 0;
            for (int i = 0; i < lines.Count && seen < 200; i++)
            {
                string l = lines[i];
                if (l.Trim().Length == 0) continue;
                seen++;
                bool ok = true;
                for (int k = 0; k < Math.Min(6, l.Length); k++)
                    if (!char.IsDigit(l[k]) && l[k] != ' ') { ok = false; break; }
                if (ok && l.Length > 6 && " *-/Dd$".IndexOf(l[6]) < 0) ok = false;
                if (ok) fixedLike++;
            }
            return seen == 0 || fixedLike * 10 >= seen * 9;
        }

        /// <summary>Spaces between column 8 and the first character of code.</summary>
        public static string CodeIndent(string line)
        {
            int i = CobolScanner.AreaA;
            while (i < line.Length && line[i] == ' ') i++;
            return i <= CobolScanner.AreaA ? "" : new string(' ', i - CobolScanner.AreaA);
        }

        static bool CodeAreaBlank(string line)
        {
            for (int i = CobolScanner.AreaA; i < line.Length; i++) if (line[i] != ' ') return false;
            return true;
        }

        /// <summary>
        /// The changes that make columns 1-6 of lines [from..] read 000010, 000020, ...
        /// Lines already right are left alone. A line with no sequence area at all
        /// (text split off or pasted from elsewhere) gets one in front of it.
        /// </summary>
        public static List<TextChange> Renumber(IReadOnlyList<string> lines, int from = 0)
        {
            var changes = new List<TextChange>();
            for (int i = Math.Max(0, from); i < lines.Count; i++)
            {
                string l = lines[i];
                string want = Number(i);
                if (HasNumber(l))
                {
                    if (string.CompareOrdinal(l, 0, want, 0, 6) != 0)
                        changes.Add(TextChange.InLine(i, 0, 6, want));
                    continue;
                }

                int head = Math.Min(6, l.Length);
                bool sequenceArea = true;
                for (int k = 0; k < head; k++)
                    if (!char.IsDigit(l[k]) && l[k] != ' ') { sequenceArea = false; break; }

                if (sequenceArea)
                {
                    // blank or partly numbered columns 1-6 (or a shorter, blank line)
                    changes.Add(TextChange.InLine(i, 0, head, want));
                }
                else
                {
                    // text with no sequence area: give it one, under the line above
                    string indent = l[0] == ' ' || i == 0 ? "" : CodeIndent(lines[i - 1]);
                    changes.Add(TextChange.InLine(i, 0, 0, want + " " + indent));
                }
            }
            return changes;
        }

        // ------------------------------------------------------------------
        // Keystrokes. Each returns null when the key should behave normally.
        // ------------------------------------------------------------------

        /// <summary>Enter, with no selection.</summary>
        public static EditPlan PlanEnter(IReadOnlyList<string> lines, int line, int col, string newline)
        {
            string text = lines[line];
            if (!HasNumber(text)) return null;
            col = Math.Min(col, text.Length);
            var plan = new EditPlan { Renumber = true };

            if (col <= CobolScanner.AreaA && !CodeAreaBlank(text))
            {
                // at the start of a line: open a new blank line above it
                plan.Changes.Add(TextChange.InLine(line, 0, 0, Number(line) + newline));
                plan.CaretLine = line + 1;
                plan.CaretColumn = Math.Max(col, Math.Min(CobolScanner.AreaA, text.Length));
                return plan;
            }

            if (CodeAreaBlank(text)) col = text.Length;          // a blank line: new line below
            string rest = text.Substring(col).TrimStart(' ');
            string lead = NewLineLead(text);
            plan.Changes.Add(TextChange.InLine(line, col, text.Length - col,
                newline + Number(line + 1) + lead + rest));
            plan.CaretLine = line + 1;
            plan.CaretColumn = 6 + lead.Length;
            return plan;
        }

        /// <summary>Columns 7 onward of a new line split off from this one, up to where text starts.</summary>
        static string NewLineLead(string text)
        {
            char ind = text.Length > 6 ? text[6] : ' ';
            if (ind == '*' || ind == '/')
            {
                // a comment continues as a comment, in the same style ("*" or "*>")
                int i = 7;
                if (ind == '*' && i < text.Length && text[i] == '>') i++;
                while (i < text.Length && text[i] == ' ') i++;
                string marker = ind == '/' ? "*" + text.Substring(7, Math.Max(0, i - 7)) : text.Substring(6, i - 6);
                if (marker.TrimEnd().Length == marker.Length) marker += " ";
                return marker;
            }

            string indent = CodeIndent(text);
            // after a paragraph or section name, the code goes in area B
            var tokens = CobolScanner.ScanLine(text, true);
            foreach (var t in tokens)
                if (t.Kind == TokenKind.ParagraphName) { indent += "    "; break; }
            return " " + indent;
        }

        /// <summary>Backspace, with no selection.</summary>
        public static EditPlan PlanBackspace(IReadOnlyList<string> lines, int line, int col)
        {
            string text = lines[line];
            if (!HasNumber(text) || col > CobolScanner.AreaA) return null;
            var plan = new EditPlan();

            if (text.Length > 6 && text[6] != ' ')
            {
                // take away the indicator ('*' etc.): the line becomes code again
                plan.Changes.Add(TextChange.InLine(line, 6, 1, " "));
                plan.CaretLine = line;
                plan.CaretColumn = CobolScanner.AreaA;
                return plan;
            }

            if (line == 0)
            {
                plan.CaretLine = 0;
                plan.CaretColumn = Math.Min(CobolScanner.AreaA, text.Length);
                return plan;   // nothing before the first line: do nothing
            }

            // join this line onto the one above, dropping this line's columns 1-7
            string prev = lines[line - 1];
            string pad = prev.Length < 7 ? new string(' ', 7 - prev.Length) : "";
            plan.Changes.Add(new TextChange(line - 1, prev.Length, line, Math.Min(7, text.Length), pad));
            plan.CaretLine = line - 1;
            plan.CaretColumn = prev.Length + pad.Length;
            plan.Renumber = true;
            return plan;
        }

        /// <summary>Delete, with no selection.</summary>
        public static EditPlan PlanDelete(IReadOnlyList<string> lines, int line, int col)
        {
            string text = lines[line];
            if (!HasNumber(text)) return null;
            var plan = new EditPlan { CaretLine = line, CaretColumn = col };

            if (col < 6) return plan;                   // the sequence number isn't typed over
            if (col == 6 && text.Length > 6)
            {
                if (text[6] != ' ') plan.Changes.Add(TextChange.InLine(line, 6, 1, " "));
                return plan;                            // never pull code into column 7
            }

            if (col >= text.Length && line + 1 < lines.Count && HasNumber(lines[line + 1]))
            {
                // join the next line onto this one, dropping its columns 1-7
                string next = lines[line + 1];
                int drop = next.Length > 6 && next[6] != ' ' ? 6 : Math.Min(7, next.Length);
                string pad = text.Length < 7 ? new string(' ', 7 - text.Length) : "";
                plan.Changes.Add(new TextChange(line, text.Length, line + 1, drop, pad));
                plan.CaretColumn = text.Length;
                plan.Renumber = true;
                return plan;
            }
            return null;
        }

        /// <summary>Typing a character.</summary>
        public static EditPlan PlanTypeChar(IReadOnlyList<string> lines, int line, int col, char ch)
        {
            string text = lines[line];
            if (!HasNumber(text) || col > 6) return null;
            var plan = new EditPlan { CaretLine = line, CaretColumn = CobolScanner.AreaA };

            if (col == 6 && "*/-Dd".IndexOf(ch) >= 0)
            {
                // typing in column 7 sets the indicator
                if (text.Length > 6) plan.Changes.Add(TextChange.InLine(line, 6, 1, ch.ToString()));
                else plan.Changes.Add(TextChange.InLine(line, text.Length, 0, ch.ToString()));
                return plan;
            }

            // anything else typed in columns 1-7 goes to column 8
            if (text.Length < 7) plan.Changes.Add(TextChange.InLine(line, text.Length, 0, new string(' ', 7 - text.Length)));
            plan.ThenDefault = true;
            return plan;
        }

        /// <summary>Tab: spaces to the next stop at column 8, 12, 16, ... (area A, area B, ...).</summary>
        public static EditPlan PlanTab(IReadOnlyList<string> lines, int line, int col)
        {
            string text = lines[line];
            if (!HasNumber(text)) return null;
            var plan = new EditPlan { CaretLine = line };
            if (col < CobolScanner.AreaA)
            {
                if (text.Length < CobolScanner.AreaA)
                    plan.Changes.Add(TextChange.InLine(line, text.Length, 0, new string(' ', CobolScanner.AreaA - text.Length)));
                plan.CaretColumn = CobolScanner.AreaA;
                return plan;
            }
            int spaces = 4 - (col - CobolScanner.AreaA) % 4;
            plan.Changes.Add(TextChange.InLine(line, Math.Min(col, text.Length), 0,
                new string(' ', spaces + Math.Max(0, col - text.Length))));
            plan.CaretColumn = col + spaces;
            return plan;
        }

        /// <summary>Home: to the first character of code, then to column 8.</summary>
        public static EditPlan PlanHome(IReadOnlyList<string> lines, int line, int col)
        {
            string text = lines[line];
            if (!HasNumber(text) || text.Length <= CobolScanner.AreaA) return null;
            int first = CobolScanner.AreaA;
            while (first < text.Length && text[first] == ' ') first++;
            if (first == text.Length) first = CobolScanner.AreaA;
            return new EditPlan
            {
                CaretLine = line,
                CaretColumn = col == first ? CobolScanner.AreaA : first,
            };
        }

        // ------------------------------------------------------------------
        // Applying changes to a list of strings (used by the unit tests;
        // the editor applies them to its text buffer instead).
        // ------------------------------------------------------------------

        public static List<string> Apply(IReadOnlyList<string> lines, IEnumerable<TextChange> changes, string newline = "\n")
        {
            // flatten to one string with offsets, apply from the end backwards
            var starts = new int[lines.Count + 1];
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < lines.Count; i++)
            {
                starts[i] = sb.Length;
                sb.Append(lines[i]);
                if (i < lines.Count - 1) sb.Append(newline);
            }
            starts[lines.Count] = sb.Length;

            var list = new List<TextChange>(changes);
            list.Sort((a, b) => b.Line != a.Line ? b.Line.CompareTo(a.Line) : b.Column.CompareTo(a.Column));
            foreach (var c in list)
            {
                int s = starts[c.Line] + c.Column;
                int e = starts[c.EndLine] + c.EndColumn;
                sb.Remove(s, e - s).Insert(s, c.Text);
            }
            return new List<string>(sb.ToString().Split(new[] { newline }, StringSplitOptions.None));
        }
    }
}
