// SCREEN SECTION data structures.  The generated code builds one CobScreen per
// screen item and links them with COB_SET_SCREEN (same shape as libcob's
// cob_screen, so GnuCOBOL's code generator output maps 1:1).

namespace CobolDotNet.Runtime;

/// <summary>One SCREEN SECTION item: a group, an input/output field, or a literal.</summary>
public sealed class CobScreen
{
    public CobScreen next, prev, child, parent;
    public CobField field;      // FIELD: the screen item's own storage (PIC ... USING/FROM/TO)
    public CobField value;      // VALUE: the literal
    public CobField line, column, foreg, backg, prompt;
    public int type;            // ScreenAttr.ITEM_*
    public int occurs;
    public long attr;           // ScreenAttr flags

    public override string ToString() => type switch
    {
        ScreenAttr.ITEM_GROUP => "screen group",
        ScreenAttr.ITEM_VALUE => $"literal \"{value}\"",
        _ => $"field \"{field}\"",
    };
}

/// <summary>Screen item attribute flags and types (values match libcob's common.h).</summary>
public static class ScreenAttr
{
    public const int ITEM_GROUP = 0, ITEM_FIELD = 1, ITEM_VALUE = 2, ITEM_ATTRIBUTE = 3;

    public const long LINE_PLUS = 1L << 0;
    public const long LINE_MINUS = 1L << 1;
    public const long COLUMN_PLUS = 1L << 2;
    public const long COLUMN_MINUS = 1L << 3;
    public const long AUTO = 1L << 4;
    public const long BELL = 1L << 5;
    public const long BLANK_LINE = 1L << 6;
    public const long BLANK_SCREEN = 1L << 7;
    public const long BLINK = 1L << 8;
    public const long ERASE_EOL = 1L << 9;
    public const long ERASE_EOS = 1L << 10;
    public const long FULL = 1L << 11;
    public const long HIGHLIGHT = 1L << 12;
    public const long LOWLIGHT = 1L << 13;
    public const long REQUIRED = 1L << 14;
    public const long REVERSE = 1L << 15;
    public const long SECURE = 1L << 16;
    public const long UNDERLINE = 1L << 17;
    public const long OVERLINE = 1L << 18;
    public const long PROMPT = 1L << 19;
    public const long UPDATE = 1L << 20;
    public const long INPUT = 1L << 21;
    public const long SCROLL_DOWN = 1L << 22;
    public const long INITIAL = 1L << 23;
    public const long NO_ECHO = 1L << 24;
    public const long LEFTLINE = 1L << 25;
    public const long NO_DISP = 1L << 26;
    public const long EMULATE_NL = 1L << 27;
    public const long UPPER = 1L << 28;
    public const long LOWER = 1L << 29;
}

/// <summary>CRT STATUS key codes (libcob values).</summary>
public static class CobKey
{
    public const int Enter = 0;
    public static int F(int n) => 1000 + n;     // F1 = 1001 ... F64 = 1064
    public const int PageUp = 2001;
    public const int PageDown = 2002;
    public const int Up = 2003;
    public const int Down = 2004;
    public const int Escape = 2005;
    public const int Print = 2006;
    public const int Tab = 2007;
    public const int BackTab = 2008;
    public const int NoField = 8000;            // ACCEPT of a screen with no input fields
}

public static partial class Cob
{
    public static void COB_SET_SCREEN(CobScreen s, int typ, long att, CobScreen nxt, CobScreen prv,
        CobScreen chld, CobScreen p, CobField fld, CobField val, CobField l, CobField c,
        CobField fg, CobField bg, CobField prmpt, int occ)
    {
        s.next = nxt; s.prev = prv; s.child = chld; s.parent = p;
        s.field = fld; s.value = val; s.line = l; s.column = c;
        s.foreg = fg; s.backg = bg; s.prompt = prmpt;
        s.type = typ; s.occurs = occ; s.attr = att;
    }
}
