// DIVIDE ... GIVING q REMAINDER r (libcob numeric.c cob_div_quotient / cob_div_remainder).
//
// The quotient is stored first (rounded or truncated to its field), then the remainder is
// computed from the quotient as stored: remainder = dividend - (quotient truncated to the
// quotient field's decimal places) * divisor. Two calls, because COBOL evaluates the
// remainder target only after the quotient is stored (REMAINDER FLD (Q) is legal).

namespace CobolDotNet.Runtime;

public static partial class Cob
{
    static readonly CobDecimal divRemainder = new();

    public static void cob_div_quotient(CobField dividend, CobField divisor, CobField quotient, int opt)
    {
        var d1 = new CobDecimal(); var d2 = new CobDecimal();
        cob_decimal_set_field(d1, dividend);
        cob_decimal_set_field(d2, divisor);
        divRemainder.value = d1.value;
        divRemainder.scale = d1.scale;

        cob_decimal_div(d1, d2);
        if (d1.scale == DecimalNaN) { divRemainder.scale = DecimalNaN; return; }

        var d3 = new CobDecimal { value = d1.value, scale = d1.scale };
        cob_decimal_get_field(d1, quotient, opt);

        // truncate the quotient to the quotient field's decimal places (shift_decimal)
        int n = quotient.attr.scale - d3.scale;
        if (n != 0)
        {
            if (d3.value.IsZero) d3.scale = 0;
            else
            {
                if (n > 0) d3.value *= Num.Pow10(n);
                else d3.value /= Num.Pow10(-n);      // BigInteger division truncates toward zero, like mpz_tdiv_q
                d3.scale += n;
            }
        }
        cob_decimal_mul(d3, d2);
        cob_decimal_sub(divRemainder, d3);
    }

    public static void cob_div_remainder(CobField remainder, int opt) => cob_decimal_get_field(divRemainder, remainder, opt);
}
