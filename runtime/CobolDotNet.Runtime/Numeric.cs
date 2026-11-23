// Numeric encode/decode.  Every numeric operation goes through one exact
// representation: (BigInteger value, int scale) meaning value × 10^-scale.
// That keeps the rules in one place: decode any USAGE, do the math, encode.

using System;
using System.Numerics;
using static CobolDotNet.Runtime.CobConst;

namespace CobolDotNet.Runtime;

internal static class Num
{
    static readonly BigInteger[] Pow10Cache = BuildPow10(80);

    static BigInteger[] BuildPow10(int n)
    {
        var a = new BigInteger[n];
        a[0] = BigInteger.One;
        for (int i = 1; i < n; i++) a[i] = a[i - 1] * 10;
        return a;
    }

    internal static BigInteger Pow10(int n) => n < Pow10Cache.Length ? Pow10Cache[n] : BigInteger.Pow(10, n);

    // ---------------------------------------------------------------- decode

    /// <summary>Value of any field as (value, scale).</summary>
    internal static (BigInteger v, int scale) Get(CobField f)
    {
        switch (f.Type)
        {
            case TYPE_NUMERIC_DISPLAY: return GetDisplay(f);
            case TYPE_NUMERIC_PACKED: return GetPacked(f);
            case TYPE_NUMERIC_BINARY:
            case TYPE_NUMERIC_COMP5: return (GetBinary(f), f.attr.scale);
            case TYPE_NUMERIC_EDITED: return DeEdit(f);
            default: return ParseText(f.Span);
        }
    }

    // Embedded-sign conventions for USAGE DISPLAY:
    //   GnuCOBOL ASCII: negative digit = digit + 0x40 ('p'..'y')
    //   EBCDIC style:   '{','A'..'I' positive, '}','J'..'R' negative
    static int SignedDigit(byte b, out bool neg)
    {
        neg = false;
        if (b >= (byte)'0' && b <= (byte)'9') return b - '0';
        if (b >= (byte)'p' && b <= (byte)'y') { neg = true; return b - 'p'; }
        if (b == (byte)'{') return 0;
        if (b >= (byte)'A' && b <= (byte)'I') return b - 'A' + 1;
        if (b == (byte)'}') { neg = true; return 0; }
        if (b >= (byte)'J' && b <= (byte)'R') { neg = true; return b - 'J' + 1; }
        return b & 0x0F; // spaces and junk: low nibble, like the old compilers
    }

    static (BigInteger, int) GetDisplay(CobField f)
    {
        var s = f.Span;
        int start = 0, end = s.Length;
        bool neg = false;
        bool signed = f.Has(FLAG_HAVE_SIGN);
        bool separate = signed && f.Has(FLAG_SIGN_SEPARATE);
        bool leading = signed && f.Has(FLAG_SIGN_LEADING);
        if (separate)
        {
            byte sb = leading ? s[0] : s[s.Length - 1];
            neg = sb == (byte)'-';
            if (leading) start = 1; else end--;
        }
        BigInteger v = BigInteger.Zero;
        for (int i = start; i < end; i++)
        {
            int d;
            if (signed && !separate && i == (leading ? start : end - 1))
            {
                d = SignedDigit(s[i], out bool n);
                neg |= n;
            }
            else
            {
                d = s[i] >= (byte)'0' && s[i] <= (byte)'9' ? s[i] - '0' : SignedDigit(s[i], out _);
            }
            v = v * 10 + d;
        }
        return (neg ? -v : v, f.attr.scale);
    }

    static (BigInteger, int) GetPacked(CobField f)
    {
        var s = f.Span;
        bool noSign = f.Has(FLAG_NO_SIGN_NIBBLE);
        int nibbles = s.Length * 2 - (noSign ? 0 : 1);
        BigInteger v = BigInteger.Zero;
        for (int i = 0; i < nibbles; i++)
        {
            int b = s[i >> 1];
            int d = (i & 1) == 0 ? b >> 4 : b & 0x0F;
            v = v * 10 + (d > 9 ? 0 : d);
        }
        bool neg = false;
        if (!noSign)
        {
            int sn = s[s.Length - 1] & 0x0F;
            neg = sn == 0x0D || sn == 0x0B;
        }
        return (neg ? -v : v, f.attr.scale);
    }

