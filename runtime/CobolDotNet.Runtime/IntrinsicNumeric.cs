// Numeric and math intrinsic functions (FUNCTION ABS, SQRT, MEAN, ANNUITY, ...),
// ported from GnuCOBOL 3.2 libcob/intrinsic.c.
//
// Two things decide whether a result prints byte-for-byte like stock:
//  1. The decimal arithmetic. libcob's cob_decimal_div keeps exactly 38 extra digits
//     (COB_MAX_DIGITS) and truncates, and its add/sub take shortcuts on zero operands
//     that change the result scale. MEAN, VARIANCE, ANNUITY... show those digits, so
//     this file has its own copies of libcob's decimal ops (LAdd, LSub, LMul, LDiv,
//     cob_decimal_pow) instead of the runtime's general-purpose ones.
//  2. The transcendental functions. libcob evaluates them with GMP floats (2048 bits)
//     and converts with mpf_get_str to 96 significant digits, rounded half up
//     (cob_decimal_set_mpf). Here they are evaluated with a small decimal big-float
//     (HF, 160 significant digits) and rounded to 96 digits the same way, which gives
//     the same digits as long as both are accurate well past digit 96.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using static CobolDotNet.Runtime.CobConst;

namespace CobolDotNet.Runtime;

public static partial class Cob
{
    const int EC_SIZE_EXPONENTIATION = 0x1002;
    const int EC_IMP_FEATURE_MISSING = 0x0605;
    const int COB_MAX_DIGITS = 38;
    const int TYPE_NUMERIC_L_DOUBLE = 0x15;

    // ------------------------------------------------------------ libcob decimal ops

    static CobDecimal Dec(CobField f) { var (v, s) = Num.Get(f); return new CobDecimal { value = v, scale = s }; }
    static CobDecimal Dec(BigInteger v, int scale) => new CobDecimal { value = v, scale = scale };
    static CobDecimal Copy(CobDecimal d) => new CobDecimal { value = d.value, scale = d.scale };

    /// <summary>libcob shift_decimal: value *= 10^n (n &lt; 0 truncates), scale += n.</summary>
    static void Shift(CobDecimal d, int n)
    {
        if (n > 0) d.value *= Num.Pow10(n);
        else if (n < 0) d.value = BigInteger.Divide(d.value, Num.Pow10(-n));
        d.scale += n;
    }

    static void AlignDec(CobDecimal a, CobDecimal b)
    {
        if (a.scale < b.scale) Shift(a, b.scale - a.scale);
        else if (a.scale > b.scale) Shift(b, a.scale - b.scale);
    }

    static bool NaNCheck(CobDecimal d1, CobDecimal d2)
    {
        if (d1.scale == DecimalNaN || d2.scale == DecimalNaN) { d1.scale = DecimalNaN; return true; }
        return false;
    }

    /// <summary>libcob cob_decimal_add (zero operand keeps the other's scale).</summary>
    static void LAdd(CobDecimal d1, CobDecimal d2)
    {
        if (NaNCheck(d1, d2)) return;
        if (d1.scale != d2.scale)
        {
            if (d2.value.IsZero) return;
            if (d1.value.IsZero) { d1.value = d2.value; d1.scale = d2.scale; return; }
            var t = Copy(d2);
            AlignDec(d1, t);
            d1.value += t.value;
        }
        else d1.value += d2.value;
    }

    /// <summary>libcob cob_decimal_sub.</summary>
    static void LSub(CobDecimal d1, CobDecimal d2)
    {
        if (NaNCheck(d1, d2)) return;
        if (d1.scale != d2.scale)
        {
            if (d2.value.IsZero) return;
            var t = Copy(d2);
            AlignDec(d1, t);
            d1.value -= t.value;
        }
        else d1.value -= d2.value;
    }

    static void LMul(CobDecimal d1, CobDecimal d2)
    {
        if (NaNCheck(d1, d2)) return;
        d1.scale += d2.scale;
        d1.value *= d2.value;
    }

    /// <summary>libcob cob_decimal_div: COB_MAX_DIGITS (38) more digits, truncated.</summary>
    static void LDiv(CobDecimal d1, CobDecimal d2)
    {
        if (NaNCheck(d1, d2)) return;
        if (d2.value.IsZero)
        {
            d1.scale = DecimalNaN;
            SetException(CobException.EC_SIZE_ZERO_DIVIDE);
            return;
        }
        if (d1.value.IsZero) { d1.scale = 0; return; }
        d1.scale -= d2.scale;
        Shift(d1, COB_MAX_DIGITS + (d1.scale < 0 ? -d1.scale : 0));
        d1.value = BigInteger.Divide(d1.value, d2.value);
    }

    static int LCmp(CobDecimal d1, CobDecimal d2)
    {
        var a = Copy(d1); var b = Copy(d2);
        AlignDec(a, b);
        return a.value.CompareTo(b.value);
    }

