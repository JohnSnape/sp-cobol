// INSPECT (TALLYING / REPLACING / CONVERTING) and UNSTRING.
//
// Same entry points and the same algorithm as GnuCOBOL 3.2's libcob/strings.c, so results
// match stock exactly, including its details:
//   - INSPECT works phrase by phrase across the BEFORE/AFTER region. A position that one
//     phrase counted or replaced is marked, and later phrases skip it.
//   - REPLACING writes into a side buffer; the marked positions are copied back at the end
//     (cob_inspect_finish), so BEFORE/AFTER searches see the original data.
//   - A signed numeric DISPLAY item is inspected without its overpunched sign, which is put
//     back afterwards.
//   - UNSTRING: DELIMITED BY [ALL], COUNT IN, DELIMITER IN, POINTER, TALLYING, ON OVERFLOW.

using System;
using static CobolDotNet.Runtime.CobConst;

namespace CobolDotNet.Runtime;

public static partial class Cob
{
    public const int EC_OVERFLOW_UNSTRING = 0x0A03;
    public const int EC_RANGE_INSPECT_SIZE = 0x0D03;

    // ------------------------------------------------------------ INSPECT state

    static CobField inspectVar;          // signed DISPLAY item whose sign is put back at the end
    static int inspectSign;
    static CobPtr inspectData;
    static int inspectSize;
    static int inspectStart, inspectEnd; // region, offsets into inspectData
    static bool inspectReplacing;
    static byte[] inspectMark = Array.Empty<byte>();
    static byte[] inspectRep = Array.Empty<byte>();
    static int markMin, markMax;
    static bool anyMark;

    static readonly CobField LowValue = new CobField(1, CobPtr.FromLatin1("\0"),
        new CobFieldAttr(TYPE_ALPHANUMERIC_ALL, 0, 0, 0, null));

    static void InspectInitCommon(CobField var)
    {
        inspectVar = null;
        if (var.Has(FLAG_HAVE_SIGN) && !var.Has(FLAG_SIGN_SEPARATE) && var.Type == TYPE_NUMERIC_DISPLAY)
        {
            inspectVar = var;
            inspectSign = TakeSign(var);
        }
        inspectData = var.data;
        inspectSize = (int)var.size;
        inspectStart = inspectEnd = 0;
        Global.cob_exception_code = 0;
    }

    public static void cob_inspect_init(CobField var, int replacing)
    {
        InspectInitCommon(var);
        inspectReplacing = replacing != 0;
        if (inspectMark.Length < inspectSize + 1) inspectMark = new byte[Math.Max(inspectSize + 1, 1024)];
        else Array.Clear(inspectMark, 0, inspectSize + 1);
        markMin = markMax = 0;
        anyMark = false;
    }

    public static void cob_inspect_init(CobField var, uint replacing) => cob_inspect_init(var, (int)replacing);

    public static void cob_inspect_init_converting(CobField var)
    {
        InspectInitCommon(var);
        inspectReplacing = false;
    }

    public static void cob_inspect_start()
    {
        inspectStart = 0;
        inspectEnd = inspectSize;
    }

    static int InspectFind(CobField str)
    {
        int len = (int)str.size;
        var s = str.Span;
        for (int p = inspectStart; p + len <= inspectEnd; p++)
            if (inspectData.Span(inspectSize).Slice(p, len).SequenceEqual(s)) return p;
        return -1;
    }

    public static void cob_inspect_before(CobField str)
    {
        int p = InspectFind(str);
        if (p >= 0) inspectEnd = p;
    }

    public static void cob_inspect_after(CobField str)
    {
        int p = InspectFind(str);
        inspectStart = p >= 0 ? p + (int)str.size : inspectEnd;
    }

    static void SetMark(int pos, int length)
    {
        if (length <= 0) return;
        Array.Fill(inspectMark, (byte)1, pos, length);
        if (!anyMark || pos < markMin) markMin = pos;
        if (pos + length - 1 > markMax || !anyMark) markMax = Math.Max(markMax, pos + length - 1);
        anyMark = true;
    }

    static bool IsMarked(int pos, int length)
    {
        if (!anyMark || markMax < pos || markMin >= pos + length) return false;
        for (int i = 0; i < length; i++) if (inspectMark[pos + i] != 0) return true;
        return false;
    }

    static void SetupRep()
    {
        if (inspectRep.Length < inspectSize + 1) inspectRep = new byte[Math.Max(inspectSize + 1, 1024)];
    }

    public static void cob_inspect_characters(CobField f1)
    {
        int pos = inspectStart, len = inspectEnd - inspectStart;
        if (len <= 0) return;
        if (inspectReplacing)
        {
            byte by = f1.data[0];
            SetupRep();
            for (int i = 0; i < len; i++) if (inspectMark[pos + i] == 0) inspectRep[pos + i] = by;
        }
        else
        {
            int n = 0;
            for (int i = 0; i < len; i++) if (inspectMark[pos + i] == 0) n++;
            if (n > 0) cob_add_int(f1, n, 0);
        }
        SetMark(pos, len);
    }

    enum InspectType { All, Leading, First, Trailing }

