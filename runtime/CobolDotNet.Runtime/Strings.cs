// STRING statement:
//   cob_string_init (dst, pointer-or-null)
//   { cob_string_delimited (dlm-or-null = SIZE); cob_string_append (src); } ...
//   cob_string_finish ()

using System;

namespace CobolDotNet.Runtime;

public static partial class Cob
{
    static CobField strDst, strPtr, strDlm;
    static int strPos;       // 0-based next position in strDst
    static bool strOverflow;

    public static void cob_string_init(CobField dst, CobField ptr)
    {
        strDst = dst;
        strPtr = ptr;
        strDlm = null;
        strOverflow = false;
        Global.cob_exception_code = 0;
        strPos = 0;
        if (ptr != null)
        {
            strPos = cob_get_int(ptr) - 1;
            if (strPos < 0 || strPos >= dst.size)
            {
                strOverflow = true;   // pointer outside the target: ON OVERFLOW (libcob cob_string_init)
                SetException(CobException.EC_OVERFLOW_STRING);
            }
        }
    }

    public static void cob_string_delimited(CobField dlm) => strDlm = dlm;

    public static void cob_string_append(CobField src)
    {
        if (strOverflow) return;
        var s = (ReadOnlySpan<byte>)src.Span;
        if (strDlm != null)
        {
            int at = s.IndexOf((ReadOnlySpan<byte>)strDlm.Span);
            if (at >= 0) s = s.Slice(0, at);
        }
        var d = strDst.Span;
        int room = d.Length - strPos;
        int n = Math.Min(room, s.Length);
        s.Slice(0, n).CopyTo(d.Slice(strPos));
        strPos += n;
        if (n < s.Length)
        {
            strOverflow = true;
            SetException(CobException.EC_OVERFLOW_STRING);
        }
    }

    public static void cob_string_finish()
    {
        if (strPtr != null) cob_set_int(strPtr, strPos + 1);
    }
}
