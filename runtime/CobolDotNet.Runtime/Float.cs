// COMP-1 (float) and COMP-2 (double) items, as libcob converts and prints them.
//
//   double -> decimal   exact binary value rounded to 96 significant digits, trailing zeros
//                       dropped (libcob: mpf_set_d + mpf_get_str (.., 96, ..))
//   decimal -> double   the exact value truncated toward zero to 53 bits (mpf_get_d
//                       truncates); COMP-1 then rounds that double to float, as C's cast does
//   DISPLAY             C's printf "%-.16G" (COMP-2) / "%-.8G" (COMP-1), then libcob's
//                       clean_double drops a leading 0 of the 3-digit Windows exponent (E-005 -> E-05)
// The conversions are done in exact integer arithmetic so they don't depend on .NET's
// own double formatting or parsing.

using System;
using System.Numerics;
using System.Text;
using static CobolDotNet.Runtime.CobConst;

namespace CobolDotNet.Runtime;

internal static class Float
{
    internal static bool IsFloat(int type) => type == TYPE_NUMERIC_FLOAT || type == TYPE_NUMERIC_DOUBLE;

    internal static double Read(CobField f) => f.Type == TYPE_NUMERIC_FLOAT ? f.data.F32 : f.data.F64;

    /// <summary>The exact decimal value of a double: (value, scale) with value * 10^-scale == d.</summary>
    static (BigInteger v, int scale) Exact(double d)
    {
        long bits = BitConverter.DoubleToInt64Bits(d);
        bool neg = bits < 0;
        int exp = (int)((bits >> 52) & 0x7FF);
        long frac = bits & 0xFFFFFFFFFFFFFL;
        BigInteger m;
        int e;
        if (exp == 0) { m = frac; e = -1074; }                 // subnormal
        else { m = frac | (1L << 52); e = exp - 1075; }
        BigInteger v; int scale;
        if (e >= 0) { v = m << e; scale = 0; }
        else { v = m * BigInteger.Pow(5, -e); scale = -e; }     // m / 2^k == m * 5^k / 10^k
        return (neg ? -v : v, scale);
    }

    static int Digits(BigInteger v) => v.IsZero ? 1 : BigInteger.Abs(v).ToString().Length;

    /// <summary>cob_decimal_set_double: zero / infinite / NaN become 0.</summary>
    internal static (BigInteger v, int scale) ToDecimal(double d)
    {
        if (d == 0 || double.IsNaN(d) || double.IsInfinity(d)) return (0, 0);
        var (v, scale) = Exact(d);
        // round half-up to 96 significant digits (mpf_get_str)
        int drop = Digits(v) - 96;
        if (drop > 0)
        {
            var p = BigInteger.Pow(10, drop);
            var q = BigInteger.DivRem(BigInteger.Abs(v), p, out var r);
            if (r * 2 >= p) q += 1;
            v = v.Sign < 0 ? -q : q;
            scale -= drop;
        }
        while (scale > 0 && v % 10 == 0) { v /= 10; scale--; }   // no trailing zeros
        if (scale < 0) { v *= BigInteger.Pow(10, -scale); scale = 0; }
        return (v, scale);
    }

    /// <summary>cob_decimal_get_double: value * 10^-scale truncated toward zero to a double.</summary>
    internal static double FromDecimal(BigInteger v, int scale)
    {
        if (v.IsZero) return 0.0;
        bool neg = v.Sign < 0;
        BigInteger num = BigInteger.Abs(v), den = BigInteger.One;
        if (scale > 0) den = BigInteger.Pow(10, scale); else if (scale < 0) num *= BigInteger.Pow(10, -scale);
        // q = floor(num * 2^shift / den) with exactly 53 significant bits
        int shift = 54 - (int)(num.GetBitLength() - den.GetBitLength());
        BigInteger q = shift >= 0 ? (num << shift) / den : num / (den << -shift);
        while (q.GetBitLength() > 53) { q >>= 1; shift--; }
        while (q.GetBitLength() < 53) { shift++; q = shift >= 0 ? (num << shift) / den : num / (den << -shift); }
        double r = Math.ScaleB((double)(ulong)q, -shift);
        return neg ? -r : r;
    }

