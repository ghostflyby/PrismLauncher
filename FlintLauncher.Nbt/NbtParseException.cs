// SPDX-License-Identifier: GPL-3.0-only
// Port of libnbt++'s nbt::io::input_error (include/io/stream_reader.h): thrown when
// reading/decoding NBT data fails — malformed structures, truncated input, invalid tag
// types, malformed Modified UTF-8, or over-deep nesting.

namespace FlintLauncher.Nbt;

/// <summary>Raised when NBT input cannot be parsed. Corresponds to nbt::io::input_error.</summary>
public sealed class NbtParseException(string message, Exception? innerException = null) : Exception(message, innerException) { }
