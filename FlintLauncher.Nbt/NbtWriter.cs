// SPDX-License-Identifier: GPL-3.0-only
// Port of libraries/libnbtplusplus/include/io/stream_writer.h and src/io/stream_writer.cpp
// (big-endian). Corresponds to nbt::io::stream_writer + nbt::io::write_tag.
//
// Exception mapping: libnbt++ write-side std::length_error (string/list/array too large)
// maps to ArgumentOutOfRangeException; std::logic_error (list element type mismatch) maps
// to InvalidOperationException; std::invalid_argument (NbtList.Add) to ArgumentException.
//
// Wire format reminder: a *named* tag is type byte + name + payload; list *elements* are
// payload-only (their type lives in the list header), which is what WritePayload is for.

using System.Buffers.Binary;

namespace FlintLauncher.Nbt;

public sealed class NbtWriter(Stream output)
{
    /// <summary>Maximum length of an NBT string in encoded bytes (16-bit unsigned).</summary>
    public const int MaxStringLength = ushort.MaxValue;

    /// <summary>Maximum length of an NBT list or array (32-bit signed).</summary>
    public const long MaxArrayLength = int.MaxValue;

    private readonly Stream _output = output ?? throw new ArgumentNullException(nameof(output));

    /// <summary>
    /// Port of nbt::io::write_tag / stream_writer::write_tag: writes a named tag
    /// (type byte, name, payload). Compression is the caller's choice.
    /// </summary>
    public void WriteTag(string name, NbtTag tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        WriteType(tag.Type);
        WriteString(name);
        WritePayload(tag);
    }

    /// <summary>Port of stream_writer::write_type.</summary>
    public void WriteType(NbtTagType type)
    {
        _output.WriteByte((byte)type);
    }

    /// <summary>
    /// Port of stream_writer::write_payload: writes only the payload of a tag — no type
    /// byte, no name. This is the form used for list elements.
    /// </summary>
    public void WritePayload(NbtTag tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        switch (tag)
        {
            case NbtByte t:
                WriteNum(t.Value);
                break;
            case NbtShort t:
                WriteNum(t.Value);
                break;
            case NbtInt t:
                WriteNum(t.Value);
                break;
            case NbtLong t:
                WriteNum(t.Value);
                break;
            case NbtFloat t:
                WriteNum(t.Value);
                break;
            case NbtDouble t:
                WriteNum(t.Value);
                break;
            case NbtString t:
                WriteString(t.Value); // string payload == name encoding: length-prefixed modified UTF-8
                break;
            case NbtByteArray t:
                WriteByteArray(t.Value);
                break;
            case NbtIntArray t:
                WriteArray(t.Value, sizeof(int), static (buffer, v) => BinaryPrimitives.WriteInt32BigEndian(buffer, v));
                break;
            case NbtLongArray t:
                WriteArray(t.Value, sizeof(long), static (buffer, v) => BinaryPrimitives.WriteInt64BigEndian(buffer, v));
                break;
            case NbtList t:
                WriteList(t);
                break;
            case NbtCompound t:
                WriteCompound(t);
                break;
            default:
                throw new InvalidOperationException($"Unsupported tag type {tag.Type}");
        }
    }

    /// <summary>Port of stream_writer::write_string (encodes as modified UTF-8).</summary>
    public void WriteString(string value)
    {
        var bytes = ModifiedUtf8.Encode(value);
        if (bytes.Length > MaxStringLength)
        {
            throw new ArgumentOutOfRangeException(nameof(value), bytes.Length,
                $"String is too long for NBT ({bytes.Length} > {MaxStringLength})");
        }
        WriteNum((ushort)bytes.Length);
        _output.Write(bytes, 0, bytes.Length);
    }

    private void WriteCompound(NbtCompound compound)
    {
        foreach (var (key, value) in compound.Entries)
        {
            WriteTag(key, value);
        }
        WriteType(NbtTagType.End);
    }

    private void WriteList(NbtList list)
    {
        // list.Count > MaxArrayLength is unreachable: MaxArrayLength is int.MaxValue.
        // An empty/undetermined list is written with element type End, like libnbt++.
        WriteType(list.ElementType == NbtTagType.Null ? NbtTagType.End : list.ElementType);
        WriteNum(list.Count);
        foreach (var tag in list.Tags)
        {
            if (tag.Type != list.ElementType)
            {
                throw new InvalidOperationException("The tags in the list do not all match the content type");
            }
            WritePayload(tag);
        }
    }

    private void WriteByteArray(byte[] value)
    {
        // value.Length > MaxArrayLength is unreachable: MaxArrayLength is int.MaxValue.
        WriteNum(value.Length);
        _output.Write(value, 0, value.Length);
    }

    private void WriteArray<T>(T[] value, int elementSize, SpanAction<T> write) where T : struct
    {
        // value.Length > MaxArrayLength is unreachable: MaxArrayLength is int.MaxValue.
        WriteNum(value.Length);
        var buffer = new byte[elementSize];
        foreach (var item in value)
        {
            write(buffer, item);
            _output.Write(buffer, 0, buffer.Length);
        }
    }

    private void WriteNum(sbyte value) => _output.WriteByte((byte)value);

    private void WriteNum(short value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteInt16BigEndian(buffer, value);
        _output.Write(buffer);
    }

    private void WriteNum(int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        _output.Write(buffer);
    }

    private void WriteNum(long value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(buffer, value);
        _output.Write(buffer);
    }

    private void WriteNum(ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
        _output.Write(buffer);
    }

    private void WriteNum(float value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteSingleBigEndian(buffer, value);
        _output.Write(buffer);
    }

    private void WriteNum(double value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(buffer, value);
        _output.Write(buffer);
    }

    private delegate void SpanAction<T>(Span<byte> destination, T value);
}
