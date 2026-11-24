// Screen I/O engine: SCREEN SECTION, DISPLAY/ACCEPT ... LINE/COL, and plain ACCEPT.
// Positioning rules follow GnuCOBOL's screenio.c so programs lay out the same;
// the code is written fresh against the ScreenGrid model.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using static CobolDotNet.Runtime.CobConst;
using static CobolDotNet.Runtime.ScreenAttr;

namespace CobolDotNet.Runtime;

public static partial class Cob
{
    const int EC_IMP_ACCEPT = 0x0601;

    static IScreenRenderer renderer;
    static ScreenGrid grid;
    static int dispRow, dispCol;      // where the last DISPLAY ended (0-based)
    static int accRow, accCol;        // where the last ACCEPT ended
    static int ttyRow = -1;           // next line for plain DISPLAY once the screen is in use
    static bool shownSinceAccept;

    /// <summary>
    /// The screen renderer. Set it before the program starts to choose one
    /// (the Windows host sets a FormsRenderer). If it isn't set, COB_SCREEN picks:
    /// "console" (default), "forms" (needs CobolDotNet.Forms), or "script" (tests).
    /// </summary>
    public static IScreenRenderer ScreenRenderer
    {
        get => renderer ??= CreateRenderer();
        set => renderer = value;
    }

    static IScreenRenderer CreateRenderer()
    {
        string mode = (Environment.GetEnvironmentVariable("COB_SCREEN") ?? "console").ToLowerInvariant();
        switch (mode)
        {
            case "script":
                return new ScriptRenderer(Environment.GetEnvironmentVariable("COB_SCREEN_KEYS"));
            case "forms":
                var t = Type.GetType("CobolDotNet.Forms.FormsRenderer, CobolDotNet.Forms");
                if (t == null)
                    throw new CobRuntimeException(
                        "COB_SCREEN=forms, but CobolDotNet.Forms.dll isn't next to the program. " +
                        "Build the Windows version of the project, or set COB_SCREEN=console.");
                return (IScreenRenderer)Activator.CreateInstance(t, new object[] { null });
            default:
                return new ConsoleRenderer();
        }
    }

    static bool ScreenActive => grid != null;

    static ScreenGrid Grid
    {
        get
        {
            if (grid == null)
            {
                grid = new ScreenGrid();
                ScreenRenderer.Present(grid);   // starting screen I/O clears the screen (like curses)
            }
            return grid;
        }
    }

    /// <summary>True when plain DISPLAY/ACCEPT must go on the screen rather than stdout/stdin.</summary>
    static bool PlainIoOnScreen => ScreenActive || (renderer?.OwnsConsoleOutput ?? false);

    internal static void ScreenShutdown()
    {
        if (renderer == null || (grid == null && !renderer.OwnsConsoleOutput)) return;
        renderer.Shutdown(Grid, shownSinceAccept);
    }

    // ------------------------------------------------------------ positions

    static int Int(CobField f) => f == null ? 0 : cob_get_int(f);

    /// <summary>LINE/COL operands -> 0-based row/col (GnuCOBOL's extract_line_and_col_vals).</summary>
    static (int row, int col) Position(CobField line, CobField column, bool accept)
    {
        int lastRow = accept ? accRow : dispRow, lastCol = accept ? accCol : dispCol;
        int l, c;
        if (column == null)
        {
            if (line == null) return (0, 0);
            if (line.size < 4) { l = Int(line); c = 1; }
            else
            {
                // AT 0510: one number holding line and column (LLCC or LLLCCC)
                int v = Int(line), w = line.size >= 6 ? 1000 : 100;
                l = v / w; c = v % w;
            }
        }
        else { l = line == null ? 1 : Int(line); c = Int(column); }

        if (l == 0 && c == 0) return (lastRow, lastCol);
        if (l == 0) return (lastRow + 1, c - 1);
        if (c == 0) return (l - 1, lastCol);
        return (l - 1, c - 1);
    }

