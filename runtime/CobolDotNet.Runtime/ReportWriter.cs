// Report Writer: INITIATE / GENERATE / TERMINATE / SUPPRESS (libcob reportio.c).
//
// The compiler describes each RD as a set of linked tables, exactly libcob's structs:
// a CobReport holds the page limits and points at the first report line; lines link to
// sister lines (next 01 group) and child lines (LINE entries inside a group); each printable
// line has a list of CobReportField (COLUMN entries: literal VALUE, SOURCE, SUM counter);
// controls (CONTROLS ARE ...) list the CONTROL HEADING / FOOTING lines that refer to them;
// SUM counters list the fields they add up. The generated code builds these as objects with
// constructors taking the C initializer's values in struct-member order, so a C pointer link
// is an object reference here.
//
// The logic below follows reportio.c step for step, including its quirks (they decide where
// the lines land on the page, and the output must match stock byte for byte). The debug-log
// dumps (COB_DEBUG_LOG) are not ported.
//
// USE BEFORE REPORTING: GENERATE / TERMINATE return a declarative section's number, the
// generated code PERFORMs it and calls again with that number; see "USE BEFORE REPORTING"
// below for how the resume points (gotos into loops in libcob) are reproduced.

using System;
using static CobolDotNet.Runtime.CobConst;

namespace CobolDotNet.Runtime;

/// <summary>cob_report_sum: one field added into a SUM counter.</summary>
public sealed class CobReportSum
{
    public CobReportSum next;
    public CobField f;
    public CobReportSum(CobReportSum next, CobField f) { this.next = next; this.f = f; }
}

/// <summary>cob_report_field: one printable item (COLUMN entry) of a report line.</summary>
public sealed class CobReportField
{
    public CobReportField next;
    public CobField f;              // the item itself (its storage is in the report record)
    public CobField source;         // SOURCE, or the SUM counter
    public CobField control;        // CONTROL field (PRESENT AFTER ...)
    public string litval;           // literal VALUE
    public int litlen;
    public int flags;
    public int line;
    public int column;
    public int step_count;
    public int next_group_line;
    public int level;
    public int group_indicate;      // GROUP INDICATE: already printed
    public int suppress;            // set by PRESENT WHEN code and SUPPRESS
    public int present_now;         // PRESENT AFTER / ABSENT AFTER state

    public CobReportField(CobReportField next, CobField f, CobField source, CobField control,
        string litval, int litlen, int flags, int line, int column, int step_count,
        int next_group_line, int level, int group_indicate, int suppress, int present_now = 0)
    {
        this.next = next; this.f = f; this.source = source; this.control = control;
        this.litval = litval; this.litlen = litlen; this.flags = flags; this.line = line;
        this.column = column; this.step_count = step_count; this.next_group_line = next_group_line;
        this.level = level; this.group_indicate = group_indicate; this.suppress = suppress;
        this.present_now = present_now;
    }
}

/// <summary>cob_report_line: an 01 report group or a LINE inside one.</summary>
public sealed class CobReportLine
{
    public CobReportLine sister;
    public CobReportLine child;
    public CobReportField fields;
    public CobField control;
    public int use_decl;
    public int flags;
    public int line;
    public int step_count;
    public int next_group_line;
    public int report_flags;
    public int suppress;

    public CobReportLine(CobReportLine sister, CobReportLine child, CobReportField fields,
        CobField control, int use_decl, int flags, int line, int step_count,
        int next_group_line, int report_flags, int suppress)
    {
        this.sister = sister; this.child = child; this.fields = fields; this.control = control;
        this.use_decl = use_decl; this.flags = flags; this.line = line; this.step_count = step_count;
        this.next_group_line = next_group_line; this.report_flags = report_flags; this.suppress = suppress;
    }
}

/// <summary>cob_report_control_ref: a line that refers to a control field.</summary>
public sealed class CobReportControlRef
{
    public CobReportControlRef next;
    public CobReportLine ref_line;
    public CobReportControlRef(CobReportControlRef next, CobReportLine ref_line) { this.next = next; this.ref_line = ref_line; }
}

/// <summary>cob_report_control: one CONTROLS ARE field.</summary>
public sealed class CobReportControl
{
    public CobReportControl next;
    public string name;
    public CobField f;
    public CobField val;            // value before the last GENERATE
    public CobField sf;             // new value, saved while the footings print the old one
    public CobReportControlRef control_ref;
    public int sequence;
    public bool data_change, has_heading, has_footing, suppress;

    public CobReportControl(CobReportControl next, string name, CobField f, CobField val, CobField sf,
        CobReportControlRef control_ref, int sequence, int data_change, int has_heading,
        int has_footing, int suppress)
    {
        this.next = next; this.name = name; this.f = f; this.val = val; this.sf = sf;
        this.control_ref = control_ref; this.sequence = sequence; this.data_change = data_change != 0;
        this.has_heading = has_heading != 0; this.has_footing = has_footing != 0; this.suppress = suppress != 0;
    }
}

/// <summary>cob_report_sum_ctr: one SUM counter.</summary>
public sealed class CobReportSumCtr
{
    public CobReportSumCtr next;
    public string name;
    public CobReportSum sum;
    public CobField counter;
    public CobField f;
    public CobReportControl control;    // RESET when this control changes
    public bool reset_final, control_final, subtotal, crossfoot;

    public CobReportSumCtr(CobReportSumCtr next, string name, CobReportSum sum, CobField counter,
        CobField f, CobReportControl control, int reset_final, int control_final, int subtotal, int crossfoot)
    {
        this.next = next; this.name = name; this.sum = sum; this.counter = counter; this.f = f;
        this.control = control; this.reset_final = reset_final != 0; this.control_final = control_final != 0;
        this.subtotal = subtotal != 0; this.crossfoot = crossfoot != 0;
    }
}

/// <summary>cob_report: one RD.</summary>
public sealed class CobReport
{
    public string report_name;
    public CobReport next;
    public CobFile report_file;
    public CobField page_counter;
    public CobField line_counter;
    public CobReportLine first_line;
    public CobReportControl controls;
    public CobReportSumCtr sum_counters;
    // the generated code sets these (PAGE LIMIT given by data items, CODE IS)
    public int def_lines, def_cols, def_heading, def_first_detail, def_last_control, def_last_detail, def_footing;
    public int curr_page, curr_line, curr_cols, curr_status, next_value;
    public int control_final, is_global;
    public bool first_detail, in_page_footing, in_page_heading, first_generate, initiate_done, next_line;
    public bool next_line_plus, next_page, next_just_set, in_report_footing, incr_line, foot_next_page;
    public int code_is_present;
    public int code_len;
    public object code_is;          // CODE IS value: a CobPtr (data item) or a string (literal)

