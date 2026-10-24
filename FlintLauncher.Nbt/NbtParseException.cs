// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2013, 2015 ljfa-ag
//
// Port of libnbt++'s nbt::io::input_error (include/io/stream_reader.h): thrown when
// reading/decoding NBT data fails — malformed structures, truncated input, invalid tag
// types, malformed Modified UTF-8, or over-deep nesting.
// libnbt++ is LGPL-3.0-or-later (see COPYING.md); this port is conveyed under GPL-3.0-only per LGPL-3.0 section 2.

namespace FlintLauncher.Nbt;

/// <summary>Raised when NBT input cannot be parsed. Corresponds to nbt::io::input_error.</summary>
public sealed class NbtParseException(string message, Exception? innerException = null) : Exception(message, innerException)
{
}
