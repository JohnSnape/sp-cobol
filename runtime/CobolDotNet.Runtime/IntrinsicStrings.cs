// Intrinsic functions: strings, characters, NUMVAL family, module / exception /
// locale information. Ported from GnuCOBOL 3.2 libcob/intrinsic.c so that a
// function result (its type, size and bytes) is what stock GnuCOBOL produces.
//
// Result-field rules taken over from libcob:
//   - UPPER-CASE, LOWER-CASE, REVERSE, TRIM, WHEN-COMPILED copy the ARGUMENT's
//     attributes (make_field_entry (srcfield)); everything else that returns text
//     is a plain alphanumeric field.
//   - Integer results (LENGTH, ORD, TEST-NUMVAL ...) are 4-byte binary, 9 digits.
//   - NUMVAL results go through cob_alloc_field (IntrDecimal).
//   - (offset, length) is reference modification of the result, applied only
//     when offset > 0, exactly where libcob applies it.
//
// Where libcob has quirks they are kept (see the notes at each function).

using System;
using System.Globalization;
using System.Numerics;
using static CobolDotNet.Runtime.CobConst;

namespace CobolDotNet.Runtime;

public static partial class Cob
{
    // (COB_MAX_DIGITS, EC_IMP_FEATURE_MISSING and NotImplemented are in IntrinsicNumeric.cs)
    const int COB_MAX_UNBOUNDED_SIZE = 2147483646;
    const int EC_DATA_PTR_NULL = 0x0306;
    const int EC_IMP_FEATURE_DISABLED = 0x0604;

    // ------------------------------------------------------------ helpers

    /// <summary>make_field_entry (srcfield): a new field with the argument's size and attributes.</summary>
    static CobField IntrLike(CobField src)
        => new CobField(src.size, new CobPtr(Math.Max(src.size, 1)), src.attr);

    /// <summary>An alphanumeric result of size 0 (libcob: size 1 allocated, data[0] = ' ', size = 0).</summary>
    static CobField IntrEmpty()
    {
        var f = IntrField(TYPE_ALPHANUMERIC, 0, 0, 0, 1);
        f.data[0] = (byte)' ';
        f.size = 0;
        return f;
    }

    /// <summary>cob_alloc_set_field_str: the string's bytes (Latin-1), then optional ref-mod.</summary>
    static CobField IntrStr(string s, int offset = 0, int length = 0)
    {
        int n = s.IndexOf('\0');                 // strlen
        if (n >= 0) s = s.Substring(0, n);
        var f = IntrAlnum(Latin1Bytes(s));
        return offset > 0 ? IntrRefMod(f, offset, length) : f;
    }

    static CobModule CurrentModule => Global.cob_current_module ?? (ModuleStack.Count > 0 ? ModuleStack.Peek() : null);

    /// <summary>
    /// libcob's last_exception_code. libcob keeps it separately from cob_exception_code
    /// (only cob_set_exception changes it); our SetException keeps one code, so this is it.
    /// </summary>
    static int LastExceptionCode => Global.cob_exception_code;

    // ------------------------------------------------------------ LENGTH family

    public static CobField cob_intr_length(CobField srcfield)
        => IntrUint((uint)(IsNational(srcfield.Type) ? srcfield.size / 2 : srcfield.size));

    public static CobField cob_intr_byte_length(CobField srcfield) => IntrUint((uint)srcfield.size);

    public static CobField cob_intr_stored_char_length(CobField srcfield)
    {
        var s = srcfield.Span;
        int count = s.Length;
        while (count > 0 && s[count - 1] == ' ') count--;
        return IntrUint((uint)count);
    }

    // ------------------------------------------------------------ case / reverse

    public static CobField cob_intr_upper_case(int offset, int length, CobField srcfield)
    {
        var f = IntrLike(srcfield);
        var s = srcfield.Span; var d = f.data.Span(f.size);
        for (int i = 0; i < s.Length; i++) { byte b = s[i]; d[i] = b >= 'a' && b <= 'z' ? (byte)(b - 32) : b; }   // toupper, "C" locale
        return offset > 0 ? IntrRefMod(f, offset, length) : f;
    }

    public static CobField cob_intr_lower_case(int offset, int length, CobField srcfield)
    {
        var f = IntrLike(srcfield);
        var s = srcfield.Span; var d = f.data.Span(f.size);
        for (int i = 0; i < s.Length; i++) { byte b = s[i]; d[i] = b >= 'A' && b <= 'Z' ? (byte)(b + 32) : b; }
        return offset > 0 ? IntrRefMod(f, offset, length) : f;
    }

    public static CobField cob_intr_reverse(int offset, int length, CobField srcfield)
    {
        var f = IntrLike(srcfield);
        var s = srcfield.Span; var d = f.data.Span(f.size);
        for (int i = 0; i < s.Length; i++) d[i] = s[s.Length - i - 1];
        return offset > 0 ? IntrRefMod(f, offset, length) : f;
    }

    // ------------------------------------------------------------ CONCATENATE, SUBSTITUTE, TRIM

    public static CobField cob_intr_concatenate(int offset, int length, int @params, params CobField[] args)
    {
        long size = 0;
        for (int i = 0; i < @params; i++) size += args[i].size;
        var f = IntrField(TYPE_ALPHANUMERIC, 0, 0, 0, size);
        int p = 0;
        var d = f.data.Span(size);
        for (int i = 0; i < @params; i++) { args[i].Span.CopyTo(d.Slice(p)); p += (int)args[i].size; }
        return offset > 0 ? IntrRefMod(f, offset, length) : f;
    }

    public static CobField cob_intr_substitute(int offset, int length, int @params, params CobField[] args)
        => Substitute(offset, length, @params, args, false);

    public static CobField cob_intr_substitute_case(int offset, int length, int @params, params CobField[] args)
        => Substitute(offset, length, @params, args, true);

