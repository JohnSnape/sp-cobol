// File I/O: ORGANIZATION SEQUENTIAL, LINE SEQUENTIAL, RELATIVE (INDEXED: see IndexedFile.cs).
//
// Behavior follows GnuCOBOL 3.2's libcob/fileio.c: the same entry points (cob_open,
// cob_read_next, ...), the same FILE STATUS values, and the same on-disk formats, so
// files written here can be read by GnuCOBOL programs and the other way round:
//   SEQUENTIAL fixed      records back to back
//   SEQUENTIAL variable   [len hi][len lo][00][00] + data  (COB_VARSEQ_FORMAT 0; 1-3 also supported)
//   LINE SEQUENTIAL       text, trailing spaces trimmed, CR LF line ends on Windows
//   RELATIVE              per slot: 8-byte little-endian length (0 = empty) + record_max bytes
// Where stock libcob has plain bugs we do what it intends instead (decision of 2026-11-24,
// DECISIONS.md "File I/O"): TAB/FF/ESC in a line sequential record are allowed, LF-only and
// CR LF text files both read correctly, Ctrl-Z is data, a quoted file name is unquoted,
// REWRITE/DELETE of an empty relative slot is 23, and error messages name the ASSIGN value.

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using static CobolDotNet.Runtime.CobConst;

namespace CobolDotNet.Runtime;

/// <summary>One RECORD KEY / ALTERNATE KEY / RELATIVE KEY of a file (libcob cob_file_key).</summary>
public sealed class CobFileKey
{
    public CobField field;
    public int tf_duplicates;
    public int tf_suppress;
    public int char_suppress;
    public int offset;
    public int count_components;
    public CobField[] component = new CobField[8];
}

/// <summary>LINAGE settings of a print file (libcob cob_linage).</summary>
public sealed class CobLinage
{
    public CobField linage, linage_ctr, latfoot, lattop, latbot;
    public int lin_lines, lin_foot, lin_top, lin_bot;
}

/// <summary>An FD / SD (libcob cob_file). Codegen fills the public fields at initialization.</summary>
public sealed class CobFile
{
    public string select_name;
    public CobPtr file_status;
    public CobField assign;
    public CobField record;
    public CobField variable_record;
    public int record_min, record_max;
    public object file;
    public int organization, access_mode, lock_mode, open_mode;
    public int flag_optional, flag_line_adv, flag_select_features;
    public int nkeys;
    public CobFileKey[] keys;
    public CobLinage linorkeyptr;
    public int last_open_mode;
    public object sort_collating, code_set, code_set_items;
    public int file_features;

    // ---- run-time state
    internal FileStream fs;
    internal string resolvedName;       // after mapping
    internal string assignName;         // trimmed ASSIGN value, for messages
    internal bool nonexistent, endOfFile, beginOfFile, readDone, needsNewline, needsTop;
    internal int firstRead;             // 2 after OPEN, 1 after START, 0 after a READ
    internal bool lastOpWasWrite;
    internal long recordOff = -1;       // LS: start of the last line read; SEQ: start of the last record
    internal long lsLineEnd = -1;       // LS: end of that line's data (before CR / LF)
    internal long relSlot;              // RELATIVE: 1-based slot of the last record read / written
    internal IndexedFile idx;           // INDEXED
}

public static partial class Cob
{
    // ------------------------------------------------------------ constants (libcob common.h)

    public const int COB_ORG_SEQUENTIAL = 0, COB_ORG_LINE_SEQUENTIAL = 1, COB_ORG_RELATIVE = 2,
                     COB_ORG_INDEXED = 3, COB_ORG_SORT = 4;
    public const int COB_ACCESS_SEQUENTIAL = 1, COB_ACCESS_DYNAMIC = 2, COB_ACCESS_RANDOM = 3;
    public const int COB_OPEN_CLOSED = 0, COB_OPEN_INPUT = 1, COB_OPEN_OUTPUT = 2, COB_OPEN_I_O = 3,
                     COB_OPEN_EXTEND = 4, COB_OPEN_LOCKED = 5;
    public const int COB_CLOSE_NORMAL = 0, COB_CLOSE_LOCK = 1, COB_CLOSE_NO_REWIND = 2,
                     COB_CLOSE_UNIT = 3, COB_CLOSE_UNIT_REMOVAL = 4;
    public const int COB_WRITE_MASK = 0x0000FFFF, COB_WRITE_LINES = 0x00010000, COB_WRITE_PAGE = 0x00020000,
                     COB_WRITE_CHANNEL = 0x00040000, COB_WRITE_AFTER = 0x00100000, COB_WRITE_BEFORE = 0x00200000,
                     COB_WRITE_EOP = 0x00400000, COB_WRITE_LOCK = 0x00800000, COB_WRITE_NO_LOCK = 0x01000000;
    public const int COB_READ_NEXT = 0x01, COB_READ_PREVIOUS = 0x02, COB_READ_FIRST = 0x04, COB_READ_LAST = 0x08,
                     COB_READ_LOCK = 0x10, COB_READ_NO_LOCK = 0x20, COB_READ_KEPT_LOCK = 0x40,
                     COB_READ_WAIT_LOCK = 0x80, COB_READ_IGNORE_LOCK = 0x100, COB_READ_MASK = 0x0F;
    public const int COB_SELECT_FILE_STATUS = 1, COB_SELECT_EXTERNAL = 2, COB_SELECT_LINAGE = 4,
                     COB_SELECT_SPLITKEY = 8, COB_SELECT_STDIN = 16, COB_SELECT_STDOUT = 32,
                     COB_SELECT_TEMPORARY = 64;
    public const int COB_LOCK_EXCLUSIVE = 1, COB_LOCK_MANUAL = 2, COB_LOCK_AUTOMATIC = 4,
                     COB_LOCK_MULTIPLE = 8, COB_LOCK_OPEN_EXCLUSIVE = 16;
    public const int COB_EQ = 1, COB_LT = 2, COB_LE = 3, COB_GT = 4, COB_GE = 5, COB_NE = 6, COB_FI = 7, COB_LA = 8;
    public const int COB_FERROR_SCR_INP = 10, COB_FERROR_FILE = 11, COB_FERROR_FUNCTION = 12;

    // ------------------------------------------------------------ settings (environment, as libcob)