    static bool IsFirstItem(CobScreen s)
    {
        for (; s != null; s = s.parent) if (s.prev != null) return false;
        return true;
    }

    static CobScreen LastChild(CobScreen s)
    {
        var c = s.child;
        while (c.next != null) c = c.next;
        return c.child != null ? LastChild(c) : c;
    }

    static CobScreen PrevItem(CobScreen s) =>
        s.prev != null ? (s.prev.child != null ? LastChild(s.prev) : s.prev) : s.parent;

    static int ItemSize(CobScreen s) => (int)(s.field?.size ?? s.value?.size ?? 0);

    static void Adjust(CobScreen s, CobField operand, long plus, long minus, ref int count, ref bool found)
    {
        if ((s.attr & plus) != 0) count += Int(operand);
        else if ((s.attr & minus) != 0) count -= Int(operand);
        else { count += Int(operand) - 1; found = true; }
    }

    /// <summary>Where a screen item goes (GnuCOBOL's get_screen_item_line_and_col).</summary>
    static (int row, int col) ItemPosition(CobScreen s, int originRow, int originCol)
    {
        int line = 0, col = 0;
        bool foundLine = false, foundCol = false, self = true;
        for (var p = s; p != null; p = PrevItem(p))
        {
            if (p.line != null && !foundLine) Adjust(p, p.line, LINE_PLUS, LINE_MINUS, ref line, ref foundLine);
            if (!foundCol)
            {
                bool elementary = p.child == null;
                if (!self && elementary) col += ItemSize(p) - 1;
                if (p.column != null) Adjust(p, p.column, COLUMN_PLUS, COLUMN_MINUS, ref col, ref foundCol);
                if (p.line != null && p.column == null) foundCol = true;
                if (!foundCol && p.column == null && elementary && !IsFirstItem(p)) col++;
            }
            self = false;
        }
        return (line + originRow, col + originCol);
    }

    // ------------------------------------------------------------ colors

    static (byte fg, byte bg) Colors(CobField fgf, CobField bgf, long attr)
    {
        // no color given: use the screen's current colors (set by the last BLANK SCREEN)
        int fg = fgf != null ? Int(fgf) & 7 : (grid?.ScreenFg ?? 7) & 7;
        int bg = bgf != null ? Int(bgf) & 7 : (grid?.ScreenBg ?? 0) & 7;
        if ((attr & HIGHLIGHT) != 0) fg |= 8;
        if ((attr & REVERSE) != 0) (fg, bg) = (bg, fg & 7);
        return ((byte)fg, (byte)bg);
    }

    // ------------------------------------------------------------ field text

    static string Latin1(ReadOnlySpan<byte> b)
    {
        var c = new char[b.Length];
        for (int i = 0; i < b.Length; i++) c[i] = (char)b[i];
        return new string(c);
    }

    /// <summary>How a data item looks on the screen.</summary>
    static string ScreenText(CobField f)
    {
        int t = f.Type;
        if (IsNumeric(t) && t != TYPE_NUMERIC_DISPLAY) return Num.Pretty(f);
        return Latin1(f.Span);
    }

    static bool IsNumericItem(CobField f) => IsNumeric(f.Type) || f.Type == TYPE_NUMERIC_EDITED;

    /// <summary>
    /// Check what the user typed into a numeric field. Returns the text re-formatted
    /// through the field's PICTURE, or a plain-English problem.
    /// </summary>
    static (string text, string error) CheckNumber(CobField f, string typed)
    {
        var sb = new StringBuilder();
        bool neg = false, point = false, digits = false;
        string s = typed.Trim();
        if (s.EndsWith("CR", StringComparison.OrdinalIgnoreCase) || s.EndsWith("DB", StringComparison.OrdinalIgnoreCase))
        { neg = true; s = s.Substring(0, s.Length - 2).TrimEnd(); }
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (char.IsAsciiDigit(ch)) { sb.Append(ch); digits = true; }
            else if (ch == DecimalPoint)
            {
                if (point) return (null, "That number has two decimal points.");
                point = true; sb.Append('.');
            }
            else if (ch == NumericSeparator || ch == ' ' || ch == CurrencySymbol) { }
            else if ((ch == '-' || ch == '+') && (i == 0 || i == s.Length - 1)) { if (ch == '-') neg = true; }
            else return (null, $"\"{typed.Trim()}\" isn't a number.");
        }
        if (!digits) sb.Append('0');

