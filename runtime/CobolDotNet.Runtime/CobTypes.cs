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

    public ref byte this[long i] => ref Arr[Off + (int)i];

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
    public CobField(long size, string lit, CobFieldAttr attr) { this.size = size; data = CobPtr.FromLatin1(lit); this.attr = attr; }

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
}

/// <summary>Arbitrary-precision decimal: value × 10^-scale.</summary>
public sealed class CobDecimal
{
    public System.Numerics.BigInteger value;
    public int scale;
}