    static readonly bool UnixLF = Env("COB_UNIX_LF") == "1";
    static readonly bool LsFixed = Env("COB_LS_FIXED") == "1";
    static readonly bool LsValidate = Env("COB_LS_VALIDATE") != "0";
    static readonly bool LsSplit = Env("COB_LS_SPLIT") != "0";
    static readonly int VarSeqFormat = int.TryParse(Env("COB_VARSEQ_FORMAT"), out var v) && v >= 0 && v <= 3 ? v : 0;
    static int VsqLen => VarSeqFormat == 3 ? 2 : 4;
    static string Env(string n) => Environment.GetEnvironmentVariable(n);

    /// <summary>Files opened and not yet closed, for the implicit CLOSE at STOP RUN.</summary>
    static readonly List<CobFile> OpenFiles = new();

    // ------------------------------------------------------------ allocation

    public static void cob_file_malloc(ref CobFile f, ref CobFileKey[] keys, int nkeys, int linage)
    {
        cob_file_malloc(ref f, (object)null, nkeys, linage);
        if (nkeys > 0)
        {
            keys = new CobFileKey[nkeys];
            for (int i = 0; i < nkeys; i++) keys[i] = new CobFileKey();
            f.keys = keys;
        }
    }

    public static void cob_file_malloc(ref CobFile f, object noKeys, int nkeys, int linage)
    {
        f = new CobFile { nkeys = nkeys };
        if (linage > 0) f.linorkeyptr = new CobLinage();
    }

    public static void cob_file_free(ref CobFile f, ref CobFileKey[] keys) { keys = null; f = null; }
    public static void cob_file_free(ref CobFile f, object noKeys) { f = null; }

    // ------------------------------------------------------------ status

    static void SaveStatus(CobFile f, CobField fnstatus, int status, bool eop = false)
    {
        Global.cob_error_file = f;
        f.file_status[0] = (byte)('0' + status / 10);
        f.file_status[1] = (byte)('0' + status % 10);
        if (fnstatus != null && fnstatus.size >= 2)
        {
            fnstatus.data[0] = f.file_status[0];
            fnstatus.data[1] = f.file_status[1];
        }
        if (status < 10) Global.cob_exception_code = eop ? CobException.EC_I_O_EOP : 0;
        else SetException(CobException.IoStatusException(status));
    }

    static int Status(CobFile f) => (f.file_status[0] - '0') * 10 + (f.file_status[1] - '0');

    // ------------------------------------------------------------ OPEN

    public static void cob_open(CobFile f, int mode, int sharing, CobField fnstatus)
    {
        if (f.open_mode == COB_OPEN_LOCKED) { SaveStatus(f, fnstatus, 38); return; }
        if (f.open_mode != COB_OPEN_CLOSED) { SaveStatus(f, fnstatus, 41); return; }
        f.readDone = false;
        f.last_open_mode = mode;
        f.nonexistent = f.endOfFile = f.beginOfFile = false;
        f.firstRead = 2;
        f.lastOpWasWrite = false;
        f.needsNewline = false;
        f.recordOff = -1; f.lsLineEnd = -1; f.relSlot = 0;
        f.lock_mode = (f.lock_mode & ~COB_LOCK_OPEN_EXCLUSIVE) | sharing;

        // ASSIGN KEYBOARD / DISPLAY
        if ((f.flag_select_features & COB_SELECT_STDIN) != 0)
        {
            if (mode != COB_OPEN_INPUT) { SaveStatus(f, fnstatus, 30); return; }
            f.open_mode = mode; f.assignName = "KEYBOARD"; SaveStatus(f, fnstatus, 0); return;
        }
        if ((f.flag_select_features & COB_SELECT_STDOUT) != 0)
        {
            if (mode != COB_OPEN_OUTPUT) { SaveStatus(f, fnstatus, 30); return; }
            f.open_mode = mode; f.assignName = "DISPLAY"; SaveStatus(f, fnstatus, 0); return;
        }

        string raw = AssignText(f);
        f.assignName = raw;
        if (raw.Length == 0) { SaveStatus(f, fnstatus, 31); return; }
        if ((raw[0] == '"' || raw[0] == '\'') && (raw.Length == 1 || raw[^1] != raw[0])) { SaveStatus(f, fnstatus, 31); return; }
        string name = MapFileName(raw);
        f.resolvedName = name;
        if (!OpenFiles.Contains(f)) OpenFiles.Add(f);

        int st = f.organization == COB_ORG_INDEXED ? IndexedFile.Open(f, mode) : OpenPlain(f, mode, name);
        SaveStatus(f, fnstatus, st);
    }

    /// <summary>The ASSIGN value: trailing spaces and NULs trimmed, leading spaces kept.</summary>
    static string AssignText(CobFile f)
    {
        if (f.assign == null || f.assign.data.IsNull) return "";
        var s = f.assign.Span;
        int n = s.Length;
        int nul = s.IndexOf((byte)0);
        if (nul >= 0) n = nul;
        while (n > 0 && (s[n - 1] == (byte)' ' || s[n - 1] == 0)) n--;
        return Latin1(s.Slice(0, n));
    }

    /// <summary>
    /// libcob's file name mapping: DD_name / dd_name / name environment variables, $VAR path
    /// elements, COB_FILE_PATH for relative names. Quotes around the name are removed.
    /// </summary>
    internal static string MapFileName(string src)
    {
        if (src.Length >= 2 && (src[0] == '"' || src[0] == '\'') && src[^1] == src[0]) src = src.Substring(1, src.Length - 2);
        // ACU style "-F name"
        if (src.Length > 2 && src[0] == '-' && "FDfd".IndexOf(src[1]) >= 0 && char.IsWhiteSpace(src[2]))
            return Unquote(src.Substring(2).Trim());

        bool absolute(string s) => s.Length > 0 && (s[0] == '/' || s[0] == '\\' || (s.Length > 1 && s[1] == ':'));
        string withPath(string s)
        {
            var fp = Env("COB_FILE_PATH");
            return !absolute(s) && !string.IsNullOrEmpty(fp) ? fp.TrimEnd('/', '\\') + "\\" + s : s;
        }

        if (!absolute(src) && src.IndexOfAny(new[] { '/', '\\' }) < 0)
        {
            string look = src.StartsWith("$") ? src.Substring(1) : src;
            var val = (src.Length > 0 && (src[0] == '-' || char.IsDigit(src[0]))) ? null : EnvFileName(look);
            if (val != null) return absolute(val) ? val : withPath(val);
            return withPath(src);
        }

        // a path: $VAR elements are replaced, elements joined with '\'
        var parts = src.Split(new[] { '/', '\\' });
        var sb = new StringBuilder();
        bool lead = src[0] == '/' || src[0] == '\\';
        for (int i = 0; i < parts.Length; i++)
        {
            string p = parts[i];
            if (p.Length == 0) { if (i == 0 && lead) sb.Append('\\'); continue; }
            string piece = p;
            if (p[0] == '$')
            {
                var val = EnvFileName(p.Substring(1));
                if (val == null) { if (i < parts.Length - 1) continue; }
                else piece = val;
            }
            else if (i == 0)
            {
                var val = p.Contains(':') ? null : EnvFileName(p);
                if (val != null) piece = val;
            }
            if (sb.Length > 0 && sb[^1] != '\\') sb.Append('\\');
            sb.Append(piece);
        }
        var r = sb.ToString();
        return absolute(r) ? r : withPath(r);
    }

