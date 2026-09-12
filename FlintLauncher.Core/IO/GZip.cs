// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/GZip.{h,cpp}.
// NOTE: the C++ GZip module has no maxDecompressedSize guard and no "DecompressMaybe"
// variant in this revision of PrismLauncher — the module only provides unzip/zip and
// readGzFileByBlocks, which are ported here 1:1.
using System.IO.Compression;

namespace FlintLauncher.Core.IO;

public static class GZip
{
    /// <summary>
    /// Port of GZip::unzip: decompress a gzip stream. Empty input echoes through unchanged.
    /// Returns false when the data is not a valid/complete gzip stream (zlib inflate error).
    /// </summary>
    public static bool Decompress(byte[] compressedBytes, out byte[] uncompressedBytes)
    {
        if (compressedBytes.Length == 0)
        {
            uncompressedBytes = compressedBytes;
            return true;
        }

        try
        {
            using var source = new MemoryStream(compressedBytes, writable: false);
            using var gzip = new GZipStream(source, CompressionMode.Decompress);
            using var output = new MemoryStream(compressedBytes.Length);
            // The C++ implementation loops inflate() until Z_STREAM_END; a truncated or
            // corrupt stream surfaces as InvalidDataException here.
            gzip.CopyTo(output, 64 * 1024);
            uncompressedBytes = output.ToArray();
            return true;
        }
        catch (InvalidDataException)
        {
            uncompressedBytes = [];
            return false;
        }
        catch (IOException)
        {
            uncompressedBytes = [];
            return false;
        }
    }

    /// <summary>
    /// Port of GZip::zip: compress into a gzip stream (zlib windowBits 16+MAX_WBITS equivalent).
    /// Empty input echoes through unchanged. Returns false on failure.
    /// </summary>
    public static bool Compress(byte[] uncompressedBytes, out byte[] compressedBytes)
    {
        if (uncompressedBytes.Length == 0)
        {
            compressedBytes = uncompressedBytes;
            return true;
        }

        try
        {
            using var output = new MemoryStream(uncompressedBytes.Length / 2 + 64);
            // Z_DEFAULT_COMPRESSION maps to Optimal.
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                gzip.Write(uncompressedBytes, 0, uncompressedBytes.Length);
            }
            compressedBytes = output.ToArray();
            return true;
        }
        catch (IOException)
        {
            compressedBytes = [];
            return false;
        }
    }

    /// <summary>
    /// Port of GZip::readGzFileByBlocks: decompress a gzip file stream in 16 KiB blocks,
    /// invoking <paramref name="handleBlock"/> for each block. Returning false from the
    /// handler stops the decompression early (treated as success, like the C++ Z_OK path).
    /// Returns null on success, or an error description mirroring the C++ zerr() strings.
    /// </summary>
    public static string? ReadGzFileByBlocks(Stream source, Func<byte[], bool> handleBlock)
    {
        const int chunk = 16384;
        try
        {
            using var gzip = new GZipStream(source, CompressionMode.Decompress, leaveOpen: true);
            var buffer = new byte[chunk];
            while (true)
            {
                var read = 0;
                while (read < chunk)
                {
                    var n = gzip.Read(buffer, read, chunk - read);
                    if (n == 0)
                    {
                        break;
                    }
                    read += n;
                }
                if (read == 0)
                {
                    return null; // Z_STREAM_END
                }
                if (read < chunk)
                {
                    // The inner loop only exits early at end of stream, so this is the
                    // final (partial) block.
                    var tail = new byte[read];
                    Array.Copy(buffer, tail, read);
                    handleBlock(tail);
                    return null;
                }
                if (!handleBlock(buffer))
                {
                    return null; // early stop, Z_OK
                }
            }
        }
        catch (InvalidDataException)
        {
            return "invalid or incomplete deflate data"; // Z_DATA_ERROR
        }
        catch (IOException)
        {
            return "error handling file"; // Z_ERRNO
        }
    }
}