    /// <summary>memcmp, or strncasecmp in the "C" locale (which stops at a NUL), equal = true.</summary>
    static bool SubstMatch(ReadOnlySpan<byte> at, ReadOnlySpan<byte> match, bool anyCase)
    {
        if (!anyCase) return at.Slice(0, match.Length).SequenceEqual(match);
        for (int i = 0; i < match.Length; i++)
        {
            int a = at[i], b = match[i];
            if (a >= 'A' && a <= 'Z') a += 32;
            if (b >= 'A' && b <= 'Z') b += 32;
            if (a != b) return false;
            if (a == 0) return true;
        }
        return true;
    }

    static CobField Substitute(int offset, int length, int @params, CobField[] args, bool anyCase)
    {
        var original = args[0];
        int numreps = @params / 2;
        var matches = new CobField[numreps];
        var reps = new CobField[numreps];
        for (int i = 0; i < @params - 1; i++)
        {
            if (i % 2 == 0) matches[i / 2] = args[i + 1];
            else reps[i / 2] = args[i + 1];
        }

        var src = (ReadOnlySpan<byte>)original.Span;
        int origSize = src.Length;

        // get_substituted_size
        long calcsize = 0;
        for (int cur = 0; cur < origSize;)
        {
            bool found = false;
            for (int i = 0; i < numreps; i++)
            {
                if (cur + matches[i].size > origSize) continue;
                if (SubstMatch(src.Slice(cur), matches[i].Span, anyCase))
                {
                    cur += (int)matches[i].size;
                    calcsize += reps[i].size;
                    found = true;
                    break;
                }
            }
            if (!found) { cur++; calcsize++; }
        }

        var f = IntrField(TYPE_ALPHANUMERIC, 0, 0, 0, calcsize);
        var d = f.data.Span(Math.Max(calcsize, 1));

        // substitute_matches
        int o = 0;
        for (int cur = 0; cur < origSize;)
        {
            bool found = false;
            for (int i = 0; i < numreps; i++)
            {
                if (cur + matches[i].size > origSize) continue;
                if (SubstMatch(src.Slice(cur), matches[i].Span, anyCase))
                {
                    reps[i].Span.CopyTo(d.Slice(o));
                    cur += (int)matches[i].size;
                    o += (int)reps[i].size;
                    found = true;
                    break;
                }
            }
            if (!found) d[o++] = src[cur++];
        }
        return offset > 0 ? IntrRefMod(f, offset, length) : f;
    }

    /// <summary>TRIM: direction 0 = both, 1 = LEADING, 2 = TRAILING.</summary>
    public static CobField cob_intr_trim(int offset, int length, CobField srcfield, int direction)
    {
        var f = IntrLike(srcfield);
        var s = srcfield.Span;
        int i = 0;
        while (i < s.Length && s[i] == ' ') i++;
        if (i == s.Length)
        {
            // all spaces (or empty): size 0, and no reference modification (as libcob)
            f.data[0] = (byte)' ';
            f.size = 0;
            return f;
        }
        int begin = 0, end = s.Length - 1;
        if (direction != 2) while (s[begin] == ' ') begin++;
        if (direction != 1) while (s[end] == ' ') end--;
        int size = end - begin + 1;
        s.Slice(begin, size).CopyTo(f.data.Span(size));
        f.size = size;
        return offset > 0 ? IntrRefMod(f, offset, length) : f;
    }

    // ------------------------------------------------------------ CHAR, ORD, ORD-MIN, ORD-MAX

    /// <summary>CHAR: the character with this ordinal (1-256); out of range gives LOW-VALUE.</summary>
    public static CobField cob_intr_char(CobField srcfield)
    {
        var f = IntrField(TYPE_ALPHANUMERIC, 0, 0, 0, 1);
        int i = cob_get_int(srcfield);
        f.data[0] = i < 1 || i > 256 ? (byte)0 : (byte)(i - 1);
        return f;
    }

    public static CobField cob_intr_ord(CobField srcfield) => IntrUint((uint)(srcfield.data[0] + 1));

    public static CobField cob_intr_ord_min(int @params, params CobField[] args)
    {
        uint ordmin = 1;
        var basef = args[0];
        for (int i = 1; i < @params; i++)
            if (cob_cmp(args[i], basef) < 0) { basef = args[i]; ordmin = (uint)(i + 1); }
        return IntrUint(ordmin);
    }

    public static CobField cob_intr_ord_max(int @params, params CobField[] args)
    {
        uint ordmax = 1;
        var basef = args[0];
        for (int i = 1; i < @params; i++)
            if (cob_cmp(args[i], basef) > 0) { basef = args[i]; ordmax = (uint)(i + 1); }
        return IntrUint(ordmax);
    }

    // ------------------------------------------------------------ NUMVAL, NUMVAL-C

    /// <summary>calculate_start_end_for_numval: skip trailing space/low-value, leading space/zero.</summary>
    static int NumvalBounds(CobField srcfield, out int p, out int pEnd)
    {
        p = 0; pEnd = -1;
        if (srcfield == null || srcfield.size == 0 || srcfield.data.IsNull) return 0;
        var s = srcfield.Span;
        pEnd = s.Length - 1;
        while (p != pEnd) { if (s[pEnd] != ' ' && s[pEnd] != 0) break; pEnd--; }
        while (p != pEnd) { if (s[p] != ' ' && s[p] != '0') break; p++; }
        return pEnd - p + 1;
    }

    static byte ToUpperC(byte b) => b >= 'a' && b <= 'z' ? (byte)(b - 32) : b;

    public static CobField cob_intr_numval(CobField srcfield) => Numval(srcfield, null, false);
    public static CobField cob_intr_numval_c(CobField srcfield, CobField currency) => Numval(srcfield, currency, true);
    /// <summary>cobc passes a C NULL (0) when the currency argument is omitted.</summary>
    public static CobField cob_intr_numval_c(CobField srcfield, int currency) => Numval(srcfield, null, true);

