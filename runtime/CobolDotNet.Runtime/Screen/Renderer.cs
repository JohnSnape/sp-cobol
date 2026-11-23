// The screen model shared by every renderer.
//
// One rule keeps the console and Windows versions identical: the runtime owns
// the screen.  It keeps an 80x25 grid of character cells (ScreenGrid) and does
// all COBOL positioning, colors, BLANK SCREEN, ERASE etc. itself.  A renderer
// only (1) shows the grid and (2) lets the user edit the input fields of one
// ACCEPT, returning the key that ended it.  So a program can't do anything on
// one renderer that the other can't show.

using System;
using System.Collections.Generic;

namespace CobolDotNet.Runtime;

/// <summary>One character cell. Colors are COBOL colors: 0-7, +8 = bright (HIGHLIGHT).</summary>
public struct ScreenCell
{
    public char Ch;
    public byte Fg, Bg;
    public bool Underline;
}

/// <summary>The whole text screen. Row and column are 0-based here (COBOL LINE 1 = row 0).</summary>
public sealed class ScreenGrid
{
    public const int DefaultRows = 25, DefaultColumns = 80;
    public readonly int Rows, Columns;
    public readonly ScreenCell[,] Cells;
    /// <summary>Background of the last BLANK SCREEN: the renderer fills the window with it.</summary>
    public byte ScreenBg;
    public byte ScreenFg = 7;

    public ScreenGrid(int rows = DefaultRows, int columns = DefaultColumns)
    {
        Rows = rows; Columns = columns;
        Cells = new ScreenCell[rows, columns];
        Clear(7, 0);
    }

    public void Clear(byte fg, byte bg)
    {
        ScreenFg = fg; ScreenBg = bg;
        for (int r = 0; r < Rows; r++) EraseLine(r, 0, fg, bg);
    }

    public void EraseLine(int row, int col, byte fg, byte bg)
    {
        if (row < 0 || row >= Rows) return;
        for (int c = Math.Max(col, 0); c < Columns; c++)
            Cells[row, c] = new ScreenCell { Ch = ' ', Fg = fg, Bg = bg };
    }

    public void EraseToEnd(int row, int col, byte fg, byte bg)
    {
        EraseLine(row, col, fg, bg);
        for (int r = row + 1; r < Rows; r++) EraseLine(r, 0, fg, bg);
    }

    public void Put(int row, int col, string text, byte fg, byte bg, bool underline = false)
    {
        if (row < 0 || row >= Rows) return;
        for (int i = 0; i < text.Length; i++)
        {
            int c = col + i;
            if (c < 0) continue;
            if (c >= Columns) break;
            Cells[row, c] = new ScreenCell { Ch = text[i], Fg = fg, Bg = bg, Underline = underline };
        }
    }

    public void ScrollUp()
    {
        for (int r = 1; r < Rows; r++)
            for (int c = 0; c < Columns; c++) Cells[r - 1, c] = Cells[r, c];
        EraseLine(Rows - 1, 0, ScreenFg, ScreenBg);
    }

    public ScreenGrid Clone()
    {
        var g = new ScreenGrid(Rows, Columns) { ScreenBg = ScreenBg, ScreenFg = ScreenFg };
        Array.Copy(Cells, g.Cells, Cells.Length);
        return g;
    }

    /// <summary>The screen as plain text, trailing blanks trimmed (used by tests).</summary>
    public string[] TextLines()
    {
        var lines = new string[Rows];
        var buf = new char[Columns];
        for (int r = 0; r < Rows; r++)
        {
            for (int c = 0; c < Columns; c++) buf[c] = Cells[r, c].Ch == '\0' ? ' ' : Cells[r, c].Ch;
            lines[r] = new string(buf).TrimEnd();
        }
        return lines;
    }
}

/// <summary>An input field taking part in one ACCEPT.</summary>
public sealed class ScreenInput
{
    public int Row, Col, Length;     // 0-based position, width in characters
    /// <summary>Current contents, exactly Length characters. The renderer edits this.</summary>
    public string Text;
    public byte Fg, Bg;
    public bool Secure, Numeric, Required, Auto, Upper, Lower, Underline;
    public char PromptChar = '_';
    /// <summary>For messages: "Amount", "field at line 5, column 15".</summary>
    public string Description;
}

/// <summary>Everything a renderer needs for one ACCEPT.</summary>
public sealed class AcceptRequest
{
    public ScreenGrid Grid;                          // what's on the screen around the fields
    public IReadOnlyList<ScreenInput> Inputs;
    public int Focus;                                // index into Inputs; the renderer updates it
    public string Message;                           // a problem to show the user, or null
    /// <summary>True when the program has CRT STATUS, so function keys mean something.</summary>
    public bool FunctionKeys;
    /// <summary>
    /// Called when the user leaves a field. Returns the text to show (e.g. a number
    /// re-formatted through its PICTURE) or an error message for the user.
    /// </summary>
    public Func<ScreenInput, string, (string text, string error)> Check;
}

public interface IScreenRenderer
{
    /// <summary>Show the grid as it is now.</summary>
    void Present(ScreenGrid grid);

    /// <summary>Let the user edit the inputs. Returns a CobKey code (Enter = 0, F1 = 1001, Esc = 2005 ...).</summary>
    int Accept(AcceptRequest request);

    void Bell();

    /// <summary>
    /// True if plain DISPLAY / ACCEPT (no LINE/COL) should go on this screen even
    /// before the program uses SCREEN SECTION. A window has no console, so yes for Forms.
    /// </summary>
    bool OwnsConsoleOutput { get; }

    /// <summary>Program is ending. waitForUser: output was shown since the last ACCEPT.</summary>
    void Shutdown(ScreenGrid grid, bool waitForUser);
}
