// COBOL names in the Visual Studio debugger.
//
// For each program the compiler emits one property per data item, named after the COBOL
// name with '-' as '_' (WS-PAY -> WS_PAY), plus COBOL, which lists every 01/77 item. Each
// returns a CobVar, which the debugger shows as:
//   Name   WS_PAY            (the property; children use the COBOL name, WS-PAY)
//   Value  0712.50           (as DISPLAY would print it; text in quotes)
//   Type   PIC 9(4)V99       (PICTURE and USAGE, or "group", plus OCCURS n)
// Groups expand to their subordinate items, tables to (1), (2), ... and then the items of
// each occurrence. Type WS_PAY in the Watch window, or COBOL to see everything.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using static CobolDotNet.Runtime.CobConst;

namespace CobolDotNet.Runtime;

[DebuggerDisplay("{Value,nq}", Name = "{Name,nq}", Type = "{Pic,nq}")]
[DebuggerTypeProxy(typeof(CobVarView))]
public sealed class CobVar
{
    public string Name { get; }
    public string Pic { get; }
    internal readonly CobField fld;
    internal readonly int occurs;        // > 1: a table, shown as occurrences
    internal readonly long stride;       // bytes from one occurrence to the next
    internal readonly CobVar[] children;

    public CobVar(string name, string pic, CobField field, int occurs, long stride, params CobVar[] children)
    {
        Name = name; Pic = pic; this.fld = field; this.occurs = occurs; this.stride = stride;
        this.children = children ?? Array.Empty<CobVar>();
    }

    /// <summary>The same item moved by delta bytes (an occurrence of a table).</summary>
    CobVar Shifted(long delta, string name, int occ, string pic = null)
    {
        var f = fld == null ? null : new CobField(fld.size, fld.data + delta, fld.attr);
        var kids = new CobVar[children.Length];
        for (int i = 0; i < kids.Length; i++) kids[i] = children[i].Shifted(delta, children[i].Name, children[i].occurs);
        return new CobVar(name, pic ?? Pic, f, occ, stride, kids);
    }

    /// <summary>A subordinate item by COBOL name (depth first; in a table, its first occurrence).</summary>
    public CobVar Get(string name)
    {
        foreach (var c in children)
        {
            if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c;
            var hit = c.Get(name);
            if (hit != null) return hit;
        }
        return null;
    }

    /// <summary>Occurrence n (1-based) of a table item.</summary>
    public CobVar this[int n] => Shifted((n - 1) * stride, $"{Name}({n})", 0, OccurrencePic);

    string OccurrencePic
    {
        get { int i = Pic.IndexOf(" OCCURS ", StringComparison.Ordinal); return i < 0 ? Pic : Pic.Substring(0, i); }
    }

    /// <summary>The value as DISPLAY would print it.</summary>
    public string Value
    {
        get
        {
            try
            {
                if (fld == null || fld.data.IsNull) return "(no data: LINKAGE item before CALL)";
                if (occurs > 1) return $"table of {occurs}";
                if (IsNumeric(fld.Type)) return Num.Pretty(fld);
                if (IsNational(fld.Type)) return "\"" + Cob.NationalText(fld) + "\"";
                return "\"" + Latin1(fld.Span) + "\"";
            }
            catch (Exception e) { return "(" + e.Message + ")"; }
        }
    }

    static string Latin1(ReadOnlySpan<byte> b)
    {
        var c = new char[b.Length];
        for (int i = 0; i < b.Length; i++) c[i] = b[i] < 0x20 ? '.' : (char)b[i];
        return new string(c);
    }

    internal CobVar[] Items
    {
        get
        {
            if (occurs > 1)
            {
                var occ = new CobVar[occurs];
                for (int i = 0; i < occurs; i++) occ[i] = this[i + 1];
                return occ;
            }
            return children;
        }
    }

    public override string ToString() => Value;
}

/// <summary>What the debugger shows when a CobVar is expanded: its items, by COBOL name.</summary>
internal sealed class CobVarView
{
    readonly CobVar v;
    public CobVarView(CobVar v) => this.v = v;

    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    public CobVar[] Items => v.Items;
}

/// <summary>COBOL: every 01 / 77 item of the program, by COBOL name.</summary>
[DebuggerDisplay("{Count} data items", Name = "COBOL")]
[DebuggerTypeProxy(typeof(CobDataView))]
public sealed class CobData
{
    internal readonly CobVar[] items;
    public CobData(params CobVar[] items) => this.items = items;
    public int Count => items.Length;

    /// <summary>A data item by its COBOL name (WS-PAY), for the Watch window: COBOL["WS-PAY"].</summary>
    public CobVar this[string name]
    {
        get
        {
            foreach (var v in items)
            {
                var hit = Find(v, name);
                if (hit != null) return hit;
            }
            return null;
        }
    }

    static CobVar Find(CobVar v, string name)
    {
        if (string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)) return v;
        foreach (var c in v.children)
        {
            var hit = Find(c, name);
            if (hit != null) return hit;
        }
        return null;
    }
}

internal sealed class CobDataView
{
    readonly CobData d;
    public CobDataView(CobData d) => this.d = d;

    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    public CobVar[] Items => d.items;
}
