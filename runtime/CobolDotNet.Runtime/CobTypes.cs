// Core data model for COBOL storage in .NET.
//
// Decision (2026-11-21): managed byte arrays.  Every storage area is a
// byte[]; a "pointer" is (array, offset); a field is (size, pointer, attr).
// REDEFINES and reference modification are just more pointers into the
// same array, so they alias exactly like COBOL storage does.

using System;

namespace CobolDotNet.Runtime;

/// <summary>A managed "pointer": a byte array plus an offset into it.</summary>
public readonly struct CobPtr : IEquatable<CobPtr>
{
    public readonly byte[] Arr;
    public readonly int Off;

    public CobPtr(long size) { Arr = new byte[size]; Off = 0; }
    public CobPtr(byte[] arr, int off) { Arr = arr; Off = off; }

    public bool IsNull => Arr == null;

    public static CobPtr operator +(CobPtr p, long n) => new(p.Arr, p.Off + (int)n);
    public static CobPtr operator -(CobPtr p, long n) => new(p.Arr, p.Off - (int)n);

    /// <summary>C's NULL as a pointer (the translator writes it on POINTER lines).</summary>
    public static readonly CobPtr Null = default;

    /// <summary>Pointer difference: same storage -> offset distance; 0 means "same address".</summary>
    public static long operator -(CobPtr a, CobPtr b)
    {
        if (ReferenceEquals(a.Arr, b.Arr)) return a.IsNull ? 0 : a.Off - b.Off;
        return a.IsNull ? -1 : 1;
    }

    /// <summary>
    /// The pointer stored in a USAGE POINTER item at this address (C's *(unsigned char **)p).
    /// A managed reference can't live in COBOL's bytes, so the 8 bytes hold an id from a
    /// registry of addresses; 0 is NULL.
    /// </summary>
    public CobPtr Ptr
    {
        get => PointerRegistry.Get(S64);
        set => S64 = PointerRegistry.Id(value);
    }

    public ref byte this[long i] => ref Arr[Off + (int)i];

    // Typed views of the bytes at this pointer, for C's  *(short *)(p)  and friends
    // (binary COMP fields read or written in place, e.g. a COMP subscript).
    // Native byte order, unaligned, readable and assignable, like the C original.
    public ref byte U8 => ref Arr[Off];
    public ref sbyte S8 => ref System.Runtime.CompilerServices.Unsafe.As<byte, sbyte>(ref Arr[Off]);
    public ref short S16 => ref System.Runtime.CompilerServices.Unsafe.As<byte, short>(ref Arr[Off]);
    public ref ushort U16 => ref System.Runtime.CompilerServices.Unsafe.As<byte, ushort>(ref Arr[Off]);
    public ref int S32 => ref System.Runtime.CompilerServices.Unsafe.As<byte, int>(ref Arr[Off]);
    public ref uint U32 => ref System.Runtime.CompilerServices.Unsafe.As<byte, uint>(ref Arr[Off]);
    public ref long S64 => ref System.Runtime.CompilerServices.Unsafe.As<byte, long>(ref Arr[Off]);
    public ref ulong U64 => ref System.Runtime.CompilerServices.Unsafe.As<byte, ulong>(ref Arr[Off]);
    public ref float F32 => ref System.Runtime.CompilerServices.Unsafe.As<byte, float>(ref Arr[Off]);
    public ref double F64 => ref System.Runtime.CompilerServices.Unsafe.As<byte, double>(ref Arr[Off]);

    public Span<byte> Span(long len) => Arr.AsSpan(Off, (int)len);

    public static CobPtr FromLatin1(string s)
    {
        var b = new byte[s.Length];
        for (int i = 0; i < s.Length; i++) b[i] = (byte)s[i];
        return new CobPtr(b, 0);
    }

    public bool Equals(CobPtr o) => ReferenceEquals(Arr, o.Arr) && Off == o.Off;
    public override bool Equals(object o) => o is CobPtr p && Equals(p);
    public override int GetHashCode() => HashCode.Combine(Arr, Off);
    public static bool operator ==(CobPtr a, CobPtr b) => a.Equals(b);
    public static bool operator !=(CobPtr a, CobPtr b) => !a.Equals(b);
}

