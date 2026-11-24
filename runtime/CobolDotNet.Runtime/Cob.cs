// The runtime API that generated code calls.  Names deliberately match
// libcob's C functions (cob_move, cob_display, ...) because the C# comes
// from GnuCOBOL's code generator; behavior is written fresh in C#.

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using static CobolDotNet.Runtime.CobConst;

namespace CobolDotNet.Runtime;

public static partial class Cob
{
    // ------------------------------------------------------------ state

    public static readonly CobGlobal Global = new();
    static readonly Stack<CobModule> ModuleStack = new();
    static string[] Args = Array.Empty<string>();

    internal static char DecimalPoint => ModuleStack.Count > 0 ? ModuleStack.Peek().decimal_point : '.';
    internal static char NumericSeparator => ModuleStack.Count > 0 ? ModuleStack.Peek().numeric_separator : ',';
    internal static char CurrencySymbol => ModuleStack.Count > 0 ? ModuleStack.Peek().currency_symbol : '$';

    internal static void SetException(int code) => Global.cob_exception_code = code;

    public const int COB_FERROR_NONE = 0;
    public const int COB_FERROR_CANCEL = 1;
    public const int COB_FERROR_INITIALIZED = 2;
    public const int COB_FERROR_CODEGEN = 3;
    public const int COB_FERROR_CHAINING = 4;
    public const int COB_FERROR_STACK = 5;
    public const int COB_FERROR_GLOBAL = 6;
    public const int COB_FERROR_MEMORY = 7;
    public const int COB_FERROR_MODULE = 8;
    public const int COB_FERROR_RECURSIVE = 9;

    // ------------------------------------------------------------ C-isms

    public static bool unlikely(bool b) => b;
    public static bool likely(bool b) => b;

    public static Delegate cob_funcptr(Func<int> f) => f;
    public static Delegate cob_funcptr(Func<int, int> f) => f;

    // libcob's byte-swap macros: COMP (big-endian) fields read and written in place
    public static short COB_BSWAP_16(short v) => System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(v);
    public static ushort COB_BSWAP_16(ushort v) => System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(v);
    public static int COB_BSWAP_32(int v) => System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(v);
    public static uint COB_BSWAP_32(uint v) => System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(v);
    public static long COB_BSWAP_64(long v) => System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(v);
    public static ulong COB_BSWAP_64(ulong v) => System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(v);

    // ------------------------------------------------------------ lifecycle

    public static void cob_init(string[] argv)
    {
        Args = argv ?? Array.Empty<string>();
        Global.cob_main_argv0 = Environment.ProcessPath;
    }

    public static bool cob_module_global_enter(ref CobModule module, ref CobGlobal glob,
                                               int auto_init, int entry, object name_hash)
    {
        glob ??= Global;
        module ??= new CobModule();
        ModuleStack.Push(module);
        Global.cob_current_module = module;
        return false;
    }

    public static void cob_module_leave(CobModule module)
    {
        if (ModuleStack.Count > 0) ModuleStack.Pop();
        Global.cob_current_module = ModuleStack.Count > 0 ? ModuleStack.Peek() : null;
    }

    public static void cob_module_free(ref CobModule module) => module = null;
    public static void cob_set_cancel(CobModule module) { }

    public static void cob_stop_run(int status)
    {
        Out.Flush();
        CloseAllFiles();
        ScreenShutdown();
        Err.Flush();
        Environment.Exit(status);
    }

    public static void cob_fatal_error(int code)
    {
        if (code == COB_FERROR_FILE) FileFatalError();   // libcob's message, implicit CLOSEs, exit 1
        string msg = code switch
        {
            COB_FERROR_CANCEL => "attempt to CANCEL an active program",
            COB_FERROR_CODEGEN => "internal code generation error (please report this program)",
            COB_FERROR_STACK => "PERFORM nesting is too deep (stack overflow)",
            _ => $"fatal runtime error {code}",
        };
        throw new CobRuntimeException(msg);
    }

    // ------------------------------------------------------------ decimals