    static string EnvFileName(string n)
    {
        if (n.Length == 0 || n[0] == '.') return null;
        n = n.Replace('.', '_');
        foreach (var cand in new[] { "DD_" + n, "dd_" + n, n })
        {
            var v = Env(cand);
            if (!string.IsNullOrEmpty(v)) return Unquote(v);
        }
        return null;
    }

    static string Unquote(string s) => s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[^1] == s[0] ? s.Substring(1, s.Length - 2) : s;

    static int OpenPlain(CobFile f, int mode, string name)
    {
        bool exists = File.Exists(name);
        if (!exists && Directory.Exists(name)) return 37;
        if (!exists && mode != COB_OPEN_OUTPUT)
        {
            if (f.flag_optional == 0) return 35;
            if (mode == COB_OPEN_INPUT || (mode == COB_OPEN_I_O && f.organization == COB_ORG_LINE_SEQUENTIAL))
            {
                f.nonexistent = f.endOfFile = f.beginOfFile = true;
                f.open_mode = mode;
                return 5;
            }
        }
        FileMode fm; FileAccess fa;
        switch (mode)
        {
            case COB_OPEN_INPUT: fm = FileMode.Open; fa = FileAccess.Read; break;
            case COB_OPEN_OUTPUT: fm = FileMode.Create; fa = f.organization == COB_ORG_RELATIVE ? FileAccess.ReadWrite : FileAccess.Write; break;
            case COB_OPEN_EXTEND: fm = FileMode.OpenOrCreate; fa = FileAccess.ReadWrite; break;
            default: fm = FileMode.OpenOrCreate; fa = FileAccess.ReadWrite; break;
        }
        try
        {
            f.fs = new FileStream(name, fm, fa, mode == COB_OPEN_INPUT ? FileShare.Read : FileShare.None, 4096);
        }
        catch (Exception e) { return OpenErrorStatus(e, mode); }
        if (mode == COB_OPEN_EXTEND) f.fs.Seek(0, SeekOrigin.End);

        if (f.organization == COB_ORG_SEQUENTIAL && f.record_min != f.record_max && exists && mode != COB_OPEN_OUTPUT && f.fs.Length > 0)
        {
            // the first record's header must make sense (libcob: 39)
            long pos = f.fs.Position;
            f.fs.Position = 0;
            int st = ReadVarHeader(f, out int size);
            f.fs.Position = pos;
            if (st == 0 && (size < f.record_min || size > f.record_max)) st = 39;
            if (st != 0 && st != 10) { f.fs.Dispose(); f.fs = null; return st; }
        }
        if (f.linorkeyptr != null && f.organization == COB_ORG_LINE_SEQUENTIAL)
        {
            if (!LinageCheck(f)) { f.fs.Dispose(); f.fs = null; return 57; }
            f.needsTop = true;
            cob_set_int(f.linorkeyptr.linage_ctr, 1);
        }
        f.open_mode = mode;
        if (!exists && f.flag_optional != 0 && (mode != COB_OPEN_OUTPUT || f.organization == COB_ORG_LINE_SEQUENTIAL)) return 5;
        return 0;
    }

    static int OpenErrorStatus(Exception e, int mode)
    {
        switch (e)
        {
            case UnauthorizedAccessException: return 37;
            case DirectoryNotFoundException: return mode == COB_OPEN_OUTPUT || mode == COB_OPEN_EXTEND ? 30 : 35;
            case FileNotFoundException: return 35;
            case IOException io when (io.HResult & 0xFFFF) == 32 || (io.HResult & 0xFFFF) == 33: return 61; // sharing / lock violation
            default: return 30;
        }
    }

    // ------------------------------------------------------------ CLOSE

    public static void cob_close(CobFile f, CobField fnstatus, int opt, int remfil)
    {
        f.readDone = false;
        f.lock_mode &= ~COB_LOCK_OPEN_EXCLUSIVE;
        if ((f.flag_select_features & (COB_SELECT_STDIN | COB_SELECT_STDOUT)) != 0)
        {
            if (f.needsNewline) { Out.Write(NewLine); f.needsNewline = false; }   // last AFTER ADVANCING line
            f.open_mode = COB_OPEN_CLOSED; Out.Flush(); SaveStatus(f, fnstatus, 0); return;
        }
        if (remfil != 0) OpenFiles.Remove(f);
        if (f.open_mode == COB_OPEN_CLOSED) { SaveStatus(f, fnstatus, 42); return; }
        int st = 0;
        if (!f.nonexistent)
        {
            if (opt == COB_CLOSE_UNIT || opt == COB_CLOSE_UNIT_REMOVAL)
            {
                f.fs?.Flush();
                SaveStatus(f, fnstatus, 7);     // the file stays open
                return;
            }
            CloseHandle(f);
            if (opt == COB_CLOSE_NO_REWIND) { f.open_mode = COB_OPEN_CLOSED; OpenFiles.Remove(f); SaveStatus(f, fnstatus, 7); return; }
        }
        f.open_mode = opt == COB_CLOSE_LOCK ? COB_OPEN_LOCKED : COB_OPEN_CLOSED;
        if (f.open_mode == COB_OPEN_CLOSED) OpenFiles.Remove(f);
        SaveStatus(f, fnstatus, st);
    }

    static void CloseHandle(CobFile f)
    {
        if (f.organization == COB_ORG_INDEXED) { f.idx?.Close(); f.idx = null; return; }
        if (f.fs == null) return;
        if (f.needsNewline && !(f.organization == COB_ORG_LINE_SEQUENTIAL && f.linorkeyptr != null))
        {
            if (f.organization == COB_ORG_LINE_SEQUENTIAL) WriteNewlines(f, 1); else f.fs.WriteByte((byte)'\n');
        }
        f.needsNewline = false;
        f.fs.Dispose();
        f.fs = null;
    }