    static CobField Numval(CobField srcfield, CobField currency, bool numvalC)
    {
        byte decPt = (byte)DecimalPoint, numSep = (byte)NumericSeparator, curSymb = (byte)CurrencySymbol;

        int datasize = NumvalBounds(srcfield, out int p, out int pEnd);
        if (datasize == 0) return IntrUint(0);
        if (datasize > COB_MAX_DIGITS) datasize = COB_MAX_DIGITS;

        var s = srcfield.Span;
        var buff = new System.Text.StringBuilder(datasize + 1);
        int digits = 0, decimalDigits = 0, sign = 0;
        bool decimalSeen = false, currencySeen = false, exception = false;
        bool useCurrency = numvalC && currency != null && currency.size < datasize;

        for (; p <= pEnd; ++p)
        {
            if (pEnd - p + 1 >= 2
             && ((ToUpperC(s[p]) == 'C' && ToUpperC(s[p + 1]) == 'R')
              || (ToUpperC(s[p]) == 'D' && ToUpperC(s[p + 1]) == 'B')))
            {
                // CR / DB always wins: sets the sign and ends
                if (sign != 0) exception = true;
                else
                {
                    p += 2;
                    while (p <= pEnd) { if (s[p] != ' ') { exception = true; break; } p++; }
                }
                sign = -1;
                goto gameOver;
            }

            if (useCurrency)
            {
                int csize = (int)currency.size;
                if (pEnd - p + 1 >= csize && s.Slice(p, csize).SequenceEqual(currency.Span))
                {
                    if (currencySeen) exception = true;
                    else
                    {
                        if (digits != 0 || decimalSeen) exception = true;
                        currencySeen = true;
                    }
                    p += csize - 1;
                    continue;
                }
            }
            else if (numvalC && s[p] == curSymb)
            {
                if (currencySeen) exception = true;
                else currencySeen = true;
                continue;
            }

            byte c = s[p];
            if (c >= '0' && c <= '9')
            {
                if (c == '0' && digits == 0 && !decimalSeen) continue;   // no data yet
                if (decimalSeen) decimalDigits++;
                buff.Append((char)c);
                digits++;
                if (digits > COB_MAX_DIGITS) { exception = true; goto gameOver; }
                continue;
            }
            switch (c)
            {
                case (byte)'+': if (sign != 0) exception = true; else sign = 1; continue;
                case (byte)'-': if (sign != 0) exception = true; else sign = -1; continue;
                case (byte)' ': continue;   // embedded spaces not checked (as libcob)
            }
            if (c == decPt)
            {
                if (decimalSeen) exception = true;
                decimalSeen = true;
            }
            else if (c == numSep && numvalC) { }   // separator positions not checked (as libcob)
            else exception = true;                  // invalid data: flag it and go on
        }

    gameOver:
        BigInteger value = BigInteger.Zero;
        if (digits != 0)
        {
            value = BigInteger.Parse(buff.ToString(), CultureInfo.InvariantCulture);
            if (sign == -1) value = -value;
        }
        if (exception) SetException(EC_ARGUMENT_FUNCTION);
        return IntrDecimal(value, decimalDigits);
    }

    // ------------------------------------------------------------ NUMVAL-F

    public static CobField cob_intr_numval_f(CobField srcfield)
    {
        byte decPt = (byte)DecimalPoint;
        int datasize = NumvalBounds(srcfield, out int p, out int pEnd);
        if (datasize == 0) return IntrUint(0);

        var s = srcfield.Span;
        var buff = new System.Text.StringBuilder();
        int plusMinus = 0, ePlusMinus = 0;
        long digits = 0, decimalDigits = 0, exponent = 0;
        bool decimalSeen = false, eSeen = false, exception = false;

        for (; p <= pEnd; ++p)
        {
            byte c = s[p];
            if (c >= '0' && c <= '9')
            {
                if (c == '0' && digits == 0 && !decimalSeen && exponent == 0) continue;
                if (eSeen) { exponent = exponent * 10 + (c - '0'); }
                else
                {
                    if (decimalSeen) decimalDigits++;
                    buff.Append((char)c);
                    digits++;
                    if (digits > COB_MAX_DIGITS) { exception = true; break; }
                }
                continue;
            }
            switch (c)
            {
                case (byte)'+':
                    if (eSeen) { if (ePlusMinus != 0) exception = true; else ePlusMinus = 1; }
                    else { if (plusMinus != 0) exception = true; else plusMinus = 1; }
                    continue;
                case (byte)'-':
                    if (eSeen) { if (ePlusMinus != 0) exception = true; else ePlusMinus = -1; }
                    else { if (plusMinus != 0) exception = true; else plusMinus = -1; }
                    continue;
                case (byte)'e':
                case (byte)'E':
                    if (eSeen) { exception = true; continue; }
                    if (digits == 0 && decimalDigits == 0) { exception = true; goto gameOver; }
                    eSeen = true;
                    continue;
                case (byte)' ':
                    continue;
            }
            if (c == decPt) { if (decimalSeen) exception = true; else decimalSeen = true; }
            else exception = true;
        }

    gameOver:
        BigInteger value = digits == 0 ? BigInteger.Zero : BigInteger.Parse(buff.ToString(), CultureInfo.InvariantCulture);
        if (exponent > 9999) { exponent = 9999; exception = true; }
        if (exception) SetException(EC_ARGUMENT_FUNCTION);

        if (value.IsZero) return IntrDecimal(value, 0);   // sign and exponent irrelevant
        if (plusMinus == -1) value = -value;
        int scale;
        if (exponent != 0)
        {
            if (ePlusMinus == -1) scale = (int)(decimalDigits + exponent);
            else if (decimalDigits >= exponent) scale = (int)(decimalDigits - exponent);
            else { value *= Num.Pow10((int)(exponent - decimalDigits)); scale = 0; }
        }
        else scale = (int)decimalDigits;
        return IntrDecimal(value, scale);
    }

    // ------------------------------------------------------------ TEST-NUMVAL family

    public static CobField cob_intr_test_numval(CobField srcfield) => IntrInt(cob_check_numval(srcfield, null, 0, 0));
    public static CobField cob_intr_test_numval_c(CobField srcfield, CobField currency) => IntrInt(cob_check_numval(srcfield, currency, 1, 0));
    public static CobField cob_intr_test_numval_c(CobField srcfield, int currency) => IntrInt(cob_check_numval(srcfield, null, 1, 0));
    public static CobField cob_intr_test_numval_f(CobField srcfield) => IntrInt(cob_check_numval_f(srcfield));