    /// <summary>Store a decimal into a COMP-1 / COMP-2 item (libcob cob_decimal_get_field).</summary>
    internal static int Store(CobField f, BigInteger v, int scale, int opt)
    {
        double d = FromDecimal(v, scale);
        bool bad = double.IsInfinity(d) || double.IsNaN(d);
        if (f.Type == TYPE_NUMERIC_FLOAT)
        {
            float fv = (float)d;
            if ((opt & STORE_KEEP_ON_OVERFLOW) != 0 && (bad || float.IsInfinity(fv)))
            {
                Cob.SetException(CobException.EC_SIZE_OVERFLOW);
                return Cob.Global.cob_exception_code;
            }
            f.data.F32 = bad ? 0f : fv;
        }
        else
        {
            if ((opt & STORE_KEEP_ON_OVERFLOW) != 0 && bad)
            {
                Cob.SetException(CobException.EC_SIZE_OVERFLOW);
                return Cob.Global.cob_exception_code;
            }
            f.data.F64 = bad ? 0.0 : d;
        }
        return 0;
    }

    /// <summary>DISPLAY of a COMP-1 / COMP-2 item: printf "%-.8G" / "%-.16G" + clean_double.</summary>
    internal static string Pretty(CobField f)
    {
        int p = f.Type == TYPE_NUMERIC_FLOAT ? 8 : 16;
        double d = Read(f);
        if (double.IsNaN(d)) return "NaN";
        if (double.IsInfinity(d)) return d > 0 ? "INF" : "-INF";
        var sb = new StringBuilder();
        if (d < 0 || (d == 0 && double.IsNegative(d))) sb.Append('-');
        if (d == 0) return sb.Append('0').ToString();

        // P significant digits of the exact value, rounded half to even (C runtime)
        var (v, scale) = Exact(Math.Abs(d));
        int n = Digits(v);
        int x = n - 1 - scale;                   // decimal exponent of the first digit
        int drop = n - p;
        BigInteger digits;
        if (drop > 0)
        {
            var pw = BigInteger.Pow(10, drop);
            var q = BigInteger.DivRem(v, pw, out var r);
            var twice = r * 2;
            if (twice > pw || (twice == pw && !q.IsEven)) q += 1;
            digits = q;
            if (Digits(q) > p) { digits = q / 10; x++; }   // 9.99.. rounded up to 10.0..
        }
        else digits = v * BigInteger.Pow(10, -drop);
        string ds = digits.ToString().PadLeft(p, '0');

        if (x < -4 || x >= p)
        {
            // d.ddddE+XX, trailing zeros removed
            string mant = ds.Substring(1).TrimEnd('0');
            sb.Append(ds[0]);
            if (mant.Length > 0) sb.Append('.').Append(mant);
            sb.Append('E').Append(x < 0 ? '-' : '+');
            // the Windows C runtime prints 3 exponent digits (E-005); clean_double drops one
            // leading zero, so stock shows at least 2 (E-05, E+20, E+300)
            string ex = Math.Abs(x).ToString().PadLeft(3, '0');
            if (ex[0] == '0') ex = ex.Substring(1);
            sb.Append(ex);
        }
        else
        {
            string intPart, fracPart;
            if (x >= 0) { intPart = ds.Substring(0, x + 1); fracPart = ds.Substring(x + 1); }
            else { intPart = "0"; fracPart = new string('0', -x - 1) + ds; }
            fracPart = fracPart.TrimEnd('0');
            sb.Append(intPart);
            if (fracPart.Length > 0) sb.Append('.').Append(fracPart);
        }
        return sb.ToString();
    }
}