    /// <summary>
    /// libcob cob_decimal_pow: pd1 = pd1 ^ pd2. Integer powers are exact (a negative
    /// one divides with cob_decimal_div); others go through the float path and come
    /// back with 96 significant digits. Like libcob it may change pd2.
    /// </summary>
    public static void cob_decimal_pow(CobDecimal pd1, CobDecimal pd2)
    {
        int sign = pd1.value.Sign;
        if (pd1.scale == DecimalNaN) return;
        if (pd2.scale == DecimalNaN) { pd1.scale = DecimalNaN; return; }

        if (pd2.value.IsZero)
        {
            if (sign == 0) SetException(EC_SIZE_EXPONENTIATION);   // 0 ^ 0
            pd1.value = 1; pd1.scale = 0;
            return;
        }
        if (sign == 0) { pd1.scale = 0; return; }

        TrimDecimal(pd2);
        if (sign == -1 && pd2.scale != 0)
        {
            pd1.scale = DecimalNaN;
            SetException(EC_SIZE_EXPONENTIATION);
            return;
        }
        TrimDecimal(pd1);

        if (pd2.scale == 0)
        {
            if (pd2.value.IsOne) return;
            // C long / unsigned long are 32 bits on Windows (stock's platform)
            if (pd2.value.Sign < 0 && pd2.value >= int.MinValue)
            {
                pd2.value = BigInteger.Abs(pd2.value);
                int n = (int)(uint)pd2.value;
                pd1.value = BigInteger.Pow(pd1.value, n);
                if (pd1.scale != 0) { pd1.scale *= n; TrimDecimal(pd1); }
                pd2.value = pd1.value; pd2.scale = pd1.scale;
                pd1.value = 1; pd1.scale = 0;
                LDiv(pd1, pd2);
                TrimDecimal(pd1);
                return;
            }
            if (pd2.value.Sign > 0 && pd2.value <= uint.MaxValue)
            {
                int n = (int)(uint)pd2.value;
                pd1.value = BigInteger.Pow(pd1.value, n);
                if (pd1.scale != 0) { pd1.scale *= n; TrimDecimal(pd1); }
                return;
            }
        }

        if (sign == -1) pd1.value = BigInteger.Abs(pd1.value);
        var x = HF.FromDec(pd1.value, pd1.scale);
        HF r;
        if (pd2.scale == 1 && pd2.value == 5) r = HF.Sqrt(x);
        else r = HF.Exp(HF.Mul(HF.Ln(x), HF.FromDec(pd2.value, pd2.scale)));
        SetFromFloat(pd1, r);
        if (sign == -1) pd1.value = -pd1.value;
    }

    /// <summary>cob_decimal_set_mpf: mpf_get_str's 96 significant digits, rounded.</summary>
    static void SetFromFloat(CobDecimal d, HF v)
    {
        if (v.M.IsZero) { d.value = 0; d.scale = 0; return; }
        var m = BigInteger.Abs(v.M);
        int e = v.E;
        int digits = HF.Digits(m);
        const int n = 96;
        if (digits > n)
        {
            var q = BigInteger.Divide(m, Num.Pow10(digits - n - 1));
            var last = (int)(q % 10);
            q /= 10;
            if (last >= 5) q += 1;
            e += digits - n;
            m = q;
        }
        if (v.M.Sign < 0) m = -m;
        if (e >= 0) { d.value = m * Num.Pow10(e); d.scale = 0; }
        else { d.value = m; d.scale = -e; }
    }

    static CobDecimal FloatDec(HF v) { var d = new CobDecimal(); SetFromFloat(d, v); return d; }

    /// <summary>cob_alloc_field + cob_decimal_get_field.</summary>
    static CobField Result(CobDecimal d) => IntrDecimal(d);

    /// <summary>make_field_entry (f) + memcpy: a copy of an argument, same attributes.</summary>
    static CobField CopyField(CobField f)
    {
        var r = new CobField(f.size, new CobPtr(Math.Max(f.size, 1)), f.attr);
        f.data.Span(f.size).CopyTo(r.data.Span(f.size));
        return r;
    }

    /// <summary>make_field_entry (f) alone: same size and attributes, zero data.</summary>
    static CobField LikeField(CobField f) => new CobField(f.size, new CobPtr(Math.Max(f.size, 1)), f.attr);

    static CobField ArgError()
    {
        SetException(EC_ARGUMENT_FUNCTION);
        return IntrUint(0);
    }

    // ------------------------------------------------------------ expressions

    /// <summary>An arithmetic expression inside a function argument.</summary>
    public static CobField cob_intr_binop(CobField f1, int op, CobField f2)
    {
        switch (op)
        {
            case 'a': return IntrUint((uint)(cob_get_int(f1) & cob_get_int(f2)));
            case 'o': return IntrUint((uint)(cob_get_int(f1) | cob_get_int(f2)));
            case 'e': return IntrUint((uint)(cob_get_int(f1) ^ cob_get_int(f2)));
            case 'l': return IntrUint((uint)(cob_get_int(f1) << cob_get_int(f2)));
            case 'r': return IntrUint((uint)(cob_get_int(f1) >> cob_get_int(f2)));
            case 'n': return IntrUint((uint)~cob_get_int(f2));
        }
        var d1 = Dec(f1);
        var d2 = Dec(f2);
        switch (op)
        {
            case '+': LAdd(d1, d2); break;
            case '-': LSub(d1, d2); break;
            case '*': LMul(d1, d2); break;
            case '/':
                Global.cob_exception_code = 0;
                if (d2.value.IsZero)
                {
                    SetException(CobException.EC_SIZE_ZERO_DIVIDE);
                    d1.value = 0; d1.scale = 0;
                }
                else LDiv(d1, d2);
                break;
            case '^': cob_decimal_pow(d1, d2); break;
        }
        return Result(d1);
    }

    // ------------------------------------------------------------ simple numeric

    public static CobField cob_intr_abs(CobField srcfield)
    {
        var d = Dec(srcfield);
        var f = LikeField(srcfield);
        Num.Store(f, BigInteger.Abs(d.value), d.scale, 0);
        return f;
    }

    public static CobField cob_intr_sign(CobField srcfield) => IntrInt(Dec(srcfield).value.Sign);

    public static CobField cob_intr_integer(CobField srcfield)
    {
        var d = Dec(srcfield);
        if (d.scale < 0) d.value *= Num.Pow10(-d.scale);
        else if (d.scale > 0)
        {
            var q = BigInteger.DivRem(d.value, Num.Pow10(d.scale), out var r);
            if (d.value.Sign < 0 && !r.IsZero) q -= 1;
            d.value = q;
        }
        d.scale = 0;
        return Result(d);
    }