    /// <summary>
    /// Validate a NUMVAL / NUMVAL-C argument: 0 if valid, else the 1-based position of the
    /// first bad character (size + 1 when no digit was found).
    /// libcob quirk kept: a currency string of more than one character is only accepted in
    /// the leading position one character at a time, so "US$12" with "US$" gives 2.
    /// </summary>
    public static int cob_check_numval(CobField srcfield, CobField currency, int chkcurr, int anycase)
    {
        int maxPos = (int)srcfield.size;
        if (maxPos == 0) return 1;
        byte decPt = (byte)DecimalPoint;
        var s = srcfield.Span;

        byte[] begp = null;
        int currcySize = 0;
        if (currency != null)
        {
            var c = currency.Span;
            int cmax = c.Length, beg = -1, endp = -1;
            for (int pos = 0; pos < cmax; pos++)
            {
                switch (c[pos])
                {
                    case (byte)'0': case (byte)'1': case (byte)'2': case (byte)'3': case (byte)'4':
                    case (byte)'5': case (byte)'6': case (byte)'7': case (byte)'8': case (byte)'9':
                    case (byte)'+': case (byte)'-': case (byte)'.': case (byte)',': case (byte)'*':
                        return 1;
                    case (byte)' ':
                        break;
                    default:
                        if (pos < cmax - 1)
                        {
                            if (c[pos] == 'C' && c[pos + 1] == 'R') return 1;
                            if (c[pos] == 'D' && c[pos + 1] == 'B') return 1;
                        }
                        if (beg < 0) beg = pos;
                        endp = pos;
                        break;
                }
            }
            if (endp < 0 || beg < 0) return 1;
            currcySize = endp - beg + 1;
            if (currcySize >= maxPos) currcySize = 0;
            else begp = c.Slice(beg, currcySize).ToArray();
        }
        else if (chkcurr != 0)
        {
            begp = new[] { (byte)CurrencySymbol };
            currcySize = 1;
        }

        int n;
        bool plusMinus = false;
        // leading positions
        for (n = 0; n < maxPos; ++n)
        {
            byte b = s[n];
            if (b >= '0' && b <= '9') break;
            if (b == ' ') continue;
            if (b == '+' || b == '-')
            {
                if (plusMinus) return n + 1;
                plusMinus = true;
                continue;
            }
            if (b == ',' || b == '.')
            {
                if (b != decPt) return n + 1;
                break;
            }
            if (begp != null && n < maxPos - currcySize && s.Slice(n, currcySize).SequenceEqual(begp))
                continue;
            return n + 1;
        }
        if (n == maxPos) return maxPos + 1;   // no digit at all

        // the data
        int digits = 0;
        bool decimalSeen = false, spaceSeen = false;
        for (; n < maxPos; ++n)
        {
            byte b = s[n];
            bool stop = false;
            switch (b)
            {
                case (byte)'0': case (byte)'1': case (byte)'2': case (byte)'3': case (byte)'4':
                case (byte)'5': case (byte)'6': case (byte)'7': case (byte)'8': case (byte)'9':
                    if (++digits > COB_MAX_DIGITS || spaceSeen) return n + 1;
                    continue;
                case (byte)',':
                case (byte)'.':
                    if (decimalSeen || spaceSeen) return n + 1;
                    if (b == decPt) decimalSeen = true;
                    else if (chkcurr == 0) return n + 1;
                    if (digits != 0)
                    {
                        byte prev = s[n - 1];
                        if (prev < '0' || prev > '9') return n + 1;
                    }
                    else if (n < maxPos - 1)
                    {
                        byte next = s[n + 1];
                        if (next < '0' || next > '9') return n + 2;
                    }
                    continue;
                case (byte)' ':
                    spaceSeen = true;
                    continue;
                case (byte)'+':
                case (byte)'-':
                    if (plusMinus) return n + 1;
                    n++;           // trailing sign: only spaces may follow
                    stop = true;
                    break;
                case (byte)'c':
                case (byte)'C':
                case (byte)'d':
                case (byte)'D':
                {
                    if (anycase == 0 && (b == 'c' || b == 'd')) return n + 1;
                    if (plusMinus) return n + 1;
                    byte want = (b == 'c' || b == 'C') ? (byte)'R' : (byte)'B';
                    if (n < maxPos - 1 && (s[n + 1] == want || (anycase != 0 && s[n + 1] == want + 32)))
                    {
                        n += 2;
                        stop = true;
                        break;
                    }
                    return n + 2;
                }
                default:
                    return n + 1;
            }
            if (stop) break;
        }

        if (digits == 0) return maxPos + 1;
        for (; n < maxPos; ++n)
            if (s[n] != ' ') return n + 1;
        return 0;
    }

    /// <summary>Validate a NUMVAL-F argument: 0 if valid, else position of the first bad character.</summary>
    public static int cob_check_numval_f(CobField srcfield)
    {
        int size = (int)srcfield.size;
        if (size == 0) return 1;
        byte decPt = (byte)DecimalPoint;
        var s = srcfield.Span;
        bool plusMinus = false, decimalSeen = false, spaceSeen = false, eSeen = false, ePlusMinus = false;
        int digits = 0, exponent = 0;
        int n;

        for (n = 0; n < size; ++n)
        {
            byte b = s[n];
            if (b >= '0' && b <= '9') break;
            if (b == ' ') continue;
            if (b == '+' || b == '-')
            {
                if (plusMinus) return n + 1;
                plusMinus = true;
                continue;
            }
            if (b == ',' || b == '.')
            {
                if (b != decPt) return n + 1;
                break;
            }
            return n + 1;
        }
        if (n == size) return n + 1;

        for (; n < size; ++n)
        {
            byte b = s[n];
            if (b >= '0' && b <= '9')
            {
                if (eSeen) { if (++exponent > 4 || !ePlusMinus) return n + 1; }
                else if (++digits > COB_MAX_DIGITS || spaceSeen) return n + 1;
                continue;
            }
            switch (b)
            {
                case (byte)',':
                case (byte)'.':
                    if (decimalSeen || spaceSeen || eSeen) return n + 1;
                    if (b == decPt) { decimalSeen = true; continue; }
                    return n + 1;
                case (byte)' ':
                    spaceSeen = true;
                    continue;
                case (byte)'E':
                case (byte)'e':
                    if (eSeen) return n + 1;
                    eSeen = true;
                    continue;
                case (byte)'+':
                case (byte)'-':
                    if (eSeen) { if (ePlusMinus) return n + 1; ePlusMinus = true; }
                    else { if (plusMinus) return n + 1; plusMinus = true; }
                    continue;
                default:
                    return n + 1;
            }
        }
        if (digits == 0 || (eSeen && exponent == 0)) return n + 1;
        return 0;
    }

