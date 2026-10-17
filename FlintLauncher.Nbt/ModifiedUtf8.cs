// SPDX-License-Identifier: GPL-3.0-only
// Java "Modified UTF-8" codec for TAG_String / tag names.
//
// This is a deliberate deviation from libnbt++: tag_string.h stores the raw payload
// bytes in a std::string and never converts them, leaving encoding concerns to callers
// (PrismLauncher then does QString::fromUtf8 on the raw bytes). .NET strings are UTF-16,
// so this port must convert explicitly — and Minecraft NBT strings are NOT standard
// UTF-8: they use the encoding of java.io.DataInput/DataOutput ("modified UTF-8"):
//
//   U+0000                      -> 0xC0 0x80   (two bytes; plain UTF-8 uses one 0x00 byte)
//   U+0001 .. U+007F            -> 1 byte 0xxxxxxx
//   U+0080 .. U+07FF            -> 2 bytes 110xxxxx 10xxxxxx
//   U+0800 .. U+FFFF            -> 3 bytes 1110xxxx 10xxxxxx 10xxxxxx
//   U+10000+ (astral)           -> encoded as a surrogate pair, each surrogate in its
//                                  3-byte form (6 bytes total; plain UTF-8 uses 4 bytes)
//
// Differences from .NET's Encoding.UTF8: NUL is two bytes (0xC0 0x80) instead of one,
// astral characters are six bytes via surrogates instead of four, and no 4-byte
// sequences are ever produced. The decoder additionally accepts well-formed 4-byte
// UTF-8 sequences (mapping them to the corresponding astral char) for robustness
// against writers that emit plain UTF-8 — Java's DataInput.readUTF would reject those,
// but accepting them here matches what libnbt++/PrismLauncher effectively show the user.
// Lone surrogates encode/decode losslessly in 3-byte form (readUTF-compatible leniency;
// DataOutput.writeUTF would reject them on the Java side).

using System.Text;

namespace FlintLauncher.Nbt;

public static class ModifiedUtf8
{
    /// <summary>
    /// Encodes a .NET string as Java modified UTF-8 bytes
    /// (java.io.DataOutput.writeUTF without the 2-byte length prefix).
    /// </summary>
    public static byte[] Encode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var bytes = new byte[value.Length]; // lower bound; grows as needed
        var count = 0;

        void Emit(int b)
        {
            if (count == bytes.Length)
            {
                Array.Resize(ref bytes, bytes.Length * 2 + 8);
            }
            bytes[count++] = (byte)b;
        }

        foreach (var ch in value) // foreach over string iterates UTF-16 code units
        {
            if (ch == 0)
            {
                Emit(0xC0);
                Emit(0x80);
            }
            else if (ch <= 0x7F)
            {
                Emit(ch);
            }
            else if (ch <= 0x7FF)
            {
                Emit(0xC0 | (ch >> 6));
                Emit(0x80 | (ch & 0x3F));
            }
            else
            {
                // Surrogates (including lone ones) and all other BMP chars share the
                // 3-byte form; a proper surrogate pair therefore becomes two 3-byte
                // sequences, which is exactly what Java's writeUTF produces.
                Emit(0xE0 | (ch >> 12));
                Emit(0x80 | ((ch >> 6) & 0x3F));
                Emit(0x80 | (ch & 0x3F));
            }
        }
        return count == bytes.Length ? bytes : bytes[..count];
    }

    /// <summary>
    /// Decodes Java modified UTF-8 bytes into a .NET string
    /// (java.io.DataInput.readUTF without the 2-byte length prefix).
    /// Additionally accepts well-formed 4-byte UTF-8 sequences (see type docs).
    /// </summary>
    /// <exception cref="NbtParseException">On malformed or truncated sequences.</exception>
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length);
        var i = 0;
        while (i < bytes.Length)
        {
            var b0 = bytes[i];
            if (b0 < 0x80)
            {
                sb.Append((char)b0);
                i++;
            }
            else if ((b0 & 0xE0) == 0xC0)
            {
                if (i + 1 >= bytes.Length || (bytes[i + 1] & 0xC0) != 0x80)
                {
                    throw new NbtParseException($"Malformed 2-byte modified UTF-8 sequence at byte {i}");
                }
                sb.Append((char)(((b0 & 0x1F) << 6) | (bytes[i + 1] & 0x3F)));
                i += 2;
            }
            else if ((b0 & 0xF0) == 0xE0)
            {
                if (i + 2 >= bytes.Length || (bytes[i + 1] & 0xC0) != 0x80 || (bytes[i + 2] & 0xC0) != 0x80)
                {
                    throw new NbtParseException($"Malformed 3-byte modified UTF-8 sequence at byte {i}");
                }
                sb.Append((char)(((b0 & 0x0F) << 12) | ((bytes[i + 1] & 0x3F) << 6) | (bytes[i + 2] & 0x3F)));
                i += 3;
            }
            else if ((b0 & 0xF8) == 0xF0)
            {
                // Lenient extension: standard 4-byte UTF-8 (astral plane) sequence.
                if (i + 3 >= bytes.Length || (bytes[i + 1] & 0xC0) != 0x80
                    || (bytes[i + 2] & 0xC0) != 0x80 || (bytes[i + 3] & 0xC0) != 0x80)
                {
                    throw new NbtParseException($"Malformed 4-byte UTF-8 sequence at byte {i}");
                }
                var codePoint = ((b0 & 0x07) << 18) | ((bytes[i + 1] & 0x3F) << 12)
                    | ((bytes[i + 2] & 0x3F) << 6) | (bytes[i + 3] & 0x3F);
                if (codePoint < 0x10000 || codePoint > 0x10FFFF)
                {
                    throw new NbtParseException($"Invalid UTF-8 code point U+{codePoint:X} at byte {i}");
                }
                codePoint -= 0x10000;
                sb.Append((char)(0xD800 + (codePoint >> 10)));
                sb.Append((char)(0xDC00 + (codePoint & 0x3FF)));
                i += 4;
            }
            else
            {
                throw new NbtParseException($"Invalid modified UTF-8 lead byte 0x{b0:X2} at byte {i}");
            }
        }
        return sb.ToString();
    }
}
