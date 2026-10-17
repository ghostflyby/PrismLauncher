// SPDX-License-Identifier: GPL-3.0-only
// Port of libraries/libnbtplusplus/include/tag.h (tag_type) and tagfwd.h.

namespace FlintLauncher.Nbt;

/// <summary>
/// Port of libnbt++'s nbt::tag_type: the tag type values used in the binary format.
/// </summary>
/// <remarks>
/// <see cref="Null"/> (= -1) never appears on the wire. Like libnbt++ it is used only as
/// the "element type" of an empty <see cref="NbtList"/> whose content type is not yet
/// determined; such a list is serialized with element type <see cref="End"/>.
/// </remarks>
public enum NbtTagType : sbyte
{
    End = 0,
    Byte = 1,
    Short = 2,
    Int = 3,
    Long = 4,
    Float = 5,
    Double = 6,
    ByteArray = 7,
    String = 8,
    List = 9,
    Compound = 10,
    IntArray = 11,
    LongArray = 12,

    /// <summary>Sentinel for "empty/undetermined" (libnbt++ tag_type::Null); never serialized as a payload type.</summary>
    Null = -1,
}

public static class NbtTagTypes
{
    /// <summary>Port of nbt::is_valid_type: whether the given byte is a valid tag type value.</summary>
    public static bool IsValidType(int type, bool allowEnd = false)
    {
        if (type == 0)
        {
            return allowEnd;
        }

        return type >= (int)NbtTagType.Byte && type <= (int)NbtTagType.LongArray;
    }
}