    // ------------------------------------------------------------ BIT-OF, BIT-TO-CHAR, HEX-OF, HEX-TO-CHAR

    public static CobField cob_intr_bit_of(CobField srcfield)
    {
        var s = srcfield.Span;
        var f = IntrField(TYPE_ALPHANUMERIC, 0, 0, 0, s.Length * 8L);
        var d = f.data.Span(Math.Max(f.size, 1));
        int j = 0;
        foreach (byte b in s)
            for (int bit = 0x80; bit != 0; bit >>= 1) d[j++] = (b & bit) != 0 ? (byte)'1' : (byte)'0';
        return f;
    }

    public static CobField cob_intr_bit_to_char(CobField srcfield)
    {
        var s = srcfield.Span;
        int size = s.Length / 8;
        var f = IntrField(TYPE_ALPHANUMERIC, 0, 0, 0, size);
        int k = 0;
        for (int i = 0; i < size; i++)
        {
            int v = 0;
            for (int bit = 0x80; bit != 0; bit >>= 1)
            {
                byte b = s[k++];
                if (b == '0') continue;
                if (b != '1') SetException(EC_ARGUMENT_FUNCTION);
                v |= bit;               // anything but '0' counts as a 1 bit (as libcob)
            }
            f.data[i] = (byte)v;
        }
        return f;
    }

    public static CobField cob_intr_hex_of(CobField srcfield)
    {
        const string hex = "0123456789ABCDEF";
        var s = srcfield.Span;
        var f = IntrField(TYPE_ALPHANUMERIC, 0, 0, 0, s.Length * 2L);
        int j = 0;
        foreach (byte b in s) { f.data[j++] = (byte)hex[(b >> 4) & 0xF]; f.data[j++] = (byte)hex[b & 0xF]; }
        return f;
    }

    static int HexDigit(byte c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        return -1;
    }

    public static CobField cob_intr_hex_to_char(CobField srcfield)
    {
        var s = srcfield.Span;
        int size = s.Length / 2;     // an odd last character is ignored
        var f = IntrField(TYPE_ALPHANUMERIC, 0, 0, 0, size);
        for (int i = 0; i < size; i++)
        {
            int hi = HexDigit(s[2 * i]), lo = HexDigit(s[2 * i + 1]);
            if (hi < 0) { hi = 0; SetException(EC_ARGUMENT_FUNCTION); }
            if (lo < 0) { lo = 0; SetException(EC_ARGUMENT_FUNCTION); }
            f.data[i] = (byte)(hi * 16 + lo);
        }
        return f;
    }

    // ------------------------------------------------------------ WHEN-COMPILED, MODULE-*

    /// <summary>WHEN-COMPILED: cobc passes the compile time as a constant; copy it (with its attributes).</summary>
    public static CobField cob_intr_when_compiled(int offset, int length, CobField f)
    {
        var r = IntrLike(f);
        f.Span.CopyTo(r.data.Span(r.size));
        return offset > 0 ? IntrRefMod(r, offset, length) : r;
    }

    public static CobField cob_intr_module_date()
    {
        var f = IntrField(TYPE_NUMERIC_DISPLAY, 8, 0, 0, 8);
        Latin1Bytes(((uint)(CurrentModule?.module_date ?? 0)).ToString("D8")).AsSpan(0, 8).CopyTo(f.data.Span(8));
        return f;
    }

    public static CobField cob_intr_module_time()
    {
        var f = IntrField(TYPE_NUMERIC_DISPLAY, 6, 0, 0, 6);
        Latin1Bytes(((uint)(CurrentModule?.module_time ?? 0)).ToString("D6")).AsSpan(0, 6).CopyTo(f.data.Span(6));
        return f;
    }

    public static CobField cob_intr_module_id() => IntrStr(CurrentModule?.module_name ?? "");

    /// <summary>The name of the program that called this one; empty for the main program.</summary>
    public static CobField cob_intr_module_caller_id()
    {
        CobModule caller = CurrentModule?.next;
        if (caller == null && ModuleStack.Count > 1)
        {
            var e = ModuleStack.GetEnumerator();
            e.MoveNext(); e.MoveNext();
            caller = e.Current;
        }
        return caller == null ? IntrEmpty() : IntrStr(caller.module_name ?? "");
    }

    public static CobField cob_intr_module_formatted_date() => IntrStr(CurrentModule?.module_formatted_date ?? "");
    public static CobField cob_intr_module_source() => IntrStr(CurrentModule?.module_source ?? "");

    public static CobField cob_intr_module_path()
    {
        var path = CurrentModule?.module_path as string;
        return string.IsNullOrEmpty(path) ? IntrEmpty() : IntrStr(path);
    }

    // ------------------------------------------------------------ EXCEPTION-*