    public static CobField cob_intr_integer_part(CobField srcfield)
    {
        var d = Dec(srcfield);
        if (d.scale < 0) d.value *= Num.Pow10(-d.scale);
        else if (d.scale > 0) d.value = BigInteger.Divide(d.value, Num.Pow10(d.scale));
        d.scale = 0;
        return Result(d);
    }

    public static CobField cob_intr_fraction_part(CobField srcfield)
    {
        var d = Dec(srcfield);
        if (d.scale > 0) d.value = BigInteger.Remainder(d.value, Num.Pow10(d.scale));
        else { d.value = 0; d.scale = 0; }
        return Result(d);
    }

    static CobField ModOrRem(CobField f1, CobField f2, bool isRem)
    {
        Global.cob_exception_code = 0;
        var d2 = Dec(f1);
        var d3 = Dec(f2);
        if (d3.value.IsZero) return ArgError();
        LDiv(d2, d3);
        if (d2.scale < 0) d2.value *= Num.Pow10(-d2.scale);
        else if (d2.scale > 0)
        {
            var p = Num.Pow10(d2.scale);
            if (isRem) d2.value = BigInteger.Divide(d2.value, p);
            else
            {
                int sign = d2.value.Sign;
                var q = BigInteger.DivRem(d2.value, p, out var r);
                if (sign == -1 && !r.IsZero) q -= 1;
                d2.value = q;
            }
        }
        d2.scale = 0;
        LMul(d2, Dec(f2));
        var d1 = Dec(f1);
        LSub(d1, d2);
        return Result(d1);
    }

    public static CobField cob_intr_mod(CobField srcfield1, CobField srcfield2) => ModOrRem(srcfield1, srcfield2, false);
    public static CobField cob_intr_rem(CobField srcfield1, CobField srcfield2) => ModOrRem(srcfield1, srcfield2, true);

    public static CobField cob_intr_factorial(CobField srcfield)
    {
        Global.cob_exception_code = 0;
        int n = cob_get_int(srcfield);
        if (n < 0) return ArgError();
        BigInteger v = 1;
        for (int i = 2; i <= n; i++) v *= i;
        return Result(Dec(v, 0));
    }

    // ------------------------------------------------------------ constants, EXP, LOG

    public static CobField cob_intr_pi() => Result(FloatDec(HF.Pi(HF.W)));

    public static CobField cob_intr_e() => Result(FloatDec(HF.Exp(HF.One)));

    public static CobField cob_intr_exp(CobField srcfield)
    {
        var d = Dec(srcfield);
        Global.cob_exception_code = 0;
        if (d.value.IsZero) return IntrUint(1);
        return Result(FloatDec(HF.Exp(HF.FromDec(d.value, d.scale))));
    }

    public static CobField cob_intr_exp10(CobField srcfield)
    {
        var d1 = Dec(srcfield);
        Global.cob_exception_code = 0;
        int sign = d1.value.Sign;
        if (sign == 0) return IntrUint(1);
        TrimDecimal(d1);
        if (d1.scale == 0)
        {
            if (sign == -1 && d1.value >= int.MinValue)
            {
                d1.scale = (int)BigInteger.Abs(d1.value);
                d1.value = 1;
                return Result(d1);
            }
            if (sign == 1 && d1.value <= uint.MaxValue)
            {
                d1.value = BigInteger.Pow(10, (int)(uint)d1.value);
                return Result(d1);
            }
        }
        var d2 = Dec(10, 0);
        cob_decimal_pow(d2, d1);
        return Result(d2);
    }

    static CobField LogCommon(CobField srcfield, bool base10)
    {
        var d1 = Dec(srcfield);
        Global.cob_exception_code = 0;
        if (d1.value.Sign != 1) return ArgError();
        if (d1.scale != 0) TrimDecimal(d1);
        if (d1.scale == 0 && d1.value.IsOne) return IntrUint(0);
        var x = HF.FromDec(d1.value, d1.scale);
        var r = base10 ? HF.Div(HF.Ln(x), HF.Ln(HF.FromInt(10))) : HF.Ln(x);
        return Result(FloatDec(r));
    }

    public static CobField cob_intr_log(CobField srcfield) => LogCommon(srcfield, false);
    public static CobField cob_intr_log10(CobField srcfield) => LogCommon(srcfield, true);

    public static CobField cob_intr_sqrt(CobField srcfield)
    {
        var d1 = Dec(srcfield);
        Global.cob_exception_code = 0;
        if (d1.value.Sign == -1) return ArgError();
        var d2 = Dec(5, 1);
        TrimDecimal(d1);
        cob_decimal_pow(d1, d2);
        return Result(d1);
    }

    // ------------------------------------------------------------ trigonometry

    static bool OutsideMinusOneToOne(CobDecimal d)
        => LCmp(d, Dec(-1, 0)) < 0 || LCmp(d, Dec(1, 0)) > 0;

    public static CobField cob_intr_sin(CobField srcfield)
    {
        var d = Dec(srcfield);
        Global.cob_exception_code = 0;
        return Result(FloatDec(HF.Sin(HF.FromDec(d.value, d.scale))));
    }

    public static CobField cob_intr_cos(CobField srcfield)
    {
        var d = Dec(srcfield);
        Global.cob_exception_code = 0;
        return Result(FloatDec(HF.Cos(HF.FromDec(d.value, d.scale))));
    }

    public static CobField cob_intr_tan(CobField srcfield)
    {
        var d = Dec(srcfield);
        Global.cob_exception_code = 0;
        var x = HF.FromDec(d.value, d.scale);
        return Result(FloatDec(HF.Div(HF.Sin(x), HF.Cos(x))));
    }

