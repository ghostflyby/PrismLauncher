// SPDX-License-Identifier: GPL-3.0-only
// Port of libraries/libnbtplusplus/include/io/stream_reader.h and src/io/stream_reader.cpp
// (big-endian). Corresponds to nbt::io::stream_reader + nbt::io::read_compound/read_tag.
//
// Robustness guarantees (required by the launcher's world/servers parsing):
//  - all input is length-checked; truncated data raises NbtParseException immediately
//    instead of hanging or reading past the end,
//  - nesting deeper than MaxDepth (1024, libnbt++'s MAX_DEPTH) is rejected,
//  - array/list payload sizes grow incrementally while reading, so a corrupt huge
//    length fails at end-of-stream instead of attempting a huge allocation.

using System.Buffers.Binary;

namespace FlintLauncher.Nbt;

public sealed class NbtReader(Stream input)
{
    /// <summary>Port of libnbt++'s MAX_DEPTH in nbt::io::stream_reader.</summary>
    public const int MaxDepth = 1024;

    private readonly Stream _input = input ?? throw new ArgumentNullException(nameof(input));
    private long _offset;
    private int _depth;

    /// <summary>
    /// Port of nbt::io::read_compound: reads a named root compound.
    /// Callers expect the root name to be empty (the launcher checks for "" like the C++).
    /// </summary>
    public (string Name, NbtCompound Root) ReadCompound()
    {
        return Run(() =>
        {
            if (ReadType(allowEnd: false) != NbtTagType.Compound)
            {
                throw new NbtParseException("Tag is not a compound");
            }

            var key = ReadString();
            var comp = new NbtCompound();
            ReadCompoundPayload(comp);
            return (key, comp);
        });
    }

    /// <summary>Port of nbt::io::read_tag: reads any named root tag.</summary>
    public (string Name, NbtTag Tag) ReadTag()
    {
        return Run(() =>
        {
            var type = ReadType(allowEnd: false);
            var key = ReadString();
            var tag = ReadPayload(type);
            return (key, tag);
        });
    }

    /// <summary>Wraps stream-level failures into NbtParseException so callers get one clear type.</summary>
    private T Run<T>(Func<T> body)
    {
        try
        {
            return body();
        }
        catch (NbtParseException)
        {
            throw;
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or InvalidDataException or ObjectDisposedException)
        {
            throw new NbtParseException($"Error reading NBT data at offset {_offset}: {e.Message}", e);
        }
    }