    /// <summary>
    /// libcob's cob_exception_tab_code / cob_exception_tab_name: exception.def (with
    /// exception-io.def) in table order, between the "None" and "Invalid" entries.
    /// </summary>
    static readonly (int code, string name)[] ExceptionTable =
    {
        (0, "None"),
        (0xFFFF, "EC-ALL"),
        (0x0100, "EC-ARGUMENT"),
        (0x0101, "EC-ARGUMENT-FUNCTION"),
        (0x0102, "EC-ARGUMENT-IMP"),
        (0x0200, "EC-BOUND"),
        (0x0209, "EC-BOUND-FUNC-RET-VALUE"),
        (0x0201, "EC-BOUND-IMP"),
        (0x0202, "EC-BOUND-ODO"),
        (0x0203, "EC-BOUND-OVERFLOW"),
        (0x0204, "EC-BOUND-PTR"),
        (0x0205, "EC-BOUND-REF-MOD"),
        (0x0206, "EC-BOUND-SET"),
        (0x0207, "EC-BOUND-SUBSCRIPT"),
        (0x0208, "EC-BOUND-TABLE-LIMIT"),
        (0x1800, "EC-CONTINUE"),
        (0x1801, "EC-CONTINUE-IMP"),
        (0x1802, "EC-CONTINUE-LESS-THAN-ZERO"),
        (0x0300, "EC-DATA"),
        (0x0301, "EC-DATA-CONVERSION"),
        (0x0302, "EC-DATA-IMP"),
        (0x0303, "EC-DATA-INCOMPATIBLE"),
        (0x0304, "EC-DATA-NOT-FINITE"),
        (0x0305, "EC-DATA-OVERFLOW"),
        (0x0306, "EC-DATA-PTR-NULL"),
        (0x1900, "EC-EXTERNAL"),
        (0x1901, "EC-EXTERNAL-DATA-MISMATCH"),
        (0x1902, "EC-EXTERNAL-FILE-MISMATCH"),
        (0x1903, "EC-EXTERNAL-FORMAT-CONFLICT"),
        (0x1904, "EC-EXTERNAL-IMP"),
        (0x0400, "EC-FLOW"),
        (0x040A, "EC-FLOW-APPLY-COMMIT"),
        (0x040B, "EC-FLOW-COMMIT"),
        (0x0401, "EC-FLOW-GLOBAL-EXIT"),
        (0x0402, "EC-FLOW-GLOBAL-GOBACK"),
        (0x0403, "EC-FLOW-IMP"),
        (0x0404, "EC-FLOW-RELEASE"),
        (0x0405, "EC-FLOW-REPORT"),
        (0x0406, "EC-FLOW-RETURN"),
        (0x040C, "EC-FLOW-ROLLBACK"),
        (0x0407, "EC-FLOW-SEARCH"),
        (0x0408, "EC-FLOW-USE"),
        (0x1500, "EC-FUNCTION"),
        (0x1503, "EC-FUNCTION-ARG-OMITTED"),
        (0x1503, "EC-FUNCTION-IMP"),
        (0x1504, "EC-FUNCTION-NOT-FOUND"),
        (0x1501, "EC-FUNCTION-PTR-INVALID"),
        (0x1502, "EC-FUNCTION-PTR-NULL"),
        (0x0500, "EC-I-O"),
        (0x0501, "EC-I-O-AT-END"),
        (0x0502, "EC-I-O-EOP"),
        (0x0503, "EC-I-O-EOP-OVERFLOW"),
        (0x0504, "EC-I-O-FILE-SHARING"),
        (0x0505, "EC-I-O-IMP"),
        (0x0506, "EC-I-O-INVALID-KEY"),
        (0x0507, "EC-I-O-LINAGE"),
        (0x0508, "EC-I-O-LOGIC-ERROR"),
        (0x0509, "EC-I-O-PERMANENT-ERROR"),
        (0x050B, "EC-I-O-RECORD-CONTENT"),
        (0x050A, "EC-I-O-RECORD-OPERATION"),
        (0x050C, "EC-I-O-RECORD-WARNING"),
        (0x0600, "EC-IMP"),
        (0x0601, "EC-IMP-ACCEPT"),
        (0x0602, "EC-IMP-DISPLAY"),
        (0x0603, "EC-IMP-UTC-UNKNOWN"),
        (0x0604, "EC-IMP-FEATURE-DISABLED"),
        (0x0605, "EC-IMP-FEATURE-MISSING"),
        (0x0700, "EC-LOCALE"),
        (0x0701, "EC-LOCALE-IMP"),
        (0x0702, "EC-LOCALE-INCOMPATIBLE"),
        (0x0703, "EC-LOCALE-INVALID"),
        (0x0704, "EC-LOCALE-INVALID-PTR"),
        (0x0705, "EC-LOCALE-MISSING"),
        (0x0706, "EC-LOCALE-SIZE"),
        (0x1A00, "EC-MCS"),
        (0x1A01, "EC-MCS-ABNORMAL-TERMINATION"),
        (0x1A02, "EC-MCS-IMP"),
        (0x1A03, "EC-MCS-INVALID-TAG"),
        (0x1A04, "EC-MCS-MESSAGE-LENGTH"),
        (0x1A05, "EC-MCS-NO-REQUESTER"),
        (0x1A06, "EC-MCS-NO-SERVER"),
        (0x1A07, "EC-MCS-NORMAL-TERMINATION"),
        (0x1A08, "EC-MCS-REQUESTOR-FAILED"),
        (0x0800, "EC-OO"),
        (0x0803, "EC-OO-ARG-OMITTED"),
        (0x0801, "EC-OO-CONFORMANCE"),
        (0x0802, "EC-OO-EXCEPTION"),
        (0x0804, "EC-OO-IMP"),
        (0x0805, "EC-OO-METHOD"),
        (0x0806, "EC-OO-NULL"),
        (0x0807, "EC-OO-RESOURCE"),
        (0x0808, "EC-OO-UNIVERSAL"),
        (0x0900, "EC-ORDER"),
        (0x0901, "EC-ORDER-IMP"),
        (0x0902, "EC-ORDER-NOT-SUPPORTED"),
        (0x0A00, "EC-OVERFLOW"),
        (0x0A01, "EC-OVERFLOW-IMP"),
        (0x0A02, "EC-OVERFLOW-STRING"),
        (0x0A03, "EC-OVERFLOW-UNSTRING"),
        (0x0B00, "EC-PROGRAM"),
        (0x0B01, "EC-PROGRAM-ARG-MISMATCH"),
        (0x0B02, "EC-PROGRAM-ARG-OMITTED"),
        (0x0B03, "EC-PROGRAM-CANCEL-ACTIVE"),
        (0x0B04, "EC-PROGRAM-IMP"),
        (0x0B05, "EC-PROGRAM-NOT-FOUND"),
        (0x0B06, "EC-PROGRAM-PTR-NULL"),
        (0x0B07, "EC-PROGRAM-RECURSIVE-CALL"),
        (0x0B08, "EC-PROGRAM-RESOURCES"),
        (0x0C00, "EC-RAISING"),
        (0x0C01, "EC-RAISING-IMP"),
        (0x0C02, "EC-RAISING-NOT-SPECIFIED"),
        (0x0D00, "EC-RANGE"),
        (0x0D01, "EC-RANGE-IMP"),
        (0x0D02, "EC-RANGE-INDEX"),
        (0x0D03, "EC-RANGE-INSPECT-SIZE"),
        (0x0D04, "EC-RANGE-INVALID"),
        (0x0D05, "EC-RANGE-PERFORM-VARYING"),
        (0x0D06, "EC-RANGE-PTR"),
        (0x0D07, "EC-RANGE-SEARCH-INDEX"),
        (0x0D08, "EC-RANGE-SEARCH-NO-MATCH"),
        (0x0E00, "EC-REPORT"),
        (0x0E01, "EC-REPORT-ACTIVE"),
        (0x0E02, "EC-REPORT-COLUMN-OVERLAP"),
        (0x0E03, "EC-REPORT-FILE-MODE"),
        (0x0E04, "EC-REPORT-IMP"),
        (0x0E05, "EC-REPORT-INACTIVE"),
        (0x0E06, "EC-REPORT-LINE-OVERLAP"),
        (0x0E08, "EC-REPORT-NOT-TERMINATED"),
        (0x0E09, "EC-REPORT-PAGE-LIMIT"),
        (0x0E0A, "EC-REPORT-PAGE-WIDTH"),
        (0x0E0B, "EC-REPORT-SUM-SIZE"),
        (0x0E0C, "EC-REPORT-VARYING"),
        (0x0F00, "EC-SCREEN"),
        (0x0F01, "EC-SCREEN-FIELD-OVERLAP"),
        (0x0F02, "EC-SCREEN-IMP"),
        (0x0F03, "EC-SCREEN-ITEM-TRUNCATED"),
        (0x0F04, "EC-SCREEN-LINE-NUMBER"),
        (0x0F05, "EC-SCREEN-STARTING-COLUMN"),
        (0x1000, "EC-SIZE"),
        (0x1001, "EC-SIZE-ADDRESS"),
        (0x1002, "EC-SIZE-EXPONENTIATION"),
        (0x1003, "EC-SIZE-IMP"),
        (0x1004, "EC-SIZE-OVERFLOW"),
        (0x1005, "EC-SIZE-TRUNCATION"),
        (0x1006, "EC-SIZE-UNDERFLOW"),
        (0x1007, "EC-SIZE-ZERO-DIVIDE"),
        (0x1100, "EC-SORT-MERGE"),
        (0x1101, "EC-SORT-MERGE-ACTIVE"),
        (0x1102, "EC-SORT-MERGE-FILE-OPEN"),
        (0x1103, "EC-SORT-MERGE-IMP"),
        (0x1104, "EC-SORT-MERGE-RELEASE"),
        (0x1105, "EC-SORT-MERGE-RETURN"),
        (0x1106, "EC-SORT-MERGE-SEQUENCE"),
        (0x1200, "EC-STORAGE"),
        (0x1201, "EC-STORAGE-IMP"),
        (0x1202, "EC-STORAGE-NOT-ALLOC"),
        (0x1203, "EC-STORAGE-NOT-AVAIL"),
        (0x1300, "EC-USER"),
        (0x1400, "EC-VALIDATE"),
        (0x1401, "EC-VALIDATE-CONTENT"),
        (0x1402, "EC-VALIDATE-FORMAT"),
        (0x1403, "EC-VALIDATE-IMP"),
        (0x1404, "EC-VALIDATE-RELATION"),
        (0x1405, "EC-VALIDATE-VARYING"),
        (0x1600, "EC-XML"),
        (0x1601, "EC-XML-CODESET"),
        (0x1602, "EC-XML-CODESET-CONVERSION"),
        (0x1603, "EC-XML-COUNT"),
        (0x1604, "EC-XML-DOCUMENT-TYPE"),
        (0x1605, "EC-XML-IMPLICIT-CLOSE"),
        (0x1606, "EC-XML-INVALID"),
        (0x1607, "EC-XML-NAMESPACE"),
        (0x1608, "EC-XML-STACKED-OPEN"),
        (0x1609, "EC-XML-RANGE"),
        (0x1610, "EC-XML-IMP"),
        (0x1700, "EC-JSON"),
        (0x1710, "EC-JSON-IMP"),
        (0, "Invalid"),
    };