    public static CobField cob_intr_asin(CobField srcfield)
    {
        var d = Dec(srcfield);
        Global.cob_exception_code = 0;
        if (OutsideMinusOneToOne(d)) return ArgError();
        if (d.value.IsZero) return IntrUint(0);
        return Result(FloatDec(HF.Asin(HF.FromDec(d.value, d.scale))));
    }

    public static CobField cob_intr_acos(CobField srcfield)
    {
        var d = Dec(srcfield);
        Global.cob_exception_code = 0;
        if (OutsideMinusOneToOne(d)) return ArgError();
        return Result(FloatDec(HF.Acos(HF.FromDec(d.value, d.scale))));
    }

    public static CobField cob_intr_atan(CobField srcfield)
    {
        var d = Dec(srcfield);
        Global.cob_exception_code = 0;
        if (d.value.IsZero) return IntrUint(0);
        return Result(FloatDec(HF.Atan(HF.FromDec(d.value, d.scale))));
    }

    // ------------------------------------------------------------ argument lists

    public static CobField cob_intr_sum(int params_, params CobField[] args)
    {
        var d1 = Dec(0, 0);
        for (int i = 0; i < params_; i++) LAdd(d1, Dec(args[i]));
        return Result(d1);
    }

    public static CobField cob_intr_min(int params_, params CobField[] args)
    {
        var basef = args[0];
        for (int i = 1; i < params_; i++) if (cob_cmp(args[i], basef) < 0) basef = args[i];
        return CopyField(basef);
    }

    public static CobField cob_intr_max(int params_, params CobField[] args)
    {
        var basef = args[0];
        for (int i = 1; i < params_; i++) if (cob_cmp(args[i], basef) > 0) basef = args[i];
        return CopyField(basef);
    }

    static void MinMax(int n, CobField[] args, out CobField min, out CobField max)
    {
        min = max = args[0];
        for (int i = 1; i < n; i++)
        {
            if (cob_cmp(args[i], min) < 0) min = args[i];
            if (cob_cmp(args[i], max) > 0) max = args[i];
        }
    }

    public static CobField cob_intr_midrange(int params_, params CobField[] args)
    {
        MinMax(params_, args, out var min, out var max);
        var d1 = Dec(min);
        LAdd(d1, Dec(max));
        LDiv(d1, Dec(2, 0));
        return Result(d1);
    }

    public static CobField cob_intr_range(int params_, params CobField[] args)
    {
        MinMax(params_, args, out var min, out var max);
        var d1 = Dec(max);
        LSub(d1, Dec(min));
        return Result(d1);
    }

    public static CobField cob_intr_median(int params_, params CobField[] args)
    {
        if (params_ == 1) return CopyField(args[0]);
        var sorted = args.Take(params_).ToList();
        // libcob uses qsort with cob_cmp; a stable sort gives the same values
        sorted = sorted.OrderBy(f => f, Comparer<CobField>.Create(cob_cmp)).ToList();
        int i = params_ / 2;
        if (params_ % 2 != 0) return CopyField(sorted[i]);
        var d1 = Dec(sorted[i - 1]);
        LAdd(d1, Dec(sorted[i]));
        LDiv(d1, Dec(2, 0));
        return Result(d1);
    }

    static CobDecimal MeanOf(int n, CobField[] args)
    {
        var d1 = Dec(0, 0);
        for (int i = 0; i < n; i++) LAdd(d1, Dec(args[i]));
        LDiv(d1, Dec(n, 0));
        return d1;
    }

    public static CobField cob_intr_mean(int params_, params CobField[] args)
    {
        if (params_ == 1) return CopyField(args[0]);
        return Result(MeanOf(params_, args));
    }

    /// <summary>libcob GET_VARIANCE: mean, then Sum ((arg - mean)^2) / n.</summary>
    static CobDecimal VarianceOf(int n, CobField[] args)
    {
        var mean = MeanOf(n, args);
        if (n == 1) return Dec(0, 0);
        var sum = Dec(0, 0);
        for (int i = 0; i < n; i++)
        {
            var diff = Dec(args[i]);
            LSub(diff, mean);
            LMul(diff, Copy(diff));
            LAdd(sum, diff);
        }
        LDiv(sum, Dec(n, 0));
        return sum;
    }

    public static CobField cob_intr_variance(int num_args, params CobField[] args) => Result(VarianceOf(num_args, args));

    public static CobField cob_intr_standard_deviation(int num_args, params CobField[] args)
    {
        var d1 = VarianceOf(num_args, args);
        TrimDecimal(d1);
        Global.cob_exception_code = 0;
        cob_decimal_pow(d1, Dec(5, 1));
        return Result(d1);
    }

    // ------------------------------------------------------------ finance

    public static CobField cob_intr_annuity(CobField srcfield1, CobField srcfield2)
    {
        var d1 = Dec(srcfield1);
        var d2 = Dec(srcfield2);
        int sign = d1.value.Sign;
        if (sign < 0 || d2.value.Sign <= 0 || d2.scale != 0) return ArgError();
        if (sign == 0)
        {
            d1.value = 1; d1.scale = 0;
            LDiv(d1, d2);
            return Result(d1);
        }
        // x = P1 / (1 - (1 + P1) ^ (-P2))
        d2.value = -d2.value;
        var d3 = Copy(d1);
        LAdd(d3, Dec(1, 0));
        TrimDecimal(d3);
        TrimDecimal(d2);
        cob_decimal_pow(d3, d2);
        var d4 = Dec(1, 0);
        LSub(d4, d3);
        TrimDecimal(d4);
        TrimDecimal(d1);
        LDiv(d1, d4);
        return Result(d1);
    }