    /// <summary>STOP RUN / end of run: close what the program left open, with libcob's warning.</summary>
    internal static void CloseAllFiles()
    {
        foreach (var f in OpenFiles.ToArray())
        {
            if (f.open_mode == COB_OPEN_CLOSED || f.open_mode == COB_OPEN_LOCKED || f.nonexistent) continue;
            if ((f.flag_select_features & (COB_SELECT_STDIN | COB_SELECT_STDOUT)) != 0) continue;
            Err.Write(Latin1Bytes($"libcob: warning: implicit CLOSE of {f.select_name} ('{f.assignName}')\n"));
            try { CloseHandle(f); } catch (IOException) { }
            f.open_mode = COB_OPEN_CLOSED;
        }
        OpenFiles.Clear();
        Err.Flush();
    }

    // ------------------------------------------------------------ START

    public static void cob_start(CobFile f, int cond, CobField key, CobField keysize, CobField fnstatus)
    {
        f.readDone = false;
        f.firstRead = 0;
        if (f.open_mode != COB_OPEN_I_O && f.open_mode != COB_OPEN_INPUT) { SaveStatus(f, fnstatus, 47); return; }
        if (f.access_mode == COB_ACCESS_RANDOM) { SaveStatus(f, fnstatus, 47); return; }
        if (f.nonexistent) { SaveStatus(f, fnstatus, 23); return; }
        int keylen = -1;
        if (keysize != null)
        {
            keylen = cob_get_int(keysize);
            if (keylen < 1 || keylen > key.size) { SaveStatus(f, fnstatus, 23); return; }
        }
        int st = f.organization switch
        {
            COB_ORG_RELATIVE => RelativeStart(f, cond, key),
            COB_ORG_INDEXED => f.idx.Start(cond, key, keylen),
            _ => 91,
        };
        f.endOfFile = st != 0;
        f.beginOfFile = false;
        f.firstRead = 1;
        SaveStatus(f, fnstatus, st);
    }

    // ------------------------------------------------------------ READ

    public static void cob_read(CobFile f, CobField key, CobField fnstatus, int read_opts)
    {
        f.readDone = false;
        if (f.open_mode != COB_OPEN_INPUT && f.open_mode != COB_OPEN_I_O) { SaveStatus(f, fnstatus, 47); return; }
        if (f.nonexistent)
        {
            if (f.firstRead == 0) { SaveStatus(f, fnstatus, 23); return; }
            f.firstRead = 0; SaveStatus(f, fnstatus, 10); return;
        }
        int st;
        if (key == null)
        {
            if (f.endOfFile && (read_opts & COB_READ_PREVIOUS) == 0) { SaveStatus(f, fnstatus, 46); return; }
            if (f.beginOfFile && (read_opts & COB_READ_PREVIOUS) != 0) { SaveStatus(f, fnstatus, 46); return; }
            st = ReadNext(f, read_opts);
        }
        else
        {
            st = f.organization switch
            {
                COB_ORG_RELATIVE => RelativeRead(f, key),
                COB_ORG_INDEXED => f.idx.Read(key),
                _ => 91,
            };
        }
        AfterRead(f, fnstatus, st, read_opts);
    }

    public static void cob_read_next(CobFile f, CobField fnstatus, int read_opts)
    {
        f.readDone = false;
        if (f.open_mode != COB_OPEN_INPUT && f.open_mode != COB_OPEN_I_O) { SaveStatus(f, fnstatus, 47); return; }
        if (f.nonexistent)
        {
            if (f.firstRead == 0) { SaveStatus(f, fnstatus, 46); return; }
            f.firstRead = 0; SaveStatus(f, fnstatus, 10); return;
        }
        if (f.endOfFile && (read_opts & COB_READ_PREVIOUS) == 0) { SaveStatus(f, fnstatus, 46); return; }
        if (f.beginOfFile && (read_opts & COB_READ_PREVIOUS) != 0) { SaveStatus(f, fnstatus, 46); return; }
        AfterRead(f, fnstatus, ReadNext(f, read_opts), read_opts);
    }

    static int ReadNext(CobFile f, int read_opts) => f.organization switch
    {
        COB_ORG_SEQUENTIAL => SequentialRead(f),
        COB_ORG_LINE_SEQUENTIAL => LineSeqRead(f),
        COB_ORG_RELATIVE => RelativeReadNext(f, read_opts),
        COB_ORG_INDEXED => f.idx.ReadNext(read_opts),
        _ => 30,
    };

    static void AfterRead(CobFile f, CobField fnstatus, int st, int read_opts)
    {
        if (st < 10)
        {
            f.firstRead = 0; f.readDone = true; f.endOfFile = false; f.beginOfFile = false;
            if (f.variable_record != null) cob_set_int(f.variable_record, (int)f.record.size);
        }
        else if (st == 10)
        {
            if ((read_opts & COB_READ_PREVIOUS) != 0) f.beginOfFile = true; else f.endOfFile = true;
        }
        SaveStatus(f, fnstatus, st);
    }

    // ------------------------------------------------------------ WRITE

    public static void cob_write(CobFile f, CobField rec, int opt, CobField fnstatus, int check_eop)
    {
        f.readDone = false;
        if (f.access_mode == COB_ACCESS_SEQUENTIAL)
        {
            if (f.open_mode != COB_OPEN_OUTPUT && f.open_mode != COB_OPEN_EXTEND) { SaveStatus(f, fnstatus, 48); return; }
        }
        else if (f.open_mode != COB_OPEN_OUTPUT && f.open_mode != COB_OPEN_I_O) { SaveStatus(f, fnstatus, 48); return; }

        if ((f.flag_select_features & COB_SELECT_STDOUT) != 0)
        {
            WriteStdout(f, rec, opt);
            SaveStatus(f, fnstatus, 0);
            return;
        }

        long size = rec.size;
        if (f.variable_record != null)
        {
            size = cob_get_int(f.variable_record);
            if (size > rec.size) size = rec.size;
        }
        if (size < f.record_min || size > f.record_max) { SaveStatus(f, fnstatus, 44); return; }
        f.record.size = size;

        bool eop = false;
        int st = f.organization switch
        {
            COB_ORG_SEQUENTIAL => SequentialWrite(f, opt),
            COB_ORG_LINE_SEQUENTIAL => LineSeqWrite(f, opt, check_eop != 0, out eop),
            COB_ORG_RELATIVE => RelativeWrite(f),
            COB_ORG_INDEXED => f.idx.Write(),
            _ => 30,
        };
        f.lastOpWasWrite = true;
        SaveStatus(f, fnstatus, st, eop);
    }

    // ------------------------------------------------------------ REWRITE / DELETE

