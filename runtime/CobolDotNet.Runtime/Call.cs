// CALL and CANCEL.
//
// Every non-nested program's entry method carries [CobolProgram("NAME")]. CALL "NAME"
// finds it among the loaded assemblies (and NAME.dll next to the program or on
// COB_LIBRARY_PATH), as libcob's cob_resolve_cobol finds a module. Nested programs are
// found through the calling program's nest table. The generated code calls
// `call_NAME.Call(args)`; Call converts each argument to the parameter type of the
// callee's entry method (CobPtr for BY REFERENCE / BY CONTENT, a number for BY VALUE)
// and passes default values for arguments the caller left out, as C would pass NULL.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;
using static CobolDotNet.Runtime.CobConst;

namespace CobolDotNet.Runtime;

/// <summary>Marks the entry method of a COBOL program (or ENTRY point) for CALL by name.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class CobolProgramAttribute : Attribute
{
    public string Name { get; }
    public CobolProgramAttribute(string name) => Name = name;
}

/// <summary>A resolved program for CALL (libcob cob_call_union).</summary>
public sealed class CobCall
{
    Delegate fn;
    public Delegate funcvoid { get => fn; set => fn = value; }
    public Delegate funcint { get => fn; set => fn = value; }
    public Delegate funcptr { get => fn; set => fn = value; }
    public Delegate funcnull { get => fn; set => fn = value; }

    /// <summary>Run the program with these arguments; returns its RETURN-CODE.</summary>
    public int Call(params object[] args) => Cob.Invoke(fn, args);
}

/// <summary>One line of a program's nested-program table (libcob cob_call_struct).</summary>
public sealed class CobCallStruct
{
    public string cob_cstr_name;
    public Delegate cob_cstr_call;
    public Delegate cob_cstr_cancel;
    public CobCallStruct(string name, Delegate call, Delegate cancel)
    {
        cob_cstr_name = name; cob_cstr_call = call; cob_cstr_cancel = cancel;
    }
}

/// <summary>A private copy of a BY CONTENT / literal argument: bytes, plus number views.</summary>
public sealed class CobContent
{
    public readonly CobPtr data;
    public CobContent(long size) => data = new CobPtr(Math.Max(size, 8));
    public long datall { get => data.S64; set => data.S64 = value; }
    public ulong dataull { get => data.U64; set => data.U64 = value; }
    public int dataint { get => data.S32; set => data.S32 = value; }
}

public static partial class Cob
{
    public const int EC_PROGRAM = 0x0B00;
    public const int EC_PROGRAM_NOT_FOUND = 0x0B05;

    public static CobGlobal cob_get_global_ptr() => Global;
    public static bool cob_is_initialized() => true;

    // ------------------------------------------------------------ program registry

    static Dictionary<string, MethodInfo> programs;
    static readonly Dictionary<string, Delegate> resolved = new(StringComparer.Ordinal);
    static readonly HashSet<Assembly> scanned = new();

    static void Scan(Assembly a)
    {
        if (!scanned.Add(a)) return;
        Type[] types;
        try { types = a.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
        foreach (var t in types)
        {
            if (t.Namespace != "CobolPrograms") continue;
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                var attr = m.GetCustomAttribute<CobolProgramAttribute>();
                if (attr != null) programs.TryAdd(attr.Name, m);
            }
        }
    }

    static MethodInfo FindProgram(string name)
    {
        if (programs == null)
        {
            programs = new Dictionary<string, MethodInfo>(StringComparer.Ordinal);
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) Scan(a);
        }
        if (programs.TryGetValue(name, out var m)) return m;
        // a separately built program: NAME.dll beside us, or on COB_LIBRARY_PATH
        var dirs = new List<string> { AppContext.BaseDirectory };
        var lp = Environment.GetEnvironmentVariable("COB_LIBRARY_PATH");
        if (!string.IsNullOrEmpty(lp)) dirs.AddRange(lp.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        foreach (var d in dirs)
        {
            var dll = Path.Combine(d, name + ".dll");
            if (!File.Exists(dll)) continue;
            try { Scan(Assembly.LoadFrom(dll)); } catch (Exception) { continue; }
            if (programs.TryGetValue(name, out m)) return m;
        }
        // COBOL names are case-insensitive
        foreach (var kv in programs)
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }

