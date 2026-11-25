// Numeric editing: MOVE a number into a PICTURE like $***,**9.99,
// ZZ,ZZ9.99CR, $$$,$$9.99, ++++9, 99/99/99 ...
//
// Written from the COBOL rules, not from libcob:
//   * 9 always shows a digit and ends zero suppression.
//   * Z and * suppress leading zeros (with space or asterisk).
//   * A repeated $ + or - is a floating symbol: it lands just left of
//     the first significant digit; the first one in the run is not a
//     digit position.
//   * , B 0 / are insertion characters; while suppressing they become
//     the fill character.
//   * . ends suppression (the point is printed); V is an invisible point.
//   * A single + - $ is fixed; CR and DB print only for negatives.
//   * A zero value with no 9 in the picture prints all fill
//     (spaces, or asterisks with the point kept for check protection).

using System;
using System.Numerics;
using System.Text;
using static CobolDotNet.Runtime.CobConst;

namespace CobolDotNet.Runtime;

internal static class Edit
{
    /// <summary>
    /// The PICTURE keeps the program's own symbols: with DECIMAL-POINT IS COMMA it is
    /// ZZ.ZZ9,99, with CURRENCY SIGN IS "#" it is ###9. The editing below works in the
    /// default symbols (. point, , separator, $ currency) and prints the program's.
    /// </summary>
    static char Canonical(char c)
    {
        if (c == Cob.DecimalPoint) return '.';
        if (c == Cob.NumericSeparator) return ',';
        if (c == Cob.CurrencySymbol) return '$';
        return c;
    }

    static string Expand(CobPicSymbol[] pic)
    {
        var sb = new StringBuilder();
        if (pic == null) return "";
        foreach (var p in pic)
        {
            if (p.symbol == '\0') break;
            // cobc stores CR / DB as one symbol 'C' / 'D' (two print positions)
            if (p.symbol == 'C') { sb.Append("CR"); continue; }
            if (p.symbol == 'D') { sb.Append("DB"); continue; }
            sb.Append(Canonical(p.symbol), p.times_repeated);
        }
        return sb.ToString();
    }

