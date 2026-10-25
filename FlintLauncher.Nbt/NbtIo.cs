// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2013, 2015 ljfa-ag
// SPDX-FileCopyrightText: 2022 Sefa Eyeoglu <contact@scrumplex.net>
// SPDX-FileCopyrightText: 2022 TheKodeToad <TheKodeToad@proton.me>
// SPDX-FileCopyrightText: 2022 Jamie Mansfield <jmansfield@cadixdev.org>
// SPDX-FileCopyrightText: 2023 TheKodeToad <TheKodeToad@proton.me>
//
// This file incorporates work covered by the following copyright and
// permission notice:
//
//     Copyright 2013-2021 MultiMC Contributors
//
//     Licensed under the Apache License, Version 2.0 (the "License");
//     you may not use this file except in compliance with the License.
//     You may obtain a copy of the License at
//
//         http://www.apache.org/licenses/LICENSE-2.0
//
//     Unless required by applicable law or agreed to in writing, software
//     distributed under the License is distributed on an "AS IS" BASIS,
//     WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//     See the License for the specific language governing permissions and
//     limitations under the License.
//
// Port of libnbt++'s nbt::io entry points (include/io/stream_reader.h: read_compound /
// read_tag; include/io/stream_writer.h: write_tag) plus the gzip handling the launcher
// does around them (launcher/minecraft/World.cpp parseLevelDat/serializeLevelDat + GZip;
// launcher/ui/pages/instance/ServersPage.cpp reads/writes raw).
// libnbt++ is LGPL-3.0-or-later (see COPYING.md); this port is conveyed under GPL-3.0-only per LGPL-3.0 section 2.
//
// Compression model: level.dat is gzipped while servers.dat is raw NBT, so reads
// auto-detect the gzip magic (0x1F 0x8B) at the start of the stream and writes select
// the mode explicitly. All failure modes (truncated stream, corrupt gzip, malformed
// NBT, over-deep nesting) surface as NbtParseException — reads never block indefinitely
// because every read is bounded by the stream's end.

using System.IO.Compression;

namespace FlintLauncher.Nbt;

public static class NbtIo
{
    /// <summary>Reads a named root compound from raw or gzip-compressed NBT data.</summary>
    public static (string Name, NbtCompound Root) ReadCompound(Stream input, bool detectCompression = true)
    {
        var stream = OpenInput(input, detectCompression, out var toDispose);
        try
        {
            return new NbtReader(stream).ReadCompound();
        }
        finally
        {
            toDispose?.Dispose();
        }
    }

    /// <summary>Reads any named root tag from raw or gzip-compressed NBT data.</summary>
    public static (string Name, NbtTag Tag) ReadTag(Stream input, bool detectCompression = true)
    {
        var stream = OpenInput(input, detectCompression, out var toDispose);
        try
        {
            return new NbtReader(stream).ReadTag();
        }
        finally
        {
            toDispose?.Dispose();
        }
    }

    /// <summary>
    /// Writes a named tag. Pass <paramref name="gzip"/> to compress (level.dat style);
    /// the default writes raw NBT (servers.dat style).
    /// </summary>
    public static void WriteTag(string name, NbtTag tag, Stream output, bool gzip = false)
    {
        if (gzip)
        {
            using var compressor = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true);
            new NbtWriter(compressor).WriteTag(name, tag);
        }
        else
        {
            new NbtWriter(output).WriteTag(name, tag);
        }
    }

    /// <summary>Serializes a named tag to bytes (raw or gzip-compressed).</summary>
    public static byte[] WriteTagToBytes(string name, NbtTag tag, bool gzip = false)
    {
        using var output = new MemoryStream();
        WriteTag(name, tag, output, gzip);
        return output.ToArray();
    }

    /// <summary>
    /// Parses a root compound from a byte blob (gzip or raw), returning null on any
    /// failure. Port of the parseLevelDat/parseServersDat pattern of catching
    /// nbt::io::input_error and yielding nullptr.
    /// </summary>
    public static NbtCompound? TryReadCompound(byte[] data)
    {
        try
        {
            using var input = new MemoryStream(data, writable: false);
            return ReadCompound(input).Root;
        }
        catch (NbtParseException)
        {
            return null;
        }
    }

    /// <summary>Serializes a root compound with an empty name, gzip-compressed (level.dat payload form).</summary>
    public static byte[] WriteCompoundGzip(NbtCompound root)
    {
        return WriteTagToBytes("", root, gzip: true);
    }

    /// <summary>Serializes a root compound with an empty name, raw (servers.dat payload form).</summary>
    public static byte[] WriteCompoundRaw(NbtCompound root)
    {
        return WriteTagToBytes("", root, gzip: false);
    }

    /// <summary>
    /// Peeks the stream head and, when auto-detection is on and the gzip magic matches,
    /// returns a decompressing wrapper. Otherwise returns the stream (with the peeked
    /// bytes pushed back, so unseekable streams work too).
    /// </summary>
    private static Stream OpenInput(Stream input, bool detectCompression, out IDisposable? toDispose)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        toDispose = null;
        if (!detectCompression)
        {
            return input;
        }

        var header = new byte[2];
        var read = 0;
        while (read < header.Length)
        {
            var n = input.Read(header, read, header.Length - read);
            if (n <= 0)
            {
                break;
            }

            read += n;
        }

        var isGzip = read == 2 && header[0] == 0x1F && header[1] == 0x8B;
        if (isGzip)
        {
            var decompressor = new GZipStream(new PushbackStream(input, [0x1F, 0x8B]), CompressionMode.Decompress,
                leaveOpen: true);
            toDispose = decompressor;
            return decompressor;
        }

        return read > 0 ? new PushbackStream(input, header[..read]) : input;
    }

    /// <summary>Stream wrapper that first serves a fixed prefix before the inner stream.</summary>
    private sealed class PushbackStream(Stream inner, byte[] prefix) : Stream
    {
        private int _prefixPosition;

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBuffer(buffer, offset, count);
            var served = 0;
            while (_prefixPosition < prefix.Length && count > 0)
            {
                buffer[offset + served] = prefix[_prefixPosition++];
                served++;
                count--;
            }

            if (count > 0)
            {
                served += inner.Read(buffer, offset + served, count);
            }

            return served;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private static void ValidateBuffer(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (offset + count > buffer.Length)
            {
                throw new ArgumentException("Offset and length were out of bounds for the array");
            }
        }
    }
}