    static Delegate ToDelegate(MethodInfo m)
    {
        var types = m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType).ToArray();
        return m.CreateDelegate(Expression.GetDelegateType(types));
    }

    static string resolveError;

    /// <summary>CALL "literal": the program, or null (EC-PROGRAM-NOT-FOUND) / a fatal error.</summary>
    public static Delegate cob_resolve_cobol(string name, int fold_case, int errind)
    {
        name = name.Trim();
        if (fold_case == 1) name = name.ToUpperInvariant();
        else if (fold_case == 2) name = name.ToLowerInvariant();
        if (!resolved.TryGetValue(name, out var d))
        {
            var m = FindProgram(name);
            if (m != null) resolved[name] = d = ToDelegate(m);
        }
        if (d == null)
        {
            resolveError = $"module '{name}' not found";
            if (errind != 0) CallError(name);
            SetException(EC_PROGRAM_NOT_FOUND);
        }
        return d;
    }

    /// <summary>CALL identifier: the name is the field's value; nested programs first.</summary>
    public static Delegate cob_call_field(CobField f, CobCallStruct[] nest, int errind, int fold_case)
    {
        string name = Latin1(f.Span).Trim();
        if (nest != null)
            foreach (var s in nest)
                if (s.cob_cstr_name != null && s.cob_cstr_name == name) return s.cob_cstr_call;
        return cob_resolve_cobol(name, fold_case, errind);
    }

    static void CallError(string name)
    {
        Out.Flush();
        Err.Write(Latin1Bytes($"libcob: error: {resolveError}\n"));
        if (Environment.GetEnvironmentVariable("COB_NO_HINTS") != "1")
            Err.Write(Latin1Bytes($"  hint: No program has PROGRAM-ID {name}. Check the spelling, and that {name}.cbl is part of this project. Use CALL ... ON EXCEPTION to handle a missing program yourself.\n"));
        CloseAllFiles();
        ScreenShutdown();
        Err.Flush();
        Environment.Exit(1);
    }

    /// <summary>Run a resolved program: arguments converted to its parameter types.</summary>
    internal static int Invoke(Delegate fn, object[] args)
    {
        if (fn == null) throw new CobRuntimeException("CALL of a program that was not found");
        var ps = fn.Method.GetParameters();
        var call = new object[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            var t = ps[i].ParameterType;
            object a = i < args.Length ? args[i] : null;
            call[i] = a == null ? (t.IsValueType ? Activator.CreateInstance(t) : null)
                    : t.IsInstanceOfType(a) ? a
                    : a is CobPtr ? Activator.CreateInstance(t)
                    : Convert.ChangeType(a is ulong u && (t == typeof(long) || t == typeof(int)) ? unchecked((long)u) : a, t);
        }
        try
        {
            var r = fn.DynamicInvoke(call);
            return r is int n ? n : 0;
        }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    /// <summary>A BY VALUE argument as the bytes of the LINKAGE item (native binary).</summary>
    public static CobPtr cob_value_ptr(long v, int size)
    {
        var p = new CobPtr(Math.Max(size, 8));
        p.S64 = v;
        return p;
    }
    public static CobPtr cob_value_ptr(ulong v, int size) => cob_value_ptr(unchecked((long)v), size);
    public static CobPtr cob_value_ptr(double v, int size)
    {
        var p = new CobPtr(Math.Max(size, 8));
        if (size == 4) p.F32 = (float)v; else p.F64 = v;
        return p;
    }
    public static CobPtr cob_value_ptr(CobPtr v, int size) => v;

    /// <summary>BY CONTENT: the argument copied into private storage (libcob cob_field_constant).</summary>
    public static void cob_field_constant(CobField src, CobField dst, CobFieldAttr attr, CobContent content)
    {
        src.Span.CopyTo(content.data.Span(src.size));
        dst.size = src.size;
        dst.data = content.data;
        var a = src.attr;
        dst.attr = new CobFieldAttr(a.type, a.digits, a.scale, a.flags | FLAG_CONSTANT, a.pic);
    }

    // ------------------------------------------------------------ CANCEL

    /// <summary>CANCEL "NAME": the program starts fresh (WORKING-STORAGE reset) next time.</summary>
    public static void cob_cancel(string name)
    {
        name = name.Trim();
        var m = FindProgram(name);
        if (m == null) return;
        CancelClassOf(m);
    }

    public static void cob_cancel_field(CobField f, CobCallStruct[] nest)
    {
        string name = Latin1(f.Span).Trim();
        if (nest != null)
            foreach (var s in nest)
                if (s.cob_cstr_name == name && s.cob_cstr_cancel != null) { s.cob_cstr_cancel.DynamicInvoke(-1); return; }
        cob_cancel(name);
    }

    static void CancelClassOf(MethodInfo entry)
    {
        // the program's internal function NAME_(int entry) handles entry -1 (P_cancel)
        var inner = entry.DeclaringType.GetMethod(entry.Name + "_", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(int) }, null);
        inner?.Invoke(null, new object[] { -1 });
    }
}
