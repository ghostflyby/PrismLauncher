// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2013, 2015 ljfa-ag
//
// Port of libraries/libnbtplusplus/include/tag_string.h.
// libnbt++ is LGPL-3.0-or-later (see COPYING.md); this port is conveyed under GPL-3.0-only per LGPL-3.0 section 2.
// Unlike the C++ original (which stores raw payload bytes), Value is a .NET string and
// conversion to/from the wire format goes through Java modified UTF-8 — see ModifiedUtf8.

namespace FlintLauncher.Nbt;

public sealed class NbtString(string value = "") : NbtTag
{
    public string Value { get; } = value;

    public override NbtTagType Type => NbtTagType.String;

    public override NbtTag Clone() => new NbtString(Value);

    public static implicit operator string(NbtString tag) => tag.Value;

    public static implicit operator NbtString(string value) => new(value);

    public override string ToString() => $"tag_string(\"{Value}\")";

    public override bool Equals(object? obj) => obj is NbtString other && Value == other.Value;

    public override int GetHashCode() => Value.GetHashCode();
}