    public static CobField cob_intr_present_value(int params_, params CobField[] args)
    {
        var d1 = Dec(args[0]);
        LAdd(d1, Dec(1, 0));
        var d4 = Dec(0, 0);
        for (int i = 1; i < params_; i++)
        {
            var d2 = Dec(args[i]);
            var d3 = Copy(d1);
            if (i > 1)
            {
                d3.value = BigInteger.Pow(d3.value, i);
                d3.scale *= i;
            }
            LDiv(d2, d3);
            LAdd(d4, d2);
        }
        return Result(d4);
    }

    // ------------------------------------------------------------ RANDOM

    static int randNeedsSeeding = 1;
    static GmpMersenneTwister randState;

    /// <summary>
    /// FUNCTION RANDOM: GMP's Mersenne Twister (gmp_randinit_mt / gmp_randseed_ui),
    /// 63 random bits through mpf_urandomb and mpf_get_d, returned as a COMP-2 field
    /// (digits 20, scale 9) like libcob.
    /// </summary>
    public static CobField cob_intr_random(int params_, params CobField[] args)
    {
        uint seed = 0;     // C unsigned long: 32 bits on Windows
        if (params_ > 0)
        {
            long specified = cob_get_llint(args[0]);
            if (specified < 0) SetException(EC_ARGUMENT_FUNCTION);
            else seed = (uint)specified;
            randNeedsSeeding++;
        }
        else if (randNeedsSeeding != 0)
        {
            // first call without a seed: libcob uses seconds past midnight times bits
            // of a module address; any value will do
            var now = DateTime.Now;
            seed = (uint)((now.Hour * 3600 + now.Minute * 60 + now.Second) * (Environment.TickCount & 0xFFFFF));
            randNeedsSeeding = 2;
        }
        if (randNeedsSeeding != 0)
        {
            randState ??= new GmpMersenneTwister();
            randState.Seed(seed);
            randNeedsSeeding = 0;
        }
        double val = randState.UrandomDouble63();

        var f = IntrField(TYPE_NUMERIC_DOUBLE, 20, 9, FLAG_HAVE_SIGN, sizeof(double));
        f.data.F64 = val;
        return f;
    }

    // ------------------------------------------------------------ LOWEST/HIGHEST-ALGEBRAIC

    public static CobField cob_intr_lowest_algebraic(CobField srcfield)
    {
        var a = srcfield.attr;
        bool signed = (a.flags & FLAG_HAVE_SIGN) != 0;
        switch (a.type)
        {
            case TYPE_ALPHANUMERIC:
            case TYPE_NATIONAL:
                return IntrField(TYPE_ALPHANUMERIC, 0, 0, 0, srcfield.size);
            case TYPE_ALPHANUMERIC_EDITED:
            case TYPE_NATIONAL_EDITED:
                return IntrField(TYPE_ALPHANUMERIC, 0, 0, 0, a.digits);
            case TYPE_NUMERIC_BINARY:
            case TYPE_NUMERIC_COMP5:
            {
                if (!signed) return IntrUint(0);
                BigInteger v;
                if ((a.flags & FLAG_REAL_BINARY) != 0 || (a.flags & FLAG_BINARY_TRUNC) == 0)
                    v = BigInteger.Pow(2, (int)(srcfield.size * 8 - 1));
                else
                    v = Num.Pow10(a.digits) - 1;
                return Result(Dec(-v, a.scale));
            }
            case TYPE_NUMERIC_FLOAT:
            case TYPE_NUMERIC_DOUBLE:
            case TYPE_NUMERIC_L_DOUBLE:
                return ArgError();
            case TYPE_NUMERIC_DISPLAY:
            case TYPE_NUMERIC_PACKED:
            case TYPE_NUMERIC_EDITED:
                if (!signed) return IntrUint(0);
                return Result(Dec(-(Num.Pow10(a.digits) - 1), a.scale));
            default:
                return ArgError();
        }
    }

    public static CobField cob_intr_highest_algebraic(CobField srcfield)
    {
        var a = srcfield.attr;
        switch (a.type)
        {
            case TYPE_ALPHANUMERIC:
            case TYPE_NATIONAL:
            case TYPE_ALPHANUMERIC_EDITED:
            case TYPE_NATIONAL_EDITED:
            {
                long size = a.type == TYPE_ALPHANUMERIC || a.type == TYPE_NATIONAL ? srcfield.size : a.digits;
                var f = IntrField(TYPE_ALPHANUMERIC, 0, 0, 0, size);
                f.data.Span(size).Fill(255);
                return f;
            }
            case TYPE_NUMERIC_BINARY:
            case TYPE_NUMERIC_COMP5:
            {
                BigInteger v;
                if ((a.flags & FLAG_REAL_BINARY) != 0 || (a.flags & FLAG_BINARY_TRUNC) == 0)
                {
                    int expo = (int)(srcfield.size * 8);
                    if ((a.flags & FLAG_HAVE_SIGN) != 0) expo--;
                    v = BigInteger.Pow(2, expo);
                }
                else v = Num.Pow10(a.digits);
                return Result(Dec(v - 1, a.scale));
            }
            case TYPE_NUMERIC_FLOAT:
            case TYPE_NUMERIC_DOUBLE:
            case TYPE_NUMERIC_L_DOUBLE:
                return ArgError();
            case TYPE_NUMERIC_DISPLAY:
            case TYPE_NUMERIC_PACKED:
            case TYPE_NUMERIC_EDITED:
                return Result(Dec(Num.Pow10(a.digits) - 1, a.scale));
            default:
                return ArgError();
        }
    }

    // ------------------------------------------------------------ comparisons