    public static void cob_rewrite(CobFile f, CobField rec, int opt, CobField fnstatus)
    {
        bool readDone = f.readDone;
        f.readDone = false;
        if (f.open_mode != COB_OPEN_I_O) { SaveStatus(f, fnstatus, 49); return; }
        if (f.access_mode == COB_ACCESS_SEQUENTIAL && !readDone) { SaveStatus(f, fnstatus, 43); return; }
        long size = rec.size;
        if (f.organization == COB_ORG_SEQUENTIAL)
        {
            if (f.record.size != rec.size) { SaveStatus(f, fnstatus, 44); return; }
            if (f.variable_record != null && cob_get_int(f.variable_record) != f.record.size) { SaveStatus(f, fnstatus, 44); return; }
        }
        if (f.variable_record != null)
        {
            size = cob_get_int(f.variable_record);
            if (size < f.record_min || size > f.record_max) { SaveStatus(f, fnstatus, 44); return; }
            if (size > rec.size) size = rec.size;
        }
        int st = f.organization switch
        {
            COB_ORG_SEQUENTIAL => SequentialRewrite(f),
            COB_ORG_LINE_SEQUENTIAL => LineSeqRewrite(f, size),
            COB_ORG_RELATIVE => RelativeRewrite(f, size),
            COB_ORG_INDEXED => f.idx.Rewrite(size),
            _ => 30,
        };
        SaveStatus(f, fnstatus, st);
    }

    public static void cob_delete(CobFile f, CobField fnstatus)
    {
        bool readDone = f.readDone;
        f.readDone = false;
        if (f.open_mode != COB_OPEN_I_O) { SaveStatus(f, fnstatus, 49); return; }
        if (f.access_mode == COB_ACCESS_SEQUENTIAL && !readDone) { SaveStatus(f, fnstatus, 43); return; }
        int st = f.organization switch
        {
            COB_ORG_RELATIVE => RelativeDelete(f),
            COB_ORG_INDEXED => f.idx.Delete(),
            _ => 91,
        };
        SaveStatus(f, fnstatus, st);
    }

    /// <summary>DELETE FILE</summary>
    public static void cob_delete_file(CobFile f, CobField fnstatus)
    {
        if (f.organization == COB_ORG_SORT) { SaveStatus(f, fnstatus, 30); return; }
        if (f.open_mode == COB_OPEN_LOCKED) { SaveStatus(f, fnstatus, 38); return; }
        if (f.open_mode != COB_OPEN_CLOSED) { SaveStatus(f, fnstatus, 41); return; }
        if ((f.flag_select_features & (COB_SELECT_STDIN | COB_SELECT_STDOUT)) != 0) { SaveStatus(f, fnstatus, 30); return; }
        f.assignName = AssignText(f);
        string name = MapFileName(f.assignName);
        int st = 0;
        try
        {
            if (!File.Exists(name)) st = 35;
            else
            {
                File.Delete(name);
                if (f.organization == COB_ORG_INDEXED) IndexedFile.DeleteFiles(name);
            }
        }
        catch (UnauthorizedAccessException) { st = 37; }
        catch (IOException) { st = 30; }
        SaveStatus(f, fnstatus, st);
    }

    public static void cob_unlock_file(CobFile f, CobField fnstatus) => SaveStatus(f, fnstatus, 0);
    public static void cob_commit() { }
    public static void cob_rollback() { }

    // ------------------------------------------------------------ SEQUENTIAL

    static int ReadVarHeader(CobFile f, out int size)
    {
        size = 0;
        var h = new byte[4];
        int n = ReadFully(f.fs, h, VsqLen);
        if (n == 0) return 10;
        if (n < VsqLen) return 39;
        switch (VarSeqFormat)
        {
            case 0: if (h[2] != 0 || h[3] != 0) return 39; size = (h[0] << 8) | h[1]; break;
            case 1: size = (h[0] << 24) | (h[1] << 16) | (h[2] << 8) | h[3]; break;
            case 2: size = h[0] | (h[1] << 8) | (h[2] << 16) | (h[3] << 24); break;
            default: size = (h[0] << 8) | h[1]; break;
        }
        return 0;
    }

    static int ReadFully(Stream s, byte[] buf, int count, int offset = 0)
    {
        int got = 0;
        while (got < count)
        {
            int n = s.Read(buf, offset + got, count - got);
            if (n <= 0) break;
            got += n;
        }
        return got;
    }

    static int SequentialRead(CobFile f)
    {
        f.lastOpWasWrite = false;
        f.recordOff = f.fs.Position;
        int ret = 0;
        long want = f.record.size;
        long skip = 0;
        bool variable = f.record_min != f.record_max;
        if (variable)
        {
            int st = ReadVarHeader(f, out int size);
            if (st != 0) return st;
            if (size < f.record_min) ret = 4;
            if (size > f.record_max) { ret = 4; skip = size - f.record_max; size = f.record_max; }
            want = size;
            f.record.size = size;
        }
        var buf = new byte[want];
        int got = ReadFully(f.fs, buf, (int)want);
        if (skip > 0) f.fs.Seek(skip, SeekOrigin.Current);
        if (got == 0 && want > 0)
        {
            if (!variable) return 10;
            f.record.size = 0;
            return 4;
        }
        buf.AsSpan(0, got).CopyTo(f.record.data.Span(got));
        if (got < want) f.record.size = got;
        return ret;
    }

    static int SequentialWrite(CobFile f, int opt)
    {
        try
        {
            if ((opt & COB_WRITE_AFTER) != 0) { SeqAdvance(f, opt); f.needsNewline = true; }
            if (f.record_min != f.record_max)
            {
                int size = (int)f.record.size;
                var h = new byte[4];
                switch (VarSeqFormat)
                {
                    case 1: h[0] = (byte)(size >> 24); h[1] = (byte)(size >> 16); h[2] = (byte)(size >> 8); h[3] = (byte)size; break;
                    case 2: h[0] = (byte)size; h[1] = (byte)(size >> 8); h[2] = (byte)(size >> 16); h[3] = (byte)(size >> 24); break;
                    default: h[0] = (byte)(size >> 8); h[1] = (byte)size; break;
                }
                f.fs.Write(h, 0, VsqLen);
            }
            f.fs.Write(f.record.data.Span(f.record.size));
            if ((opt & COB_WRITE_BEFORE) != 0) { SeqAdvance(f, opt); f.needsNewline = false; }
            return 0;
        }
        catch (IOException) { return 30; }
    }

