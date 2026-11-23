// Program-level runtime structures the generated code fills in:
// the module record, the global record, and the PERFORM frame stack.

using System;

namespace CobolDotNet.Runtime;

public sealed class CobCallUnion
{
    public Delegate funcptr;
}

/// <summary>Per-program runtime record (libcob's cob_module).</summary>
public sealed class CobModule
{
    public CobModule next;
    public string module_name;
    public string module_formatted_date;
    public string module_source;
    public string gc_version;
    public CobCallUnion module_entry = new();
    public CobCallUnion module_cancel = new();
    public object module_ref_count;
    public object module_path;
    public int module_active;
    public int module_date;
    public int module_time;
    public int module_type;
    public int module_param_cnt;
    public int ebcdic_sign;
    public char decimal_point = '.';
    public char currency_symbol = '$';
    public char numeric_separator = ',';
    public int flag_filename_mapping;
    public int flag_binary_truncate;
    public int flag_pretty_display;
    public int flag_host_sign;
    public int flag_no_phys_canc;
    public int flag_main;
    public int flag_fold_call;
    public int flag_exit_program;
    public int flag_debug_trace;
    public int flag_dump_ready;
    public int xml_mode;
    public int module_stmt;
    public object module_sources;
    public CobField[] cob_procedure_params;
    public object collating_sequence;
    public CobField crt_status;
    public CobField cursor_pos;
    public object xml_code, xml_event, xml_information;
    public object xml_namespace, xml_namespace_prefix;
    public object xml_nnamespace, xml_nnamespace_prefix;
    public object xml_ntext, xml_text;
    public object json_code, json_status;
    public string section_name;
    public string paragraph_name;
    public CobFramePtr frame_ptr;
}

/// <summary>Process-wide runtime state (libcob's cob_global).</summary>
public sealed class CobGlobal
{
    public int cob_exception_code;
    public string cob_main_argv0;
    public int cob_call_params;
    public CobModule cob_current_module;
}

public sealed class CobFrame
{
    public uint perform_through;
    public uint return_address_num;
    public object return_address_ptr;
    public int module_stmt;
    public string section_name;
    public string paragraph_name;
}

/// <summary>
/// The PERFORM return stack.  Generated code treats it like a C pointer
/// into an array (frame_ptr++, frame_ptr->perform_through), so this
/// struct is (array, index) with ++/-- and member properties.
/// </summary>
public struct CobFramePtr : IEquatable<CobFramePtr>
{
    readonly CobFrame[] arr;
    readonly int idx;

    public CobFramePtr(int size)
    {
        arr = new CobFrame[size];
        for (int i = 0; i < size; i++) arr[i] = new CobFrame();
        idx = 0;
    }

    CobFramePtr(CobFrame[] a, int i) { arr = a; idx = i; }

    CobFrame Cur => idx < arr.Length
        ? arr[idx]
        : throw new CobRuntimeException("PERFORM nesting is too deep (stack overflow). Check for a paragraph that PERFORMs itself.");

    public static CobFramePtr operator ++(CobFramePtr p) => new(p.arr, p.idx + 1);
    public static CobFramePtr operator --(CobFramePtr p) => new(p.arr, p.idx - 1);

    public uint perform_through { get => Cur.perform_through; set => Cur.perform_through = value; }
    public uint return_address_num { get => Cur.return_address_num; set => Cur.return_address_num = value; }
    public object return_address_ptr { get => Cur.return_address_ptr; set => Cur.return_address_ptr = value; }
    public int module_stmt { get => Cur.module_stmt; set => Cur.module_stmt = value; }
    public string section_name { get => Cur.section_name; set => Cur.section_name = value; }
    public string paragraph_name { get => Cur.paragraph_name; set => Cur.paragraph_name = value; }

    public bool Equals(CobFramePtr o) => ReferenceEquals(arr, o.arr) && idx == o.idx;
    public override bool Equals(object o) => o is CobFramePtr p && Equals(p);
    public override int GetHashCode() => HashCode.Combine(arr, idx);
    public static bool operator ==(CobFramePtr a, CobFramePtr b) => a.Equals(b);
    public static bool operator !=(CobFramePtr a, CobFramePtr b) => !a.Equals(b);
}

/// <summary>A runtime error with a plain-English message for the learner.</summary>
public sealed class CobRuntimeException : Exception
{
    public CobRuntimeException(string message) : base(message) { }
}

/// <summary>
/// COBOL exception conditions (subset).  Values match libcob's
/// exception.def because generated code tests them directly, e.g.
/// (cob_exception_code &amp; 0xff00) == 0x1000 for ON SIZE ERROR.
/// </summary>
public static class CobException
{
    public const int EC_OVERFLOW = 0x0A00;
    public const int EC_OVERFLOW_STRING = 0x0A02;
    public const int EC_SIZE = 0x1000;
    public const int EC_SIZE_OVERFLOW = 0x1004;
    public const int EC_SIZE_TRUNCATION = 0x1005;
    public const int EC_SIZE_ZERO_DIVIDE = 0x1007;
}
