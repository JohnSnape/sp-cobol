// XML GENERATE and JSON GENERATE: port of libcob mlio.c (GnuCOBOL 3.2).
//
// cobc builds a tree of cob_ml_tree / cob_ml_attr nodes per statement (names are
// constants, content is the data item); the runtime walks it. Stock GnuCOBOL hands
// the tree to libxml2's xmlTextWriter or to cJSON; we write the same text ourselves
// (no XML / JSON library needed): libxml2's escaping, self-closing empty elements,
// namespace declarations after the attributes; cJSON's unformatted print.
//
// XML PARSE is not here: GnuCOBOL 3.2 doesn't implement it either (see DECISIONS.md).

using System;
using System.Collections.Generic;
using System.Text;
using static CobolDotNet.Runtime.CobConst;

namespace CobolDotNet.Runtime;

/// <summary>One element (XML) / member (JSON) of a GENERATE tree (libcob cob_ml_tree).</summary>
public sealed class CobMlTree
{
    public CobField name;           // null: content only (TYPE OF ... IS CONTENT, NAME ... OMITTED)
    public CobMlAttr attrs;
    public CobField content;
    public bool is_suppressed;
    public CobMlTree children;
    public CobMlTree sibling;
}

/// <summary>An XML attribute of an element (libcob cob_ml_attr).</summary>
public sealed class CobMlAttr
{
    public CobField name;
    public CobField value;
    public bool is_suppressed;
    public CobMlAttr sibling;
}

public static partial class Cob
{
    const int XML_STMT_SUCCESSFULL = 0;
    const int XML_OUT_FIELD_TOO_SMALL = 400;
    const int XML_INVALID_NAMESPACE = 416;
    const int XML_INVALID_CHAR_REPLACED = 417;
    const int XML_INVALID_NAMESPACE_PREFIX = 419;
    const int XML_INTERNAL_ERROR = 600;
    const int JSON_OUT_FIELD_TOO_SMALL = 1;
    const int JSON_INTERNAL_ERROR = 500;
    const int COB_EC_XML_IMP = 0x1610;
    const int COB_EC_JSON_IMP = 0x1710;

    public static void cob_set_ml_tree(CobMlTree t, CobField name, CobMlAttr attrs, CobField content,
        int is_suppressed, CobMlTree children, CobMlTree sibling)
    {
        t.name = name; t.attrs = attrs; t.content = content; t.is_suppressed = is_suppressed != 0;
        t.children = children; t.sibling = sibling;
    }

    /// <summary>NAME OF ... IS OMITTED: codegen writes the C null pointer as 0.</summary>
    public static void cob_set_ml_tree(CobMlTree t, int nullName, CobMlAttr attrs, CobField content,
        int is_suppressed, CobMlTree children, CobMlTree sibling) =>
        cob_set_ml_tree(t, (CobField)null, attrs, content, is_suppressed, children, sibling);

    public static void cob_set_ml_attr(CobMlAttr a, CobField name, CobField value, int is_suppressed, CobMlAttr sibling)
    {
        a.name = name; a.value = value; a.is_suppressed = is_suppressed != 0; a.sibling = sibling;
    }

    static CobModule MlModule => Global.cob_current_module ?? (ModuleStack.Count > 0 ? ModuleStack.Peek() : null);

    static void SetXmlCode(int code)
    {
        if (MlModule?.xml_code is CobField f) cob_set_int(f, code);
    }

    static void SetXmlException(int code) { SetException(COB_EC_XML_IMP); SetXmlCode(code); }

    static void SetJsonCode(int code)
    {
        if (MlModule?.json_code is CobField f) cob_set_int(f, code);
    }

    static void SetJsonException(int code) { SetException(COB_EC_JSON_IMP); SetJsonCode(code); }

    // ------------------------------------------------------------ shared helpers

    static bool MlIsEmpty(CobField f)
    {
        foreach (var b in f.Span) if (b != ' ') return false;
        return true;
    }

    /// <summary>The field's bytes, right-trimmed (left-trimmed if JUSTIFIED); " " if nothing is left.</summary>
    static byte[] MlTrimmed(CobField f)
    {
        var s = f.Span;
        if (s.Length == 0) return new[] { (byte)' ' };
        int start = 0, len = s.Length;
        if (f.Has(FLAG_JUSTIFIED))
            for (; s[start] == ' ' && len > 1; start++, len--) { }
        else
            for (; (s[len - 1] == ' ' || s[len - 1] == 0) && len > 1; len--) { }
        return s.Slice(start, len).ToArray();
    }

