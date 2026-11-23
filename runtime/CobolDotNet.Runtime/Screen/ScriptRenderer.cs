// Headless renderer for automated tests: keystrokes come from a script, and the
// screen is printed as text at every ACCEPT and at the end of the program.
//
// Select with environment variables:
//   COB_SCREEN=script
//   COB_SCREEN_KEYS=<file>      e.g.   JONES{TAB}500{TAB}secret{ENTER}
// Key names in braces: ENTER TAB BTAB ESC UP DOWN LEFT RIGHT HOME END BS DEL INS
// PGUP PGDN F1..F24.  Line breaks in the file are ignored (use {ENTER}).

using System;
using System.Collections.Generic;
using System.IO;

namespace CobolDotNet.Runtime;

public sealed class ScriptRenderer : IScreenRenderer
{
    readonly Queue<ConsoleKeyInfo> keys = new();
    readonly TextWriter log;
    int accepts;

    public ScriptRenderer(string keyFile, TextWriter log = null)
    {
        this.log = log ?? Console.Out;
        string text = keyFile != null && File.Exists(keyFile) ? File.ReadAllText(keyFile) : "";
        Parse(text.Replace("\r", "").Replace("\n", ""));
    }

    void Parse(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '{')
            {
                int j = s.IndexOf('}', i);
                if (j < 0) throw new CobRuntimeException("key script: missing '}'");
                keys.Enqueue(Named(s.Substring(i + 1, j - i - 1).ToUpperInvariant()));
                i = j;
            }
            else keys.Enqueue(new ConsoleKeyInfo(s[i], ConsoleKey.A, false, false, false));
        }
    }

    static ConsoleKeyInfo Named(string n)
    {
        ConsoleKeyInfo K(ConsoleKey k, char c = '\0', bool shift = false) => new(c, k, shift, false, false);
        if (n.Length > 1 && n[0] == 'F' && int.TryParse(n.AsSpan(1), out int fn) && fn >= 1 && fn <= 24)
            return K(ConsoleKey.F1 + (fn - 1));
        return n switch
        {
            "ENTER" => K(ConsoleKey.Enter, '\r'),
            "TAB" => K(ConsoleKey.Tab, '\t'),
            "BTAB" => K(ConsoleKey.Tab, '\t', shift: true),
            "ESC" => K(ConsoleKey.Escape),
            "UP" => K(ConsoleKey.UpArrow),
            "DOWN" => K(ConsoleKey.DownArrow),
            "LEFT" => K(ConsoleKey.LeftArrow),
            "RIGHT" => K(ConsoleKey.RightArrow),
            "HOME" => K(ConsoleKey.Home),
            "END" => K(ConsoleKey.End),
            "BS" => K(ConsoleKey.Backspace),
            "DEL" => K(ConsoleKey.Delete),
            "INS" => K(ConsoleKey.Insert),
            "PGUP" => K(ConsoleKey.PageUp),
            "PGDN" => K(ConsoleKey.PageDown),
            _ => throw new CobRuntimeException($"key script: unknown key {{{n}}}"),
        };
    }

    public bool OwnsConsoleOutput => false;

    public void Present(ScreenGrid grid) { }

    public int Accept(AcceptRequest req)
    {
        accepts++;
        var g = req.Grid.Clone();
        foreach (var inp in req.Inputs)
            g.Put(inp.Row, inp.Col, FieldEditor.Shown(inp), inp.Fg, inp.Bg);
        Dump(g, $"ACCEPT {accepts}");
        int result = FieldEditor.Run(req,
            () =>
            {
                if (keys.Count == 0) throw new CobRuntimeException("key script ran out of keys");
                return keys.Dequeue();
            },
            (inp, cursor) => { },
            msg => { if (msg != null) log.WriteLine($"  [message] {msg}"); },
            () => log.WriteLine("  [bell]"));
        log.WriteLine($"  [key {result}]");
        return result;
    }

    public void Bell() => log.WriteLine("  [bell]");

    public void Shutdown(ScreenGrid grid, bool waitForUser) => Dump(grid, "END");

    void Dump(ScreenGrid g, string title)
    {
        var lines = g.TextLines();
        int last = lines.Length - 1;
        while (last > 0 && lines[last].Length == 0) last--;
        log.WriteLine($"+--- {title} " + new string('-', Math.Max(0, g.Columns - 5 - title.Length)) + "+");
        for (int r = 0; r <= last; r++) log.WriteLine("|" + lines[r]);
        log.WriteLine("+" + new string('-', g.Columns) + "+");
        log.Flush();
    }
}