    /// <summary>
    /// FUNCTION LOCALE-COMPARE: '&lt;', '=' or '&gt;' by the locale's collation (libcob
    /// uses strcoll on the operands without trailing spaces). The optional third
    /// argument names a locale; an unknown one gives ' ' and EC-ARGUMENT-FUNCTION.
    /// </summary>
    public static CobField cob_intr_locale_compare(int params_, params CobField[] args)
    {
        Global.cob_exception_code = 0;
        var f1 = args[0];
        var f2 = args[1];
        var locale = params_ > 2 ? args[2] : null;
        var r = IntrField(TYPE_ALPHANUMERIC, 0, 0, 0, 1);

        CompareInfo ci = CultureInfo.CurrentCulture.CompareInfo;
        if (locale != null)
        {
            string name = IntrText(locale).Trim();
            try
            {
                if (name.Length == 0) throw new CultureNotFoundException();
                ci = CultureInfo.GetCultureInfo(name.Replace('_', '-').Split('.')[0]).CompareInfo;
            }
            catch (CultureNotFoundException)
            {
                r.data[0] = (byte)' ';
                SetException(EC_ARGUMENT_FUNCTION);
                return r;
            }
        }
        int ret = ci.Compare(TrimTrailingSpaces(f1), TrimTrailingSpaces(f2), CompareOptions.None);
        r.data[0] = (byte)(ret < 0 ? '<' : ret > 0 ? '>' : '=');
        return r;
    }

    // libcob's loop never looks at the first byte, so " " stays " "
    static string TrimTrailingSpaces(CobField f)
    {
        var s = IntrText(f);
        int n = s.Length;
        while (n > 1 && s[n - 1] == ' ') n--;
        return s.Substring(0, n);
    }

    static CobField NotImplemented()
    {
        SetException(EC_IMP_FEATURE_MISSING);
        throw new CobRuntimeException("attempt to use non-implemented function");
    }

    /// <summary>Not implemented in libcob 3.2 either: a fatal runtime error.</summary>
    public static CobField cob_intr_boolean_of_integer(CobField f1, CobField f2) => NotImplemented();

    /// <summary>Not implemented in libcob 3.2 either: a fatal runtime error.</summary>
    public static CobField cob_intr_integer_of_boolean(CobField srcfield) => NotImplemented();

    /// <summary>Not implemented in libcob 3.2 either: a fatal runtime error.</summary>
    public static CobField cob_intr_standard_compare(int params_, params CobField[] args) => NotImplemented();
}

