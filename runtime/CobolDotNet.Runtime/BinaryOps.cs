// Binary (COMP / COMP-5) and packed fast paths.
//
// cobc's code optimizer (codeoptim.c) normally writes these as small static C
// functions into every generated program: cob_cmp_u16, cob_addswp_s32,
// cob_setswp_u24, ... For C# output they live here instead, built from four
// generic routines. Naming, as in codeoptim.c:
//   cmp / add / sub      native byte order (COMP-5, BINARY with -fbinary-byteorder=native)
//   cmpswp / addswp /    big-endian (plain COMP / BINARY, the COBOL default)
//   subswp / setswp
//   _align_              same as the plain form (alignment doesn't matter here)
//   u / s + bits         unsigned / signed, 8 to 64 bits (24, 40, 48, 56 included)
// Arithmetic wraps at the field width, like the C originals.

using System;
using System.Numerics;
using static CobolDotNet.Runtime.CobConst;

namespace CobolDotNet.Runtime;

public static partial class Cob
{
    // ---------------------------------------------------------------- generic

    static ulong LoadRaw(CobPtr p, int bytes, bool bigEndian)
    {
        ulong v = 0;
        for (int i = 0; i < bytes; i++)
        {
            int at = bigEndian ? i : bytes - 1 - i;
            v = (v << 8) | p[at];
        }
        return v;
    }

    static void StoreRaw(CobPtr p, int bytes, bool bigEndian, ulong v)
    {
        for (int i = 0; i < bytes; i++)
        {
            int at = bigEndian ? bytes - 1 - i : i;
            p[at] = (byte)v;
            v >>= 8;
        }
    }

    static long SignExtend(ulong v, int bytes) => bytes == 8 ? (long)v : ((long)(v << (64 - 8 * bytes))) >> (64 - 8 * bytes);

    static int BinCmp(CobPtr p, int bytes, bool signed, bool bigEndian, long n)
    {
        ulong raw = LoadRaw(p, bytes, bigEndian);
        if (signed)
        {
            long v = SignExtend(raw, bytes);
            return v < n ? -1 : (v > n ? 1 : 0);
        }
        if (n < 0) return 1;
        ulong un = (ulong)n;
        return raw < un ? -1 : (raw > un ? 1 : 0);
    }

    static void BinAdd(CobPtr p, int bytes, bool bigEndian, long val)
        => StoreRaw(p, bytes, bigEndian, LoadRaw(p, bytes, bigEndian) + (ulong)val);

    static void BinSet(CobPtr p, int bytes, bool bigEndian, long val)
        => StoreRaw(p, bytes, bigEndian, (ulong)val);

    // ---------------------------------------------------------------- packed (COMP-3) integers

    public static int cob_get_packed_int(CobField f) => (int)Num.Get(f).v;
    public static long cob_get_packed_int64(CobField f) => (long)Num.Get(f).v;
    public static void cob_add_packed_int(CobField f, long val) { var (v, s) = Num.Get(f); Num.Store(f, v + val * Num.Pow10(s), s, 0); }
    public static void cob_add_packed_int64(CobField f, long val) => cob_add_packed_int(f, val);
    public static void cob_set_packed_int(CobField f, long val) => Num.Store(f, new BigInteger(val) * Num.Pow10(f.attr.scale), f.attr.scale, 0);
    public static int cob_cmp_packed(CobField f, long n) => cob_cmp_packed_int(f, n);
    public static int cob_cmp_packed_int(CobField f, long n)
    {
        var (v, s) = Num.Get(f);
        return v.CompareTo(new BigInteger(n) * Num.Pow10(s)) switch { < 0 => -1, > 0 => 1, _ => 0 };
    }

    // signed USAGE DISPLAY integer (sign overpunched in the last byte, or a leading '-')
    public static int cob_get_numdisps(CobPtr data, long size)
    {
        int v = 0; bool neg = false;
        for (int i = 0; i < size; i++)
        {
            byte b = data[i];
            if (b == (byte)'-') { neg = true; continue; }
            if (b == (byte)'+') continue;
            int d;
            if (b >= (byte)'0' && b <= (byte)'9') d = b - '0';
            else if (b >= (byte)'p' && b <= (byte)'y') { d = b - 'p'; neg = true; }
            else d = (b & 0x0F) % 10;
            v = v * 10 + d;
        }
        return neg ? -v : v;
    }