    /// <summary>v is already scaled to the field's scale.</summary>
    internal static void Format(CobField f, BigInteger v)
    {
        string pic = Expand(f.attr.pic);
        var dst = f.Span;
        bool neg = v.Sign < 0;
        bool zero = v.IsZero;
        char cur = Cob.CurrencySymbol;
        char point = Cob.DecimalPoint;
        char comma = Cob.NumericSeparator;

        if (f.Has(FLAG_BLANK_ZERO) && zero) { dst.Fill((byte)' '); return; }

        // --- find a floating symbol: first of $ + - that repeats
        char floatSym = '\0';
        int floatStart = -1, floatEnd = -1;
        foreach (char c in "$+-")
        {
            int first = pic.IndexOf(c);
            if (first >= 0 && pic.IndexOf(c, first + 1) >= 0)
            {
                if (floatStart < 0 || first < floatStart) { floatSym = c; floatStart = first; }
            }
        }
        if (floatSym != '\0') floatEnd = pic.LastIndexOf(floatSym);

        // --- count digit positions before/after the point
        int intPos = 0, fracPos = 0;
        bool after = false;
        bool hasNine = false;
        for (int i = 0; i < pic.Length; i++)
        {
            char c = pic[i];
            if (c == '.' || c == 'V' || c == 'v') { after = true; continue; }
            bool digitPos = c == '9' || c == 'Z' || c == 'z' || c == '*'
                         || (c == floatSym && i > floatStart && i <= floatEnd);
            if (c == '9') hasNine = true;
            if (!digitPos) continue;
            if (after) fracPos++; else intPos++;
        }

        // --- the digits, right-aligned into intPos.fracPos
        string abs = BigInteger.Abs(v).ToString();
        int scale = f.attr.scale;
        // v carries 'scale' fraction digits; line them up with fracPos
        string intDigits = abs.Length > scale ? abs.Substring(0, abs.Length - scale) : "";
        string fracDigits = abs.Length > scale ? abs.Substring(abs.Length - scale) : abs.PadLeft(scale, '0');
        if (fracDigits.Length > fracPos) fracDigits = fracDigits.Substring(0, fracPos);
        fracDigits = fracDigits.PadRight(fracPos, '0');
        if (intDigits.Length > intPos) intDigits = intDigits.Substring(intDigits.Length - intPos);
        intDigits = intDigits.PadLeft(intPos, '0');
        string digits = intDigits + fracDigits;

        char fill = pic.IndexOf('*') >= 0 ? '*' : ' ';
        var outc = new char[pic.Length];

        // all-zero, no 9: whole field is fill
        if (zero && !hasNine)
        {
            for (int i = 0; i < pic.Length; i++)
                outc[i] = fill == '*' && pic[i] == '.' ? point : fill;
            Write(dst, outc);
            return;
        }

        bool suppress = true;   // leading-zero suppression in effect
        int di = 0;             // next digit
        int lastBlank = -1;     // where a floating symbol can land
        bool floatPlaced = floatSym == '\0';

        char FloatChar() => floatSym == '$' ? cur : (floatSym == '+' ? (neg ? '-' : '+') : (neg ? '-' : ' '));

        void Significant()
        {
            if (!suppress) return;
            suppress = false;
            if (!floatPlaced && lastBlank >= 0) { outc[lastBlank] = FloatChar(); floatPlaced = true; }
        }

        for (int i = 0; i < pic.Length; i++)
        {
            char c = pic[i];
            bool inFloat = floatSym != '\0' && i >= floatStart && i <= floatEnd;

            if (c == '9')
            {
                Significant();
                outc[i] = digits[di++];
            }
            else if (c == 'Z' || c == 'z' || c == '*')
            {
                char d = digits[di++];
                if (suppress && d == '0') outc[i] = c == '*' ? '*' : ' ';
                else { Significant(); outc[i] = d; }
            }
            else if (c == floatSym && inFloat)
            {
                if (i == floatStart)
                {
                    outc[i] = ' ';
                    lastBlank = i;
                }
                else
                {
                    char d = digits[di++];
                    if (suppress && d == '0') { outc[i] = ' '; lastBlank = i; }
                    else { Significant(); outc[i] = d; }
                }
            }
            else if (c == ',' || c == 'B' || c == 'b' || c == '0' || c == '/')
            {
                char ins = c == ',' ? comma : (c == '0' ? '0' : (c == '/' ? '/' : ' '));
                if (suppress)
                {
                    outc[i] = inFloat ? ' ' : fill;
                    if (inFloat) lastBlank = i;
                }
                else outc[i] = ins;
            }
            else if (c == '.')
            {
                Significant();
                outc[i] = point;
            }
            else if (c == 'V' || c == 'v' || c == 'P' || c == 'p')
            {
                outc[i] = '\0'; // no print position
            }
            else if (c == '$')
            {
                outc[i] = cur;
            }
            else if (c == '+')
            {
                outc[i] = neg ? '-' : '+';
            }
            else if (c == '-')
            {
                outc[i] = neg ? '-' : ' ';
            }
            else if ((c == 'C' || c == 'c') && i + 1 < pic.Length && (pic[i + 1] == 'R' || pic[i + 1] == 'r'))
            {
                outc[i] = neg ? 'C' : ' ';
                outc[++i] = neg ? 'R' : ' ';
            }
            else if ((c == 'D' || c == 'd') && i + 1 < pic.Length && (pic[i + 1] == 'B' || pic[i + 1] == 'b'))
            {
                outc[i] = neg ? 'D' : ' ';
                outc[++i] = neg ? 'B' : ' ';
            }
            else
            {
                outc[i] = c;
            }
        }
        Write(dst, outc);
    }

    static void Write(Span<byte> dst, char[] outc)
    {
        int j = 0;
        foreach (char c in outc)
        {
            if (c == '\0') continue; // V / P take no space
            if (j < dst.Length) dst[j++] = (byte)c;
        }
        while (j < dst.Length) dst[j++] = (byte)' ';
    }
}
