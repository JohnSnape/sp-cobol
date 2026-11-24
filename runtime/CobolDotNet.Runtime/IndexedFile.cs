// ORGANIZATION INDEXED: SP COBOL's own simple format (user's decision, 2026-11-24).
//
// Stock GnuCOBOL keeps indexed files in an external library (Berkeley DB, VBISAM, ...);
// the build we compare against has none, so there is no format to match. Ours is one file:
//
//   header   "SPCOBIX1", then int32 LE: header size, record_max, record_min, key count,
//            and per key: offset in the record, length, duplicates allowed (0/1)
//   slots    one per record ever written, in write order:
//              int64 LE  record length (0 = deleted)
//              int64 LE  sequence number (orders records with equal alternate keys)
//              record_max bytes of record data
//
// The key indexes live in memory: OPEN reads every slot and builds one sorted set per key.
// No separate index file can disagree with the data, and a damaged index can't exist.
// Keys compare byte by byte (as GnuCOBOL's indexed handlers do). Deleted slots are not
// reused; the file only grows (copy it with a program to compact it).
//
// FILE STATUS values follow the COBOL standard as GnuCOBOL reports them: 02 duplicate
// alternate key, 21 sequence error, 22 duplicate key, 23 not found, 39 attributes differ.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using static CobolDotNet.Runtime.Cob;

namespace CobolDotNet.Runtime;

internal sealed class IndexedFile
{
    const string Magic = "SPCOBIX1";

    sealed class Entry
    {
        public byte[] Key;      // null = +infinity (search sentinel)
        public long Seq;
        public long Slot;
    }