    static BigInteger GetBinary(CobField f)
    {
        var s = f.Span;
        bool bigEndian = f.Type == TYPE_NUMERIC_BINARY && f.Has(FLAG_BINARY_SWAP);
        bool signed = f.Has(FLAG_HAVE_SIGN);
        var tmp = new byte[s.Length];
        s.CopyTo(tmp);
        return new BigInteger(tmp, isUnsigned: !signed, isBigEndian: bigEndian);
    }

    static (BigInteger, int) DeEdit(CobField f)
    {
        var s = f.Span;
        BigInteger v = BigInteger.Zero;
        bool neg = false;
        for (int i = 0; i < s.Length; i++)
        {
            byte b = s[i];
            if (b >= (byte)'0' && b <= (byte)'9') v = v * 10 + (b - '0');
            else if (b == (byte)'-') neg = true;
            else if ((b == (byte)'C' && i + 1 < s.Length && s[i + 1] == (byte)'R')
                  || (b == (byte)'D' && i + 1 < s.Length && s[i + 1] == (byte)'B')) neg = true;
        }
        return (neg ? -v : v, f.attr.scale);
    }

    /// <summary>MOVE alphanumeric TO numeric: " -12.5 " style text.</summary>
    internal static (BigInteger, int) ParseText(ReadOnlySpan<byte> s)
    {
        BigInteger v = BigInteger.Zero;
        int scale = 0;
        bool neg = false, afterPoint = false;
        foreach (byte b in s)
        {
            if (b >= (byte)'0' && b <= (byte)'9')
            {
                v = v * 10 + (b - '0');
                if (afterPoint) scale++;
            }
            else if (b == (byte)'-') neg = true;
            else if (b == (byte)'.') afterPoint = true;
        }
        return (neg ? -v : v, scale);
    }

    // ---------------------------------------------------------------- encode

    /// <summary>Round or truncate (value, scale) to a new scale.</summary>
    internal static BigInteger Rescale(BigInteger v, int scale, int newScale, bool round)
    {
        if (scale == newScale) return v;
        if (scale < newScale) return v * Pow10(newScale - scale);
        var div = Pow10(scale - newScale);
        var q = BigInteger.DivRem(v, div, out var r); // truncates toward zero
        if (round && BigInteger.Abs(r) * 2 >= div) q += v.Sign; // half away from zero
        return q;
    }

    static int DigitCapacity(CobField f)
    {
        int t = f.Type;
        if (t == TYPE_NUMERIC_BINARY || t == TYPE_NUMERIC_COMP5)
        {
            if (f.Has(FLAG_BINARY_TRUNC) && !f.Has(FLAG_REAL_BINARY) && f.attr.digits > 0)
                return f.attr.digits;
            return -1; // limited only by the bytes
        }
        return f.attr.digits;
    }

    /// <summary>
    /// Store (value, scale) into any numeric or numeric-edited field.
    /// Returns 0, or non-zero on a size error when STORE_KEEP_ON_OVERFLOW is set.
    /// </summary>
    internal static int Store(CobField f, BigInteger v, int scale, int opt)
    {
        v = Rescale(v, scale, f.attr.scale, (opt & STORE_ROUND) != 0);

        int cap = DigitCapacity(f);
        if (cap >= 0)
        {
            var max = Pow10(cap);
            if (BigInteger.Abs(v) >= max)
            {
                if ((opt & STORE_KEEP_ON_OVERFLOW) != 0)
                {
                    Cob.SetException(CobException.EC_SIZE_OVERFLOW);
                    return Cob.Global.cob_exception_code;
                }
                v %= max; // MOVE / no ON SIZE ERROR: lose high-order digits
            }
        }
        if (!f.Has(FLAG_HAVE_SIGN) && f.Type != TYPE_NUMERIC_EDITED) v = BigInteger.Abs(v);

        switch (f.Type)
        {
            case TYPE_NUMERIC_DISPLAY: PutDisplay(f, v); break;
            case TYPE_NUMERIC_PACKED: PutPacked(f, v); break;
            case TYPE_NUMERIC_BINARY:
            case TYPE_NUMERIC_COMP5: PutBinary(f, v); break;
            case TYPE_NUMERIC_EDITED: Edit.Format(f, v); break;
            default: throw new NotSupportedException($"store into field type 0x{f.Type:X2}");
        }
        return 0;
    }