/// <summary>
/// A decimal big-float, M × 10^E, kept to W significant digits (truncated). Enough
/// precision that rounding the results to libcob's 96 digits is exact.
/// </summary>
internal readonly struct HF
{
    public const int W = 160;
    public readonly BigInteger M;
    public readonly int E;

    public HF(BigInteger m, int e) { M = m; E = e; }

    public static readonly HF Zero = new(0, 0);
    public static readonly HF One = new(1, 0);
    public static HF FromInt(long v) => new(v, 0);
    public static HF FromDec(BigInteger v, int scale) => Norm(new HF(v, -scale));

    public static int Digits(BigInteger m)
    {
        if (m.IsZero) return 1;
        m = BigInteger.Abs(m);
        int d = (int)Math.Floor(BigInteger.Log10(m)) + 1;
        if (d < 1) d = 1;
        while (m >= Num.Pow10(d)) d++;
        while (d > 1 && m < Num.Pow10(d - 1)) d--;
        return d;
    }

    static HF Norm(HF a, int w = W)
    {
        if (a.M.IsZero) return Zero;
        int d = Digits(a.M);
        if (d <= w) return a;
        return new HF(BigInteger.Divide(a.M, Num.Pow10(d - w)), a.E + d - w);
    }

    /// <summary>Exponent of the leading digit: value is in [10^Mag, 10^(Mag+1)).</summary>
    public static int Mag(HF a) => a.M.IsZero ? int.MinValue / 2 : Digits(a.M) - 1 + a.E;

    public static HF Neg(HF a) => new(-a.M, a.E);
    public static HF Abs(HF a) => new(BigInteger.Abs(a.M), a.E);
    public static int Sign(HF a) => a.M.Sign;

    public static HF Add(HF a, HF b)
    {
        if (a.M.IsZero) return b;
        if (b.M.IsZero) return a;
        // drop an operand far below the other's precision
        if (Mag(b) < Mag(a) - W - 5) return a;
        if (Mag(a) < Mag(b) - W - 5) return b;
        int e = Math.Min(a.E, b.E);
        var m = a.M * Num.Pow10(a.E - e) + b.M * Num.Pow10(b.E - e);
        return Norm(new HF(m, e));
    }

    public static HF Sub(HF a, HF b) => Add(a, Neg(b));
    public static HF Mul(HF a, HF b) => Norm(new HF(a.M * b.M, a.E + b.E));

    public static HF Div(HF a, HF b)
    {
        if (a.M.IsZero) return Zero;
        int k = W + 2 + Digits(b.M) - Digits(a.M);
        if (k < 0) k = 0;
        return Norm(new HF(BigInteger.Divide(a.M * Num.Pow10(k), b.M), a.E - b.E - k));
    }

    static HF Half(HF a) => Norm(new HF(a.M * 5, a.E - 1));
    static HF Twice(HF a) => Norm(new HF(a.M * 2, a.E));
    static HF MulInt(HF a, long n) => Norm(new HF(a.M * n, a.E));
    static HF DivInt(HF a, long n) => Div(a, FromInt(n));

    public static int Cmp(HF a, HF b) => Sub(a, b).M.Sign;

    public static HF Sqrt(HF a)
    {
        if (a.M.Sign <= 0) return Zero;
        // scale so the mantissa has about 2W digits and an even exponent
        int k = 2 * W + 4 - Digits(a.M);
        if (k < 0) k = 0;
        if (((a.E - k) & 1) != 0) k++;
        var m = a.M * Num.Pow10(k);
        return Norm(new HF(ISqrt(m), (a.E - k) / 2));
    }

    static BigInteger ISqrt(BigInteger n)
    {
        if (n < 2) return n;
        var x = BigInteger.One << ((int)(n.GetBitLength() / 2) + 1);
        while (true)
        {
            var y = (x + n / x) >> 1;
            if (BigInteger.Abs(y - x) <= 1)
            {
                x = y;
                while (x * x > n) x--;
                while ((x + 1) * (x + 1) <= n) x++;
                return x;
            }
            x = y;
        }
    }

    static bool Negligible(HF term, HF sum) => term.M.IsZero || Mag(term) < Mag(sum) - W - 5;

    public static HF Exp(HF x)
    {
        if (x.M.IsZero) return One;
        bool neg = x.M.Sign < 0;
        x = Abs(x);
        int s = 0;
        var limit = new HF(1, -3);
        while (Cmp(x, limit) > 0) { x = Half(x); s++; }
        HF sum = One, term = One;
        for (int n = 1; ; n++)
        {
            term = DivInt(Mul(term, x), n);
            if (Negligible(term, sum)) break;
            sum = Add(sum, term);
        }
        for (int i = 0; i < s; i++) sum = Mul(sum, sum);
        return neg ? Div(One, sum) : sum;
    }

    static readonly Dictionary<int, HF> Ln10Cache = new();

    public static HF Ln(HF x)
    {
        if (x.M.Sign <= 0) return Zero;
        // x = m × 10^j with m in [1, 10)
        int j = Mag(x);
        var m = new HF(x.M, x.E - j);
        var r = LnSmall(m);
        if (j != 0)
        {
            if (!Ln10Cache.TryGetValue(W, out var ln10)) Ln10Cache[W] = ln10 = LnSmall(FromInt(10));
            r = Add(r, MulInt(ln10, j));
        }
        return r;
    }

    /// <summary>ln m for m in [1, 10]: 2^r × 2 atanh ((y - 1) / (y + 1)), y = m^(1/2^r).</summary>
    static HF LnSmall(HF m)
    {
        if (Cmp(m, One) == 0) return Zero;
        const int r = 12;
        for (int i = 0; i < r; i++) m = Sqrt(m);
        var z = Div(Sub(m, One), Add(m, One));
        var z2 = Mul(z, z);
        HF sum = z, pow = z;
        for (int k = 3; ; k += 2)
        {
            pow = Mul(pow, z2);
            var term = DivInt(pow, k);
            if (Negligible(term, sum)) break;
            sum = Add(sum, term);
        }
        return Norm(new HF(sum.M * (BigInteger.One << (r + 1)), sum.E));
    }

    static readonly Dictionary<int, HF> PiCache = new();

    /// <summary>π to at least w significant digits (Machin's formula).</summary>
    public static HF Pi(int w)
    {
        if (PiCache.TryGetValue(w, out var p)) return p;
        int prec = w + 10;
        var one = Num.Pow10(prec);
        static BigInteger ArcCot(int n, BigInteger unity)
        {
            BigInteger sum = 0, term = unity / n;
            int nn = n * n;
            for (int k = 1; !term.IsZero; k += 2)
            {
                sum += ((k / 2) % 2 == 0 ? term : -term) / k;
                term /= nn;
            }
            return sum;
        }
        var pi = 4 * (4 * ArcCot(5, one) - ArcCot(239, one));
        p = Norm(new HF(pi, -prec), w);
        PiCache[w] = p;
        return p;
    }

    public static HF Sin(HF x)
    {
        if (x.M.IsZero) return Zero;
        // reduce to |r| <= π/2, with π carried far enough for x's integer digits
        int extra = Math.Max(0, Mag(x)) + 10;
        var pi = Pi(W + extra);
        var twoPi = Norm(new HF(pi.M * 2, pi.E), W + extra);
        var q = Div2(x, twoPi, W + extra);
        var r = Sub2(x, Norm(new HF(twoPi.M * q, twoPi.E), W + extra + 5), W + extra);
        var halfPi = new HF(pi.M * 5, pi.E - 1);
        if (Cmp(r, halfPi) > 0) r = Sub(Norm(pi), r);
        else if (Cmp(r, Neg(halfPi)) < 0) r = Sub(Neg(Norm(pi)), r);
        r = Norm(r);
        if (r.M.IsZero) return Zero;
        var r2 = Neg(Mul(r, r));
        HF sum = r, term = r;
        for (int n = 2; ; n += 2)
        {
            term = DivInt(Mul(term, r2), (long)n * (n + 1));
            if (Negligible(term, sum)) break;
            sum = Add(sum, term);
        }
        return sum;
    }

    // nearest integer to a / b, at w digits
    static BigInteger Div2(HF a, HF b, int w)
    {
        var e = Math.Min(a.E, b.E);
        var am = a.M * Num.Pow10(a.E - e);
        var bm = b.M * Num.Pow10(b.E - e);
        var q = BigInteger.DivRem(am, bm, out var rem);
        if (BigInteger.Abs(rem) * 2 > BigInteger.Abs(bm)) q += am.Sign * bm.Sign;
        return q;
    }

    static HF Sub2(HF a, HF b, int w)
    {
        int e = Math.Min(a.E, b.E);
        var m = a.M * Num.Pow10(a.E - e) - b.M * Num.Pow10(b.E - e);
        return Norm(new HF(m, e), w);
    }

    public static HF Cos(HF x)
    {
        // cos x = sin (π/2 - x), as libcob does
        int extra = Math.Max(0, Mag(x)) + 10;
        var pi = Pi(W + extra);
        var halfPi = new HF(pi.M * 5, pi.E - 1);
        return Sin(Sub2(halfPi, x, W + extra));
    }

    public static HF Atan(HF x)
    {
        if (x.M.IsZero) return Zero;
        bool neg = x.M.Sign < 0;
        x = Abs(x);
        HF res;
        if (Cmp(x, One) > 0)
            res = Sub(Half(Pi(W)), AtanSmall(Div(One, x)));
        else
            res = AtanSmall(x);
        return neg ? Neg(res) : res;
    }

    static HF AtanSmall(HF x)
    {
        // atan x = 2 atan (x / (1 + sqrt (1 + x²))) until x is small
        int k = 0;
        var limit = new HF(1, -2);
        while (Cmp(x, limit) > 0)
        {
            x = Div(x, Add(One, Sqrt(Add(One, Mul(x, x)))));
            k++;
        }
        var x2 = Neg(Mul(x, x));
        HF sum = x, pow = x;
        for (int n = 3; ; n += 2)
        {
            pow = Mul(pow, x2);
            var term = DivInt(pow, n);
            if (Negligible(term, sum)) break;
            sum = Add(sum, term);
        }
        return Norm(new HF(sum.M * (BigInteger.One << k), sum.E));
    }

    public static HF Asin(HF x)
    {
        int c = Cmp(Abs(x), One);
        if (c == 0) return x.M.Sign < 0 ? Neg(Half(Pi(W))) : Half(Pi(W));
        if (x.M.IsZero) return Zero;
        return Atan(Div(x, Sqrt(Sub(One, Mul(x, x)))));
    }

    public static HF Acos(HF x)
    {
        if (x.M.IsZero) return Half(Pi(W));
        if (Cmp(x, One) == 0) return Zero;
        if (Cmp(x, Neg(One)) == 0) return Pi(W);
        // acos x = π/2 - asin x, at extra precision for x near 1
        return Sub(Half(Pi(W)), Asin(x));
    }
}

