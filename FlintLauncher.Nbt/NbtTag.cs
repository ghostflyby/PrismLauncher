// SPDX-License-Identifier: GPL-3.0-only
// Port of libraries/libnbtplusplus/include/tag.h (class tag) and crtp_tag.h.

namespace FlintLauncher.Nbt;

/// <summary>Base class of all NBT tags. Corresponds to libnbt++'s nbt::tag.</summary>
public abstract class NbtTag
{
    /// <summary>The tag type (nbt::tag::get_type).</summary>
    public abstract NbtTagType Type { get; }

    /// <summary>Polymorphic deep copy (nbt::tag::clone).</summary>
    public abstract NbtTag Clone();
}