    sealed class EntryComparer : IComparer<Entry>
    {
        public static readonly EntryComparer Instance = new();
        public int Compare(Entry a, Entry b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a.Key == null) return b.Key == null ? a.Seq.CompareTo(b.Seq) : 1;
            if (b.Key == null) return -1;
            int c = a.Key.AsSpan().SequenceCompareTo(b.Key);
            return c != 0 ? c : a.Seq.CompareTo(b.Seq);
        }
    }

    readonly CobFile f;
    readonly FileStream fs;
    readonly int headerSize;
    readonly long slotSize;
    readonly (int off, int len, bool dups)[] keyDefs;
    readonly SortedSet<Entry>[] index;
    long nextSeq = 1;

    // position for READ NEXT / PREVIOUS
    int curKey;                 // key of reference
    Entry cur;                  // last record returned / positioned at
    bool startPending;          // after START: next READ NEXT returns `cur` itself
    bool atStart = true;        // after OPEN: next READ NEXT returns the first record
    long lastSlot = -1;         // record read last (REWRITE/DELETE in sequential access)
    byte[] lastWrittenKey;      // ACCESS SEQUENTIAL WRITE order check

    IndexedFile(CobFile f, FileStream fs, (int, int, bool)[] defs, int headerSize)
    {
        this.f = f;
        this.fs = fs;
        keyDefs = defs;
        this.headerSize = headerSize;
        slotSize = 16 + f.record_max;
        index = new SortedSet<Entry>[defs.Length];
        for (int i = 0; i < defs.Length; i++) index[i] = new SortedSet<Entry>(EntryComparer.Instance);
    }

    // ------------------------------------------------------------------ open / close

    static (int, int, bool)[] KeyDefsOf(CobFile f)
    {
        int n = Math.Max(f.nkeys, f.keys?.Length ?? 0);
        var d = new (int, int, bool)[n];
        for (int i = 0; i < n; i++)
        {
            var k = f.keys[i];
            int off = k.offset;
            if (k.field != null && ReferenceEquals(k.field.data.Arr, f.record.data.Arr)) off = k.field.data.Off - f.record.data.Off;
            int len = KeyLength(k);
            d[i] = (off, len, k.tf_duplicates != 0);
        }
        return d;
    }

    static int KeyLength(CobFileKey k)
    {
        if (k.count_components > 0)
        {
            int len = 0;
            for (int c = 0; c < k.count_components; c++) len += (int)k.component[c].size;
            return len;
        }
        return (int)k.field.size;
    }

    internal static int Open(CobFile f, int mode)
    {
        string name = f.resolvedName;
        bool exists = File.Exists(name);
        if (!exists && Directory.Exists(name)) return 37;
        if (!exists && mode != COB_OPEN_OUTPUT)
        {
            if (f.flag_optional == 0) return 35;
            if (mode == COB_OPEN_INPUT)
            {
                f.nonexistent = f.endOfFile = f.beginOfFile = true;
                f.open_mode = mode;
                return 5;
            }
        }
        FileStream fs;
        try
        {
            var fm = mode == COB_OPEN_OUTPUT ? FileMode.Create : mode == COB_OPEN_INPUT ? FileMode.Open : FileMode.OpenOrCreate;
            var fa = mode == COB_OPEN_INPUT ? FileAccess.Read : FileAccess.ReadWrite;
            fs = new FileStream(name, fm, fa, mode == COB_OPEN_INPUT ? FileShare.Read : FileShare.None, 1 << 16);
        }
        catch (Exception e) { return OpenErrorFor(e, mode); }

        var defs = KeyDefsOf(f);
        int headerSize = 28 + 12 * defs.Length;
        try
        {
            if (fs.Length == 0)
            {
                if (mode == COB_OPEN_INPUT) { fs.Dispose(); return 39; }
                WriteHeader(fs, f, defs, headerSize);
            }
            else if (!HeaderMatches(fs, f, defs, headerSize)) { fs.Dispose(); return 39; }
            var x = new IndexedFile(f, fs, defs, headerSize);
            x.Load();
            // OPEN EXTEND: new records must come after the highest key already there (else 21)
            if (mode == COB_OPEN_EXTEND && x.index[0].Count > 0) x.lastWrittenKey = x.index[0].Max.Key;
            f.idx = x;
        }
        catch (IOException) { fs.Dispose(); return 30; }
        f.open_mode = mode;
        if (!exists && f.flag_optional != 0 && mode != COB_OPEN_OUTPUT) return 5;
        return 0;
    }

    static int OpenErrorFor(Exception e, int mode) => e switch
    {
        UnauthorizedAccessException => 37,
        DirectoryNotFoundException => mode == COB_OPEN_OUTPUT || mode == COB_OPEN_EXTEND ? 30 : 35,
        FileNotFoundException => 35,
        IOException io when (io.HResult & 0xFFFF) == 32 || (io.HResult & 0xFFFF) == 33 => 61,
        _ => 30,
    };

    static void WriteHeader(FileStream fs, CobFile f, (int off, int len, bool dups)[] defs, int headerSize)
    {
        var h = new byte[headerSize];
        Encoding.ASCII.GetBytes(Magic).CopyTo(h, 0);
        BitConverter.GetBytes(headerSize).CopyTo(h, 8);
        BitConverter.GetBytes(f.record_max).CopyTo(h, 12);
        BitConverter.GetBytes(f.record_min).CopyTo(h, 16);
        BitConverter.GetBytes(defs.Length).CopyTo(h, 20);
        // 24..27 reserved
        for (int i = 0; i < defs.Length; i++)
        {
            BitConverter.GetBytes(defs[i].off).CopyTo(h, 28 + 12 * i);
            BitConverter.GetBytes(defs[i].len).CopyTo(h, 32 + 12 * i);
            BitConverter.GetBytes(defs[i].dups ? 1 : 0).CopyTo(h, 36 + 12 * i);
        }
        fs.Position = 0;
        fs.Write(h, 0, h.Length);
        fs.Flush();
    }

    /// <summary>The file was written with the same record size and keys (else status 39).</summary>
    static bool HeaderMatches(FileStream fs, CobFile f, (int off, int len, bool dups)[] defs, int headerSize)
    {
        if (fs.Length < headerSize) return false;
        var h = new byte[headerSize];
        fs.Position = 0;
        if (fs.Read(h, 0, headerSize) < headerSize) return false;
        if (Encoding.ASCII.GetString(h, 0, 8) != Magic) return false;
        if (BitConverter.ToInt32(h, 8) != headerSize) return false;
        if (BitConverter.ToInt32(h, 12) != f.record_max) return false;
        if (BitConverter.ToInt32(h, 20) != defs.Length) return false;
        for (int i = 0; i < defs.Length; i++)
        {
            if (BitConverter.ToInt32(h, 28 + 12 * i) != defs[i].off) return false;
            if (BitConverter.ToInt32(h, 32 + 12 * i) != defs[i].len) return false;
        }
        return true;
    }

    void Load()
    {
        long count = (fs.Length - headerSize) / slotSize;
        var hdr = new byte[16];
        var rec = new byte[f.record_max];
        fs.Position = headerSize;
        for (long s = 0; s < count; s++)
        {
            fs.ReadExactly(hdr, 0, 16);
            long len = BitConverter.ToInt64(hdr, 0);
            long seq = BitConverter.ToInt64(hdr, 8);
            fs.ReadExactly(rec, 0, rec.Length);
            if (seq >= nextSeq) nextSeq = seq + 1;
            if (len <= 0) continue;
            for (int k = 0; k < keyDefs.Length; k++)
                index[k].Add(new Entry { Key = KeyOf(rec, k), Seq = seq, Slot = s });
        }
    }

    internal void Close()
    {
        fs.Flush();
        fs.Dispose();
    }

    internal static void DeleteFiles(string name) { }

    // ------------------------------------------------------------------ helpers

    byte[] KeyOf(ReadOnlySpan<byte> record, int k)
    {
        var key = f.keys[k];
        if (key.count_components > 0)
        {
            // split key: the components, joined
            var b = new byte[keyDefs[k].len];
            int at = 0;
            for (int c = 0; c < key.count_components; c++)
            {
                var comp = key.component[c];
                int off = comp.data.Off - f.record.data.Off;
                record.Slice(off, (int)comp.size).CopyTo(b.AsSpan(at));
                at += (int)comp.size;
            }
            return b;
        }
        return record.Slice(keyDefs[k].off, keyDefs[k].len).ToArray();
    }

    byte[] RecordBuffer => f.record.data.Span(f.record_max).ToArray();

    long SlotPos(long slot) => headerSize + slot * slotSize;

    (long len, long seq) ReadSlot(long slot, bool intoRecord)
    {
        var hdr = new byte[16];
        fs.Position = SlotPos(slot);
        fs.ReadExactly(hdr, 0, 16);
        long len = BitConverter.ToInt64(hdr, 0);
        if (intoRecord)
        {
            var rec = new byte[f.record_max];
            fs.ReadExactly(rec, 0, rec.Length);
            rec.CopyTo(f.record.data.Span(f.record_max));
            f.record.size = len;
        }
        return (len, BitConverter.ToInt64(hdr, 8));
    }

    void WriteSlot(long slot, long len, long seq, ReadOnlySpan<byte> data)
    {
        fs.Position = SlotPos(slot);
        fs.Write(BitConverter.GetBytes(len), 0, 8);
        fs.Write(BitConverter.GetBytes(seq), 0, 8);
        fs.Write(data);
        fs.Flush();
    }

    Entry Find(int k, byte[] key) => FindFirst(k, key, key.Length, COB_EQ);

    /// <summary>First entry in key order k meeting cond against the first len bytes of key.</summary>
    Entry FindFirst(int k, byte[] key, int len, int cond)
    {
        var set = index[k];
        if (set.Count == 0) return null;
        int Cmp(Entry e) => e.Key.AsSpan(0, len).SequenceCompareTo(key.AsSpan(0, len));
        switch (cond)
        {
            case COB_EQ:
            case COB_GE:
            {
                // smallest entry whose key prefix >= key
                var lo = new Entry { Key = Pad(key, len, 0x00, keyDefs[k].len), Seq = long.MinValue };
                var view = set.GetViewBetween(lo, new Entry { Key = null, Seq = long.MaxValue });
                var e = view.Count > 0 ? view.Min : null;
                if (e == null) return null;
                return cond == COB_EQ && Cmp(e) != 0 ? null : e;
            }
            case COB_GT:
            {
                var lo = new Entry { Key = Pad(key, len, 0xFF, keyDefs[k].len), Seq = long.MaxValue };
                var view = set.GetViewBetween(lo, new Entry { Key = null, Seq = long.MaxValue });
                foreach (var e in view) if (Cmp(e) > 0) return e;
                return null;
            }
            case COB_LE:
            case COB_LT:
            {
                // the largest entry whose key prefix is <= (or <) key
                var hi = new Entry { Key = Pad(key, len, cond == COB_LE ? (byte)0xFF : (byte)0x00, keyDefs[k].len), Seq = cond == COB_LE ? long.MaxValue : long.MinValue };
                if (EntryComparer.Instance.Compare(set.Min, hi) > 0) return null;
                foreach (var e in set.GetViewBetween(set.Min, hi).Reverse())
                {
                    int c = Cmp(e);
                    if (cond == COB_LE ? c <= 0 : c < 0) return e;
                }
                return null;
            }
            case COB_FI: return set.Min;
            case COB_LA: return set.Max;
        }
        return null;
    }

    static byte[] Pad(byte[] key, int len, byte fill, int full)
    {
        var b = new byte[full];
        Array.Fill(b, fill);
        Array.Copy(key, b, Math.Min(len, full));
        return b;
    }

    static Entry Successor(SortedSet<Entry> set, Entry e)
    {
        var view = set.GetViewBetween(e, new Entry { Key = null, Seq = long.MaxValue });
        foreach (var x in view) if (EntryComparer.Instance.Compare(x, e) > 0) return x;
        return null;
    }

    static Entry Predecessor(SortedSet<Entry> set, Entry e)
    {
        if (set.Count == 0 || EntryComparer.Instance.Compare(set.Min, e) >= 0) return null;
        var view = set.GetViewBetween(set.Min, e);
        Entry best = null;
        foreach (var x in view.Reverse()) { if (EntryComparer.Instance.Compare(x, e) < 0) { best = x; break; } }
        return best;
    }

    /// <summary>Which key a key field (READ KEY / START KEY) refers to.</summary>
    int KeyIndexOf(CobField key)
    {
        for (int i = 0; i < keyDefs.Length; i++)
            if (ReferenceEquals(f.keys[i].field, key)) return i;
        int off = ReferenceEquals(key.data.Arr, f.record.data.Arr) ? key.data.Off - f.record.data.Off : -1;
        for (int i = 0; i < keyDefs.Length; i++)
            if (keyDefs[i].off == off) return i;
        return 0;
    }

    /// <summary>02: another record has the same alternate key after this one.</summary>
    int DupStatus(int k, Entry e)
    {
        if (k == 0 || !keyDefs[k].dups || e == null) return 0;
        var n = Successor(index[k], e);
        return n != null && n.Key.AsSpan().SequenceEqual(e.Key) ? 2 : 0;
    }

    // ------------------------------------------------------------------ operations

    internal int Start(int cond, CobField key, int keylen)
    {
        int k = KeyIndexOf(key);
        byte[] kb;
        if (cond == COB_FI || cond == COB_LA) kb = Array.Empty<byte>();
        else kb = key.Span.ToArray();
        int len = keylen > 0 ? keylen : Math.Min(kb.Length, keyDefs[k].len);
        if (cond != COB_FI && cond != COB_LA && kb.Length > keyDefs[k].len) kb = kb.AsSpan(0, keyDefs[k].len).ToArray();
        var e = FindFirst(k, kb, Math.Min(len, kb.Length), cond);
        curKey = k;
        atStart = false;
        if (e == null) { cur = null; startPending = false; return 23; }
        cur = e;
        startPending = true;
        return 0;
    }

    internal int Read(CobField key)
    {
        int k = KeyIndexOf(key);
        var kb = key.Span.ToArray();
        if (kb.Length != keyDefs[k].len) kb = Pad(kb, kb.Length, (byte)' ', keyDefs[k].len);
        var e = Find(k, kb);
        if (e == null) return 23;
        ReadSlot(e.Slot, true);
        curKey = k;
        cur = e;
        startPending = false;
        atStart = false;
        lastSlot = e.Slot;
        return DupStatus(k, e);
    }

    internal int ReadNext(int opts)
    {
        var set = index[curKey];
        bool prev = (opts & COB_READ_PREVIOUS) != 0;
        Entry e;
        if ((opts & COB_READ_FIRST) != 0) e = set.Count > 0 ? set.Min : null;
        else if ((opts & COB_READ_LAST) != 0) e = set.Count > 0 ? set.Max : null;
        else if (atStart) e = prev ? (set.Count > 0 ? set.Max : null) : (set.Count > 0 ? set.Min : null);
        else if (startPending) e = Current(set);
        else if (cur == null) return 46;
        else e = prev ? Predecessor(set, cur) : Successor(set, cur);
        atStart = false;
        startPending = false;
        if (e == null) { return 10; }
        cur = e;
        ReadSlot(e.Slot, true);
        lastSlot = e.Slot;
        return prev ? 0 : DupStatus(curKey, e);
    }

    // after START, or when the positioned record was deleted: the entry at or after `cur`
    Entry Current(SortedSet<Entry> set) => set.Contains(cur) ? cur : Successor(set, cur);

    internal int Write()
    {
        var rec = RecordBuffer;
        var keys = new byte[keyDefs.Length][];
        for (int k = 0; k < keyDefs.Length; k++) keys[k] = KeyOf(rec, k);
        if (f.access_mode == COB_ACCESS_SEQUENTIAL && lastWrittenKey != null)
        {
            int c = keys[0].AsSpan().SequenceCompareTo(lastWrittenKey);
            if (c < 0) return 21;
        }
        if (Find(0, keys[0]) != null) return 22;
        int st = 0;
        for (int k = 1; k < keyDefs.Length; k++)
        {
            if (Find(k, keys[k]) == null) continue;
            if (!keyDefs[k].dups) return 22;
            st = 2;
        }
        long slot = (fs.Length - headerSize) / slotSize;
        long seq = nextSeq++;
        try { WriteSlot(slot, f.record.size, seq, rec); }
        catch (IOException) { return 30; }
        for (int k = 0; k < keyDefs.Length; k++) index[k].Add(new Entry { Key = keys[k], Seq = seq, Slot = slot });
        lastWrittenKey = keys[0];
        return st;
    }

    Entry Located(out int status)
    {
        status = 0;
        var key = KeyOf(RecordBuffer, 0);
        if (f.access_mode == COB_ACCESS_SEQUENTIAL)
        {
            // must be the record read last, unchanged primary key
            if (lastSlot < 0) { status = 43; return null; }
            var last = Find(0, key);
            if (last == null || last.Slot != lastSlot) { status = 21; return null; }
            return last;
        }
        var e = Find(0, key);
        if (e == null) status = 23;
        return e;
    }

    internal int Rewrite(long size)
    {
        var e = Located(out int st);
        if (e == null) return st;
        var (_, seq) = ReadSlot(e.Slot, false);
        // the old record's keys, for the alternate indexes
        var old = new byte[f.record_max];
        fs.Position = SlotPos(e.Slot) + 16;
        fs.ReadExactly(old, 0, old.Length);
        var rec = RecordBuffer;
        int status = 0;
        var newKeys = new byte[keyDefs.Length][];
        var oldKeys = new byte[keyDefs.Length][];
        for (int k = 1; k < keyDefs.Length; k++)
        {
            newKeys[k] = KeyOf(rec, k);
            oldKeys[k] = KeyOf(old, k);
            if (newKeys[k].AsSpan().SequenceEqual(oldKeys[k])) continue;
            if (Find(k, newKeys[k]) != null)
            {
                if (!keyDefs[k].dups) return 22;
                status = 2;
            }
        }
        try { WriteSlot(e.Slot, size, seq, rec); }
        catch (IOException) { return 30; }
        f.record.size = size;
        for (int k = 1; k < keyDefs.Length; k++)
        {
            if (newKeys[k].AsSpan().SequenceEqual(oldKeys[k])) continue;
            index[k].Remove(new Entry { Key = oldKeys[k], Seq = seq, Slot = e.Slot });
            index[k].Add(new Entry { Key = newKeys[k], Seq = seq, Slot = e.Slot });
        }
        return status;
    }

    internal int Delete()
    {
        var e = Located(out int st);
        if (e == null) return st;
        var old = new byte[f.record_max];
        var (_, seq) = ReadSlot(e.Slot, false);
        fs.ReadExactly(old, 0, old.Length);
        try
        {
            fs.Position = SlotPos(e.Slot);
            fs.Write(BitConverter.GetBytes(0L), 0, 8);
            fs.Flush();
        }
        catch (IOException) { return 30; }
        for (int k = 0; k < keyDefs.Length; k++)
            index[k].Remove(new Entry { Key = KeyOf(old, k), Seq = seq, Slot = e.Slot });
        lastSlot = -1;
        return 0;
    }
}