/// <summary>Addresses stored in USAGE POINTER items, by id (see CobPtr.Ptr).</summary>
internal static class PointerRegistry
{
    sealed class ArrRef : System.Collections.Generic.IEqualityComparer<(byte[] a, int o)>
    {
        public bool Equals((byte[] a, int o) x, (byte[] a, int o) y) => ReferenceEquals(x.a, y.a) && x.o == y.o;
        public int GetHashCode((byte[] a, int o) k) => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(k.a), k.o);
    }

    static readonly System.Collections.Generic.Dictionary<(byte[], int), long> ids = new(new ArrRef());
    static readonly System.Collections.Generic.List<CobPtr> ptrs = new() { default };

    internal static long Id(CobPtr p)
    {
        if (p.IsNull) return 0;
        lock (ptrs)
        {
            if (ids.TryGetValue((p.Arr, p.Off), out long id)) return id;
            id = ptrs.Count;
            ptrs.Add(p);
            ids[(p.Arr, p.Off)] = id;
            return id;
        }
    }

    internal static CobPtr Get(long id)
    {
        lock (ptrs) return id > 0 && id < ptrs.Count ? ptrs[(int)id] : default;
    }
}

/// <summary>One PICTURE symbol and its repeat count, e.g. ('*', 3).</summary>
public readonly struct CobPicSymbol
{
    public readonly char symbol;
    public readonly int times_repeated;
    public CobPicSymbol(char symbol, int times) { this.symbol = symbol; times_repeated = times; }
}

/// <summary>Field attributes: type, digits, scale, flags, edit picture.</summary>
public sealed class CobFieldAttr
{
    public readonly int type;
    public readonly int digits;
    public readonly int scale;
    public readonly int flags;
    public readonly CobPicSymbol[] pic;

    public CobFieldAttr(int type, int digits, int scale, int flags, CobPicSymbol[] pic)
    {
        this.type = type; this.digits = digits; this.scale = scale;
        this.flags = flags; this.pic = pic;
    }
}

/// <summary>A COBOL data item: size, where it lives, and what it is.</summary>
public sealed class CobField
{
    public long size;
    public CobPtr data;
    public CobFieldAttr attr;

    public CobField() { }
    public CobField(long size, CobPtr data, CobFieldAttr attr) { this.size = size; this.data = data; this.attr = attr; }
    /// <summary>Literal constant: the string's chars are the bytes (Latin-1).</summary>
    public CobField(long size, string lit, CobFieldAttr attr) { this.size = size; data = lit == null ? default : CobPtr.FromLatin1(lit); this.attr = attr; }   // null: LINKAGE item, data set at CALL

    internal Span<byte> Span => data.Span(size);
    internal int Type => attr.type;
    internal bool Has(int flag) => (attr.flags & flag) != 0;

    public override string ToString()
    {
        // Shows the raw COBOL bytes in the debugger's watch window.
        if (data.IsNull) return "<no storage>";
        var s = Span;
        var c = new char[s.Length];
        for (int i = 0; i < s.Length; i++) c[i] = (char)s[i];
        return new string(c);
    }
}

/// <summary>Constants mirroring libcob's common.h.</summary>
public static class CobConst
{
    public const int TYPE_GROUP = 0x01;
    public const int TYPE_NUMERIC_DISPLAY = 0x10;
    public const int TYPE_NUMERIC_BINARY = 0x11;
    public const int TYPE_NUMERIC_PACKED = 0x12;
    public const int TYPE_NUMERIC_FLOAT = 0x13;
    public const int TYPE_NUMERIC_DOUBLE = 0x14;
    public const int TYPE_NUMERIC_COMP5 = 0x1B;
    public const int TYPE_NUMERIC_EDITED = 0x24;
    public const int TYPE_ALPHANUMERIC = 0x21;
    public const int TYPE_ALPHANUMERIC_ALL = 0x22;
    public const int TYPE_ALPHANUMERIC_EDITED = 0x23;
    public const int TYPE_NATIONAL = 0x40;
    public const int TYPE_NATIONAL_EDITED = 0x41;

    public const int FLAG_HAVE_SIGN = 0x0001;
    public const int FLAG_SIGN_SEPARATE = 0x0002;
    public const int FLAG_SIGN_LEADING = 0x0004;
    public const int FLAG_BLANK_ZERO = 0x0008;
    public const int FLAG_JUSTIFIED = 0x0010;
    public const int FLAG_BINARY_SWAP = 0x0020;
    public const int FLAG_REAL_BINARY = 0x0040;
    public const int FLAG_NO_SIGN_NIBBLE = 0x0100;
    public const int FLAG_BINARY_TRUNC = 0x0800;
    public const int FLAG_CONSTANT = 0x1000;

    public const int STORE_ROUND = 1 << 0;
    public const int STORE_KEEP_ON_OVERFLOW = 1 << 1;
    public const int STORE_TRUNC_ON_OVERFLOW = 1 << 2;

    internal static bool IsNumeric(int type) => type >= 0x10 && type <= 0x1B;
    internal static bool IsNational(int type) => (type & TYPE_NATIONAL) != 0;   // COB_FIELD_IS_NATIONAL
}

/// <summary>Arbitrary-precision decimal: value × 10^-scale.</summary>
public sealed class CobDecimal
{
    public System.Numerics.BigInteger value;
    public int scale;
}
