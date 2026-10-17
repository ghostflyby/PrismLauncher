// SPDX-License-Identifier: GPL-3.0-only
// Port of libraries/libnbtplusplus/include/tag_array.h (tag_byte_array, tag_int_array,
// tag_long_array). Big-endian on the wire with an int32 length prefix.

namespace FlintLauncher.Nbt;

public abstract class NbtArray : NbtTag { }

/// <summary>Port of nbt::tag_byte_array.</summary>
public sealed class NbtByteArray : NbtArray
{
    /// <summary>The payload; exposed mutably like the C++ std::vector member.</summary>
    public byte[] Value { get; }

    public NbtByteArray() : this([]) { }

    public NbtByteArray(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
    }

    public override NbtTagType Type => NbtTagType.ByteArray;

    public override NbtTag Clone() => new NbtByteArray((byte[])Value.Clone());

    public static implicit operator byte[](NbtByteArray tag) => tag.Value;

    public override string ToString() => $"tag_byte_array(size={Value.Length})";

    public override bool Equals(object? obj) => obj is NbtByteArray other && Value.AsSpan().SequenceEqual(other.Value);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(Value);
        return hash.ToHashCode();
    }
}

/// <summary>Port of nbt::tag_int_array (32-bit elements).</summary>
public sealed class NbtIntArray : NbtArray
{
    public int[] Value { get; }

    public NbtIntArray() : this([]) { }

    public NbtIntArray(int[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
    }

    public override NbtTagType Type => NbtTagType.IntArray;

    public override NbtTag Clone() => new NbtIntArray((int[])Value.Clone());

    public static implicit operator int[](NbtIntArray tag) => tag.Value;

    public override string ToString() => $"tag_int_array(size={Value.Length})";

    public override bool Equals(object? obj) => obj is NbtIntArray other && Value.AsSpan().SequenceEqual(other.Value);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var v in Value)
        {
            hash.Add(v);
        }

        return hash.ToHashCode();
    }
}

/// <summary>Port of nbt::tag_long_array (64-bit elements).</summary>
public sealed class NbtLongArray : NbtArray
{
    public long[] Value { get; }

    public NbtLongArray() : this([]) { }

    public NbtLongArray(long[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
    }

    public override NbtTagType Type => NbtTagType.LongArray;

    public override NbtTag Clone() => new NbtLongArray((long[])Value.Clone());

    public static implicit operator long[](NbtLongArray tag) => tag.Value;

    public override string ToString() => $"tag_long_array(size={Value.Length})";

    public override bool Equals(object? obj) => obj is NbtLongArray other && Value.AsSpan().SequenceEqual(other.Value);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var v in Value)
        {
            hash.Add(v);
        }

        return hash.ToHashCode();
    }
}