    /// <summary>Port of stream_reader::read_payload: reads a payload-only tag of the given type.</summary>
    private NbtTag ReadPayload(NbtTagType type)
    {
        if (++_depth > MaxDepth)
        {
            throw new NbtParseException($"Too deeply nested (more than {MaxDepth} levels)");
        }

        try
        {
            return type switch
            {
                NbtTagType.Byte => new NbtByte(unchecked((sbyte)ReadByteChecked())),
                NbtTagType.Short => new NbtShort(ReadShort()),
                NbtTagType.Int => new NbtInt(ReadInt()),
                NbtTagType.Long => new NbtLong(ReadLong()),
                NbtTagType.Float => new NbtFloat(ReadFloat()),
                NbtTagType.Double => new NbtDouble(ReadDouble()),
                NbtTagType.String => new NbtString(ReadString()),
                NbtTagType.ByteArray => new NbtByteArray(ReadByteArrayPayload()),
                NbtTagType.IntArray => new NbtIntArray(ReadNumericArrayPayload(sizeof(int), static bytes =>
                {
                    var result = new int[bytes.Length / sizeof(int)];
                    for (var i = 0; i < result.Length; i++)
                    {
                        result[i] = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(i * sizeof(int), sizeof(int)));
                    }

                    return result;
                })),
                NbtTagType.LongArray => new NbtLongArray(ReadNumericArrayPayload(sizeof(long), static bytes =>
                {
                    var result = new long[bytes.Length / sizeof(long)];
                    for (var i = 0; i < result.Length; i++)
                    {
                        result[i] = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(i * sizeof(long), sizeof(long)));
                    }

                    return result;
                })),
                NbtTagType.List => ReadListPayload(),
                NbtTagType.Compound => ReadCompoundPayload(new NbtCompound()),
                _ => throw new NbtParseException($"Invalid tag type: {(int)type}"),
            };
        }
        finally
        {
            _depth--;
        }
    }

    /// <summary>Port of tag_compound::read_payload.</summary>
    private NbtCompound ReadCompoundPayload(NbtCompound compound)
    {
        while (ReadType(allowEnd: true) is var tt && tt != NbtTagType.End)
        {
            string key;
            try
            {
                key = ReadString();
            }
            catch (NbtParseException)
            {
                throw new NbtParseException($"Error reading key of tag_{tt}");
            }

            compound.Put(key, ReadPayload(tt));
        }

        return compound;
    }

    /// <summary>Port of tag_list::read_payload.</summary>
    private NbtList ReadListPayload()
    {
        var elementType = ReadType(allowEnd: true);
        var length = ReadInt();
        if (length < 0)
        {
            throw new NbtParseException($"Negative tag_list length {length}");
        }

        var list = new NbtList();
        if (elementType != NbtTagType.End)
        {
            list.Reset(elementType);
            for (var i = 0; i < length; i++)
            {
                list.Add(ReadPayload(elementType));
            }
        }

        // elementType == End: ignore the length and leave the type undetermined, like libnbt++.
        return list;
    }

    private byte[] ReadByteArrayPayload()
    {
        var length = ReadInt();
        if (length < 0)
        {
            throw new NbtParseException($"Negative tag_byte_array length {length}");
        }

        return ReadExact(length);
    }

    private T[] ReadNumericArrayPayload<T>(int elementSize, Func<byte[], T[]> decode)
    {
        var length = ReadInt();
        if (length < 0)
        {
            throw new NbtParseException($"Negative array tag length {length}");
        }

        // Bounds the allocation by what the stream actually contains: a corrupt huge
        // length hits end-of-stream here instead of allocating length * elementSize.
        return decode(ReadExact((long)length * elementSize));
    }

    /// <summary>Port of stream_reader::read_type.</summary>
    private NbtTagType ReadType(bool allowEnd)
    {
        var b = ReadByteChecked();
        var type = (int)(sbyte)b;
        if (!NbtTagTypes.IsValidType(type, allowEnd))
        {
            throw new NbtParseException($"Invalid tag type: {type}");
        }

        return (NbtTagType)type;
    }

    /// <summary>Port of stream_reader::read_string: uint16 length + modified UTF-8 bytes.</summary>
    public string ReadString()
    {
        var length = ReadUShort();
        var bytes = ReadExact(length);
        return ModifiedUtf8.Decode(bytes);
    }

    public short ReadShort()
    {
        Span<byte> buffer = stackalloc byte[2];
        ReadExact(buffer);
        return BinaryPrimitives.ReadInt16BigEndian(buffer);
    }

    public ushort ReadUShort()
    {
        Span<byte> buffer = stackalloc byte[2];
        ReadExact(buffer);
        return BinaryPrimitives.ReadUInt16BigEndian(buffer);
    }

    public int ReadInt()
    {
        Span<byte> buffer = stackalloc byte[4];
        ReadExact(buffer);
        return BinaryPrimitives.ReadInt32BigEndian(buffer);
    }

    public long ReadLong()
    {
        Span<byte> buffer = stackalloc byte[8];
        ReadExact(buffer);
        return BinaryPrimitives.ReadInt64BigEndian(buffer);
    }

    public float ReadFloat()
    {
        Span<byte> buffer = stackalloc byte[4];
        ReadExact(buffer);
        return BinaryPrimitives.ReadSingleBigEndian(buffer);
    }

    public double ReadDouble()
    {
        Span<byte> buffer = stackalloc byte[8];
        ReadExact(buffer);
        return BinaryPrimitives.ReadDoubleBigEndian(buffer);
    }

    private byte ReadByteChecked()
    {
        var b = _input.ReadByte();
        if (b < 0)
        {
            throw new NbtParseException($"Unexpected end of NBT data at offset {_offset} while reading a tag type");
        }

        _offset++;
        return (byte)b;
    }

    private void ReadExact(Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = _input.Read(buffer[read..]);
            if (n <= 0)
            {
                throw new NbtParseException(
                    $"Unexpected end of NBT data at offset {_offset} (needed {buffer.Length - read} more bytes)");
            }

            read += n;
            _offset += n;
        }
    }

    /// <summary>
    /// Reads exactly <paramref name="count"/> bytes. For large counts the buffer grows in
    /// blocks as bytes actually arrive, so corrupt oversized lengths fail at EOF quickly.
    /// </summary>
    private byte[] ReadExact(long count)
    {
        if (count is < 0 or > int.MaxValue)
        {
            throw new NbtParseException($"Invalid NBT read length {count} at offset {_offset}");
        }

        if (count <= 64 * 1024)
        {
            var result = new byte[count];
            ReadExact(result.AsSpan());
            return result;
        }

        const int blockSize = 64 * 1024;
        var chunks = new List<byte[]>();
        long total = 0;
        while (total < count)
        {
            var block = new byte[(int)Math.Min(blockSize, count - total)];
            ReadExact(block.AsSpan());
            chunks.Add(block);
            total += block.Length;
        }

        var combined = new byte[count];
        var pos = 0;
        foreach (var chunk in chunks)
        {
            chunk.CopyTo(combined, pos);
            pos += chunk.Length;
        }

        return combined;
    }
}
