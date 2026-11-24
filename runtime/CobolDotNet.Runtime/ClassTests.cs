// Class conditions: IF x NUMERIC / ALPHABETIC / ALPHABETIC-UPPER / ALPHABETIC-LOWER,
// IF x OMITTED (libcob common.c cob_is_*, returning bool: generated code uses them as
// conditions). cobc tests simple DISPLAY items inline and calls
// these for the rest (function results, packed, binary, edited, groups).
//
// One deliberate difference from stock 3.2: for COMP-1 / COMP-2 libcob's NUMERIC test is
// inverted (true only for NaN / infinity). Here a finite float is NUMERIC.

using static CobolDotNet.Runtime.CobConst;

namespace CobolDotNet.Runtime;

public static partial class Cob
{
    static bool IsDigitByte(byte b) => b >= (byte)'0' && b <= (byte)'9';
    static bool IsBcdByte(byte b) => (b & 0x0F) <= 9 && (b >> 4) <= 9;

    public static bool cob_is_numeric(CobField f)
    {
        var s = f.Span;
        switch (f.Type)
        {
            case TYPE_NUMERIC_BINARY:
                return true;
            case TYPE_NUMERIC_FLOAT:
                return float.IsFinite(f.data.F32);
            case TYPE_NUMERIC_DOUBLE:
                return double.IsFinite(f.data.F64);
            case TYPE_NUMERIC_PACKED:
            {
                if (s.Length == 0) return true;
                byte last = s[^1];
                int sign = last & 0x0F;
                if (f.Has(FLAG_NO_SIGN_NIBBLE)) { if (sign > 9) return false; }
                else if (f.Has(FLAG_HAVE_SIGN)) { if (sign != 0x0C && sign != 0x0D) return false; }
                else if (sign != 0x0F) return false;
                if ((last & 0xF0) > 0x90) return false;
                for (int i = 0; i < s.Length - 1; i++) if (!IsBcdByte(s[i])) return false;
                return true;
            }
            case TYPE_NUMERIC_DISPLAY:
            {
                int start = 0, end = s.Length;
                if (f.Has(FLAG_HAVE_SIGN) && s.Length > 0)
                {
                    byte c;
                    if (f.Has(FLAG_SIGN_LEADING)) c = s[start++]; else c = s[--end];
                    if (f.Has(FLAG_SIGN_SEPARATE)) { if (c != (byte)'+' && c != (byte)'-') return false; }
                    else if (!IsDigitByte(c) && !(c >= (byte)'p' && c <= (byte)'y')) return false;
                }
                for (int i = start; i < end; i++) if (!IsDigitByte(s[i])) return false;
                return true;
            }
            default:
                foreach (var b in s) if (!IsDigitByte(b)) return false;
                return true;
        }
    }

    static bool IsAsciiUpper(byte b) => b >= (byte)'A' && b <= (byte)'Z';
    static bool IsAsciiLower(byte b) => b >= (byte)'a' && b <= (byte)'z';

    public static bool cob_is_alpha(CobField f)
    {
        foreach (var b in f.Span) if (b != (byte)' ' && !IsAsciiUpper(b) && !IsAsciiLower(b)) return false;
        return true;
    }

    public static bool cob_is_upper(CobField f)
    {
        foreach (var b in f.Span) if (b != (byte)' ' && !IsAsciiUpper(b)) return false;
        return true;
    }

    public static bool cob_is_lower(CobField f)
    {
        foreach (var b in f.Span) if (b != (byte)' ' && !IsAsciiLower(b)) return false;
        return true;
    }

    public static bool cob_is_omitted(CobField f) => f == null || f.data.IsNull;
}