    public static void cob_decimal_alloc(int n, ref CobDecimal a) { a ??= new(); }
    public static void cob_decimal_alloc(int n, ref CobDecimal a, ref CobDecimal b) { a ??= new(); b ??= new(); }
    public static void cob_decimal_alloc(int n, ref CobDecimal a, ref CobDecimal b, ref CobDecimal c) { a ??= new(); b ??= new(); c ??= new(); }
    public static void cob_decimal_alloc(int n, ref CobDecimal a, ref CobDecimal b, ref CobDecimal c, ref CobDecimal d) { a ??= new(); b ??= new(); c ??= new(); d ??= new(); }
    public static void cob_decimal_alloc(int n, ref CobDecimal a, ref CobDecimal b, ref CobDecimal c, ref CobDecimal d, ref CobDecimal e) { a ??= new(); b ??= new(); c ??= new(); d ??= new(); e ??= new(); }
    public static void cob_decimal_alloc(int n, ref CobDecimal a, ref CobDecimal b, ref CobDecimal c, ref CobDecimal d, ref CobDecimal e, ref CobDecimal f) { a ??= new(); b ??= new(); c ??= new(); d ??= new(); e ??= new(); f ??= new(); }

    public static void cob_decimal_set_field(CobDecimal d, CobField f) => (d.value, d.scale) = Num.Get(f);
    public static int cob_decimal_get_field(CobDecimal d, CobField f, int opt) => Num.Store(f, d.value, d.scale, opt);
    public static void cob_decimal_init(CobDecimal d) { d.value = 0; d.scale = 0; }
    public static void cob_decimal_clear(CobDecimal d) { if (d != null) { d.value = 0; d.scale = 0; } }
    public static void cob_decimal_set_llint(CobDecimal d, long n) { d.value = n; d.scale = 0; }
    public static void cob_decimal_set_ullint(CobDecimal d, ulong n) { d.value = n; d.scale = 0; }

    static void Align(CobDecimal a, CobDecimal b)
    {
        if (a.scale < b.scale) { a.value *= Num.Pow10(b.scale - a.scale); a.scale = b.scale; }
        else if (b.scale < a.scale) { b.value *= Num.Pow10(a.scale - b.scale); b.scale = a.scale; }
    }

    public static void cob_decimal_add(CobDecimal a, CobDecimal b) { Align(a, b); a.value += b.value; }
    public static void cob_decimal_sub(CobDecimal a, CobDecimal b) { Align(a, b); a.value -= b.value; }
    public static void cob_decimal_mul(CobDecimal a, CobDecimal b) { a.value *= b.value; a.scale += b.scale; }

    public static void cob_decimal_div(CobDecimal a, CobDecimal b)
    {
        if (b.value.IsZero)
        {
            SetException(CobException.EC_SIZE_ZERO_DIVIDE);
            a.value = 0; a.scale = 0;
            return;
        }
        const int extra = 40; // plenty of fraction digits before the store rounds/truncates
        a.value = a.value * Num.Pow10(extra) / b.value;
        a.scale = a.scale + extra - b.scale;
    }

    public static int cob_decimal_cmp(CobDecimal a, CobDecimal b)
    {
        var x = new CobDecimal { value = a.value, scale = a.scale };
        var y = new CobDecimal { value = b.value, scale = b.scale };
        Align(x, y);
        return x.value.CompareTo(y.value);
    }

    // ------------------------------------------------------------ arithmetic shortcuts

    public static int cob_add_int(CobField f, int n, int opt)
    {
        var (v, s) = Num.Get(f);
        return Num.Store(f, v + n * Num.Pow10(s), s, opt);
    }

    public static int cob_sub_int(CobField f, int n, int opt) => cob_add_int(f, -n, opt);

    public static int cob_add(CobField f1, CobField f2, int opt)
    {
        var (a, sa) = Num.Get(f1);
        var (b, sb) = Num.Get(f2);
        int s = Math.Max(sa, sb);
        return Num.Store(f1, Num.Rescale(a, sa, s, false) + Num.Rescale(b, sb, s, false), s, opt);
    }

    public static int cob_sub(CobField f1, CobField f2, int opt)
    {
        var (a, sa) = Num.Get(f1);
        var (b, sb) = Num.Get(f2);
        int s = Math.Max(sa, sb);
        return Num.Store(f1, Num.Rescale(a, sa, s, false) - Num.Rescale(b, sb, s, false), s, opt);
    }