    static void PutDisplay(CobField f, BigInteger v)
    {
        var s = f.Span;
        bool neg = v.Sign < 0;
        var digits = BigInteger.Abs(v).ToString();
        bool signed = f.Has(FLAG_HAVE_SIGN);
        bool separate = signed && f.Has(FLAG_SIGN_SEPARATE);
        bool leading = signed && f.Has(FLAG_SIGN_LEADING);
        int start = separate && leading ? 1 : 0;
        int end = separate && !leading ? s.Length - 1 : s.Length;
        int n = end - start;
        // right-align digits, zero fill
        for (int i = 0; i < n; i++)
        {
            int di = digits.Length - n + i;
            s[start + i] = di >= 0 ? (byte)digits[di] : (byte)'0';
        }
        if (separate)
        {
            s[leading ? 0 : s.Length - 1] = neg ? (byte)'-' : (byte)'+';
        }
        else if (signed && neg)
        {
            int p = leading ? start : end - 1;
            s[p] = (byte)(s[p] + 0x40); // '0'..'9' -> 'p'..'y'
        }
    }

    static void PutPacked(CobField f, BigInteger v)
    {
        var s = f.Span;
        bool noSign = f.Has(FLAG_NO_SIGN_NIBBLE);
        bool neg = v.Sign < 0;
        var digits = BigInteger.Abs(v).ToString();
        int nibbles = s.Length * 2 - (noSign ? 0 : 1);
        s.Clear();
        for (int i = 0; i < nibbles; i++)
        {
            int di = digits.Length - nibbles + i;
            int d = di >= 0 ? digits[di] - '0' : 0;
            if ((i & 1) == 0) s[i >> 1] |= (byte)(d << 4);
            else s[i >> 1] |= (byte)d;
        }
        if (!noSign)
        {
            int sn = f.Has(FLAG_HAVE_SIGN) ? (neg ? 0x0D : 0x0C) : 0x0F;
            s[s.Length - 1] |= (byte)sn;
        }
    }

    static void PutBinary(CobField f, BigInteger v)
    {
        var s = f.Span;
        bool bigEndian = f.Type == TYPE_NUMERIC_BINARY && f.Has(FLAG_BINARY_SWAP);
        // two's complement, truncated to the field's bytes
        var mod = BigInteger.One << (8 * s.Length);
        var u = v % mod;
        if (u.Sign < 0) u += mod;
        var bytes = u.ToByteArray(isUnsigned: true, isBigEndian: false);
        for (int i = 0; i < s.Length; i++)
        {
            byte b = i < bytes.Length ? bytes[i] : (byte)0;
            s[bigEndian ? s.Length - 1 - i : i] = b;
        }
    }

    // ---------------------------------------------------------------- display

    /// <summary>DISPLAY form of a numeric item: sign, digits, decimal point.</summary>
    internal static string Pretty(CobField f)
    {
        var (v, scale) = Get(f);
        int digits = f.attr.digits;
        if (digits <= 0) digits = BigInteger.Abs(v).ToString().Length;
        var abs = BigInteger.Abs(v).ToString().PadLeft(digits, '0');
        if (abs.Length > digits) abs = abs.Substring(abs.Length - digits);
        string body;
        if (scale > 0 && scale <= digits)
            body = abs.Substring(0, digits - scale) + Cob.DecimalPoint + abs.Substring(digits - scale);
        else
            body = abs;
        if (f.Has(FLAG_HAVE_SIGN)) body = (v.Sign < 0 ? "-" : "+") + body;
        return body;
    }
}