    // C iscntrl(): 0x00-0x1F and 0x7F
    static bool MlHasInvalidXmlChar(CobField f)
    {
        foreach (var b in f.Span)
            if ((b < 0x20 || b == 0x7F) && b != 0x09 && b != 0x0A && b != 0x0D) return true;
        return false;
    }

    static bool MlNameStartChar(int c) =>
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_'
        || (c >= 0xC0 && c <= 0xD6) || (c >= 0xD8 && c <= 0xF6) || c >= 0xF8;

    static bool MlNameChar(int c) =>
        MlNameStartChar(c) || c == '-' || c == '.' || (c >= '0' && c <= '9') || c == 0xB7;

    static bool MlValidXmlName(CobField f)
    {
        if (f.size == 0 || !MlNameStartChar(f.Span[0])) return false;
        var s = MlTrimmed(f);
        for (int i = 1; i < s.Length; i++) if (!MlNameChar(s[i])) return false;
        return true;
    }

    /// <summary>A number as text: no leading zeros, '-' if negative, decimals kept (libcob get_num).</summary>
    static byte[] MlNumber(CobField f, char decimalPoint)
    {
        int digits = f.attr.digits, scale = f.attr.scale;
        int intDigits = Math.Max(0, digits - scale), decDigits = Math.Max(0, scale);
        var pic = new List<CobPicSymbol> { new('-', Math.Max(intDigits, 1)), new('9', 1) };
        if (decDigits > 0) { pic.Add(new(DecimalPoint, 1)); pic.Add(new('9', decDigits)); }
        pic.Add(new('\0', 0));
        var attr = new CobFieldAttr(TYPE_NUMERIC_EDITED, digits, scale, FLAG_JUSTIFIED, pic.ToArray());
        long size = Math.Max(2, intDigits + 1);
        if (decDigits > 0) size += 1 + decDigits;
        var edited = new CobField(size, new CobPtr(new byte[size], 0), attr);
        cob_move(f, edited);
        var span = edited.Span;
        int dp = span.IndexOf((byte)DecimalPoint);
        if (dp >= 0) span[dp] = (byte)decimalPoint;
        return MlTrimmed(edited);
    }

    static void MlCopyOut(CobField outf, byte[] text, out int copyLen, out int numNewlines)
    {
        var o = outf.Span;
        copyLen = (int)Math.Min(text.Length, o.Length);
        text.AsSpan(0, copyLen).CopyTo(o);
        o.Slice(copyLen).Fill((byte)' ');
        numNewlines = 0;
        for (; copyLen > 0 && o[copyLen - 1] == '\n'; copyLen--)
        {
            o[copyLen - 1] = (byte)' ';
            numNewlines++;
        }
    }

    // ------------------------------------------------------------ XML GENERATE

    public static void cob_xml_generate(CobField outf, CobMlTree tree, CobField count, int with_xml_dec,
        CobField ns, CobField ns_prefix) =>
        cob_xml_generate_new(outf, tree, count, with_xml_dec, ns, ns_prefix, DecimalPoint);

    // codegen writes a missing NAMESPACE / NAMESPACE-PREFIX as the C null pointer 0
    public static void cob_xml_generate_new(CobField outf, CobMlTree tree, CobField count, int with_xml_dec,
        CobField ns, int noPrefix, int decimal_point) =>
        cob_xml_generate_new(outf, tree, count, with_xml_dec, ns, (CobField)null, decimal_point);

    public static void cob_xml_generate_new(CobField outf, CobMlTree tree, CobField count, int with_xml_dec,
        int noNs, CobField ns_prefix, int decimal_point) =>
        cob_xml_generate_new(outf, tree, count, with_xml_dec, (CobField)null, ns_prefix, decimal_point);