    public CobReport(string report_name, CobReport next, CobFile report_file, CobField page_counter,
        CobField line_counter, CobReportLine first_line, CobReportControl controls, CobReportSumCtr sum_counters,
        int def_lines, int def_cols, int def_heading, int def_first_detail, int def_last_control,
        int def_last_detail, int def_footing, int curr_page, int curr_line, int curr_cols,
        int curr_status, int next_value, int control_final, int is_global)
    {
        this.report_name = report_name; this.next = next; this.report_file = report_file;
        this.page_counter = page_counter; this.line_counter = line_counter; this.first_line = first_line;
        this.controls = controls; this.sum_counters = sum_counters;
        this.def_lines = def_lines; this.def_cols = def_cols; this.def_heading = def_heading;
        this.def_first_detail = def_first_detail; this.def_last_control = def_last_control;
        this.def_last_detail = def_last_detail; this.def_footing = def_footing;
        this.curr_page = curr_page; this.curr_line = curr_line; this.curr_cols = curr_cols;
        this.curr_status = curr_status; this.next_value = next_value;
        this.control_final = control_final; this.is_global = is_global;
    }
}

public static partial class Cob
{
    public const int COB_REPORT_LINE = 1 << 0;
    public const int COB_REPORT_LINE_PLUS = 1 << 1;
    public const int COB_REPORT_COLUMN_PLUS = 1 << 2;
    public const int COB_REPORT_RESET_FINAL = 1 << 3;
    public const int COB_REPORT_HEADING = 1 << 4;
    public const int COB_REPORT_FOOTING = 1 << 5;
    public const int COB_REPORT_PAGE_HEADING = 1 << 6;
    public const int COB_REPORT_PAGE_FOOTING = 1 << 7;
    public const int COB_REPORT_CONTROL_HEADING = 1 << 8;
    public const int COB_REPORT_CONTROL_HEADING_FINAL = 1 << 9;
    public const int COB_REPORT_CONTROL_FOOTING = 1 << 10;
    public const int COB_REPORT_CONTROL_FOOTING_FINAL = 1 << 11;
    public const int COB_REPORT_DETAIL = 1 << 12;
    public const int COB_REPORT_NEXT_GROUP_LINE = 1 << 13;
    public const int COB_REPORT_NEXT_GROUP_PLUS = 1 << 14;
    public const int COB_REPORT_NEXT_GROUP_PAGE = 1 << 15;
    public const int COB_REPORT_LINE_NEXT_PAGE = 1 << 16;
    public const int COB_REPORT_NEXT_PAGE = 1 << 17;
    public const int COB_REPORT_GROUP_INDICATE = 1 << 18;
    public const int COB_REPORT_GROUP_ITEM = 1 << 19;
    public const int COB_REPORT_HAD_WHEN = 1 << 20;
    public const int COB_REPORT_COLUMN_LEFT = 1 << 21;
    public const int COB_REPORT_COLUMN_CENTER = 1 << 22;
    public const int COB_REPORT_COLUMN_RIGHT = 1 << 23;
    public const int COB_REPORT_PRESENT = 1 << 24;
    public const int COB_REPORT_BEFORE = 1 << 25;
    public const int COB_REPORT_PAGE = 1 << 26;
    public const int COB_REPORT_ALL = 1 << 27;
    public const int COB_REPORT_NEGATE = 1 << 28;

    const int REPORT_MAX_LINES = 9999;
    const int REPORT_MAX_COLS = 999;
    const int MAX_ACTIVE_REPORTS = 10;
    const int COB_SMALL_MAX = 1023;

    const int EC_REPORT_ACTIVE = 0x0E01, EC_REPORT_INACTIVE = 0x0E05, EC_REPORT_PAGE_LIMIT = 0x0E09;

    const int RW_ND = COB_REPORT_HEADING | COB_REPORT_FOOTING | COB_REPORT_PAGE_HEADING | COB_REPORT_PAGE_FOOTING
                    | COB_REPORT_CONTROL_HEADING | COB_REPORT_CONTROL_HEADING_FINAL
                    | COB_REPORT_CONTROL_FOOTING | COB_REPORT_CONTROL_FOOTING_FINAL;
    static bool RwNotDetail(int f) => (f & RW_ND) != 0;

    static readonly CobReport[] rwActive = new CobReport[MAX_ACTIVE_REPORTS];
    static readonly CobFieldAttr rwAlphaAttr = new(TYPE_ALPHANUMERIC, 0, 0, 0, null);
    // libcob's const_num_attr is {COB_TYPE_NUMERIC, 0, 0, 0}; one digit here says the same
    // thing (the source is the single digit "0") without relying on a 0-digit field
    static readonly CobFieldAttr rwNumAttr = new(TYPE_NUMERIC_DISPLAY, 1, 0, 0, null);

    // cob_report_generate's function-static variables
    static int rwMaxctl, rwLn, rwNum, rwGengrp, rwLastUse;

    /// <summary>COB_COL_JUST_LRC (runtime.cfg col_just_lrc, default true): honor COLUMN LEFT/CENTER/RIGHT.</summary>
    static readonly bool rwColJustLrc = RwEnvBool("COB_COL_JUST_LRC", true);