    /// <summary>cob_get_last_exception_name, including its fall-through to the "Invalid" sentinel.</summary>
    static string LastExceptionName(int code)
    {
        var tab = ExceptionTable;
        for (int n = 1; n < tab.Length; n++)
            if (code == tab[n].code) return tab[n].name;
        if ((code & EC_IMP_FEATURE_MISSING) == EC_IMP_FEATURE_MISSING) return "EC-IMP-FEATURE-MISSING";
        if ((code & EC_IMP_FEATURE_DISABLED) == EC_IMP_FEATURE_DISABLED) return "EC-IMP-FEATURE-DISABLED";
        for (int n = tab.Length - 1; n != 0; n--)
            if ((code & tab[n].code) == tab[n].code) return tab[n].name;   // the 0 sentinel always matches
        return null;
    }

    /// <summary>EXCEPTION-STATUS: X(31), the name of the last exception or spaces.</summary>
    public static CobField cob_intr_exception_status()
    {
        var f = IntrField(TYPE_ALPHANUMERIC, 0, 0, 0, 31);
        var d = f.data.Span(31);
        d.Fill((byte)' ');
        int code = LastExceptionCode;
        if (code != 0)
        {
            var name = LastExceptionName(code) ?? "EXCEPTION-OBJECT";
            Latin1Bytes(name).AsSpan(0, Math.Min(name.Length, 31)).CopyTo(d);
        }
        return f;
    }