/// <summary>
/// GMP's Mersenne Twister (randmt.c / randmts.c): gmp_randinit_mt, gmp_randseed_ui
/// with GMP's seed mangling and 2000-word warm-up, and mpf_urandomb (63 bits) +
/// mpf_get_d as libcob's FUNCTION RANDOM uses them.
/// </summary>
internal sealed class GmpMersenneTwister
{
    const int N = 624, M = 397;
    const uint MATRIX_A = 0x9908B0DF;
    const int WARM_UP = 2000;
    readonly uint[] mt = new uint[N];
    int mti;

    static void Recalc(uint[] mt)
    {
        uint y;
        int kk;
        for (kk = 0; kk < N - M; kk++)
        {
            y = (mt[kk] & 0x80000000) | (mt[kk + 1] & 0x7FFFFFFF);
            mt[kk] = mt[kk + M] ^ (y >> 1) ^ ((y & 1) != 0 ? MATRIX_A : 0);
        }
        for (; kk < N - 1; kk++)
        {
            y = (mt[kk] & 0x80000000) | (mt[kk + 1] & 0x7FFFFFFF);
            mt[kk] = mt[kk - (N - M)] ^ (y >> 1) ^ ((y & 1) != 0 ? MATRIX_A : 0);
        }
        y = (mt[N - 1] & 0x80000000) | (mt[0] & 0x7FFFFFFF);
        mt[N - 1] = mt[M - 1] ^ (y >> 1) ^ ((y & 1) != 0 ? MATRIX_A : 0);
    }

    uint Next()
    {
        if (mti >= N) { Recalc(mt); mti = 0; }
        uint y = mt[mti++];
        y ^= y >> 11;
        y ^= (y << 7) & 0x9D2C5680;
        y ^= (y << 15) & 0xEFC60000;
        y ^= y >> 18;
        return y;
    }

    // (b ^ 0x40118124) mod (2^19937 - 20023), reduced the way GMP does it
    static BigInteger Mangle(BigInteger r)
    {
        var b = r;
        uint e = 0x40118124, bit = 0x20000000;
        var mask = (BigInteger.One << 19937) - 1;
        do
        {
            r *= r;
            while (true)
            {
                while (true)
                {
                    var t = r >> 19937;
                    if (t.IsZero) break;
                    r = (r & mask) + t * 20023;
                }
                if ((e & bit) != 0) { e ^= bit; r *= b; continue; }
                break;
            }
            bit >>= 1;
        } while (bit != 0);
        return r;
    }

    public void Seed(uint seed)
    {
        var mod = (BigInteger.One << 19937) - 20027;
        var seed1 = new BigInteger(seed) % mod + 2;
        seed1 = Mangle(seed1);
        mt[0] = !(seed1 >> 19936).IsEven ? 0x80000000u : 0u;
        seed1 &= (BigInteger.One << 19936) - 1;
        // mpz_export into mt[1..]: 32-bit words, least significant first; rest zero
        var bytes = seed1.IsZero ? Array.Empty<byte>() : seed1.ToByteArray(isUnsigned: true, isBigEndian: false);
        for (int i = 1; i < N; i++) mt[i] = 0;
        for (int w = 0; w * 4 < bytes.Length && 1 + w < N; w++)
        {
            uint v = 0;
            for (int b = 0; b < 4; b++)
            {
                int at = w * 4 + b;
                if (at < bytes.Length) v |= (uint)bytes[at] << (8 * b);
            }
            mt[1 + w] = v;
        }
        for (int i = 0; i < WARM_UP / N; i++) Recalc(mt);
        mti = WARM_UP % N;
    }

    /// <summary>mpf_urandomb (f, state, 63) then mpf_get_d (truncating to 53 bits).</summary>
    public double UrandomDouble63()
    {
        ulong lo = Next();
        ulong hi = Next() & 0x7FFFFFFF;
        ulong r = (lo | (hi << 32)) << 1;
        if (r == 0) return 0.0;
        int top = 63 - System.Numerics.BitOperations.LeadingZeroCount(r);
        if (top > 52) r &= ~((1UL << (top - 52)) - 1);
        return (double)r / 18446744073709551616.0;
    }
}
