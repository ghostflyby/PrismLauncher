// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2013, 2015 ljfa-ag
//
// Port of libraries/libnbtplusplus/include/tag.h (class tag) and crtp_tag.h.
// libnbt++ is LGPL-3.0-or-later (see COPYING.md); this port is conveyed under GPL-3.0-only per LGPL-3.0 section 2.

namespace FlintLauncher.Nbt;

/// <summary>Base class of all NBT tags. Corresponds to libnbt++'s nbt::tag.</summary>
public abstract class NbtTag
{
    /// <summary>The tag type (nbt::tag::get_type).</summary>
    public abstract NbtTagType Type { get; }

    /// <summary>Polymorphic deep copy (nbt::tag::clone).</summary>
    public abstract NbtTag Clone();
}
