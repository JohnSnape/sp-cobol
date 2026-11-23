// DOS-style text screen on System.Console.

using System;
using System.Text;

namespace CobolDotNet.Runtime;

public sealed class ConsoleRenderer : IScreenRenderer
{
    static readonly ConsoleColor[] Colors =
    {
        ConsoleColor.Black, ConsoleColor.DarkBlue, ConsoleColor.DarkGreen, ConsoleColor.DarkCyan,
        ConsoleColor.DarkRed, ConsoleColor.DarkMagenta, ConsoleColor.DarkYellow, ConsoleColor.Gray,
        ConsoleColor.DarkGray, ConsoleColor.Blue, ConsoleColor.Green, ConsoleColor.Cyan,
        ConsoleColor.Red, ConsoleColor.Magenta, ConsoleColor.Yellow, ConsoleColor.White,
    };

    ScreenGrid shown;          // what is on the console now (to redraw only changed rows)
    byte shownScreenBg = 255;
    int rows, cols;            // the part of the grid that fits in the window
    readonly ConsoleColor startFg, startBg;

    public ConsoleRenderer()
    {
        if (Console.IsOutputRedirected || Console.IsInputRedirected)
            throw new CobRuntimeException(
                "This program uses a SCREEN SECTION or DISPLAY/ACCEPT with LINE and COL, so it needs " +
                "a console window. Its input or output is redirected to a file or pipe.");
        startFg = Console.ForegroundColor;
        startBg = Console.BackgroundColor;
        Console.OutputEncoding = Encoding.Latin1;
        Console.TreatControlCAsInput = false;
    }

    public bool OwnsConsoleOutput => false;

    public void Present(ScreenGrid grid)
    {
        rows = Math.Min(grid.Rows, Console.WindowHeight);
        cols = Math.Min(grid.Columns, Console.WindowWidth);
        bool full = shown == null || grid.ScreenBg != shownScreenBg;
        if (full)
        {
            // fill the whole window with the screen background, like a DOS screen
            Console.BackgroundColor = Colors[grid.ScreenBg & 7];
            Console.ForegroundColor = Colors[grid.ScreenFg & 15];
            Console.Clear();
            shownScreenBg = grid.ScreenBg;
        }
        for (int r = 0; r < rows; r++)
            if (full || !SameRow(grid, shown, r)) DrawRow(grid, r);
        shown = grid.Clone();
    }

    static bool SameRow(ScreenGrid a, ScreenGrid b, int r)
    {
        for (int c = 0; c < a.Columns; c++)
            if (!a.Cells[r, c].Equals(b.Cells[r, c])) return false;
        return true;
    }

    void DrawRow(ScreenGrid g, int r)
    {
        // one Write per run of same-colored cells
        int c = 0;
        var sb = new StringBuilder();
        while (c < cols)
        {
            var first = g.Cells[r, c];
            sb.Clear();
            int start = c;
            while (c < cols && g.Cells[r, c].Fg == first.Fg && g.Cells[r, c].Bg == first.Bg)
            {
                char ch = g.Cells[r, c].Ch;
                sb.Append(ch < ' ' ? ' ' : ch);
                c++;
            }
            Write(r, start, sb.ToString(), first.Fg, first.Bg);
        }
    }

    void Write(int r, int c, string s, byte fg, byte bg)
    {
        if (r >= rows || c >= cols) return;
        if (c + s.Length > cols) s = s.Substring(0, cols - c);
        // writing the very last cell of the window would scroll it
        if (r == Console.WindowHeight - 1 && c + s.Length >= Console.WindowWidth && s.Length > 0)
            s = s.Substring(0, s.Length - 1);
        Console.SetCursorPosition(c, r);
        Console.ForegroundColor = Colors[fg & 15];
        Console.BackgroundColor = Colors[bg & 7];
        Console.Write(s);
    }

    public int Accept(AcceptRequest req)
    {
        Present(req.Grid);
        int msgRow = Math.Min(req.Grid.Rows, Console.WindowHeight) - 1;
        Console.CursorVisible = true;
        try
        {
            return FieldEditor.Run(req,
                () => Console.ReadKey(intercept: true),
                (inp, cursor) =>
                {
                    Write(inp.Row, inp.Col, FieldEditor.Shown(inp), inp.Fg, inp.Bg);
                    if (cursor >= 0) Console.SetCursorPosition(Math.Min(inp.Col + cursor, cols - 1), inp.Row);
                },
                msg =>
                {
                    // the message goes on the bottom line; clearing it restores what was there
                    int left = Console.CursorLeft, top = Console.CursorTop;
                    if (msg == null) DrawRow(req.Grid, msgRow);
                    else Write(msgRow, 0, (" " + msg).PadRight(cols), 15, 4);
                    Console.SetCursorPosition(left, top);
                },
                Bell);
        }
        finally
        {
            shown = null;   // fields were drawn behind the grid's back: next Present redraws
            shownScreenBg = req.Grid.ScreenBg;
        }
    }

    public void Bell() { try { Console.Beep(); } catch (PlatformNotSupportedException) { } }

    public void Shutdown(ScreenGrid grid, bool waitForUser)
    {
        Present(grid);
        // leave the cursor below what the program drew, in the console's own colors
        int last = 0;
        var lines = grid.TextLines();
        for (int r = 0; r < lines.Length; r++) if (lines[r].Length > 0) last = r;
        Console.ForegroundColor = startFg;
        Console.BackgroundColor = startBg;
        Console.SetCursorPosition(0, Math.Min(last + 1, Console.WindowHeight - 1));
        Console.WriteLine();
    }
}