    public static void cob_inspect_all(CobField f1, CobField f2) => InspectCommon(f1, f2, InspectType.All);
    public static void cob_inspect_leading(CobField f1, CobField f2) => InspectCommon(f1, f2, InspectType.Leading);
    public static void cob_inspect_first(CobField f1, CobField f2) => InspectCommon(f1, f2, InspectType.First);
    public static void cob_inspect_trailing(CobField f1, CobField f2) => InspectCommon(f1, f2, InspectType.Trailing);

    /// <summary>ALL "x" figurative repeated to the other operand's length.</summary>
    static CobField Figurative(CobField fig, long size)
    {
        var p = new CobPtr(Math.Max(size, 1));
        var s = fig.Span;
        for (int i = 0; i < size; i++) p[i] = s[i % s.Length];
        return new CobField(size, p, new CobFieldAttr(TYPE_ALPHANUMERIC, 0, 0, 0, null));
    }

    static bool Matches(int at, CobField f2) =>
        inspectData.Span(inspectSize).Slice(at, (int)f2.size).SequenceEqual(f2.Span);

    static void InspectCommon(CobField f1, CobField f2, InspectType type)
    {
        int pos = inspectStart, len = inspectEnd - inspectStart;
        if (len <= 0) return;
        f1 ??= LowValue;
        f2 ??= LowValue;
        if (inspectReplacing && f1.size != f2.size)
        {
            if (f1.Type == TYPE_ALPHANUMERIC_ALL) f1 = Figurative(f1, f2.size);
            else { SetException(EC_RANGE_INSPECT_SIZE); return; }
        }
        int fs = (int)f2.size;
        if (fs > len || fs == 0) return;

        if (!inspectReplacing)
        {
            int n = 0;
            if (type == InspectType.Trailing)
            {
                int firstMarker = 0;
                for (int i = len - fs; ; i--)
                {
                    if (!Matches(pos + i, f2)) break;
                    if (!IsMarked(pos + i, fs)) { n++; firstMarker = i; i -= fs - 1; }
                    if (i <= 0) break;
                }
                if (n > 0) SetMark(pos + firstMarker, len - firstMarker);
            }
            else if (type == InspectType.Leading)
            {
                int lastMarker = 0;
                for (int i = 0; i < len - fs + 1; i++)
                {
                    if (!Matches(pos + i, f2)) break;
                    if (!IsMarked(pos + i, fs)) { n++; i += fs - 1; lastMarker = i; }
                }
                if (n > 0) SetMark(pos, lastMarker);    // as libcob (marks pos .. pos+lastMarker-1)
            }
            else
            {
                for (int i = 0; i < len - fs + 1; i++)
                {
                    if (!Matches(pos + i, f2)) continue;
                    if (IsMarked(pos + i, fs)) continue;
                    n++;
                    SetMark(pos + i, fs);
                    if (type == InspectType.First) break;
                    i += fs - 1;
                }
            }
            if (n != 0) cob_add_int(f1, n, 0);
            return;
        }

        // REPLACING
        if (type == InspectType.Trailing)
        {
            for (int i = len - fs; ; i--)
            {
                if (!Matches(pos + i, f2)) break;
                if (DoMark(pos + i, fs, f1)) i -= fs - 1;
                if (i <= 0) break;
            }
        }
        else if (type == InspectType.Leading)
        {
            for (int i = 0; i < len - fs + 1; i++)
            {
                if (!Matches(pos + i, f2)) break;
                if (DoMark(pos + i, fs, f1)) i += fs - 1;
            }
        }
        else
        {
            for (int i = 0; i < len - fs + 1; i++)
            {
                if (!Matches(pos + i, f2)) continue;
                if (DoMark(pos + i, fs, f1))
                {
                    if (type == InspectType.First) break;
                    i += fs - 1;
                }
            }
        }
    }

    static bool DoMark(int pos, int length, CobField by)
    {
        if (IsMarked(pos, length)) return false;
        SetupRep();
        by.Span.Slice(0, length).CopyTo(inspectRep.AsSpan(pos, length));
        SetMark(pos, length);
        return true;
    }

    public static void cob_inspect_converting(CobField f1, CobField f2)
    {
        int len = inspectEnd - inspectStart;
        if (len <= 0) return;
        f1 ??= LowValue;
        f2 ??= LowValue;
        if (f1.size != f2.size)
        {
            if (f2.Type == TYPE_ALPHANUMERIC_ALL) f2 = Figurative(f2, f1.size);
            else { SetException(EC_RANGE_INSPECT_SIZE); return; }
        }
        var tab = new byte[256];
        var set = new bool[256];
        var from = f1.Span; var to = f2.Span;
        for (int i = 0; i < from.Length; i++)
            if (!set[from[i]]) { set[from[i]] = true; tab[from[i]] = to[i]; }
        var d = inspectData.Span(inspectSize);
        for (int i = inspectStart; i < inspectEnd; i++) if (set[d[i]]) d[i] = tab[d[i]];
        if (inspectVar != null) PutSign(inspectVar, inspectSign);
    }