    public static void cob_xml_generate_new(CobField outf, CobMlTree tree, CobField count, int with_xml_dec,
        CobField ns, CobField ns_prefix, int decimal_point)
    {
        if (outf == null || tree == null) { SetXmlException(XML_INTERNAL_ERROR); cob_fatal_error(COB_FERROR_CODEGEN); }
        if (outf.data.IsNull || outf.size == 0) { SetXmlException(XML_INTERNAL_ERROR); return; }

        byte[] nsData = null;
        if (ns != null && !MlIsEmpty(ns))
        {
            if (MlHasInvalidXmlChar(ns)) { SetXmlException(XML_INVALID_NAMESPACE); return; }
            nsData = MlTrimmed(ns);
            if (!MlValidUri(nsData)) { SetXmlException(XML_INVALID_NAMESPACE); return; }
        }
        if (ns_prefix != null)
        {
            if (MlIsEmpty(ns_prefix)) ns_prefix = null;
            else if (!MlValidXmlName(ns_prefix)) { SetXmlException(XML_INVALID_NAMESPACE_PREFIX); return; }
        }

        SetXmlCode(XML_STMT_SUCCESSFULL);
        var w = new XmlWriter();
        if (with_xml_dec != 0) w.StartDocument();
        var prefix = ns_prefix != null ? MlTrimmed(ns_prefix) : null;
        XmlFromTree(w, tree, nsData, prefix, (char)decimal_point);
        w.EndDocument();

        var text = w.Bytes();
        MlCopyOut(outf, text, out int copyLen, out int nl);
        int written = w.Count - nl;
        if (text.Length - nl > copyLen) SetXmlException(XML_OUT_FIELD_TOO_SMALL);
        if (count != null) cob_set_int(count, written);
    }

