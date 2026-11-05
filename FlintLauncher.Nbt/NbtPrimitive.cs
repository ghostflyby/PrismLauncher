// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2013, 2015 ljfa-ag
//
// Port of libraries/libnbtplusplus/include/tag_primitive.h (tag_byte .. tag_double).
// libnbt++ is LGPL-3.0-or-later (see COPYING.md); this port is conveyed under GPL-3.0-only per LGPL-3.0 section 2.

namespace FlintLauncher.Nbt;

/// <summary>Port of nbt::tag_primitive — the shared shape of the six primitive tags.</summary>
public abstract class NbtPrimitive : NbtTag
{
}

/// <summary>Port of nbt::tag_byte (signed 8-bit, like Java's byte).</summary>
public sealed class NbtByte(sbyte value) : NbtPrimitive
{
    public sbyte Value { get; } = value;

    public override NbtTagType Type => NbtTagType.Byte;

    public override NbtTag Clone() => new NbtByte(Value);

    public static implicit operator sbyte(NbtByte tag) => tag.Value;

    public static implicit operator NbtByte(sbyte value) => new(value);

    public static implicit operator bool(NbtByte tag) => tag.Value != 0;

    public override string ToString() => $"tag_byte({Value})";

    public override bool Equals(object? obj) => obj is NbtByte other && Value == other.Value;

    public override int GetHashCode() => Value.GetHashCode();
}

/// <summary>Port of nbt::tag_short (signed 16-bit).</summary>
public sealed class NbtShort(short value) : NbtPrimitive
{
    public short Value { get; } = value;

    public override NbtTagType Type => NbtTagType.Short;

    public override NbtTag Clone() => new NbtShort(Value);

    public static implicit operator short(NbtShort tag) => tag.Value;

    public static implicit operator NbtShort(short value) => new(value);

    public override string ToString() => $"tag_short({Value})";

    public override bool Equals(object? obj) => obj is NbtShort other && Value == other.Value;

    public override int GetHashCode() => Value.GetHashCode();
}

/// <summary>Port of nbt::tag_int (signed 32-bit).</summary>
public sealed class NbtInt(int value) : NbtPrimitive
{
    public int Value { get; } = value;

    public override NbtTagType Type => NbtTagType.Int;

    public override NbtTag Clone() => new NbtInt(Value);

    public static implicit operator int(NbtInt tag) => tag.Value;

    public static implicit operator NbtInt(int value) => new(value);

    public override string ToString() => $"tag_int({Value})";

    public override bool Equals(object? obj) => obj is NbtInt other && Value == other.Value;

    public override int GetHashCode() => Value.GetHashCode();
}

/// <summary>Port of nbt::tag_long (signed 64-bit).</summary>
public sealed class NbtLong(long value) : NbtPrimitive
{
    public long Value { get; } = value;

    public override NbtTagType Type => NbtTagType.Long;

    public override NbtTag Clone() => new NbtLong(Value);

    public static implicit operator long(NbtLong tag) => tag.Value;

    public static implicit operator NbtLong(long value) => new(value);

    public override string ToString() => $"tag_long({Value})";

    public override bool Equals(object? obj) => obj is NbtLong other && Value == other.Value;

    public override int GetHashCode() => Value.GetHashCode();
}

/// <summary>Port of nbt::tag_float (IEEE 754 single precision, bit-exact round trip).</summary>
public sealed class NbtFloat(float value) : NbtPrimitive
{
    public float Value { get; } = value;

    public override NbtTagType Type => NbtTagType.Float;

    public override NbtTag Clone() => new NbtFloat(Value);

    public static implicit operator float(NbtFloat tag) => tag.Value;

    public static implicit operator NbtFloat(float value) => new(value);

    public override string ToString() => $"tag_float({Value:R})";

    public override bool Equals(object? obj) => obj is NbtFloat other
                                                && BitConverter.SingleToInt32Bits(Value) == BitConverter.SingleToInt32Bits(other.Value);

    public override int GetHashCode() => BitConverter.SingleToInt32Bits(Value).GetHashCode();
}

/// <summary>Port of nbt::tag_double (IEEE 754 double precision, bit-exact round trip).</summary>
public sealed class NbtDouble(double value) : NbtPrimitive
{
    public double Value { get; } = value;

    public override NbtTagType Type => NbtTagType.Double;

    public override NbtTag Clone() => new NbtDouble(Value);

    public static implicit operator double(NbtDouble tag) => tag.Value;

    public static implicit operator NbtDouble(double value) => new(value);

    public override string ToString() => $"tag_double({Value:R})";

    public override bool Equals(object? obj) => obj is NbtDouble other
                                                && BitConverter.DoubleToInt64Bits(Value) == BitConverter.DoubleToInt64Bits(other.Value);

    public override int GetHashCode() => BitConverter.DoubleToInt64Bits(Value).GetHashCode();
}