    public static void cob_set_int(CobField f, int n) => Num.Store(f, n, 0, 0);
    public static int cob_get_int(CobField f)
    {
        var (v, s) = Num.Get(f);
        return (int)Num.Rescale(v, s, 0, false);
    }

    public static long cob_get_llint(CobField f)
    {
        var (v, s) = Num.Get(f);
        return (long)Num.Rescale(v, s, 0, false);
    }

    public static void cob_set_packed_zero(CobField f)
    {
        var s = f.Span;
        s.Clear();
        if (!f.Has(FLAG_NO_SIGN_NIBBLE))
            s[s.Length - 1] = (byte)(f.Has(FLAG_HAVE_SIGN) ? 0x0C : 0x0F);
    }

    // ------------------------------------------------------------ compares

    public static int cob_cmp_numdisp(CobPtr data, long size, long n, long has_sign)
    {
        BigInteger v = BigInteger.Zero;
        bool neg = false;
        for (int i = 0; i < size; i++)
        {
            byte b = data[i];
            if (has_sign != 0 && i == size - 1 && b >= (byte)'p' && b <= (byte)'y') { neg = true; b -= 0x40; }
            v = v * 10 + (b >= (byte)'0' && b <= (byte)'9' ? b - '0' : 0);
        }
        if (neg) v = -v;
        return v.CompareTo(new BigInteger(n));
    }

    /// <summary>Value of an unsigned USAGE DISPLAY integer (optimizer shortcut).</summary>
    public static int cob_get_numdisp(CobPtr data, long size)
    {
        int v = 0;
        for (int i = 0; i < size; i++)
        {
            byte b = data[i];
            v = v * 10 + (b >= (byte)'0' && b <= (byte)'9' ? b - '0' : (b & 0x0F) % 10);
        }
        return v;
    }

    public static int cob_cmp_llint(CobField f, long n)
    {
        var (v, s) = Num.Get(f);
        return v.CompareTo(new BigInteger(n) * Num.Pow10(s));
    }

    public static int cob_cmp_int(CobField f, int n) => cob_cmp_llint(f, n);

    public static int cob_cmp(CobField f1, CobField f2)
    {
        bool n1 = IsNumeric(f1.Type), n2 = IsNumeric(f2.Type);
        if (n1 && n2)
        {
            var (a, sa) = Num.Get(f1);
            var (b, sb) = Num.Get(f2);
            int s = Math.Max(sa, sb);
            return Num.Rescale(a, sa, s, false).CompareTo(Num.Rescale(b, sb, s, false));
        }
        // alphanumeric compare: shorter operand padded with spaces
        var x = f1.Span; var y = f2.Span;
        int len = Math.Max(x.Length, y.Length);
        for (int i = 0; i < len; i++)
        {
            int cx = i < x.Length ? x[i] : ' ';
            int cy = i < y.Length ? y[i] : ' ';
            if (cx != cy) return cx < cy ? -1 : 1;
        }
        return 0;
    }

    // ------------------------------------------------------------ memory

    public static void memcpy(CobPtr dst, string src, long n)
    {
        for (int i = 0; i < n; i++) dst[i] = (byte)src[i];
    }

    public static void memcpy(CobPtr dst, CobPtr src, long n) => src.Span(n).CopyTo(dst.Span(n));
    public static void memmove(CobPtr dst, CobPtr src, long n) => src.Span(n).CopyTo(dst.Span(n));
    public static void memset(CobPtr dst, int c, long n) => dst.Span(n).Fill((byte)c);

    public static int memcmp(CobPtr a, string b, long n)
    {
        for (int i = 0; i < n; i++) { int d = a[i] - (byte)b[i]; if (d != 0) return d; }
        return 0;
    }

    public static int memcmp(CobPtr a, CobPtr b, long n) => a.Span(n).SequenceCompareTo(b.Span(n));

    public static CobField COB_SET_FLD(CobField f, long size, CobPtr data, CobFieldAttr attr)
    {
        f.size = size; f.data = data; f.attr = attr;
        return f;
    }

    // ------------------------------------------------------------ MOVE

