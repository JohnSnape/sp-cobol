// SORT and MERGE (file sort through an SD) and SORT of a table (libcob fileio.c / common.c).
//
// File SORT/MERGE: records arrive through USING files or RELEASE, are held in memory, and
// leave in key order through GIVING files or RETURN. Keys compare in order: numeric keys
// by value, others byte by byte (or through the COLLATING SEQUENCE); records with equal keys
// keep their arrival order (libcob tags each with a sequence number), so MERGE keeps the
// first USING file's records ahead of the next one's. SORT-RETURN gets 0, or 16 on errors.
//
// Table SORT: stock calls C's qsort, which isn't stable, so elements with equal keys come
// out in an order that depends on the C runtime. Ours is stable (equal elements keep their
// order); every other result is the same.

using System;
using System.Collections.Generic;
using static CobolDotNet.Runtime.CobConst;

namespace CobolDotNet.Runtime;

public static partial class Cob
{
    public const int COB_ASCENDING = 0, COB_DESCENDING = 1;

    sealed class SortWork
    {
        public CobField fnstatus;
        public CobPtr sortReturn;            // SORT-RETURN, or IsNull
        public byte[] collating;
        public bool merge;
        public readonly List<byte[]> records = new();
        public List<byte[]> sorted;          // after the first RETURN / GIVING
        public int next;
        public readonly List<(CobField field, int flag, int offset)> keys = new();
    }

    static byte[] CollatingTable(object c) => c switch
    {
        CobPtr p when !p.IsNull => p.Span(256).ToArray(),
        byte[] b => b,
        _ => null,
    };

    static void SetSortReturn(SortWork w, int v) { if (!w.sortReturn.IsNull) w.sortReturn.S32 = v; }

    public static void cob_file_sort_init(CobFile f, int nkeys, object collating, object sortReturn, CobField fnstatus)
    {
        var w = new SortWork { fnstatus = fnstatus, collating = CollatingTable(collating) };
        if (sortReturn is CobPtr sr) w.sortReturn = sr;
        SetSortReturn(w, 0);
        f.file = w;
        f.nkeys = 0;
        SaveStatus(f, fnstatus, 0);
    }

    public static void cob_file_sort_options(CobFile f, string parms, params object[] more)
    {
        if (f.file is SortWork w) w.merge = parms.Length > 0 && parms[0] == 'M';
    }

    public static void cob_file_sort_init_key(CobFile f, CobField field, int flag, int offset)
    {
        if (f.file is SortWork w) w.keys.Add((field, flag, offset));
        f.nkeys++;
    }

    /// <summary>cob_copy_check: copy a record, space-filling a longer target.</summary>
    static void CopyRecord(CobField to, ReadOnlySpan<byte> from)
    {
        var t = to.Span;
        if (t.Length > from.Length) { from.CopyTo(t); t.Slice(from.Length).Fill((byte)' '); }
        else from.Slice(0, t.Length).CopyTo(t);
    }

    static void Submit(SortWork w, CobFile f) => w.records.Add(f.record.data.Span(f.record_max).ToArray());

    /// <summary>USING file: read every record of it into the sort.</summary>
    public static void cob_file_sort_using(CobFile sortFile, CobFile dataFile)
    {
        var w = (SortWork)sortFile.file;
        cob_open(dataFile, COB_OPEN_INPUT, 0, null);
        if (dataFile.file_status[0] != (byte)'0')
        {
            if (dataFile.file_status[0] == (byte)'4') SetException(EC_SORT_MERGE_FILE_OPEN);
            SetSortReturn(w, 16);
            return;
        }
        while (true)
        {
            cob_read_next(dataFile, null, COB_READ_NEXT);
            if (dataFile.file_status[0] != (byte)'0') break;
            CopyRecord(sortFile.record, dataFile.record.Span);
            Submit(w, sortFile);
        }
        cob_close(dataFile, null, COB_CLOSE_NORMAL, 0);
    }

    public const int EC_SORT_MERGE_FILE_OPEN = 0x1102;

    static int SortCompare(SortWork w, byte[] a, byte[] b)
    {
        foreach (var (field, flag, offset) in w.keys)
        {
            int cmp;
            var fa = new CobField(field.size, new CobPtr(a, offset), field.attr);
            var fb = new CobField(field.size, new CobPtr(b, offset), field.attr);
            if (IsNumeric(field.Type))
            {
                var (va, sa) = Num.Get(fa);
                var (vb, sb) = Num.Get(fb);
                int s = Math.Max(sa, sb);
                cmp = (va * Num.Pow10(s - sa)).CompareTo(vb * Num.Pow10(s - sb));
            }
            else
            {
                cmp = 0;
                for (int i = 0; i < field.size && cmp == 0; i++)
                {
                    byte x = a[offset + i], y = b[offset + i];
                    cmp = w.collating != null ? w.collating[x] - w.collating[y] : x - y;
                }
            }
            if (cmp != 0) return flag == COB_ASCENDING ? cmp : -cmp;
        }
        return 0;
    }