    // libxml2 xmlParseURI accepts nearly anything without spaces or bad '%' escapes
    static bool MlValidUri(byte[] s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            byte c = s[i];
            if (c <= 0x20 || c == '"' || c == '<' || c == '>' || c == '\\' || c == '^' || c == '`'
                || c == '{' || c == '|' || c == '}' || c >= 0x7F) return false;
            if (c == '%' && (i + 2 >= s.Length || !Uri.IsHexDigit((char)s[i + 1]) || !Uri.IsHexDigit((char)s[i + 2])))
                return false;
        }
        return true;
    }

    static byte[] MlXmlName(CobField f)
    {
        var n = MlTrimmed(f);
        if (n.Length > 0 && !MlNameStartChar(n[0]))
        {
            var u = new byte[n.Length + 1];
            u[0] = (byte)'_';
            n.CopyTo(u, 1);
            return u;
        }
        return n;
    }

    static byte[] MlHexName(CobField f)
    {
        var n = MlXmlName(f);
        var r = new byte[n.Length + 4];
        "hex."u8.CopyTo(r);
        n.CopyTo(r, 4);
        return r;
    }

    static byte[] MlHexData(CobField f)
    {
        const string hex = "0123456789abcdef";
        var s = f.Span;
        var r = new byte[s.Length * 2];
        for (int i = 0; i < s.Length; i++) { r[2 * i] = (byte)hex[s[i] >> 4]; r[2 * i + 1] = (byte)hex[s[i] & 15]; }
        return r;
    }

    static void XmlAttributes(XmlWriter w, CobMlAttr a)
    {
        for (; a != null; a = a.sibling)
        {
            if (a.is_suppressed) continue;
            if (MlHasInvalidXmlChar(a.value))
            {
                SetXmlCode(XML_INVALID_CHAR_REPLACED);
                w.Attribute(MlHexName(a.name), MlHexData(a.value));
            }
            else
                w.Attribute(MlXmlName(a.name), MlTrimmed(a.value));
        }
    }

    static void XmlContent(XmlWriter w, CobMlTree t, char dp)
    {
        var c = t.content;
        if (Float.IsFloat(c.Type))
        {
            // libcob: "TO-DO: Implement!" - a fatal error at run time
            throw new CobRuntimeException("XML GENERATE: a COMP-1 / COMP-2 item can't be generated (GnuCOBOL 3.2 doesn't support it either).");
        }
        w.Text(IsNumeric(c.Type) ? MlNumber(c, dp) : MlTrimmed(c));
    }

    static void XmlFromTree(XmlWriter w, CobMlTree t, byte[] ns, byte[] prefix, char dp)
    {
        if (t.is_suppressed) return;
        if (t.name == null) { XmlContent(w, t, dp); return; }

        if (t.content != null && !IsNumeric(t.content.Type) && MlHasInvalidXmlChar(t.content))
        {
            SetXmlCode(XML_INVALID_CHAR_REPLACED);
            w.StartElement(prefix, MlHexName(t.name), ns);
            XmlAttributes(w, t.attrs);
            w.Text(MlHexData(t.content));
            w.EndElement();
            return;
        }
        w.StartElement(prefix, MlXmlName(t.name), ns);
        XmlAttributes(w, t.attrs);
        if (t.children != null)
        {
            // only the outermost element carries the namespace declaration
            for (var c = t.children; c != null; c = c.sibling) XmlFromTree(w, c, null, prefix, dp);
        }
        else if (t.content != null)
            XmlContent(w, t, dp);
        w.EndElement();
    }

    /// <summary>
    /// The output of libxml2's xmlTextWriter (no indentation) for the calls libcob makes,
    /// and its byte count (COUNT IN), which is all of it.
    /// </summary>
    sealed class XmlWriter
    {
        readonly List<byte> buf = new();
        readonly Stack<(byte[] qname, bool open)> stack = new();   // open: start tag not closed yet
        readonly List<(byte[] prefix, byte[] uri)> nsDecls = new();
        public int Count;

        public byte[] Bytes() => buf.ToArray();

        void Put(ReadOnlySpan<byte> s, bool counted = true)
        {
            foreach (var b in s) buf.Add(b);
            if (counted) Count += s.Length;
        }
        void Put(string s) => Put(Encoding.ASCII.GetBytes(s));

        public void StartDocument() => Put("<?xml version=\"1.0\"?>\n");

        void CloseStartTag()
        {
            if (stack.Count > 0 && stack.Peek().open)
            {
                OutputNsDecls();
                Put(">");
                var top = stack.Pop();
                stack.Push((top.qname, false));
            }
        }

        void OutputNsDecls()
        {
            foreach (var (p, uri) in nsDecls)
            {
                var name = p == null ? Encoding.ASCII.GetBytes("xmlns") : Concat("xmlns:"u8.ToArray(), p);
                Attribute(name, uri);
            }
            nsDecls.Clear();
        }

        static byte[] Concat(byte[] a, byte[] b)
        {
            var r = new byte[a.Length + b.Length];
            a.CopyTo(r, 0); b.CopyTo(r, a.Length);
            return r;
        }

        public void StartElement(byte[] prefix, byte[] name, byte[] ns)
        {
            CloseStartTag();
            var q = prefix != null ? Concat(Concat(prefix, new[] { (byte)':' }), name) : name;
            Put("<");
            Put(q);
            stack.Push((q, true));
            if (ns != null) nsDecls.Add((prefix, ns));
        }

        public void Attribute(byte[] name, byte[] value)
        {
            Put(" ");
            Put(name);
            Put("=\"");
            Put(EscapeAttr(value));
            Put("\"");
        }

        public void Text(byte[] text)
        {
            CloseStartTag();
            Put(EscapeText(text));
        }

        public void EndElement()
        {
            var top = stack.Pop();
            if (top.open)
            {
                OutputNsDecls();
                Put("/>");
            }
            else
            {
                Put("</");
                Put(top.qname);
                Put(">");
            }
        }

        public void EndDocument()
        {
            while (stack.Count > 0) EndElement();
            Put("\n");
        }

        // xmlEncodeSpecialChars
        static byte[] EscapeText(byte[] s)
        {
            var o = new List<byte>(s.Length + 8);
            foreach (var b in s)
            {
                switch (b)
                {
                    case (byte)'<': o.AddRange("&lt;"u8.ToArray()); break;
                    case (byte)'>': o.AddRange("&gt;"u8.ToArray()); break;
                    case (byte)'&': o.AddRange("&amp;"u8.ToArray()); break;
                    case (byte)'"': o.AddRange("&quot;"u8.ToArray()); break;
                    case (byte)'\r': o.AddRange("&#13;"u8.ToArray()); break;
                    default: o.Add(b); break;
                }
            }
            return o.ToArray();
        }

        // xmlBufAttrSerializeTxtContent with no document: also tab/newline, and UTF-8 as &#xHH;
        static byte[] EscapeAttr(byte[] s)
        {
            var o = new List<byte>(s.Length + 8);
            for (int i = 0; i < s.Length; i++)
            {
                byte b = s[i];
                switch (b)
                {
                    case (byte)'<': o.AddRange("&lt;"u8.ToArray()); break;
                    case (byte)'>': o.AddRange("&gt;"u8.ToArray()); break;
                    case (byte)'&': o.AddRange("&amp;"u8.ToArray()); break;
                    case (byte)'"': o.AddRange("&quot;"u8.ToArray()); break;
                    case (byte)'\n': o.AddRange("&#10;"u8.ToArray()); break;
                    case (byte)'\r': o.AddRange("&#13;"u8.ToArray()); break;
                    case (byte)'\t': o.AddRange("&#9;"u8.ToArray()); break;
                    default:
                        if (b >= 0x80 && TryUtf8(s, i, out int cp, out int len))
                        {
                            o.AddRange(Encoding.ASCII.GetBytes($"&#x{cp:X};"));
                            i += len - 1;
                        }
                        else o.Add(b);
                        break;
                }
            }
            return o.ToArray();
        }

        static bool TryUtf8(byte[] s, int i, out int cp, out int len)
        {
            cp = 0; len = 0;
            byte b = s[i];
            if ((b & 0xE0) == 0xC0) { len = 2; cp = b & 0x1F; }
            else if ((b & 0xF0) == 0xE0) { len = 3; cp = b & 0x0F; }
            else if ((b & 0xF8) == 0xF0) { len = 4; cp = b & 0x07; }
            else return false;
            if (i + len > s.Length) return false;
            for (int k = 1; k < len; k++)
            {
                if ((s[i + k] & 0xC0) != 0x80) return false;
                cp = (cp << 6) | (s[i + k] & 0x3F);
            }
            return true;
        }
    }

    // ------------------------------------------------------------ JSON GENERATE

    public static void cob_json_generate(CobField outf, CobMlTree tree, CobField count) =>
        cob_json_generate_new(outf, tree, count, DecimalPoint);

    public static void cob_json_generate_new(CobField outf, CobMlTree tree, CobField count, int decimal_point)
    {
        SetJsonCode(0);
        // cJSON: one root object; tree members are added to it
        var root = new JsonObject();
        JsonFromTree(tree, (char)decimal_point, root);
        var text = new List<byte>();
        root.Print(text);
        var bytes = text.ToArray();
        int printLen = bytes.Length;
        MlCopyOut(outf, bytes, out int copyLen, out int nl);
        printLen -= nl;
        if (printLen - nl > copyLen) SetJsonException(JSON_OUT_FIELD_TOO_SMALL);
        if (count != null) cob_set_int(count, printLen);
    }

    static void JsonFromTree(CobMlTree t, char dp, JsonObject outObj)
    {
        if (t.is_suppressed) return;
        byte[] name = t.name != null ? MlTrimmed(t.name) : null;   // null: NAME OF ... OMITTED
        if (t.children != null)
        {
            var kids = name != null ? new JsonObject() : outObj;
            for (var c = t.children; c != null; c = c.sibling) JsonFromTree(c, dp, kids);
            if (name != null) outObj.Members.Add((name, kids));
        }
        else if (t.content != null)
        {
            if (name == null || Float.IsFloat(t.content.Type))
                throw new CobRuntimeException(name == null
                    ? "JSON GENERATE: an item without a name (NAME OF ... OMITTED) must be a group."
                    : "JSON GENERATE: a COMP-1 / COMP-2 item can't be generated (GnuCOBOL 3.2 doesn't support it either).");
            if (IsNumeric(t.content.Type))
                outObj.Members.Add((name, new JsonRaw(MlNumber(t.content, dp))));
            else
                outObj.Members.Add((name, new JsonString(MlTrimmed(t.content))));
        }
    }

    abstract class JsonValue { public abstract void Print(List<byte> o); }

    sealed class JsonRaw : JsonValue
    {
        readonly byte[] raw;
        public JsonRaw(byte[] r) => raw = r;
        public override void Print(List<byte> o) => o.AddRange(raw);
    }

    sealed class JsonString : JsonValue
    {
        readonly byte[] s;
        public JsonString(byte[] s) => this.s = s;
        public override void Print(List<byte> o) => PrintString(s, o);

        // cJSON print_string_ptr
        internal static void PrintString(byte[] s, List<byte> o)
        {
            o.Add((byte)'"');
            foreach (var b in s)
            {
                switch (b)
                {
                    case (byte)'"': o.AddRange("\\\""u8.ToArray()); break;
                    case (byte)'\\': o.AddRange("\\\\"u8.ToArray()); break;
                    case (byte)'\b': o.AddRange("\\b"u8.ToArray()); break;
                    case (byte)'\f': o.AddRange("\\f"u8.ToArray()); break;
                    case (byte)'\n': o.AddRange("\\n"u8.ToArray()); break;
                    case (byte)'\r': o.AddRange("\\r"u8.ToArray()); break;
                    case (byte)'\t': o.AddRange("\\t"u8.ToArray()); break;
                    default:
                        if (b < 32) o.AddRange(Encoding.ASCII.GetBytes($"\\u{b:x4}"));
                        else o.Add(b);
                        break;
                }
            }
            o.Add((byte)'"');
        }
    }

    sealed class JsonObject : JsonValue
    {
        public readonly List<(byte[] name, JsonValue value)> Members = new();

        // cJSON_PrintUnformatted
        public override void Print(List<byte> o)
        {
            o.Add((byte)'{');
            for (int i = 0; i < Members.Count; i++)
            {
                if (i > 0) o.Add((byte)',');
                JsonString.PrintString(Members[i].name, o);
                o.Add((byte)':');
                Members[i].value.Print(o);
            }
            o.Add((byte)'}');
        }
    }
}