    static void SeqAdvance(CobFile f, int opt)
    {
        if ((opt & COB_WRITE_PAGE) != 0) { f.fs.WriteByte((byte)'\f'); return; }
        if ((opt & COB_WRITE_LINES) != 0)
        {
            int n = opt & COB_WRITE_MASK;
            if (n == 0) f.fs.WriteByte((byte)'\r');
            for (int i = 0; i < n; i++) f.fs.WriteByte((byte)'\n');
        }
    }

    static int SequentialRewrite(CobFile f)
    {
        try
        {
            f.fs.Seek(-f.record.size, SeekOrigin.Current);
            f.fs.Write(f.record.data.Span(f.record.size));
            return 0;
        }
        catch (IOException) { return 30; }
    }

    // ------------------------------------------------------------ LINE SEQUENTIAL

    static bool LsAllowedControl(byte b) => b == 0x08 || b == 0x1B || b == 0x0C || b == 0x0F || b == 0x09;

    static int LineSeqRead(CobFile f)
    {
        var fs = f.fs;
        long start = fs.Position;
        int max = f.record_max;
        var data = f.record.data;
        int i = 0, sts = 0;
        bool any = false;
        long dataEnd = start;
        while (true)
        {
            int n = fs.ReadByte();
            if (n < 0)
            {
                if (!any) return 10;
                dataEnd = fs.Position;
                break;
            }
            any = true;
            if (n == '\r')
            {
                int nx = fs.ReadByte();
                if (nx == '\n') { dataEnd = fs.Position - 2; break; }
                if (nx >= 0) fs.Seek(-1, SeekOrigin.Current);
                if (nx < 0) { dataEnd = fs.Position - 1; break; }
            }
            else if (n == '\n') { dataEnd = fs.Position - 1; break; }
            if (LsValidate && f.flag_line_adv == 0 && n < 0x20 && !LsAllowedControl((byte)n)) sts = 9;
            if (i < max)
            {
                data[i++] = (byte)n;
                if (i == max && LsSplit)
                {
                    // a line exactly record_max long is fine; a longer one comes back in pieces (06)
                    long here = fs.Position;
                    int a = fs.ReadByte();
                    if (a == '\r') { int b = fs.ReadByte(); if (b == '\n') { dataEnd = here; break; } }
                    else if (a == '\n') { dataEnd = here; break; }
                    else if (a < 0) { dataEnd = here; break; }
                    fs.Position = here;
                    dataEnd = here;
                    sts = 6;
                    break;
                }
            }
            else if (!LsSplit) sts = 4;
        }
        for (int k = i; k < max; k++) data[k] = (byte)' ';
        f.record.size = i;
        f.recordOff = start;
        f.lsLineEnd = dataEnd;
        return sts;
    }

    static int LineSeqSize(CobFile f)
    {
        long size = f.record.size;
        if (LsFixed) return (int)size;
        var s = f.record.data.Span(size);
        int n = (int)size;
        while (n > 0 && s[n - 1] == (byte)' ') n--;
        return n;
    }

    static void WriteNewlines(CobFile f, int n)
    {
        for (int i = 0; i < n; i++)
        {
            if (!UnixLF) f.fs.WriteByte((byte)'\r');
            f.fs.WriteByte((byte)'\n');
        }
    }

    static int LineSeqWrite(CobFile f, int opt, bool checkEop, out bool eop)
    {
        eop = false;
        int size = LineSeqSize(f);
        var lin = f.linorkeyptr;
        try
        {
            if (lin != null && f.needsTop) { WriteNewlines(f, lin.lin_top); f.needsTop = false; }
            if ((opt & COB_WRITE_AFTER) != 0)
            {
                int st = LsAdvance(f, opt, checkEop, ref eop);
                if (st != 0) return st;
                f.needsNewline = true;
            }
            if (size > 0)
            {
                var s = f.record.data.Span(size);
                if (LsValidate && f.flag_line_adv == 0)
                    foreach (var b in s) if (b < 0x20 && !LsAllowedControl(b)) return 71;
                f.fs.Write(s);
            }
            if (lin != null) WriteNewlines(f, 1);
            else if (opt == 0) { WriteNewlines(f, 1); f.needsNewline = false; }
            if ((opt & COB_WRITE_BEFORE) != 0)
            {
                int st = LsAdvance(f, opt, checkEop, ref eop);
                if (st != 0) return st;
                f.needsNewline = false;
            }
            return 0;
        }
        catch (IOException) { return 30; }
    }

    static int LsAdvance(CobFile f, int opt, bool checkEop, ref bool eop)
    {
        if (f.linorkeyptr != null) return LinageAdvance(f, opt, checkEop, ref eop);
        if ((opt & COB_WRITE_PAGE) != 0) { f.fs.WriteByte((byte)'\f'); return 0; }
        if ((opt & COB_WRITE_LINES) != 0)
        {
            int n = opt & COB_WRITE_MASK;
            if (n == 0) f.fs.WriteByte((byte)'\r');
            else WriteNewlines(f, n);
        }
        return 0;
    }

    static bool LinageCheck(CobFile f)
    {
        var l = f.linorkeyptr;
        l.lin_lines = cob_get_int(l.linage);
        if (l.lin_lines < 1) { cob_set_int(l.linage_ctr, 0); return false; }
        if (l.latfoot != null)
        {
            l.lin_foot = cob_get_int(l.latfoot);
            if (l.lin_foot < 1 || l.lin_foot > l.lin_lines) { cob_set_int(l.linage_ctr, 0); return false; }
        }
        else l.lin_foot = 0;
        l.lin_top = l.lattop != null ? cob_get_int(l.lattop) : 0;
        l.lin_bot = l.latbot != null ? cob_get_int(l.latbot) : 0;
        if (l.lin_top < 0 || l.lin_bot < 0) { cob_set_int(l.linage_ctr, 0); return false; }
        return true;
    }

    static int LinageAdvance(CobFile f, int opt, bool checkEop, ref bool eop)
    {
        var l = f.linorkeyptr;
        if ((opt & COB_WRITE_PAGE) != 0)
        {
            int i = cob_get_int(l.linage_ctr);
            if (i == 0) return 57;
            if (i < l.lin_lines) WriteNewlines(f, l.lin_lines - i);
            WriteNewlines(f, l.lin_bot);
            if (!LinageCheck(f)) return 57;
            WriteNewlines(f, l.lin_top);
            cob_set_int(l.linage_ctr, 1);
            return 0;
        }
        if ((opt & COB_WRITE_LINES) != 0)
        {
            int n = opt & COB_WRITE_MASK;
            int c = cob_get_int(l.linage_ctr);
            if (c == 0) return 57;
            int now = c + n;
            cob_set_int(l.linage_ctr, now);
            if (checkEop && l.lin_foot != 0 && now >= l.lin_foot) eop = true;
            if (now > l.lin_lines)
            {
                if (checkEop) eop = true;
                if (l.lin_lines > c) WriteNewlines(f, l.lin_lines - c);
                WriteNewlines(f, l.lin_bot);
                if (!LinageCheck(f)) return 57;
                cob_set_int(l.linage_ctr, 1);
                WriteNewlines(f, l.lin_top);
            }
            else if (n > 1) WriteNewlines(f, n - 1);
        }
        return 0;
    }

