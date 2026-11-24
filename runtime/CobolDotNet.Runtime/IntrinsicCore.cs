// Shared plumbing for the intrinsic functions (FUNCTION UPPER-CASE, NUMVAL, ...).
//
// Every cob_intr_* returns a fresh result field. Its type (binary or display, digits,
// scale) decides how DISPLAY FUNCTION X prints, so these helpers build result fields
// exactly the way libcob/intrinsic.c does (make_field_entry, cob_alloc_set_field_int,
// cob_alloc_field, calc_ref_mod). The functions themselves live in Intrinsic*.cs.

using System;
using System.Numerics;
using static CobolDotNet.Runtime.CobConst;

namespace CobolDotNet.Runtime;

public static partial class Cob
{
    public const int EC_ARGUMENT_FUNCTION = 0x0101;

    /// <summary>A new result field: the given type and size, zero-filled data (make_field_entry).</summary>
    internal static CobField IntrField(int type, int digits, int scale, int flags, long size)
        => new CobField(size, new CobPtr(Math.Max(size, 1)), new CobFieldAttr(type, digits, scale, flags, null));

    /// <summary>An alphanumeric result holding these bytes.</summary>
    internal static CobField IntrAlnum(ReadOnlySpan<byte> bytes)
    {
        var f = IntrField(TYPE_ALPHANUMERIC, 0, 0, 0, bytes.Length);
        bytes.CopyTo(f.data.Span(bytes.Length));
        return f;
    }

    /// <summary>cob_alloc_set_field_int: 4-byte native binary, 9 digits, signed if negative.</summary>
    internal static CobField IntrInt(int val)
    {
        var f = IntrField(TYPE_NUMERIC_BINARY, 9, 0, val < 0 ? FLAG_HAVE_SIGN : 0, 4);
        f.data.S32 = val;
        return f;
    }

    /// <summary>cob_alloc_set_field_uint: 4-byte native binary, 9 digits, unsigned.</summary>
    internal static CobField IntrUint(uint val)
    {
        var f = IntrField(TYPE_NUMERIC_BINARY, 9, 0, 0, 4);
        f.data.U32 = val;
        return f;
    }

    /// <summary>cob_trim_decimal: drop trailing zeros after the decimal point.</summary>
    internal static void TrimDecimal(CobDecimal d)
    {
        if (d.value.IsZero) { d.scale = 0; return; }
        while (d.scale > 0 && d.value % 10 == 0) { d.value /= 10; d.scale--; }
    }

    /// <summary>
    /// GMP's mpz_sizeinbase (x, 10): may be one too big, exactly as GMP computes it
    /// (bit length times log10(2) as a 64-bit fraction, plus one). Result field widths
    /// depend on it, so it must match GMP rather than the true digit count.
    /// </summary>
    internal static int GmpSizeInBase10(BigInteger v)
    {
        if (v.IsZero) return 1;
        long bits = (long)BigInteger.Abs(v).GetBitLength();
        var logb2 = new BigInteger(0x4D104D427DE7FBCCUL);   // GMP mp_bases[10].logb2
        return (int)((new BigInteger(bits) * logb2) >> 64) + 1;
    }

    internal static int BitLength(BigInteger v) => v.IsZero ? 0 : (int)BigInteger.Abs(v).GetBitLength();

    /// <summary>cob_alloc_field + cob_decimal_get_field: the result field for a decimal value.</summary>
    internal static CobField IntrDecimal(CobDecimal d)
    {
        if (d.scale == DecimalNaN)
        {
            SetException(EC_ARGUMENT_FUNCTION);
            return IntrField(TYPE_NUMERIC_BINARY, 9, 0, 0, 4);
        }
        bool neg = d.value.Sign < 0;
        int negPos = neg ? 1 : 0;
        int flags = neg ? FLAG_HAVE_SIGN : 0;
        TrimDecimal(d);
        // libcob measures mpz_sizeinbase (value, 2): bits of |value| (1 for zero)
        int bitnum = d.value.IsZero ? 1 : BitLength(d.value);
        CobField f;
        if (bitnum < 33 - negPos && d.scale < 10)
            f = IntrField(TYPE_NUMERIC_BINARY, 9, d.scale, flags, 4);
        else if (bitnum < 65 - negPos && d.scale < 19)
            f = IntrField(TYPE_NUMERIC_BINARY, 20, d.scale, flags, 8);
        else
        {
            int size = GmpSizeInBase10(d.value);
            if (d.scale > size) size = d.scale;
            f = IntrField(TYPE_NUMERIC_DISPLAY, size, d.scale, flags, size);
        }
        Num.Store(f, d.value, d.scale, 0);
        return f;
    }

    internal static CobField IntrDecimal(BigInteger value, int scale) => IntrDecimal(new CobDecimal { value = value, scale = scale });

    /// <summary>libcob's COB_DECIMAL_NAN marker (an invalid result).</summary>
    internal const int DecimalNaN = -32768;

    /// <summary>calc_ref_mod: FUNCTION X (offset:length) on a result field.</summary>
    internal static CobField IntrRefMod(CobField f, int offset, int length)
    {
        if (offset <= 0 || offset > f.size) return f;
        int off = offset - 1;
        long size = f.size - off;
        if (length > 0 && length < size) size = length;
        if (off > 0) f.data.Span(f.size).Slice(off, (int)size).CopyTo(f.data.Span(size));
        f.size = size;
        return f;
    }

    /// <summary>The text of an alphanumeric argument (Latin-1).</summary>
    internal static string IntrText(CobField f) => Latin1(f.Span);

    internal static (BigInteger v, int scale) IntrNum(CobField f) => Num.Get(f);
}