    public static void cob_inspect_finish()
    {
        if (inspectReplacing && anyMark && inspectRep.Length > 0)
        {
            var d = inspectData.Span(inspectSize);
            for (int i = markMin; i <= markMax && i < inspectSize; i++)
                if (inspectMark[i] != 0) d[i] = inspectRep[i];
        }
        if (inspectVar != null) PutSign(inspectVar, inspectSign);
    }

    // overpunched sign of a signed DISPLAY item (GnuCOBOL ASCII: 'p'..'y' = -0..-9)
    static int SignPos(CobField f) => f.Has(FLAG_SIGN_LEADING) ? 0 : (int)f.size - 1;

    static int TakeSign(CobField f)
    {
        if (f.size == 0) return 1;
        int p = SignPos(f);
        byte b = f.data[p];
        if (b >= (byte)'p' && b <= (byte)'y') { f.data[p] = (byte)(b & 0x3F); return -1; }
        return 1;
    }

    static void PutSign(CobField f, int sign)
    {
        if (sign >= 0 || f.size == 0) return;
        int p = SignPos(f);
        byte b = f.data[p];
        if (b >= (byte)'0' && b <= (byte)'9') f.data[p] = (byte)(b | 0x40);
    }

    // ------------------------------------------------------------ UNSTRING

    static CobField unsSrc, unsPtr;
    static int unsOffset, unsCount;
    static readonly System.Collections.Generic.List<(CobField dlm, bool all)> unsDelims = new();

    public static void cob_unstring_init(CobField src, CobField ptr, long num_dlm)
    {
        unsSrc = src;
        unsPtr = ptr;
        unsOffset = 0;
        unsCount = 0;
        unsDelims.Clear();
        Global.cob_exception_code = 0;
        if (ptr != null)
        {
            unsOffset = cob_get_int(ptr) - 1;
            if (unsOffset < 0 || unsOffset >= src.size) SetException(EC_OVERFLOW_UNSTRING);
        }
    }

    public static void cob_unstring_delimited(CobField dlm, int all) => unsDelims.Add((dlm, all != 0));
    public static void cob_unstring_delimited(CobField dlm, uint all) => cob_unstring_delimited(dlm, (int)all);

    /// <summary>COBOL MOVE of raw bytes (alphanumeric) into a field.</summary>
    static void StrMove(CobField dst, CobPtr data, int size)
    {
        var tmp = new CobField(size, data, new CobFieldAttr(TYPE_ALPHANUMERIC, 0, 0, 0, null));
        cob_move(tmp, dst);
    }

    public static void cob_unstring_into(CobField dst, CobField dlm, CobField cnt)
    {
        if (Global.cob_exception_code != 0) return;
        int srsize = (int)unsSrc.size;
        if (unsOffset >= srsize) return;

        var src = unsSrc.Span;
        var start = unsSrc.data + unsOffset;
        int matchSize = 0;
        CobField found = null;

        if (unsDelims.Count == 0)
        {
            matchSize = Math.Min((int)dst.size, srsize - unsOffset);
            StrMove(dst, start, matchSize);
            unsOffset += matchSize;
        }
        else
        {
            for (int p = unsOffset; p < srsize && found == null; p++)
            {
                foreach (var (d, all) in unsDelims)
                {
                    int dl = (int)d.size;
                    if (dl == 0 || p + dl > srsize) continue;
                    if (!src.Slice(p, dl).SequenceEqual(d.Span)) continue;
                    matchSize = p - unsOffset;
                    StrMove(dst, start, matchSize);
                    unsOffset += matchSize + dl;
                    found = d;
                    if (all)
                    {
                        // libcob: further copies only while one more fits before the end
                        for (int q = p + dl; q < srsize - dl + 1; q += dl)
                        {
                            if (!src.Slice(q, dl).SequenceEqual(d.Span)) break;
                            unsOffset += dl;
                        }
                    }
                    break;
                }
            }
            if (found == null)
            {
                matchSize = srsize - unsOffset;
                StrMove(dst, start, matchSize);
                unsOffset = srsize;
            }
        }
        unsCount++;

        if (dlm != null)
        {
            if (found != null) StrMove(dlm, found.data, (int)found.size);
            else if (IsNumeric(dlm.Type)) cob_set_int(dlm, 0);
            else dlm.Span.Fill((byte)' ');
        }
        if (cnt != null) cob_set_int(cnt, matchSize);
    }

    // cobc writes a literal 0 (not NULL) for a missing DELIMITER IN / COUNT IN
    public static void cob_unstring_into(CobField dst, int dlm, int cnt) => cob_unstring_into(dst, null, null);
    public static void cob_unstring_into(CobField dst, CobField dlm, int cnt) => cob_unstring_into(dst, dlm, null);
    public static void cob_unstring_into(CobField dst, int dlm, CobField cnt) => cob_unstring_into(dst, null, cnt);

    public static void cob_unstring_tallying(CobField f) => cob_add_int(f, unsCount, 0);

    public static void cob_unstring_finish()
    {
        if (unsOffset < unsSrc.size) SetException(EC_OVERFLOW_UNSTRING);
        if (unsPtr != null) cob_set_int(unsPtr, unsOffset + 1);
    }
}