        var (v, scale) = Num.ParseText(Encoding.Latin1.GetBytes(sb.ToString()));
        if (neg) v = -v;
        if (v < 0 && !f.Has(FLAG_HAVE_SIGN) && f.Type != TYPE_NUMERIC_EDITED)
            return (null, "This field can't hold a negative number.");
        int fs = f.attr.scale, fd = f.attr.digits;
        if (scale > fs && fs >= 0)
        {
            var drop = BigInteger.Pow(10, scale - fs);
            if (v % drop != 0)
                return (null, fs == 0 ? "This field holds whole numbers only."
                                      : $"This field holds only {fs} digit{(fs == 1 ? "" : "s")} after the decimal point.");
        }
        int intDigits = BigInteger.Abs(v).ToString().Length - scale;
        if (fd > 0 && intDigits > fd - Math.Max(fs, 0) && v != 0)
            return (null, $"That number is too big for this field (at most {fd - Math.Max(fs, 0)} digits before the point).");

        // store into a scratch copy of the field to get its PICTURE formatting
        var tmp = new CobField(f.size, new CobPtr(f.size), f.attr);
        Num.Store(tmp, v, scale, 0);
        return (ScreenText(tmp), null);
    }

    static void StoreTyped(CobField f, string text)
    {
        if (IsNumericItem(f))
        {
            var (shown, error) = CheckNumber(f, text);
            if (error != null) return;              // leave the old value
            var src = new CobField(shown.Length, CobPtr.FromLatin1(shown),
                new CobFieldAttr(TYPE_ALPHANUMERIC, 0, 0, 0, null));
            cob_move(src, f);
        }
        else
        {
            var d = f.Span;
            for (int i = 0; i < d.Length; i++) d[i] = i < text.Length ? (byte)text[i] : (byte)' ';
        }
    }

    // ------------------------------------------------------------ CRT STATUS

    static void SetCrtStatus(int code)
    {
        if (code != 0) SetException(EC_IMP_ACCEPT);
        var st = Global.cob_current_module?.crt_status;
        if (st == null) return;
        if (IsNumeric(st.Type)) cob_set_int(st, code);
        else
        {
            var txt = code.ToString("D4");
            var d = st.Span;
            for (int i = 0; i < d.Length && i < 4; i++) d[i] = (byte)txt[i];
        }
    }

    // ------------------------------------------------------------ SCREEN SECTION

    sealed class Placed
    {
        public CobScreen Item;
        public int Row, Col;
        public ScreenInput Input;
    }

    /// <summary>Walk a screen, drawing every item; collects the input fields.</summary>
    static void Lay(CobScreen s, int oRow, int oCol, bool accept, List<Placed> inputs)
    {
        switch (s.type)
        {
            case ITEM_GROUP:
                if ((s.attr & BLANK_SCREEN) != 0 && !accept)
                {
                    var (fg, bg) = Colors(s.foreg, s.backg, s.attr & ~REVERSE);
                    Grid.Clear(fg, bg);
                }
                for (var c = s.child; c != null; c = c.next) Lay(c, oRow, oCol, accept, inputs);
                break;
            case ITEM_FIELD:
            case ITEM_VALUE:
            {
                var (row, col) = ItemPosition(s, oRow, oCol);
                var (fg, bg) = Colors(s.foreg, s.backg, s.attr);
                if (!accept)
                {
                    if ((s.attr & BLANK_LINE) != 0) Grid.EraseLine(row, 0, fg, bg);
                    if ((s.attr & ERASE_EOL) != 0) Grid.EraseLine(row, col, fg, bg);
                    if ((s.attr & ERASE_EOS) != 0) Grid.EraseToEnd(row, col, fg, bg);
                }
                bool underline = (s.attr & UNDERLINE) != 0;
                if (s.type == ITEM_VALUE)
                {
                    string lit = Latin1(s.value.Span);
                    int times = Math.Max(s.occurs, 1);
                    for (int i = 0; i < times; i++) Grid.Put(row, col + i * lit.Length, lit, fg, bg, underline);
                    if (accept) { accRow = row; accCol = col + lit.Length * times; }
                    else { dispRow = row; dispCol = col + lit.Length * times; }
                    break;
                }
                bool input = (s.attr & INPUT) != 0;
                string text = (s.attr & NO_DISP) != 0 ? new string(' ', (int)s.field.size) : ScreenText(s.field);
                if ((s.attr & SECURE) != 0) text = new string(' ', text.Length);
                if (!accept || !input) Grid.Put(row, col, text, fg, bg, underline);
                if (accept && input)
                {
                    inputs.Add(new Placed
                    {
                        Item = s, Row = row, Col = col,
                        Input = new ScreenInput
                        {
                            Row = row, Col = col, Length = (int)s.field.size, Text = Latin1(s.field.Span),
                            Fg = fg, Bg = bg,
                            Secure = (s.attr & SECURE) != 0,
                            Numeric = IsNumericItem(s.field),
                            Required = (s.attr & REQUIRED) != 0,
                            Auto = (s.attr & AUTO) != 0,
                            Upper = (s.attr & UPPER) != 0,
                            Lower = (s.attr & LOWER) != 0,
                            Underline = underline,
                            PromptChar = s.prompt != null && s.prompt.size > 0 ? (char)s.prompt.data[0] : '_',
                            Description = $"line {row + 1}, column {col + 1}",
                        },
                    });
                }
                if (accept) { accRow = row; accCol = col + (int)s.field.size; }
                else { dispRow = row; dispCol = col + (int)s.field.size; }
                break;
            }
        }
    }

    /// <summary>DISPLAY screen-name [AT ...]</summary>
    public static void cob_screen_display(CobScreen s, CobField line, CobField column, int zero_line_col_allowed)
    {
        var (row, col) = Position(line, column, false);
        Lay(s, row, col, false, null);
        ScreenRenderer.Present(Grid);
        shownSinceAccept = true;
        ttyRow = Math.Max(ttyRow, dispRow + 1);
    }

    /// <summary>ACCEPT screen-name [AT ...]</summary>
    public static void cob_screen_accept(CobScreen s, CobField line, CobField column, CobField timeout,
                                         int zero_line_col_allowed)
    {
        Global.cob_exception_code = 0;
        var (row, col) = Position(line, column, true);
        var placed = new List<Placed>();
        Lay(s, row, col, true, placed);
        if (placed.Count == 0) { SetCrtStatus(CobKey.NoField); return; }

        // fields are visited top to bottom, left to right (as GnuCOBOL does)
        placed.Sort((a, b) => a.Row != b.Row ? a.Row.CompareTo(b.Row) : a.Col.CompareTo(b.Col));
        int focus = placed.FindIndex(p => (p.Item.attr & INITIAL) != 0);
        int key = RunAccept(placed.ConvertAll(p => p.Input), placed.ConvertAll(p => p.Item.field), Math.Max(focus, 0));
        ttyRow = Math.Max(ttyRow, accRow + 1);
        SetCrtStatus(key);
    }

    /// <summary>The ACCEPT loop shared by screens and single fields: edit, then check REQUIRED.</summary>
    static int RunAccept(List<ScreenInput> inputs, List<CobField> fields, int focus)
    {
        var req = new AcceptRequest
        {
            Grid = Grid,
            Inputs = inputs,
            Focus = focus,
            FunctionKeys = Global.cob_current_module?.crt_status != null,
        };
        req.Check = (inp, text) =>
        {
            int i = inputs.IndexOf(inp);
            var f = fields[i];
            if (inp.Numeric)
            {
                if (text.Trim().Length == 0 && !inp.Required) return (CheckNumber(f, "0").text, null);
                var (shown, error) = CheckNumber(f, text);
                return error != null ? (null, error) : (shown.PadRight(inp.Length).Substring(0, inp.Length), null);
            }
            return (text, null);
        };

        int key;
        while (true)
        {
            key = ScreenRenderer.Accept(req);
            if (key != CobKey.Enter) break;
            int missing = inputs.FindIndex(i => i.Required && i.Text.Trim().Length == 0);
            if (missing < 0) break;
            ScreenRenderer.Bell();
            req.Focus = missing;
            req.Message = "Please fill in this field.";
        }

        for (int i = 0; i < inputs.Count; i++)
        {
            StoreTyped(fields[i], inputs[i].Text);
            var inp = inputs[i];
            string text = inp.Secure ? new string(' ', inp.Length) : inp.Text;
            Grid.Put(inp.Row, inp.Col, text, inp.Fg, inp.Bg, inp.Underline);
        }
        ScreenRenderer.Present(Grid);
        shownSinceAccept = false;
        return key;
    }

    // ------------------------------------------------------------ DISPLAY / ACCEPT field AT

    struct FieldOptions
    {
        public CobField Line, Column, Fg, Bg, Prompt;
    }

    static FieldOptions ParseOptions(string parms, CobField[] args)
    {
        var o = new FieldOptions();
        for (int i = 0; i < parms.Length && i < args.Length; i++)
        {
            switch (parms[i])
            {
                case 'p': case 'l': o.Line = args[i]; break;
                case 'c': o.Column = args[i]; break;
                case 'f': o.Fg = args[i]; break;
                case 'b': o.Bg = args[i]; break;
                case 'i': o.Prompt = args[i]; break;
                // s scroll, t timeout, S size, C control, L color, u cursor: not used yet
            }
        }
        return o;
    }

    /// <summary>DISPLAY item AT/LINE/COL/WITH ...</summary>
    public static void cob_display_field(CobField f, long fattr, string parms, params CobField[] args)
    {
        var o = ParseOptions(parms, args);
        var (row, col) = Position(o.Line, o.Column, false);
        var (fg, bg) = Colors(o.Fg, o.Bg, fattr);
        var g = Grid;
        if ((fattr & BLANK_SCREEN) != 0) g.Clear(fg, bg);
        if ((fattr & BLANK_LINE) != 0) g.EraseLine(row, 0, fg, bg);
        if ((fattr & ERASE_EOL) != 0) g.EraseLine(row, col, fg, bg);
        if ((fattr & ERASE_EOS) != 0) g.EraseToEnd(row, col, fg, bg);
        string text = ScreenText(f);
        g.Put(row, col, text, fg, bg, (fattr & UNDERLINE) != 0);
        dispRow = row; dispCol = col + text.Length;
        ttyRow = Math.Max(ttyRow, row + 1);
        ScreenRenderer.Present(g);
        shownSinceAccept = true;
    }

    /// <summary>ACCEPT item AT/LINE/COL/WITH ...</summary>
    public static void cob_accept_field(CobField f, long fattr, string parms, params CobField[] args)
    {
        Global.cob_exception_code = 0;
        var o = ParseOptions(parms, args);
        var (row, col) = Position(o.Line, o.Column, true);
        int key = AcceptOneField(f, fattr, row, col, o);
        accRow = row; accCol = col + (int)f.size;
        ttyRow = Math.Max(ttyRow, row + 1);
        SetCrtStatus(key);
    }

    static int AcceptOneField(CobField f, long fattr, int row, int col, FieldOptions o)
    {
        var (fg, bg) = Colors(o.Fg, o.Bg, fattr);
        int len = (int)Math.Min(f.size, Grid.Columns - Math.Max(col, 0));
        var inp = new ScreenInput
        {
            Row = row, Col = col, Length = Math.Max(len, 1),
            // WITH UPDATE starts from the current value; otherwise the field starts empty
            Text = (fattr & UPDATE) != 0 ? Latin1(f.Span).PadRight(len).Substring(0, len) : new string(' ', len),
            Fg = fg, Bg = bg,
            Secure = (fattr & SECURE) != 0,
            Numeric = IsNumericItem(f),
            Required = (fattr & REQUIRED) != 0,
            Auto = (fattr & AUTO) != 0,
            Upper = (fattr & UPPER) != 0,
            Lower = (fattr & LOWER) != 0,
            Underline = (fattr & UNDERLINE) != 0,
            PromptChar = o.Prompt != null && o.Prompt.size > 0 ? (char)o.Prompt.data[0] : '_',
            Description = $"line {row + 1}, column {col + 1}",
        };
        return RunAccept(new List<ScreenInput> { inp }, new List<CobField> { f }, 0);
    }

    // ------------------------------------------------------------ plain DISPLAY / ACCEPT on the screen

    static int ttyCol;

    /// <summary>Plain DISPLAY while the screen is in use: next line, like a terminal.</summary>
    static void DisplayOnScreen(CobField[] fields, bool newline)
    {
        var g = Grid;
        if (ttyRow < 0) ttyRow = 0;
        var sb = new StringBuilder();
        foreach (var f in fields) if (f != null) sb.Append(IsNumeric(f.Type) ? Num.Pretty(f) : IsNational(f.Type) ? NationalText(f) : Latin1(f.Span));
        string text = sb.ToString();
        while (true)
        {
            if (ttyRow >= g.Rows) { g.ScrollUp(); ttyRow = g.Rows - 1; }
            int room = g.Columns - ttyCol;
            g.Put(ttyRow, ttyCol, text.Length > room ? text.Substring(0, room) : text, g.ScreenFg, g.ScreenBg);
            if (text.Length <= room) { ttyCol += text.Length; break; }
            text = text.Substring(room);
            ttyRow++; ttyCol = 0;
        }
        dispRow = ttyRow; dispCol = ttyCol;
        if (newline) { ttyRow++; ttyCol = 0; }
        ScreenRenderer.Present(g);
        shownSinceAccept = true;
    }

    static void AcceptOnScreen(CobField f)
    {
        var g = Grid;
        if (ttyRow < 0) ttyRow = 0;
        if (ttyRow >= g.Rows) { g.ScrollUp(); ttyRow = g.Rows - 1; }
        int key = AcceptOneField(f, 0, ttyRow, ttyCol, default);
        ttyRow++; ttyCol = 0;
        SetCrtStatus(key);
    }

    /// <summary>Plain ACCEPT item: a line from the keyboard (or stdin).</summary>
    public static void cob_accept(CobField f)
    {
        if (PlainIoOnScreen) { AcceptOnScreen(f); return; }
        Out.Flush();
        string line = Console.In.ReadLine();
        if (line == null)
        {
            SetException(EC_IMP_ACCEPT);     // end of input: the field is blanked (as GnuCOBOL does)
            line = "";
        }
        if (IsNumericItem(f))
        {
            var src = new CobField(line.Length, CobPtr.FromLatin1(line), new CobFieldAttr(TYPE_ALPHANUMERIC, 0, 0, 0, null));
            cob_move(src, f);
        }
        else
        {
            var d = f.Span;
            for (int i = 0; i < d.Length; i++) d[i] = i < line.Length ? (byte)Math.Min((int)line[i], 255) : (byte)' ';
        }
    }
}