    public static void cob_move(CobField src, CobField dst)
    {
        if (ReferenceEquals(src, dst) || dst.size == 0) return;
        int st = src.Type, dt = dst.Type;

        if (st == TYPE_ALPHANUMERIC_ALL) { MoveAll(src, dst); return; }
        if (st == TYPE_GROUP || dt == TYPE_GROUP) { MoveAlnum(src.Span, dst); return; }

        bool srcNum = IsNumeric(st) || st == TYPE_NUMERIC_EDITED;
        bool dstNum = IsNumeric(dt) || dt == TYPE_NUMERIC_EDITED;

        if (dstNum)
        {
            var (v, s) = Num.Get(src);  // alphanumeric sources are parsed as text
            Num.Store(dst, v, s, 0);    // MOVE truncates, never rounds
            return;
        }
        if (srcNum && st != TYPE_NUMERIC_EDITED)
        {
            // numeric -> alphanumeric: the digits, unsigned, no point
            var (v, s) = Num.Get(src);
            int digits = Math.Max(src.attr.digits, 1);
            var text = BigInteger.Abs(v).ToString().PadLeft(digits, '0');
            if (text.Length > digits) text = text.Substring(text.Length - digits);
            MoveAlnum(CobPtr.FromLatin1(text).Span(text.Length), dst);
            return;
        }
        MoveAlnum(src.Span, dst);
    }

    static void MoveAlnum(ReadOnlySpan<byte> s, CobField dst)
    {
        var d = dst.Span;
        bool just = dst.Has(FLAG_JUSTIFIED);
        if (s.Length >= d.Length)
        {
            (just ? s.Slice(s.Length - d.Length) : s.Slice(0, d.Length)).CopyTo(d);
        }
        else if (just)
        {
            d.Slice(0, d.Length - s.Length).Fill((byte)' ');
            s.CopyTo(d.Slice(d.Length - s.Length));
        }
        else
        {
            s.CopyTo(d);
            d.Slice(s.Length).Fill((byte)' ');
        }
    }

    static void MoveAll(CobField src, CobField dst)
    {
        var pat = src.Span;
        int dt = dst.Type;
        if (IsNumeric(dt) || dt == TYPE_NUMERIC_EDITED)
        {
            // ALL "5" / ZERO into a number: the pattern repeated over its digits
            int digits = Math.Max(dst.attr.digits, 1);
            var buf = new byte[digits];
            for (int i = 0; i < digits; i++) buf[i] = pat[i % pat.Length];
            var (v, _) = Num.ParseText(buf);
            Num.Store(dst, v, dst.attr.scale, 0);
            return;
        }
        var d = dst.Span;
        for (int i = 0; i < d.Length; i++) d[i] = pat[i % pat.Length];
    }

    // ------------------------------------------------------------ DISPLAY

    static readonly Stream Out = new BufferedStream(Console.OpenStandardOutput());
    static readonly Stream Err = new BufferedStream(Console.OpenStandardError());
    static readonly byte[] NewLine = System.Text.Encoding.ASCII.GetBytes(Environment.NewLine);

    public static void cob_display(int to_device, int newline, int varcnt, params CobField[] fields)
    {
        if (to_device != 1 && PlainIoOnScreen) { DisplayOnScreen(fields, newline != 0); return; }
        var o = to_device == 1 ? Err : Out;
        foreach (var f in fields)
        {
            if (f == null) continue;
            if (IsNumeric(f.Type))
            {
                foreach (char c in Num.Pretty(f)) o.WriteByte((byte)c);
            }
            else if (IsNational(f.Type))
            {
                foreach (char c in NationalText(f)) o.WriteByte(c < 256 ? (byte)c : (byte)'?');
            }
            else
            {
                o.Write(f.Span);
            }
        }
        if (newline != 0) o.Write(NewLine);
        o.Flush();
    }

    /// <summary>
    /// The characters of a PIC N field. GnuCOBOL 3.2 stores national data as UTF-16
    /// big-endian, and its own DISPLAY writes those raw bytes (NUL A NUL B ...).
    /// We show the characters instead (decision of 2026-11-24, DECISIONS.md).
    /// </summary>
    internal static string NationalText(CobField f)
    {
        var s = f.Span;
        var sb = new System.Text.StringBuilder(s.Length / 2);
        for (int i = 0; i + 1 < s.Length; i += 2) sb.Append((char)((s[i] << 8) | s[i + 1]));
        return sb.ToString();
    }
}