    static bool RwEnvBool(string name, bool dflt)
    {
        string v = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(v)) return dflt;
        v = v.Trim().ToLowerInvariant();
        if (v is "1" or "true" or "yes" or "on" or "t" or "y") return true;
        if (v is "0" or "false" or "no" or "off" or "f" or "n") return false;
        return dflt;
    }

    /// <summary>COB_SET_REPORT (codeoptim): the FD's file handle for the RD.</summary>
    public static void cob_set_report(CobReport r, CobFile f) => r.report_file = f;

    static void RwRuntimeError(string msg)
    {
        Out.Flush();
        Err.Write(Latin1Bytes($"libcob: error: {msg}\n"));
        Err.Flush();
    }

    // Move "String" to 'dst' field
    static void RwStrMove(CobField dst, byte[] src, int size)
        => cob_move(new CobField(size, new CobPtr(src, 0), rwAlphaAttr), dst);

    // Initialize data field: MOVE ZERO (numeric) or SPACES
    static CobField RwFieldInit(CobField f)
    {
        if (f == null) return null;
        if (IsNumeric(f.Type)) cob_move(new CobField(1, "0", rwNumAttr), f);
        else cob_move(new CobField(1, " ", rwAlphaAttr), f);
        return f;
    }

    // Make a new field the same format as that given
    static CobField RwFieldDup(CobField f, int incr)
    {
        long dsize = f.size + incr;
        var fld = new CobField(dsize, new CobPtr(Math.Max(dsize, 38) + 1), f.attr);   // COB_MAX_DIGITS
        RwFieldInit(fld);
        return fld;
    }

    // Free control temp areas
    static void RwFreeControlFields(CobReport r)
    {
        for (var rc = r.controls; rc != null; rc = rc.next)
        {
            rc.val = null;
            rc.sf = null;
            rc.has_heading = false;
            rc.has_footing = false;
            for (var rr = rc.control_ref; rr != null; rr = rr.next)
            {
                if ((rr.ref_line.flags & (COB_REPORT_CONTROL_HEADING | COB_REPORT_CONTROL_HEADING_FINAL)) != 0)
                    rc.has_heading = true;
                if ((rr.ref_line.flags & (COB_REPORT_CONTROL_FOOTING | COB_REPORT_CONTROL_FOOTING_FINAL)) != 0)
                    rc.has_footing = true;
            }
        }
        for (int k = 0; k < MAX_ACTIVE_REPORTS; k++)
            if (rwActive[k] == r) rwActive[k] = null;
    }

    // Clear the 'group_indicate' flag for all fields
    static void RwClearGroupIndicate(CobReportLine l)
    {
        for (var f = l.fields; f != null; f = f.next) f.group_indicate = 0;
        if (l.child != null) RwClearGroupIndicate(l.child);
        if (l.sister != null) RwClearGroupIndicate(l.sister);
    }

    // Clear the 'suppress' flag for all fields (group items keep theirs)
    static void RwClearSuppress(CobReportLine l)
    {
        l.suppress = 0;
        for (var f = l.fields; f != null; f = f.next)
        {
            if ((f.flags & COB_REPORT_GROUP_ITEM) != 0) continue;
            f.suppress = 0;
        }
        if (l.child != null) RwClearSuppress(l.child);
        if (l.sister != null) RwClearSuppress(l.sister);
    }

    // Control field sequence value for the given report line, else -1
    static int RwGetControlSequence(CobReport r, CobReportLine l)
    {
        for (var c = r.controls; c != null; c = c.next)
            for (var rr = c.control_ref; rr != null; rr = rr.next)
                if (rr.ref_line == l) return c.sequence;
        return -1;
    }

    // If this line has NEXT GROUP ... then set info in report header
    static void RwSetNextInfo(CobReport r, CobReportLine l)
    {
        if ((l.flags & COB_REPORT_NEXT_GROUP_LINE) != 0)
        {
            r.next_value = l.next_group_line;
            r.next_line = true;
            r.next_just_set = true;
            r.next_line_plus = false;
        }
        if ((l.flags & COB_REPORT_NEXT_GROUP_PLUS) != 0)
        {
            r.next_value = l.next_group_line;
            r.next_line = false;
            r.next_line_plus = true;
            r.next_just_set = true;
        }
        if ((l.flags & COB_REPORT_NEXT_GROUP_PAGE) != 0)
        {
            r.next_value = l.next_group_line;
            r.next_line = false;
            r.next_page = true;
            r.next_just_set = true;
        }
    }

    static CobReportLine RwGetPrintLine(CobReportLine l)
    {
        while (l != null && l.fields == null && l.child != null)
            l = l.child;                // Find line with data fields
        return l;
    }

    // Add two fields together giving result. Both are copied into PIC 9 DISPLAY fields of
    // the operand's size, digits and scale first (cob_add doesn't handle NUMERIC EDITED).
    static void RwAddFields(CobField op1, CobField op2, CobField rslt)
    {
        var fld1 = RwDisplayCopy(op1);
        var fld2 = RwDisplayCopy(op2);
        cob_add(fld1, fld2, 0);
        cob_move(fld1, rslt);           // Copy SUM back to result field
    }

    static CobField RwDisplayCopy(CobField op)
    {
        var a = op.attr;
        var buf = new byte[Math.Max(30, op.size)];      // libcob: char data[30]
        buf.AsSpan(0, (int)op.size).Fill((byte)'0');
        var fld = new CobField(op.size, new CobPtr(buf, 0),
            new CobFieldAttr(TYPE_NUMERIC_DISPLAY, a.digits, a.scale, a.flags, a.pic));
        cob_move(op, fld);
        return fld;
    }

    // Verify that each LINE # is within PAGE LIMITS
    static void RwLimitCheckOneLine(CobReport r, CobReportLine fl)
    {
        if (fl.line > 0 && r.def_lines > 0 && fl.line > r.def_lines)
        {
            RwRuntimeError($"INITIATE {r.report_name} LINE {fl.line} exceeds PAGE LIMIT {r.def_lines}");
            SetException(EC_REPORT_PAGE_LIMIT);
            r.initiate_done = false;
            return;
        }
        if (fl.next_group_line > 0 && r.def_lines > 0 && fl.next_group_line > r.def_lines)
        {
            RwRuntimeError($"INITIATE {r.report_name} NEXT GROUP {fl.next_group_line} exceeds PAGE LIMIT");
            SetException(EC_REPORT_PAGE_LIMIT);
            r.initiate_done = false;
            return;
        }
        for (var rf = fl.fields; rf != null; rf = rf.next)
        {
            if (rf.line != 0 && rf.line > r.def_lines)
            {
                RwRuntimeError($"INITIATE {r.report_name} LINE {rf.line} exceeds PAGE LIMIT");
                SetException(EC_REPORT_PAGE_LIMIT);
                r.initiate_done = false;
                return;
            }
            if (rf.next_group_line != 0 && rf.next_group_line > r.def_lines)
            {
                RwRuntimeError($"INITIATE {r.report_name} NEXT GROUP {rf.next_group_line} exceeds PAGE LIMIT");
                SetException(EC_REPORT_PAGE_LIMIT);
                r.initiate_done = false;
                return;
            }
        }
    }

    static void RwLimitCheckLine(CobReport r, CobReportLine fl)
    {
        RwLimitCheckOneLine(r, fl);
        if (fl.child != null) RwLimitCheckLine(r, fl.child);
        if (fl.sister != null) RwLimitCheckLine(r, fl.sister);
    }

    static void RwSaveLineCounter(CobReport r)
    {
        int ln = r.curr_line;
        if (ln > r.def_lines) ln = 0;
        if (ln < 0) ln = 0;
        if (r.page_counter != null) cob_set_int(r.page_counter, r.curr_page);
        if (r.line_counter != null) cob_set_int(r.line_counter, ln);
    }

    // PRESENT AFTER / ABSENT AFTER: f == null means a new page, else the control that changed
    static void RwLineControlOne(CobReport r, CobReportLine l, CobField f)
    {
        if (l == null) return;
        for (var rf = l.fields; rf != null; rf = rf.next)
        {
            if ((rf.flags & COB_REPORT_PRESENT) == 0) continue;
            if ((rf.flags & COB_REPORT_NEGATE) == 0 && rf.present_now == 0)
            {
                if (f == null)
                {
                    if ((rf.flags & COB_REPORT_PAGE) != 0) rf.present_now = 1;     // PRESENT After New Page
                }
                else if (rf.control == f) rf.present_now = 1;                      // Control field changed
            }
            else if ((rf.flags & COB_REPORT_NEGATE) != 0 && rf.present_now != 0)
            {
                if (f == null)
                {
                    if ((rf.flags & COB_REPORT_PAGE) != 0) rf.present_now = 0;
                }
                else if (rf.control == f) rf.present_now = 0;
            }
        }
    }

    static void RwLineControlChg(CobReport r, CobReportLine l, CobField f)
    {
        RwLineControlOne(r, l, f);
        if (l.child != null) RwLineControlChg(r, l.child, f);
        if (l.sister != null) RwLineControlChg(r, l.sister, f);
    }

    // Write one line of report
    static void RwWriteRec(CobReport r, int opt)
    {
        var f = r.report_file;
        long recordSize = f.record.size;
        int num = opt & COB_WRITE_MASK;

        // temporarily truncate the line if needed, max is REPORT_MAX_COLS
        if (f.record.size > (uint)r.def_cols) f.record.size = r.def_cols;

        if (r.code_is_present != 0 && r.code_len > 0)
        {
            // Insert the CODE IS value in front: the record is swapped for a temporary
            // buffer and restored afterwards, as libcob does
            var data = new byte[REPORT_MAX_COLS + 2];
            var origData = f.record.data;
            long left = f.record.size;
            f.record.data = new CobPtr(data, 0);
            f.record.size += r.code_len;
            if (f.record.size > REPORT_MAX_COLS)
            {
                f.record.size = REPORT_MAX_COLS;
                left = f.record.size - r.code_len;
            }
            byte[] code = r.code_is switch
            {
                CobPtr p => p.Span(r.code_len).ToArray(),
                string s => Latin1Bytes(s),
                _ => new byte[r.code_len],
            };
            Array.Copy(code, 0, data, 0, Math.Min(code.Length, r.code_len));
            origData.Span(left).CopyTo(data.AsSpan(r.code_len));

            if (num > 1 && (opt & COB_WRITE_LINES) != 0)
            {
                opt = (opt & ~COB_WRITE_MASK) | 1;
                while (num > 0)
                {
                    cob_write(f, f.record, opt, null, 0);
                    data.AsSpan(r.code_len, (int)left).Fill((byte)' ');
                    num--;
                }
            }
            else cob_write(f, f.record, opt, null, 0);
            f.record.data = origData;
        }
        else cob_write(f, f.record, opt, null, 0);
        f.record.size = recordSize;
    }

    static void RwClearRecord(CobReport r)
    {
        var f = r.report_file;
        var p = f.record.data;
        int n = Math.Min(f.record_max, p.Arr.Length - p.Off);
        p.Arr.AsSpan(p.Off, n).Fill((byte)' ');
    }

    // Write the Page Footing
    static void RwDoPageFooting(CobReport r)
    {
        if (r.in_page_footing) return;
        r.in_page_footing = true;
        RwReportLineType(r, r.first_line, COB_REPORT_PAGE_FOOTING);
        RwClearRecord(r);
        if (r.curr_line < r.def_lines)
        {
            RwWriteRec(r, COB_WRITE_BEFORE | COB_WRITE_LINES | (r.def_lines - r.curr_line));
            r.curr_line = r.def_lines;
            r.incr_line = false;
        }
        else r.curr_line = 1;
        RwSaveLineCounter(r);
        r.first_detail = true;
        r.in_page_footing = false;
    }

    // Write the Page Heading
    static void RwDoPageHeading(CobReport r)
    {
        if (r.in_page_heading) return;
        int opt = COB_WRITE_BEFORE | COB_WRITE_LINES | 1;
        RwClearRecord(r);
        if (!r.in_page_heading
         && !r.first_generate
         && r.def_lines > 0
         && r.def_heading > 0
         && r.curr_line <= r.def_lines
         && r.curr_line > r.def_heading)
        {   // Skip to end of page
            while (r.curr_line <= r.def_lines)
            {
                RwWriteRec(r, opt);
                r.curr_line++;
            }
            if (r.curr_line > r.def_lines) r.curr_line = 1;     // Reset line to 1
            RwSaveLineCounter(r);
        }
        r.in_page_heading = true;
        if (!r.first_generate) r.curr_page++;
        r.first_detail = false;
        while (r.curr_line < r.def_heading)
        {   // Skip to Heading position on page
            RwWriteRec(r, opt);
            r.curr_line++;
            RwSaveLineCounter(r);
        }
        RwReportLineType(r, r.first_line, COB_REPORT_PAGE_HEADING);
        RwClearRecord(r);
        while (r.curr_line < r.def_first_detail)
        {
            RwWriteRec(r, opt);
            r.curr_line++;
            RwSaveLineCounter(r);
        }
        RwClearGroupIndicate(r.first_line);
        r.in_page_heading = false;
        RwLineControlChg(r, r.first_line, null);
    }

    // cob_field_to_string: the field's data without trailing spaces / NULs;
    // returns its length, or 0 / negative (nothing to print)
    static int RwFieldToString(CobField f, byte[] wrk, int maxsize)
    {
        if (f == null) return -1;
        if (f.size == 0) return -2;
        if (f.data.IsNull) return -3;
        var d = f.Span;
        int end = d.Length - 1;
        while (end > 0 && (d[end] == ' ' || d[end] == 0)) end--;
        if (d[end] == ' ' || d[end] == 0) return 0;
        if (end > maxsize) return -4;       // note: the max does not contain the low-value
        d.Slice(0, end + 1).CopyTo(wrk);
        return end + 1;
    }

    // Format one field into the print line
    static void RwPrintField(CobReportField rf, CobPtr rec)
    {
        var wrk = new byte[COB_SMALL_MAX + 2];
        int ln = 0;
        long destPos = rf.column - 1;
        int ret = RwFieldToString(rf.f, wrk, COB_SMALL_MAX);
        if (ret > 0) ln = ret;
        long size = rf.f.size;
        if (!rwColJustLrc)
        {
            // Data justify is turned off, no adjustment
        }
        else if ((rf.flags & COB_REPORT_COLUMN_RIGHT) != 0 && ln < size)
        {
            destPos += size - ln;
        }
        else if ((rf.flags & COB_REPORT_COLUMN_CENTER) != 0)
        {
            int start = 0;
            for (long k = 0; k < size && wrk[start] == ' ' && ln > 0; k++) { start++; ln--; }   // remove leading spaces
            Array.Copy(wrk, start, wrk, 0, ln);
            int i = 1 - (ln & 1);
            if (ln < size) destPos += (size - ln - i) / 2;
        }
        else if ((rf.flags & COB_REPORT_COLUMN_LEFT) != 0)
        {
            int start = 0;
            for (long k = 0; k < size && wrk[start] == ' ' && ln > 0; k++) { start++; ln--; }
            Array.Copy(wrk, start, wrk, 0, ln);
        }
        RwPut(rec, destPos, wrk, ln);
    }

    // memcpy into the print line; libcob does not check the bounds (a column past the
    // record overwrites whatever follows it); here anything past the storage is dropped
    static void RwPut(CobPtr rec, long pos, byte[] src, int len)
    {
        long room = rec.Arr.Length - rec.Off - pos;
        if (pos < 0 || room <= 0) return;
        Array.Copy(src, 0, rec.Arr, rec.Off + pos, Math.Min(len, room));
    }

    // GENERATE one report line
    static void RwReportLine(CobReport r, CobReportLine l)
    {
        var f = r.report_file;
        bool bChkLinePlus = false;
        int opt = COB_WRITE_BEFORE | COB_WRITE_LINES | 1;
        var rec = f.record.data;

        RwClearRecord(r);
        if (r.curr_line > r.def_last_detail && !r.in_report_footing && !r.in_page_footing)
        {   // Page overflow
            RwDoPageFooting(r);
            RwDoPageHeading(r);
        }
        if (!r.next_just_set && r.next_line_plus)
        {
            opt = COB_WRITE_BEFORE | COB_WRITE_LINES | r.next_value;
            RwWriteRec(r, opt);
            r.curr_line += r.next_value;
            r.next_line_plus = false;
            bChkLinePlus = true;
        }
        else if (!r.next_just_set && r.next_line)
        {
            r.next_line = false;
            if (r.curr_line > r.next_value)
            {
                RwDoPageFooting(r);
                RwDoPageHeading(r);
            }
            while (r.curr_line < r.next_value)
            {
                RwWriteRec(r, opt);
                r.curr_line++;
            }
            bChkLinePlus = true;
        }
        else if (!r.next_just_set && r.next_page)
        {
            r.next_page = false;
            RwDoPageFooting(r);
            RwDoPageHeading(r);
        }
        else if ((l.flags & COB_REPORT_LINE_PLUS) == 0 && (l.flags & COB_REPORT_LINE) != 0)
        {
            if (r.curr_line > l.line)
            {   // Eject page
                RwDoPageFooting(r);
                if (r.in_report_footing)
                {
                    r.curr_page++;      // Now on next page
                    r.curr_line = 1;
                }
                else RwDoPageHeading(r);
                r.first_detail = false;
            }
            while (r.curr_line < l.line)
            {
                RwWriteRec(r, opt);
                r.curr_line++;
            }
        }
        else bChkLinePlus = true;

        if (bChkLinePlus && (l.flags & COB_REPORT_LINE_PLUS) != 0 && l.line > 1)
        {
            if (r.curr_line != r.def_first_detail || r.def_first_detail == 0)
            {
                opt = COB_WRITE_BEFORE | COB_WRITE_LINES | (l.line - 1);
                RwWriteRec(r, opt);
                r.curr_line += l.line - 1;
            }
        }
        if (r.curr_line > r.def_last_detail && !r.in_report_footing && !r.in_page_heading && !r.in_page_footing)
        {   // Page overflow
            RwDoPageFooting(r);
            RwDoPageHeading(r);
        }
        RwSaveLineCounter(r);
        if (l.fields == null)
        {
            RwSetNextInfo(r, l);
            return;
        }
        if (l.suppress != 0)
        {
            RwSetNextInfo(r, l);
            return;
        }

        // Copy fields to print line area
        for (var rf = l.fields; rf != null; rf = rf.next)
        {
            if ((rf.flags & COB_REPORT_GROUP_ITEM) != 0)
            {
                if (rf.suppress != 0)
                {
                    // group item SUPPRESSed printing, so skip its members
                    rf.suppress = 0;
                    var prf = rf;
                    for (var nrf = rf.next; nrf != null && nrf.level > rf.level; nrf = nrf.next) prf = nrf;
                    rf = prf;           // Continue from here (prf is never null in libcob either)
                    continue;
                }
                continue;               // Group items are not printed
            }
            if ((rf.flags & COB_REPORT_PRESENT) != 0 && rf.present_now == 0) continue;
            if (rf.suppress != 0 || rf.group_indicate != 0) continue;
            if (rf.source != null)
            {   // Copy source field in
                cob_move(rf.source, rf.f);
                RwPrintField(rf, rec);
            }
            else if (rf.litval != null)
            {   // Refresh literal value
                var lit = Latin1Bytes(rf.litval);
                if (rf.f != null) RwStrMove(rf.f, lit, rf.litlen);
                RwPut(rec, rf.column - 1, lit, Math.Min(rf.litlen, lit.Length));
            }
            else if (rf.f != null)
            {
                RwPrintField(rf, rec);
            }
            if ((rf.flags & COB_REPORT_GROUP_INDICATE) != 0)
                rf.group_indicate = 1;  // Suppress subsequent printings
        }
        for (var rf = l.fields; rf != null; rf = rf.next)
            rf.present_now = (rf.flags & COB_REPORT_NEGATE) != 0 ? 1 : 0;
        opt = COB_WRITE_BEFORE | COB_WRITE_LINES | 1;
        RwWriteRec(r, opt);
        r.curr_line++;
        RwSaveLineCounter(r);

        RwSetNextInfo(r, l);
    }

    static void RwReportLineAnd(CobReport r, CobReportLine l, int type)
    {
        if (l == null) return;
        if (l.fields == null && l.child != null)
        {
            if ((l.flags & type) != 0)
            {
                RwReportLine(r, l);
                if (l.child != null) RwReportLineType(r, l.child, COB_REPORT_LINE);
                return;
            }
            l = l.child;
        }
        RwReportLineType(r, l, type);
    }

    // Find report line of the given type
    static CobReportLine RwGetLineType(CobReport r, CobReportLine l, int type)
    {
        if (l == null) return null;
        if ((l.flags & type) != 0) return l;
        if (l.child != null)
        {
            var t = RwGetLineType(r, l.child, type);
            if (t != null) return t;
        }
        if (l.sister != null) return RwGetLineType(r, l.sister, type);
        return null;
    }

    // GENERATE report line(s) of the given type
    static bool RwReportLineType(CobReport r, CobReportLine l, int type)
    {
        if (l == null) return false;
        if ((l.flags & type) != 0)
        {
            RwReportLine(r, l);
            if (l.child != null) RwReportLineType(r, l.child, COB_REPORT_LINE);
            if (l.sister != null)
            {
                if (type == COB_REPORT_CONTROL_FOOTING && (l.sister.flags & COB_REPORT_CONTROL_FOOTING) != 0)
                {
                    int curseq = RwGetControlSequence(r, l);
                    int sisseq = RwGetControlSequence(r, l.sister);
                    if (curseq > 0 && sisseq > 0 && sisseq > curseq) return true;
                }
                RwReportLineType(r, l.sister, type);
            }
            return true;
        }
        if (l.child != null && RwReportLineType(r, l.child, type)) return true;
        if (l.sister != null) return RwReportLineType(r, l.sister, type);
        return false;
    }

    // SUM all DETAIL counters
    static void RwSumAllDetail(CobReport r)
    {
        for (var sc = r.sum_counters; sc != null; sc = sc.next)
            for (var rs = sc.sum; rs != null && !sc.subtotal; rs = rs.next)
                RwAddFields(sc.counter, rs.f, sc.counter);
    }

    // If the counter is part of another SUM then it is 'rolling forward'
    static void RwSumThisCounter(CobReport r, CobField counter)
    {
        for (var sc = r.sum_counters; sc != null; sc = sc.next)
        {
            for (var rs = sc.sum; rs != null; rs = rs.next)
            {
                if (rs.f == counter)
                {
                    for (rs = sc.sum; rs != null; rs = rs.next)
                        RwAddFields(sc.counter, rs.f, sc.counter);
                    break;
                }
            }
        }
    }

    // ZERO counters for a given control level
    static void RwZeroAllCounters(CobReport r, int flag, CobReportLine l)
    {
        l = RwGetPrintLine(l);
        for (var sc = r.sum_counters; sc != null; sc = sc.next)
        {
            for (var rs = sc.sum; rs != null; rs = rs.next)
            {
                if ((flag & COB_REPORT_CONTROL_FOOTING_FINAL) != 0)
                {
                    if (sc.control_final) RwFieldInit(sc.counter);
                }
                else if (sc.control != null)
                {
                    var rc = sc.control;
                    for (var rr = rc.control_ref; rr != null; rr = rr.next)
                    {
                        if (rr.ref_line != null && (rr.ref_line.flags & COB_REPORT_CONTROL_HEADING) != 0) continue;
                        if (rr.ref_line != null && (rr.ref_line.flags & COB_REPORT_CONTROL_HEADING_FINAL) != 0) continue;
                        if (l != null && l != RwGetPrintLine(rr.ref_line)) continue;
                        if (rr.ref_line != null && (rr.ref_line.flags & flag) != 0)
                        {
                            RwSumThisCounter(r, sc.counter);
                            RwFieldInit(sc.counter);
                        }
                    }
                }
            }
        }
    }

    /// <summary>INITIATE report</summary>
    public static void cob_report_initiate(CobReport r)
    {
        if (r.initiate_done)
        {
            RwRuntimeError($"INITIATE {r.report_name} was already done");
            SetException(EC_REPORT_ACTIVE);
            return;
        }
        if (r.def_lines > REPORT_MAX_LINES) r.def_lines = REPORT_MAX_LINES;
        if (r.def_cols > REPORT_MAX_COLS || r.def_cols < 1) r.def_cols = REPORT_MAX_COLS;
        if ((r.def_first_detail > 0 && !(r.def_first_detail >= r.def_heading))
         || (r.def_last_detail > 0 && !(r.def_last_detail >= r.def_first_detail))
         || (r.def_footing > 0 && !(r.def_footing >= r.def_heading))
         || (r.def_footing > 0 && !(r.def_footing >= r.def_last_detail))
         || (r.def_lines > 0 && !(r.def_lines >= r.def_heading))
         || (r.def_lines > 0 && !(r.def_lines >= r.def_footing)))
        {
            RwRuntimeError($"INITIATE {r.report_name} PAGE LIMIT problem");
            SetException(EC_REPORT_PAGE_LIMIT);
            return;
        }
        r.curr_page = 1;
        r.curr_line = 0;
        r.incr_line = true;
        RwSaveLineCounter(r);
        r.initiate_done = true;
        RwLimitCheckLine(r, r.first_line);
        if (!r.initiate_done) return;   // Problem during LIMIT check
        r.first_detail = true;
        r.first_generate = true;
        r.next_value = 0;
        r.next_line = false;
        r.next_line_plus = false;
        r.next_page = false;
        // Allocate temp area for each control field
        for (var rc = r.controls; rc != null; rc = rc.next)
        {
            rc.val = RwFieldDup(rc.f, 0);
            rc.sf = RwFieldDup(rc.f, 0);
            for (int k = 0; k < MAX_ACTIVE_REPORTS; k++)
            {
                if (rwActive[k] == r) break;
                if (rwActive[k] == null) { rwActive[k] = r; break; }
            }
            rc.has_heading = false;
            rc.has_footing = false;
            for (var rr = rc.control_ref; rr != null; rr = rr.next)
            {
                if ((rr.ref_line.flags & (COB_REPORT_CONTROL_HEADING | COB_REPORT_CONTROL_HEADING_FINAL)) != 0)
                    rc.has_heading = true;
                if ((rr.ref_line.flags & (COB_REPORT_CONTROL_FOOTING | COB_REPORT_CONTROL_FOOTING_FINAL)) != 0)
                    rc.has_footing = true;
            }
        }
        for (var sc = r.sum_counters; sc != null; sc = sc.next)
            RwFieldInit(sc.counter);
    }

    static void RwHardFailure()
    {
        cob_stop_run(1);
    }

    // ------------------------------------------------------------ USE BEFORE REPORTING
    //
    // libcob returns a USE BEFORE REPORTING section's number to the generated code, which
    // PERFORMs that section and calls again with the number (ctl). libcob then jumps with a
    // goto to the label right after the "return for declaratives" point it left from, often
    // inside nested loops (PrintFooting, PrintHeading, do_detail, ...). C# can't jump into a
    // loop, so here the loops skip ahead to the remembered control / line instead and run the
    // labelled code from there; everything before the label is skipped, as with the goto.

    enum RwResume { None, Footing, Heading, FirstHeading, Detail, DetailCheck, FootingFinal, ReportFooting }

    static int rwInDetailDecl;              // libcob inDetailDecl
    // Fix over stock 3.2: libcob has no resume point for the two "before this detail line"
    // returns below, so it starts GENERATE again from the top and adds the detail into the SUM
    // counters twice (and re-runs control-heading declaratives). We resume right after them.
    static int rwDetailPoint;               // 1: group-line check, 2: detail-line check
    static CobReportLine rwDetailLine;      // the line being generated at that point
    static CobReportLine rwXpl;             // libcob xpl: detail line to resume at

    /// <summary>A control reference whose line (or print line) is declarative ctl.</summary>
    static bool RwRefIs(CobReportControlRef rr, int ctl, out CobReportLine pl)
    {
        pl = RwGetPrintLine(rr.ref_line);
        return rr.ref_line.use_decl == ctl || (pl != rr.ref_line && pl.use_decl == ctl);
    }

    /// <summary>TERMINATE report; returns a USE BEFORE REPORTING section's number, or 0 when done</summary>
    public static int cob_report_terminate(CobReport r, int ctl)
    {
        if (!r.initiate_done)
        {
            RwRuntimeError($"TERMINATE {r.report_name} but no INITIATE was done");
            SetException(EC_REPORT_INACTIVE);
            RwHardFailure();
        }
        if (r.first_generate) return 0;     // No GENERATE was ever done

        var resume = RwResume.None;
        CobReportControl resRc = null;
        CobReportControlRef resRr = null;
        CobReportLine pl;
        bool runFootings = true;
        if (ctl > 0)
        {   // Continue processing footings from the point we left
            runFootings = false;
            for (var rc = r.controls; rc != null && resume == RwResume.None; rc = rc.next)
            {
                for (var rr = rc.control_ref; rr != null; rr = rr.next)
                {
                    int f = rr.ref_line.flags;
                    if ((f & COB_REPORT_CONTROL_FOOTING) != 0 && RwRefIs(rr, ctl, out _))
                    { resume = RwResume.Footing; resRc = rc; resRr = rr; break; }
                    if ((f & COB_REPORT_CONTROL_FOOTING_FINAL) != 0 && RwRefIs(rr, ctl, out _))
                    { resume = RwResume.FootingFinal; break; }
                    if ((f & COB_REPORT_FOOTING) != 0 && RwRefIs(rr, ctl, out _))
                    { resume = RwResume.ReportFooting; break; }
                }
            }
            if (resume == RwResume.None)
            {
                pl = RwGetLineType(r, r.first_line, COB_REPORT_CONTROL_FOOTING_FINAL);
                if (pl != null && pl.use_decl == ctl) resume = RwResume.FootingFinal;
                else
                {
                    pl = RwGetLineType(r, r.first_line, COB_REPORT_FOOTING);
                    if (pl != null && pl.use_decl == ctl) resume = RwResume.ReportFooting;
                }
            }
            if (resume == RwResume.Footing) runFootings = true;
        }

        // Do CONTROL FOOTING breaks
        if (runFootings)
        {
            bool skipping = resume == RwResume.Footing;
            for (var rc = r.controls; rc != null; rc = rc.next)
            {
                if (skipping && rc != resRc) continue;
                for (var rr = rc.control_ref; rr != null; rr = rr.next)
                {
                    if ((rr.ref_line.flags & COB_REPORT_CONTROL_FOOTING) == 0) continue;
                    if (skipping)
                    {
                        if (rr != resRr) continue;
                        skipping = false;
                        pl = RwGetPrintLine(rr.ref_line);
                    }
                    else
                    {
                        if (rr.ref_line.use_decl != 0) return rr.ref_line.use_decl;
                        pl = RwGetPrintLine(rr.ref_line);
                        if (pl != rr.ref_line && pl.use_decl != 0) return pl.use_decl;
                    }
                    if (!rc.suppress) RwReportLineAnd(r, rr.ref_line, COB_REPORT_CONTROL_FOOTING);
                    rc.suppress = false;
                    RwZeroAllCounters(r, COB_REPORT_CONTROL_FOOTING, pl);
                }
            }
        }

        if (resume != RwResume.ReportFooting)
        {
            // Do CONTROL FOOTING FINAL
            var fl = RwGetLineType(r, r.first_line, COB_REPORT_CONTROL_FOOTING_FINAL);
            if (fl != null)
            {
                if (resume != RwResume.FootingFinal && fl.use_decl != 0) return fl.use_decl;
                RwReportLineType(r, r.first_line, COB_REPORT_CONTROL_FOOTING_FINAL);
            }
            RwZeroAllCounters(r, COB_REPORT_CONTROL_FOOTING_FINAL, null);

            RwDoPageFooting(r);
        }

        var rf = RwGetLineType(r, r.first_line, COB_REPORT_FOOTING);
        if (rf != null)
        {
            if (resume != RwResume.ReportFooting && rf.use_decl != 0) return rf.use_decl;
            r.in_report_footing = true;
            RwReportLineType(r, r.first_line, COB_REPORT_FOOTING);
            r.in_report_footing = false;
        }

        RwFreeControlFields(r);
        r.initiate_done = false;
        return 0;
    }

    /// <summary>
    /// GENERATE detail line l, or GENERATE report-name when l is null (summary reporting);
    /// returns a USE BEFORE REPORTING section's number, or 0 when done
    /// </summary>
    public static int cob_report_generate(CobReport r, CobReportLine l, int ctl)
    {
        if (!r.initiate_done)
        {
            RwRuntimeError($"GENERATE {r.report_name} but no INITIATE was done");
            SetException(EC_REPORT_INACTIVE);
            RwHardFailure();
        }

        r.foot_next_page = false;
        var resume = RwResume.None;
        CobReportControl resRc = null;
        CobReportControlRef resRr = null;
        CobReportLine pl;
        if (ctl > 0)
        {   // Continue processing from the point we left for the declarative
            if (ctl == rwInDetailDecl) resume = RwResume.Detail;
            else
            {
                for (var rc = r.controls; rc != null && resume == RwResume.None; rc = rc.next)
                {
                    for (var rr = rc.control_ref; rr != null; rr = rr.next)
                    {
                        if ((rr.ref_line.flags & COB_REPORT_CONTROL_FOOTING) != 0 && RwRefIs(rr, ctl, out _))
                        { resume = RwResume.Footing; resRc = rc; resRr = rr; break; }
                        if ((rr.ref_line.flags & COB_REPORT_CONTROL_HEADING) != 0 && RwRefIs(rr, ctl, out _))
                        {
                            resume = r.first_generate ? RwResume.FirstHeading : RwResume.Heading;
                            resRc = rc; resRr = rr; break;
                        }
                    }
                }
            }
            if (resume == RwResume.None && rwDetailPoint != 0 && ctl == rwLastUse) resume = RwResume.DetailCheck;
            // otherwise not found: carry on from the start, keeping last_use (libcob)
        }
        else { rwLastUse = 0; rwDetailPoint = 0; }

        if (resume != RwResume.Detail && resume != RwResume.DetailCheck)
        {
            if (resume == RwResume.None && r.incr_line)
            {
                r.incr_line = false;
                r.curr_line++;
                RwSaveLineCounter(r);
            }
            if (r.first_generate && resume is RwResume.None or RwResume.FirstHeading)
            {
                // First GENERATE of the report
                bool skipping = resume == RwResume.FirstHeading;
                if (!skipping)
                {
                    if (r.first_line.use_decl != 0 && r.first_line.use_decl != rwLastUse)
                    {
                        rwLastUse = r.first_line.use_decl;
                        return r.first_line.use_decl;
                    }
                    RwReportLineType(r, r.first_line, COB_REPORT_HEADING);
                    RwDoPageHeading(r);
                }
                // do CONTROL Headings
                for (var rc = r.controls; rc != null; rc = rc.next)
                {
                    if (skipping && rc != resRc) continue;
                    for (var rr = rc.control_ref; rr != null; rr = rr.next)
                    {
                        if ((rr.ref_line.flags & COB_REPORT_CONTROL_HEADING) == 0) continue;
                        if (skipping)
                        {
                            if (rr != resRr) continue;
                            skipping = false;
                        }
                        else
                        {
                            if (rr.ref_line.use_decl != 0) return rr.ref_line.use_decl;
                            pl = RwGetPrintLine(rr.ref_line);
                            if (pl != rr.ref_line && pl.use_decl != 0) return pl.use_decl;
                        }
                        RwReportLineAnd(r, rr.ref_line, COB_REPORT_CONTROL_HEADING);
                    }
                    cob_move(rc.f, rc.val);         // Save current field data
                    rc.data_change = false;
                }
            }
            else if (!r.first_generate || resume != RwResume.None)
            {
                if (resume == RwResume.None)
                {
                    if (r.curr_line > r.def_last_detail)
                    {   // Page overflow
                        RwDoPageFooting(r);
                        r.curr_line = 1;
                        RwDoPageHeading(r);
                        r.first_detail = false;
                    }
                    else if (r.curr_line <= 1 || r.first_detail)
                    {
                        if (r.first_detail) r.curr_line = 1;
                        RwDoPageHeading(r);
                        r.first_detail = false;
                    }

                    // Check for FOOTINGs on other GENERATEs
                    rwMaxctl = 0;
                    for (var rc = r.controls; rc != null; rc = rc.next)
                    {
                        rc.data_change = cob_cmp(rc.f, rc.val) != 0;
                        if (rc.data_change)
                        {   // Data change, implies control break at lower levels
                            cob_move(rc.f, rc.sf);      // Save new CONTROL value
                            cob_move(rc.val, rc.f);     // Prev value for FOOTING
                            if (rc.sequence > rwMaxctl) rwMaxctl = rc.sequence;
                        }
                    }
                    if (rwMaxctl > 0)
                    {
                        for (var rp = r.controls; rp != null; rp = rp.next)
                        {
                            if (rp.sequence < rwMaxctl && !rp.data_change)
                            {
                                rp.data_change = true;
                                cob_move(rp.f, rp.sf);  // Save CONTROL value
                                cob_move(rp.val, rp.f); // Prev value for FOOTING
                            }
                        }
                    }

                    for (var rc = r.controls; rc != null; rc = rc.next)
                        if (rc.data_change)             // Check for PRESENT WHEN control-id
                            RwLineControlChg(r, r.first_line, rc.f);
                }

                if (resume is RwResume.None or RwResume.Footing)
                {
                    bool skipping = resume == RwResume.Footing;
                    for (var rc = r.controls; rc != null; rc = rc.next)
                    {
                        if (skipping && rc != resRc) continue;
                        if (!rc.data_change) continue;
                        for (var rr = rc.control_ref; rr != null; rr = rr.next)
                        {
                            if ((rr.ref_line.flags & COB_REPORT_CONTROL_FOOTING) == 0) continue;
                            if (skipping)
                            {
                                if (rr != resRr) continue;
                                skipping = false;
                                pl = RwGetPrintLine(rr.ref_line);
                            }
                            else
                            {
                                if (rr.ref_line.use_decl != 0) return rr.ref_line.use_decl;
                                pl = RwGetPrintLine(rr.ref_line);
                                if (pl != rr.ref_line && pl.use_decl != 0) return pl.use_decl;
                            }
                            if (!rc.suppress && rr.ref_line.suppress == 0)
                                RwReportLineAnd(r, rr.ref_line, COB_REPORT_CONTROL_FOOTING);
                            rc.suppress = false;
                            rr.ref_line.suppress = 0;
                            RwZeroAllCounters(r, COB_REPORT_CONTROL_FOOTING, pl);
                            RwClearGroupIndicate(r.first_line);
                            r.next_just_set = false;
                            if (r.next_page)
                            {
                                r.foot_next_page = true;
                                r.next_page = false;
                            }
                        }
                        cob_move(rc.sf, rc.f);          // Put new CONTROL value back
                    }
                    if (r.foot_next_page)
                    {
                        r.next_page = false;
                        r.foot_next_page = false;
                        RwDoPageFooting(r);
                        RwDoPageHeading(r);
                    }
                }
                // Check for Control Headings
                {
                    bool skipping = resume == RwResume.Heading;
                    for (var rc = r.controls; rc != null; rc = rc.next)
                    {
                        if (skipping && rc != resRc) continue;
                        if (rc.data_change)
                        {
                            for (var rr = rc.control_ref; rr != null; rr = rr.next)
                            {
                                if ((rr.ref_line.flags & COB_REPORT_CONTROL_HEADING) == 0) continue;
                                if (skipping)
                                {
                                    if (rr != resRr) continue;
                                    skipping = false;
                                }
                                else
                                {
                                    if (rr.ref_line.use_decl != 0) return rr.ref_line.use_decl;
                                    pl = RwGetPrintLine(rr.ref_line);
                                    if (pl != rr.ref_line && pl.use_decl != 0) return pl.use_decl;
                                }
                                if (rr.ref_line.suppress == 0)
                                    RwReportLineAnd(r, rr.ref_line, COB_REPORT_CONTROL_HEADING);
                                rr.ref_line.suppress = 0;
                            }
                            cob_move(rc.f, rc.val);     // Save current field data
                        }
                        rc.data_change = false;
                    }
                }
            }

            RwSumAllDetail(r);                      // SUM detail counters
        }

        if (resume == RwResume.Detail)
        {
            // libcob do_detail: print the line the declarative was for (l is still the
            // GENERATE's line, as in C), then carry on with the following detail lines
            rwInDetailDecl = 0;
            pl = rwXpl;
            rwXpl = null;
            RwReportLine(r, l);
            l.suppress = 0;
            if (rwGengrp != 0)
            {
                for (pl = pl?.sister; pl != null; pl = pl.sister)
                {
                    if (RwNotDetail(pl.flags)) break;
                    l = RwGetPrintLine(pl);
                    if (l.suppress == 0)
                    {
                        r.next_just_set = false;
                        if (l.use_decl != 0 && l.use_decl != rwLastUse)
                        {
                            rwXpl = pl;
                            rwInDetailDecl = l.use_decl;
                            return l.use_decl;
                        }
                        RwReportLine(r, l);
                    }
                    l.suppress = 0;
                }
            }
        }
        else if (l == null)
        {
            // GENERATE <report-name>: summary reporting, no detail line
        }
        else if (l.suppress != 0 && resume != RwResume.DetailCheck)
        {
            l.suppress = 0;
        }
        else
        {
            // entry 0: from the start; 1: after the group-line check; 2: after the detail check
            int entry = 0;
            if (resume == RwResume.DetailCheck)
            {
                entry = rwDetailPoint;
                l = rwDetailLine;
                rwDetailPoint = 0;
                rwDetailLine = null;
            }
            if (entry == 0)
            {
                if (l.fields == null && l.child != null && l.child.sister != null)
                {
                    if (l.use_decl != 0 && l.use_decl != rwLastUse)
                    {
                        rwLastUse = l.use_decl;
                        rwDetailPoint = 1; rwDetailLine = l;
                        return l.use_decl;
                    }
                    l = l.child;                // Multiple Detail Lines in group
                    rwGengrp = 1;
                }
                else rwGengrp = 0;
            }
            else if (entry == 1)
            {
                l = l.child;                    // Multiple Detail Lines in group
                rwGengrp = 1;
            }

            if (entry != 2)
            {
                rwNum = rwLn = 0;
                for (pl = l; pl != null; pl = pl.sister)
                {
                    if (RwNotDetail(pl.flags)) break;
                    if ((pl.flags & COB_REPORT_LINE_PLUS) != 0 && pl.line > 1) rwLn += pl.line;
                    rwNum++;
                    if (rwGengrp == 0) break;
                }
                if (rwNum > 1 && (r.curr_line + rwLn) > r.def_last_detail)
                {   // Page overflow
                    RwDoPageFooting(r);
                    r.curr_line = 1;
                    RwDoPageHeading(r);
                    r.first_detail = false;
                    RwSaveLineCounter(r);
                }

                if (l.use_decl != 0 && l.use_decl != rwLastUse)
                {
                    rwLastUse = l.use_decl;
                    rwDetailPoint = 2; rwDetailLine = l;
                    return l.use_decl;
                }
            }
            for (pl = l; pl != null; pl = pl.sister)
            {
                if (RwNotDetail(pl.flags)) break;
                l = RwGetPrintLine(pl);         // Find line with data fields
                if (l.suppress == 0)
                {
                    r.next_just_set = false;
                    if (l.use_decl != 0 && l.use_decl != rwLastUse)
                    {   // libcob keeps its place (xpl) and resumes at do_detail
                        rwXpl = pl;
                        rwInDetailDecl = l.use_decl;
                        return l.use_decl;
                    }
                    RwReportLine(r, l);         // Generate this DETAIL line
                }
                l.suppress = 0;
                if (rwGengrp == 0) break;
            }
        }

        // Zero out SUM counters
        RwZeroAllCounters(r, COB_REPORT_DETAIL, null);
        RwClearSuppress(r.first_line);
        r.first_generate = false;
        r.next_just_set = false;
        r.curr_line--;
        r.incr_line = true;
        RwSaveLineCounter(r);
        return 0;
    }

    /// <summary>SUPPRESS PRINTING (in a USE BEFORE REPORTING declarative)</summary>
    public static void cob_report_suppress(CobReport r, CobReportLine l)
    {
        for (var rc = r.controls; rc != null; rc = rc.next)
        {
            for (var rr = rc.control_ref; rr != null; rr = rr.next)
            {
                if (rr.ref_line == l) { rc.suppress = true; return; }
                if (RwGetPrintLine(rr.ref_line) == l) { rc.suppress = true; return; }
            }
        }
        RwRuntimeError($"could not find line to SUPPRESS in report {r.report_name}");
    }
}