    /// <summary>
    /// EXCEPTION-STATEMENT: X(31). libcob fills it from module->statement, which is only
    /// recorded with runtime checks/tracing; we never record it, so it is spaces.
    /// </summary>
    public static CobField cob_intr_exception_statement()
    {
        var f = IntrField(TYPE_ALPHANUMERIC, 0, 0, 0, 31);
        f.data.Span(31).Fill((byte)' ');
        return f;
    }

    /// <summary>EXCEPTION-FILE: file status + SELECT name of the file in error, else "00".</summary>
    public static CobField cob_intr_exception_file()
    {
        var file = Global.cob_error_file;
        // cob_last_exception_is (COB_EC_I_O): (code & 0x0500) == 0x0500 (so EC-LOCALE matches too, as in libcob)
        if (file == null || (LastExceptionCode & CobException.EC_I_O) != CobException.EC_I_O)
            return IntrAlnum("00"u8);
        var name = Latin1Bytes(file.select_name ?? "");
        var f = IntrField(TYPE_ALPHANUMERIC, 0, 0, 0, name.Length + 2);
        f.data[0] = file.file_status[0];
        f.data[1] = file.file_status[1];
        name.CopyTo(f.data.Span(f.size).Slice(2));
        return f;
    }

    /// <summary>
    /// EXCEPTION-LOCATION: "program; paragraph OF section; line", or one space when no
    /// exception is active. libcob records the location when the exception is raised;
    /// we read the module's current one (the same when nothing traces paragraphs/lines).
    /// </summary>
    public static CobField cob_intr_exception_location()
    {
        var mod = CurrentModule;
        if (LastExceptionCode == 0 || mod?.module_name == null)
        {
            var f = IntrField(TYPE_ALPHANUMERIC, 0, 0, 0, 1);
            f.data[0] = (byte)' ';
            return f;
        }
        string id = mod.module_name, sec = mod.section_name, para = mod.paragraph_name;
        uint line = (uint)(mod.module_stmt & 0xFFFFF);
        string text;
        if (sec != null && para != null) text = $"{id}; {para} OF {sec}; {line}";
        else if (sec != null) text = $"{id}; {sec}; {line}";
        else if (para != null) text = $"{id}; {para}; {line}";
        else text = $"{id}; ; {line}";
        if (text.Length > 255) text = text.Substring(0, 255);   // COB_SMALL_MAX
        return IntrStr(text);
    }

    public static CobField cob_intr_exception_file_n() => NotImplemented();
    public static CobField cob_intr_exception_location_n() => NotImplemented();

    // ------------------------------------------------------------ national (not implemented in libcob 3.2)

    public static CobField cob_intr_char_national(CobField srcfield) => NotImplemented();
    public static CobField cob_intr_display_of(int offset, int length, int @params, params CobField[] args) => NotImplemented();
    public static CobField cob_intr_national_of(int offset, int length, int @params, params CobField[] args) => NotImplemented();

    // ------------------------------------------------------------ CONTENT-LENGTH, CONTENT-OF

    /// <summary>
    /// The data a USAGE POINTER field points to. Our storage is managed, and a pointer
    /// value cannot be followed, so every pointer reads as NULL (EC-DATA-PTR-NULL).
    /// </summary>
    static byte[] PointedData(CobField srcfield) => null;

    public static CobField cob_intr_content_length(CobField srcfield)
    {
        SetException(0);
        var p = srcfield == null ? null : PointedData(srcfield);
        uint val = 0;
        if (p != null && p.Length > 0 && p[0] != 0)
        {
            int n = Array.IndexOf(p, (byte)0);
            val = (uint)(n < 0 ? p.Length : n);
        }
        else SetException(EC_DATA_PTR_NULL);
        return IntrUint(val);
    }

    public static CobField cob_intr_content_of(int offset, int length, int @params, params CobField[] args)
    {
        SetException(0);
        var srcfield = args.Length > 0 ? args[0] : null;
        uint requestLen = @params > 1 ? (uint)cob_get_int(args[1]) : 0;
        var p = srcfield == null ? null : PointedData(srcfield);
        long size = 0;
        if (p != null && p.Length > 0 && p[0] != 0)
        {
            if (requestLen != 0) size = requestLen;
            else { int n = Array.IndexOf(p, (byte)0); size = n < 0 ? p.Length : n; }
            if (size > COB_MAX_UNBOUNDED_SIZE) { SetException(CobException.EC_SIZE_TRUNCATION); size = COB_MAX_UNBOUNDED_SIZE; }
        }
        else SetException(EC_DATA_PTR_NULL);

        CobField f;
        if (size != 0)
        {
            f = IntrField(TYPE_ALPHANUMERIC, 0, 0, 0, size);
            p.AsSpan(0, (int)Math.Min(size, p.Length)).CopyTo(f.data.Span(size));
        }
        else f = IntrEmpty();
        return offset > 0 ? IntrRefMod(f, offset, length) : f;
    }

    // ------------------------------------------------------------ locale

    // libcob reads localeconv () after setlocale (LC_ALL, "") and then LC_NUMERIC "C":
    // monetary values come from the user's locale, numeric ones are the "C" locale's.
    static CobField LocaleText(string s)
    {
        Global.cob_exception_code = 0;
        return string.IsNullOrEmpty(s) ? IntrEmptyLow() : IntrAlnum(Latin1Bytes(s));
    }

    /// <summary>An empty locale value: size 0, data[0] = NUL.</summary>
    static CobField IntrEmptyLow()
    {
        var f = IntrField(TYPE_ALPHANUMERIC, 0, 0, 0, 1);
        f.size = 0;
        return f;
    }

    public static CobField cob_intr_mon_decimal_point() => LocaleText(CultureInfo.CurrentCulture.NumberFormat.CurrencyDecimalSeparator);
    public static CobField cob_intr_num_decimal_point() => LocaleText(".");
    public static CobField cob_intr_mon_thousands_sep() => LocaleText(CultureInfo.CurrentCulture.NumberFormat.CurrencyGroupSeparator);
    public static CobField cob_intr_num_thousands_sep() => LocaleText("");
    public static CobField cob_intr_currency_symbol() => LocaleText(CultureInfo.CurrentCulture.NumberFormat.CurrencySymbol);
}