    // ---------------------------------------------------------------- generated entry points
    public static int cob_cmp_u8(CobPtr p, long n) => BinCmp(p, 1, false, false, n);
    public static void cob_add_u8(CobPtr p, long v) => BinAdd(p, 1, false, v);
    public static void cob_sub_u8(CobPtr p, long v) => BinAdd(p, 1, false, -v);
    public static int cob_cmp_s8(CobPtr p, long n) => BinCmp(p, 1, true, false, n);
    public static void cob_add_s8(CobPtr p, long v) => BinAdd(p, 1, false, v);
    public static void cob_sub_s8(CobPtr p, long v) => BinAdd(p, 1, false, -v);
    public static int cob_cmp_u16(CobPtr p, long n) => BinCmp(p, 2, false, false, n);
    public static void cob_add_u16(CobPtr p, long v) => BinAdd(p, 2, false, v);
    public static void cob_sub_u16(CobPtr p, long v) => BinAdd(p, 2, false, -v);
    public static int cob_cmpswp_u16(CobPtr p, long n) => BinCmp(p, 2, false, true, n);
    public static void cob_addswp_u16(CobPtr p, long v) => BinAdd(p, 2, true, v);
    public static void cob_subswp_u16(CobPtr p, long v) => BinAdd(p, 2, true, -v);
    public static void cob_setswp_u16(CobPtr p, long v) => BinSet(p, 2, true, v);
    public static int cob_cmp_align_u16(CobPtr p, long n) => BinCmp(p, 2, false, false, n);
    public static void cob_add_align_u16(CobPtr p, long v) => BinAdd(p, 2, false, v);
    public static void cob_sub_align_u16(CobPtr p, long v) => BinAdd(p, 2, false, -v);
    public static int cob_cmpswp_align_u16(CobPtr p, long n) => BinCmp(p, 2, false, true, n);
    public static int cob_cmp_s16(CobPtr p, long n) => BinCmp(p, 2, true, false, n);
    public static void cob_add_s16(CobPtr p, long v) => BinAdd(p, 2, false, v);
    public static void cob_sub_s16(CobPtr p, long v) => BinAdd(p, 2, false, -v);
    public static int cob_cmpswp_s16(CobPtr p, long n) => BinCmp(p, 2, true, true, n);
    public static void cob_addswp_s16(CobPtr p, long v) => BinAdd(p, 2, true, v);
    public static void cob_subswp_s16(CobPtr p, long v) => BinAdd(p, 2, true, -v);
    public static void cob_setswp_s16(CobPtr p, long v) => BinSet(p, 2, true, v);
    public static int cob_cmp_align_s16(CobPtr p, long n) => BinCmp(p, 2, true, false, n);
    public static void cob_add_align_s16(CobPtr p, long v) => BinAdd(p, 2, false, v);
    public static void cob_sub_align_s16(CobPtr p, long v) => BinAdd(p, 2, false, -v);
    public static int cob_cmpswp_align_s16(CobPtr p, long n) => BinCmp(p, 2, true, true, n);
    public static int cob_cmp_u24(CobPtr p, long n) => BinCmp(p, 3, false, false, n);
    public static void cob_add_u24(CobPtr p, long v) => BinAdd(p, 3, false, v);
    public static void cob_sub_u24(CobPtr p, long v) => BinAdd(p, 3, false, -v);
    public static int cob_cmpswp_u24(CobPtr p, long n) => BinCmp(p, 3, false, true, n);
    public static void cob_addswp_u24(CobPtr p, long v) => BinAdd(p, 3, true, v);
    public static void cob_subswp_u24(CobPtr p, long v) => BinAdd(p, 3, true, -v);
    public static void cob_setswp_u24(CobPtr p, long v) => BinSet(p, 3, true, v);
    public static int cob_cmp_s24(CobPtr p, long n) => BinCmp(p, 3, true, false, n);
    public static void cob_add_s24(CobPtr p, long v) => BinAdd(p, 3, false, v);
    public static void cob_sub_s24(CobPtr p, long v) => BinAdd(p, 3, false, -v);
    public static int cob_cmpswp_s24(CobPtr p, long n) => BinCmp(p, 3, true, true, n);
    public static void cob_addswp_s24(CobPtr p, long v) => BinAdd(p, 3, true, v);
    public static void cob_subswp_s24(CobPtr p, long v) => BinAdd(p, 3, true, -v);
    public static void cob_setswp_s24(CobPtr p, long v) => BinSet(p, 3, true, v);
    public static int cob_cmp_u32(CobPtr p, long n) => BinCmp(p, 4, false, false, n);
    public static void cob_add_u32(CobPtr p, long v) => BinAdd(p, 4, false, v);
    public static void cob_sub_u32(CobPtr p, long v) => BinAdd(p, 4, false, -v);
    public static int cob_cmpswp_u32(CobPtr p, long n) => BinCmp(p, 4, false, true, n);
    public static void cob_addswp_u32(CobPtr p, long v) => BinAdd(p, 4, true, v);
    public static void cob_subswp_u32(CobPtr p, long v) => BinAdd(p, 4, true, -v);
    public static void cob_setswp_u32(CobPtr p, long v) => BinSet(p, 4, true, v);
    public static int cob_cmp_align_u32(CobPtr p, long n) => BinCmp(p, 4, false, false, n);
    public static void cob_add_align_u32(CobPtr p, long v) => BinAdd(p, 4, false, v);
    public static void cob_sub_align_u32(CobPtr p, long v) => BinAdd(p, 4, false, -v);
    public static int cob_cmpswp_align_u32(CobPtr p, long n) => BinCmp(p, 4, false, true, n);
    public static int cob_cmp_s32(CobPtr p, long n) => BinCmp(p, 4, true, false, n);
    public static void cob_add_s32(CobPtr p, long v) => BinAdd(p, 4, false, v);
    public static void cob_sub_s32(CobPtr p, long v) => BinAdd(p, 4, false, -v);
    public static int cob_cmpswp_s32(CobPtr p, long n) => BinCmp(p, 4, true, true, n);
    public static void cob_addswp_s32(CobPtr p, long v) => BinAdd(p, 4, true, v);
    public static void cob_subswp_s32(CobPtr p, long v) => BinAdd(p, 4, true, -v);
    public static void cob_setswp_s32(CobPtr p, long v) => BinSet(p, 4, true, v);
    public static int cob_cmp_align_s32(CobPtr p, long n) => BinCmp(p, 4, true, false, n);
    public static void cob_add_align_s32(CobPtr p, long v) => BinAdd(p, 4, false, v);
    public static void cob_sub_align_s32(CobPtr p, long v) => BinAdd(p, 4, false, -v);
    public static int cob_cmpswp_align_s32(CobPtr p, long n) => BinCmp(p, 4, true, true, n);
    public static int cob_cmp_u40(CobPtr p, long n) => BinCmp(p, 5, false, false, n);
    public static void cob_add_u40(CobPtr p, long v) => BinAdd(p, 5, false, v);
    public static void cob_sub_u40(CobPtr p, long v) => BinAdd(p, 5, false, -v);
    public static int cob_cmpswp_u40(CobPtr p, long n) => BinCmp(p, 5, false, true, n);
    public static void cob_addswp_u40(CobPtr p, long v) => BinAdd(p, 5, true, v);
    public static void cob_subswp_u40(CobPtr p, long v) => BinAdd(p, 5, true, -v);
    public static void cob_setswp_u40(CobPtr p, long v) => BinSet(p, 5, true, v);
    public static int cob_cmp_s40(CobPtr p, long n) => BinCmp(p, 5, true, false, n);
    public static void cob_add_s40(CobPtr p, long v) => BinAdd(p, 5, false, v);
    public static void cob_sub_s40(CobPtr p, long v) => BinAdd(p, 5, false, -v);
    public static int cob_cmpswp_s40(CobPtr p, long n) => BinCmp(p, 5, true, true, n);
    public static void cob_addswp_s40(CobPtr p, long v) => BinAdd(p, 5, true, v);
    public static void cob_subswp_s40(CobPtr p, long v) => BinAdd(p, 5, true, -v);
    public static void cob_setswp_s40(CobPtr p, long v) => BinSet(p, 5, true, v);
    public static int cob_cmp_u48(CobPtr p, long n) => BinCmp(p, 6, false, false, n);
    public static void cob_add_u48(CobPtr p, long v) => BinAdd(p, 6, false, v);
    public static void cob_sub_u48(CobPtr p, long v) => BinAdd(p, 6, false, -v);
    public static int cob_cmpswp_u48(CobPtr p, long n) => BinCmp(p, 6, false, true, n);
    public static void cob_addswp_u48(CobPtr p, long v) => BinAdd(p, 6, true, v);
    public static void cob_subswp_u48(CobPtr p, long v) => BinAdd(p, 6, true, -v);
    public static void cob_setswp_u48(CobPtr p, long v) => BinSet(p, 6, true, v);
    public static int cob_cmp_s48(CobPtr p, long n) => BinCmp(p, 6, true, false, n);
    public static void cob_add_s48(CobPtr p, long v) => BinAdd(p, 6, false, v);
    public static void cob_sub_s48(CobPtr p, long v) => BinAdd(p, 6, false, -v);
    public static int cob_cmpswp_s48(CobPtr p, long n) => BinCmp(p, 6, true, true, n);
    public static void cob_addswp_s48(CobPtr p, long v) => BinAdd(p, 6, true, v);
    public static void cob_subswp_s48(CobPtr p, long v) => BinAdd(p, 6, true, -v);
    public static void cob_setswp_s48(CobPtr p, long v) => BinSet(p, 6, true, v);
    public static int cob_cmp_u56(CobPtr p, long n) => BinCmp(p, 7, false, false, n);
    public static void cob_add_u56(CobPtr p, long v) => BinAdd(p, 7, false, v);
    public static void cob_sub_u56(CobPtr p, long v) => BinAdd(p, 7, false, -v);
    public static int cob_cmpswp_u56(CobPtr p, long n) => BinCmp(p, 7, false, true, n);
    public static void cob_addswp_u56(CobPtr p, long v) => BinAdd(p, 7, true, v);
    public static void cob_subswp_u56(CobPtr p, long v) => BinAdd(p, 7, true, -v);
    public static void cob_setswp_u56(CobPtr p, long v) => BinSet(p, 7, true, v);
    public static int cob_cmp_s56(CobPtr p, long n) => BinCmp(p, 7, true, false, n);
    public static void cob_add_s56(CobPtr p, long v) => BinAdd(p, 7, false, v);
    public static void cob_sub_s56(CobPtr p, long v) => BinAdd(p, 7, false, -v);
    public static int cob_cmpswp_s56(CobPtr p, long n) => BinCmp(p, 7, true, true, n);
    public static void cob_addswp_s56(CobPtr p, long v) => BinAdd(p, 7, true, v);
    public static void cob_subswp_s56(CobPtr p, long v) => BinAdd(p, 7, true, -v);
    public static void cob_setswp_s56(CobPtr p, long v) => BinSet(p, 7, true, v);
    public static int cob_cmp_u64(CobPtr p, long n) => BinCmp(p, 8, false, false, n);
    public static void cob_add_u64(CobPtr p, long v) => BinAdd(p, 8, false, v);
    public static void cob_sub_u64(CobPtr p, long v) => BinAdd(p, 8, false, -v);
    public static int cob_cmpswp_u64(CobPtr p, long n) => BinCmp(p, 8, false, true, n);
    public static void cob_addswp_u64(CobPtr p, long v) => BinAdd(p, 8, true, v);
    public static void cob_subswp_u64(CobPtr p, long v) => BinAdd(p, 8, true, -v);
    public static void cob_setswp_u64(CobPtr p, long v) => BinSet(p, 8, true, v);
    public static int cob_cmp_align_u64(CobPtr p, long n) => BinCmp(p, 8, false, false, n);
    public static void cob_add_align_u64(CobPtr p, long v) => BinAdd(p, 8, false, v);
    public static void cob_sub_align_u64(CobPtr p, long v) => BinAdd(p, 8, false, -v);
    public static int cob_cmpswp_align_u64(CobPtr p, long n) => BinCmp(p, 8, false, true, n);
    public static int cob_cmp_s64(CobPtr p, long n) => BinCmp(p, 8, true, false, n);
    public static void cob_add_s64(CobPtr p, long v) => BinAdd(p, 8, false, v);
    public static void cob_sub_s64(CobPtr p, long v) => BinAdd(p, 8, false, -v);
    public static int cob_cmpswp_s64(CobPtr p, long n) => BinCmp(p, 8, true, true, n);
    public static void cob_addswp_s64(CobPtr p, long v) => BinAdd(p, 8, true, v);
    public static void cob_subswp_s64(CobPtr p, long v) => BinAdd(p, 8, true, -v);
    public static void cob_setswp_s64(CobPtr p, long v) => BinSet(p, 8, true, v);
    public static int cob_cmp_align_s64(CobPtr p, long n) => BinCmp(p, 8, true, false, n);
    public static void cob_add_align_s64(CobPtr p, long v) => BinAdd(p, 8, false, v);
    public static void cob_sub_align_s64(CobPtr p, long v) => BinAdd(p, 8, false, -v);
    public static int cob_cmpswp_align_s64(CobPtr p, long n) => BinCmp(p, 8, true, true, n);
}