    static int LineSeqRewrite(CobFile f, long size)
    {
        if (f.recordOff < 0) return 43;
        f.record.size = size;
        int psize = LineSeqSize(f);
        long slot = f.lsLineEnd - f.recordOff;
        if (psize > slot) return 44;
        var s = f.record.data.Span(psize);
        if (LsValidate && f.flag_line_adv == 0)
            foreach (var b in s) if (b < 0x20 && !LsAllowedControl(b)) return 71;
        try
        {
            long cur = f.fs.Position;
            f.fs.Position = f.recordOff;
            if (psize > 0)
            {
                f.fs.Write(s);
                for (long k = psize; k < slot; k++) f.fs.WriteByte((byte)' ');
            }
            f.fs.Position = cur;
            return 0;
        }
        catch (IOException) { return 30; }
    }

    // ASSIGN DISPLAY: records go to standard output as lines, with the same
    // BEFORE / AFTER ADVANCING rules as a LINE SEQUENTIAL file (a report's page
    // padding is blank lines written that way)
    static void WriteStdout(CobFile f, CobField rec, int opt)
    {
        long size = rec.size;
        var s = rec.Span;
        int n = (int)size;
        while (n > 0 && s[n - 1] == (byte)' ') n--;
        if ((opt & COB_WRITE_AFTER) != 0) { StdoutAdvance(opt); f.needsNewline = true; }
        Out.Write(s.Slice(0, n));
        if (opt == 0) { Out.Write(NewLine); f.needsNewline = false; }
        if ((opt & COB_WRITE_BEFORE) != 0) { StdoutAdvance(opt); f.needsNewline = false; }
    }

    static void StdoutAdvance(int opt)
    {
        if ((opt & COB_WRITE_PAGE) != 0) { Out.WriteByte((byte)'\f'); return; }
        if ((opt & COB_WRITE_LINES) != 0)
        {
            int n = opt & COB_WRITE_MASK;
            if (n == 0) Out.WriteByte((byte)'\r');
            for (int i = 0; i < n; i++) Out.Write(NewLine);
        }
    }

    // ------------------------------------------------------------ RELATIVE

    static long RelSize(CobFile f) => f.record_max + 8;

    static long RelKey(CobFile f) => f.keys != null && f.keys.Length > 0 && f.keys[0].field != null ? cob_get_llint(f.keys[0].field) : f.relSlot;

    /// <summary>Store a slot number in the RELATIVE KEY; false if it doesn't fit (status 14).</summary>
    static bool SetRelKey(CobFile f, long slot)
    {
        if (f.keys == null || f.keys.Length == 0 || f.keys[0].field == null) return true;
        var k = f.keys[0].field;
        return Num.Store(k, slot, 0, STORE_KEEP_ON_OVERFLOW) == 0;
    }

    static long ReadSlotHeader(CobFile f, long slot)
    {
        long pos = (slot - 1) * RelSize(f);
        if (pos + 8 > f.fs.Length) return -1;
        f.fs.Position = pos;
        var h = new byte[8];
        if (ReadFully(f.fs, h, 8) < 8) return -1;
        return BitConverter.ToInt64(h, 0);
    }

    static int RelativeRead(CobFile f, CobField key)
    {
        long k = cob_get_llint(key);
        if (k < 1) return 23;
        long len = ReadSlotHeader(f, k);
        if (len <= 0) return 23;
        var buf = new byte[f.record_max];
        if (ReadFully(f.fs, buf, f.record_max) < f.record_max) return 30;
        buf.AsSpan(0, f.record_max).CopyTo(f.record.data.Span(f.record_max));
        f.record.size = len;
        f.relSlot = k;
        return 0;
    }

    static int RelativeReadNext(CobFile f, int opts)
    {
        var fs = f.fs;
        long rs = RelSize(f);
        long flen = fs.Length;
        if (flen == 0) return 10;
        if (flen < rs) return 30;
        bool prev = (opts & COB_READ_PREVIOUS) != 0;
        if (f.firstRead == 2 && !prev) fs.Position = 0;
        if ((opts & COB_READ_FIRST) != 0) fs.Position = 0;
        if ((opts & COB_READ_LAST) != 0) { fs.Position = (flen / rs - 1) * rs; prev = true; f.firstRead = 1; }
        if (prev && f.firstRead == 2) fs.Position = (flen / rs - 1) * rs;
        else if (prev && f.firstRead == 0)
        {
            long p = fs.Position;
            if (p >= 2 * rs) fs.Position = p - 2 * rs; else return 10;
        }
        var h = new byte[8];
        while (true)
        {
            long pos = fs.Position;
            int n = ReadFully(fs, h, 8);
            if (n == 0) return 10;
            if (n < 8) return 30;
            long len = BitConverter.ToInt64(h, 0);
            if (len > 0)
            {
                var buf = new byte[f.record_max];
                if (ReadFully(fs, buf, f.record_max) < f.record_max) return 30;
                long slot = pos / rs + 1;
                if (!SetRelKey(f, slot))
                {
                    // status 14: the record number doesn't fit the RELATIVE KEY. Like
                    // GnuCOBOL: the record is still read, the key is set to 0, and the file
                    // stays on this record (libcob relative_read_next)
                    Num.Store(f.keys[0].field, 0, 0, 0);
                    buf.AsSpan().CopyTo(f.record.data.Span(f.record_max));
                    f.record.size = len;
                    f.relSlot = slot;
                    fs.Position = pos;
                    return 14;
                }
                buf.AsSpan().CopyTo(f.record.data.Span(f.record_max));
                f.record.size = len;
                f.relSlot = slot;
                return 0;
            }
            if (prev)
            {
                if (pos < rs) return 10;
                fs.Position = pos - rs;
            }
            else fs.Position = pos + rs;
        }
    }

