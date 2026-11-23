// Keyboard editing of ACCEPT fields on a character screen.  Shared by the
// console renderer and the scripted (test) renderer, so tests exercise the
// exact editing rules a user gets.
//
// Keys, DOS-style:
//   typing        overwrites (Insert toggles insert mode); the first key typed
//                 in a field replaces its old contents, like a Windows text box
//   Left/Right, Home/End, Backspace, Delete
//   Tab / Down    next field       Shift+Tab / Up   previous field
//   Enter         finish (0)       Esc              2005
//   F1..F24       1001..1024       PgUp / PgDn      2001 / 2002

using System;
using System.Text;

namespace CobolDotNet.Runtime;

internal static class FieldEditor
{
    /// <param name="paint">draw one field (input, cursor position or -1 when not focused)</param>
    /// <param name="showMessage">show or clear (null) the message line</param>
    public static int Run(AcceptRequest req, Func<ConsoleKeyInfo> readKey,
                          Action<ScreenInput, int> paint, Action<string> showMessage, Action bell)
    {
        var inputs = req.Inputs;
        if (inputs.Count == 0) return CobKey.Enter;
        int f = Math.Clamp(req.Focus, 0, inputs.Count - 1);
        int pos = 0;
        bool fresh = true;          // nothing typed in this field yet
        bool insert = false;
        string message = req.Message;

        foreach (var inp in inputs) paint(inp, -1);
        showMessage(message);
        paint(inputs[f], pos);

        // Leave the current field: normalize it. Returns false (and stays) if it's not valid.
        bool Leave()
        {
            var inp = inputs[f];
            if (req.Check == null) return true;
            var (text, error) = req.Check(inp, inp.Text);
            if (error != null)
            {
                bell();
                message = error;
                showMessage(message);
                pos = 0; fresh = true;
                paint(inp, pos);
                return false;
            }
            inp.Text = text;
            paint(inp, -1);
            return true;
        }

        void Move(int to)
        {
            if (!Leave()) return;
            if (message != null) { message = null; showMessage(null); }
            f = (to + inputs.Count) % inputs.Count;
            pos = 0; fresh = true;
            paint(inputs[f], pos);
        }

        int Finish(int key)
        {
            req.Focus = f;
            paint(inputs[f], -1);
            return key;
        }

        while (true)
        {
            var k = readKey();
            var inp = inputs[f];
            var buf = new StringBuilder(inp.Text);

            if (k.Key >= ConsoleKey.F1 && k.Key <= ConsoleKey.F24)
                return Finish(CobKey.F(k.Key - ConsoleKey.F1 + 1));

            switch (k.Key)
            {
                case ConsoleKey.Enter:
                    if (!Leave()) continue;
                    return Finish(CobKey.Enter);
                case ConsoleKey.Escape: return Finish(CobKey.Escape);
                case ConsoleKey.PageUp: return Finish(CobKey.PageUp);
                case ConsoleKey.PageDown: return Finish(CobKey.PageDown);
                case ConsoleKey.Tab:
                    Move((k.Modifiers & ConsoleModifiers.Shift) != 0 ? f - 1 : f + 1);
                    continue;
                case ConsoleKey.DownArrow: Move(f + 1); continue;
                case ConsoleKey.UpArrow: Move(f - 1); continue;
                case ConsoleKey.LeftArrow: if (pos > 0) pos--; fresh = false; paint(inp, pos); continue;
                case ConsoleKey.RightArrow: if (pos < inp.Length - 1) pos++; fresh = false; paint(inp, pos); continue;
                case ConsoleKey.Home: pos = 0; fresh = false; paint(inp, pos); continue;
                case ConsoleKey.End:
                    pos = Math.Min(inp.Text.TrimEnd().Length, inp.Length - 1);
                    fresh = false; paint(inp, pos); continue;
                case ConsoleKey.Insert: insert = !insert; continue;
                case ConsoleKey.Backspace:
                    if (pos > 0)
                    {
                        pos--;
                        buf.Remove(pos, 1).Append(' ');
                        inp.Text = buf.ToString();
                    }
                    fresh = false; paint(inp, pos); continue;
                case ConsoleKey.Delete:
                    buf.Remove(pos, 1).Append(' ');
                    inp.Text = buf.ToString();
                    fresh = false; paint(inp, pos); continue;
            }

            char ch = k.KeyChar;
            if (ch < ' ' || ch == 127 || ch > 255) continue;   // not a printable Latin-1 character
            if (inp.Numeric && !(char.IsAsciiDigit(ch) || ".,-+ ".IndexOf(ch) >= 0))
            {
                bell();
                message = "Only numbers can be typed here.";
                showMessage(message);
                continue;
            }
            if (inp.Upper) ch = char.ToUpperInvariant(ch);
            if (inp.Lower) ch = char.ToLowerInvariant(ch);
            if (pos >= inp.Length) { bell(); continue; }        // field is full
            if (message != null) { message = null; showMessage(null); }

            if (fresh && pos == 0) { buf.Clear().Append(' ', inp.Length); }
            fresh = false;
            if (insert)
            {
                buf.Insert(pos, ch);
                buf.Length = inp.Length;
            }
            else buf[pos] = ch;
            inp.Text = buf.ToString();
            pos++;
            if (pos >= inp.Length && inp.Auto)
            {
                if (f == inputs.Count - 1)
                {
                    if (!Leave()) continue;
                    return Finish(CobKey.Enter);    // AUTO on the last field ends the ACCEPT
                }
                Move(f + 1);
                continue;
            }
            paint(inp, Math.Min(pos, inp.Length - 1));
            if (pos >= inp.Length) pos = inp.Length;    // cursor parked after the last character
        }
    }

    /// <summary>What a field looks like while being edited.</summary>
    public static string Shown(ScreenInput inp)
    {
        var sb = new StringBuilder(inp.Length);
        foreach (char c in inp.Text)
            sb.Append(c == ' ' ? inp.PromptChar : inp.Secure ? '*' : c);
        return sb.ToString();
    }
}