    static void EnsureSorted(SortWork w)
    {
        if (w.sorted != null) return;
        // stable: equal keys keep arrival order (libcob's unique sequence numbers)
        var idx = new int[w.records.Count];
        for (int i = 0; i < idx.Length; i++) idx[i] = i;
        var recs = w.records;
        Array.Sort(idx, (i, j) => { int c = SortCompare(w, recs[i], recs[j]); return c != 0 ? c : i.CompareTo(j); });
        w.sorted = new List<byte[]>(idx.Length);
        foreach (var i in idx) w.sorted.Add(recs[i]);
        w.next = 0;
    }

    /// <summary>GIVING files: write every sorted record to each.</summary>
    public static void cob_file_sort_giving(CobFile sortFile, long count, params CobFile[] files)
    {
        var w = (SortWork)sortFile.file;
        EnsureSorted(w);
        var opt = new int[files.Length];
        for (int i = 0; i < files.Length; i++)
        {
            var g = files[i];
            cob_open(g, COB_OPEN_OUTPUT, 0, null);
            if (g.file_status[0] == (byte)'0')
                opt[i] = (g.flag_select_features & (COB_SELECT_STDIN | COB_SELECT_STDOUT)) != 0
                      || g.organization == COB_ORG_LINE_SEQUENTIAL ? COB_WRITE_BEFORE | COB_WRITE_LINES | 1 : 0;
            else
            {
                if (g.file_status[0] == (byte)'4') SetException(EC_SORT_MERGE_FILE_OPEN);
                opt[i] = -1;
            }
        }
        while (w.next < w.sorted.Count)
        {
            var rec = w.sorted[w.next++];
            rec.AsSpan().CopyTo(sortFile.record.data.Span(rec.Length));
            bool any = false;
            for (int i = 0; i < files.Length; i++)
            {
                if (opt[i] < 0) continue;
                var g = files[i];
                g.record.size = g.record_max;
                CopyRecord(g.record, sortFile.record.Span);
                cob_write(g, g.record, opt[i], null, 0);
                if (g.file_status[0] == (byte)'3') opt[i] = -2; else any = true;
            }
            if (!any && Array.TrueForAll(opt, o => o < 0)) break;
        }
        sortFile.file_status[0] = (byte)'1'; sortFile.file_status[1] = (byte)'0';
        for (int i = 0; i < files.Length; i++)
            if (opt[i] != -1) cob_close(files[i], null, COB_CLOSE_NORMAL, 0);
        foreach (var o in opt) if (o < 0) { SetSortReturn(w, 16); break; }
    }

    public static void cob_file_sort_close(CobFile f)
    {
        var fnstatus = (f.file as SortWork)?.fnstatus;
        f.file = null;
        SaveStatus(f, fnstatus, 0);
    }

    /// <summary>RELEASE record (INPUT PROCEDURE).</summary>
    public static void cob_file_release(CobFile f)
    {
        if (f.file is SortWork w && w.sorted == null)
        {
            Submit(w, f);
            SaveStatus(f, w.fnstatus, 0);
            return;
        }
        if (f.file is SortWork w2) SetSortReturn(w2, 16);
        SaveStatus(f, (f.file as SortWork)?.fnstatus, 30);
    }

    /// <summary>RETURN file (OUTPUT PROCEDURE): next record in order, or 10 at the end.</summary>
    public static void cob_file_return(CobFile f)
    {
        if (f.file is not SortWork w) { SaveStatus(f, null, 30); return; }
        EnsureSorted(w);
        if (w.next >= w.sorted.Count) { SaveStatus(f, w.fnstatus, 10); return; }
        var rec = w.sorted[w.next++];
        rec.AsSpan().CopyTo(f.record.data.Span(rec.Length));
        SaveStatus(f, w.fnstatus, 0);
    }

    // ------------------------------------------------------------ table SORT

    static readonly List<(CobField field, int flag, int offset)> tableKeys = new();
    static byte[] tableCollate;

    public static void cob_table_sort_init(long nkeys, object collating)
    {
        tableKeys.Clear();
        tableCollate = CollatingTable(collating);
    }

    public static void cob_table_sort_init_key(CobField field, int flag, int offset) => tableKeys.Add((field, flag, offset));

    /// <summary>SORT table: n elements of f.size bytes each, starting at f.data.</summary>
    public static void cob_table_sort(CobField f, int n)
    {
        int size = (int)f.size;
        var elems = new byte[n][];
        for (int i = 0; i < n; i++) elems[i] = (f.data + (long)i * size).Span(size).ToArray();
        var w = new SortWork { collating = tableCollate };
        w.keys.AddRange(tableKeys);
        var idx = new int[n];
        for (int i = 0; i < n; i++) idx[i] = i;
        Array.Sort(idx, (i, j) => { int c = SortCompare(w, elems[i], elems[j]); return c != 0 ? c : i.CompareTo(j); });
        for (int i = 0; i < n; i++) elems[idx[i]].AsSpan().CopyTo((f.data + (long)i * size).Span(size));
    }
}