    static int RelativeWrite(CobFile f)
    {
        var fs = f.fs;
        long rs = RelSize(f);
        long slot;
        if (f.access_mode != COB_ACCESS_SEQUENTIAL)
        {
            slot = RelKey(f);
            if (slot < 1) return 24;
            long len = ReadSlotHeader(f, slot);
            if (len > 0) return 22;
            fs.Position = (slot - 1) * rs;
        }
        else
        {
            if (fs.Position % rs != 0) fs.Position = (fs.Position / rs + 1) * rs;
            slot = fs.Position / rs + 1;
            if (!SetRelKey(f, slot)) return 24;
        }
        try
        {
            fs.Write(BitConverter.GetBytes(f.record.size), 0, 8);
            fs.Write(f.record.data.Span(f.record_max));
            f.relSlot = slot;
            return 0;
        }
        catch (IOException) { return 30; }
    }

    static int RelativeRewrite(CobFile f, long size)
    {
        long slot = f.access_mode == COB_ACCESS_SEQUENTIAL ? f.relSlot : RelKey(f);
        if (slot < 1) return 24;
        long len = ReadSlotHeader(f, slot);
        if (len <= 0) return 23;
        try
        {
            long pos = (slot - 1) * RelSize(f);
            f.fs.Position = pos;
            f.fs.Write(BitConverter.GetBytes(size), 0, 8);
            f.fs.Write(f.record.data.Span(f.record_max));
            f.record.size = size;
            if (f.access_mode == COB_ACCESS_SEQUENTIAL) f.fs.Position = pos + RelSize(f);
            return 0;
        }
        catch (IOException) { return 30; }
    }

    static int RelativeDelete(CobFile f)
    {
        long slot = f.access_mode == COB_ACCESS_SEQUENTIAL ? f.relSlot : RelKey(f);
        if (slot < 1) return 24;
        long len = ReadSlotHeader(f, slot);
        if (len <= 0) return 23;
        long pos = (slot - 1) * RelSize(f);
        long back = f.fs.Position;
        f.fs.Position = pos;
        f.fs.Write(new byte[8], 0, 8);
        f.fs.Position = f.access_mode == COB_ACCESS_SEQUENTIAL ? pos + RelSize(f) : back;
        return 0;
    }

    static int RelativeStart(CobFile f, int cond, CobField key)
    {
        var fs = f.fs;
        long rs = RelSize(f);
        long count = fs.Length / rs;
        if (count == 0) return 23;
        long k = cond == COB_FI ? 1 : cond == COB_LA ? count : cob_get_llint(key);
        if (cond == COB_FI) cond = COB_GE;
        if (cond == COB_LA) cond = COB_LE;
        if ((cond == COB_LT || cond == COB_LE) && k > count) k = count + (cond == COB_LT ? 1 : 0);
        if (k < 1)
        {
            if (cond == COB_GE || cond == COB_GT) k = cond == COB_GT ? 0 : 1;
            else return 23;
        }
        if (cond == COB_LT) k--;
        if (cond == COB_GT) k++;
        if (k < 1) return 23;
        int step = cond == COB_LT || cond == COB_LE ? -1 : 1;
        for (long s = k; s >= 1 && s <= count; s += step)
        {
            if (ReadSlotHeader(f, s) > 0) { fs.Position = (s - 1) * rs; return 0; }
            if (cond == COB_EQ) break;
        }
        return 23;
    }

    // ------------------------------------------------------------ fatal error (no FILE STATUS, no handler)

    static string IoStatusText(int st) => st switch
    {
        10 => "end of file",
        14 => "key out of range",
        21 => "key order not ascending",
        22 => "record key already exists",
        23 => "record key does not exist",
        30 => "permanent file error",
        31 => "inconsistent file name",
        35 => "file does not exist",
        37 => "permission denied",
        39 => "mismatch of fixed file attributes",
        41 => "file already open",
        42 => "file not open",
        43 => "READ must be executed first",
        44 => "record overflow",
        46 => "READ after unsuccessful READ/START",
        47 => "READ/START not allowed, file not open for input",
        48 => "WRITE not allowed, file not open for output",
        49 => "DELETE/REWRITE not allowed, file not open for I-O",
        51 => "record locked by another file connector",
        57 => "LINAGE values invalid",
        61 => "file sharing conflict",
        71 => "invalid data in LINE SEQUENTIAL file",
        91 => "runtime library is not configured for this operation",
        _ => "unknown file error",
    };

    // one line of advice for the learner, after libcob's own message
    static string IoStatusHint(int st, CobFile f) => st switch
    {
        10 => "Use READ ... AT END to stop reading when the file runs out.",
        35 => $"Check the name in ASSIGN. The program looked in {Directory.GetCurrentDirectory()}. Use OPEN OUTPUT (or SELECT OPTIONAL) to create the file.",
        37 => "Windows refused access: the file may be read-only, or open in another program.",
        41 => "The file is OPEN already. CLOSE it before opening it again.",
        42 => "The file isn't open. Check the OPEN statement runs first.",
        43 => "READ the record before REWRITE or DELETE.",
        44 => "The record is longer or shorter than the file allows (RECORD clause / record size).",
        46 => "A previous READ reached the end (or failed); the file has no next record.",
        47 => "OPEN the file INPUT or I-O before READ or START.",
        48 => "OPEN the file OUTPUT, EXTEND or I-O before WRITE.",
        49 => "OPEN the file I-O before REWRITE or DELETE.",
        61 => "The file is open somewhere else (another OPEN in this program, or another program).",
        71 => "A LINE SEQUENTIAL record may not contain control characters (below space) except TAB and form feed.",
        _ => null,
    };

    static void FileFatalError()
    {
        var f = Global.cob_error_file;
        Out.Flush();
        if (f == null) { Err.Write(Latin1Bytes("libcob: error: file error\n")); }
        else
        {
            int st = Status(f);
            string name = f.assignName ?? AssignText(f);
            string shown = f.resolvedName != null && f.resolvedName != name ? $"'{name}' => {f.resolvedName}" : $"'{name}'";
            Err.Write(Latin1Bytes($"libcob: error: {IoStatusText(st)} (status = {st:00}) for file {f.select_name} ({shown})\n"));
            var hint = IoStatusHint(st, f);
            if (hint != null && Env("COB_NO_HINTS") != "1") Err.Write(Latin1Bytes($"  hint: {hint}\n"));
        }
        CloseAllFiles();
        ScreenShutdown();
        Err.Flush();
        Environment.Exit(1);
    }

    static byte[] Latin1Bytes(string s)
    {
        var b = new byte[s.Length];
        for (int i = 0; i < s.Length; i++) b[i] = s[i] < 256 ? (byte)s[i] : (byte)'?';
        return b;
    }
}
