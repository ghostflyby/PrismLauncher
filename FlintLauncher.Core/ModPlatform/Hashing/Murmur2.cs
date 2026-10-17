// SPDX-License-Identifier: GPL-3.0-only
// Port of libraries/murmur2/src/MurmurHash2.{h,cpp}: the modified, incrementally-usable
// MurmurHash2 variant (forced seed 1) that CurseForge fingerprints are based on.
//
// The C++ reader-based streaming loop is replaced by a whole-buffer loop with the exact
// same byte semantics:
//  - 'm'/'r' mixing constants and the "final call even when len == 0" behavior are kept,
//    so the output matches the canonical SMHasher MurmurHash2 with seed = 1 (verified
//    against a reference implementation for the vectors in Murmur2Tests).
//  - The optional filter drops bytes from the hashed stream before size counting (this is
//    what Hashing::hash(..., Murmur2) uses to skip tab/LF/CR/space).

namespace FlintLauncher.Core.ModPlatform.Hashing;

public static class Murmur2
{
    // 'm' and 'r' are mixing constants generated offline.
    private const uint M = 0x5bd1e995;
    private const int R = 24;

    /// <summary>MiB (the C++ header defines KiB/MiB; the default buffer is 4 MiB).</summary>
    public const int KiB = 1024;

    public const int MiB = 1024 * KiB;

    /// <summary>
    /// Port of Murmur2::hash: hash the whole stream, optionally skipping bytes for which
    /// <paramref name="filterOut"/> returns true (the filter is applied before size
    /// counting, exactly like the C++ two-pass reader loop).
    /// </summary>
    public static uint Hash(ReadOnlySpan<byte> data, FilterFunc? filterOut = null)
    {
        // We need the size without the filtered out characters before actually calculating
        // the hash, to setup the initial value for the hash.
        uint size = 0;
        foreach (var c in data)
        {
            if (!(filterOut ?? NullFilter)(c))
            {
                size += 1;
            }
        }

        // This forces a seed of 1.
        var h = 1u ^ size;
        var len = size;
        Span<byte> block = [0, 0, 0, 0];
        var index = 0;

        foreach (var c in data)
        {
            if ((filterOut ?? NullFilter)(c))
            {
                continue;
            }

            block[index] = c;
            index = (index + 1) % 4;

            // Mix 4 bytes at a time into the hash
            if (index == 0)
            {
                FourBytesMurmurHash2(block, ref h, ref len);
            }
        }

        // Do one last bit shuffle in the hash
        FourBytesMurmurHash2(block, ref h, ref len);

        return h;
    }

    /// <summary>Murmur2::hash over a file's full content.</summary>
    public static uint HashFile(string path, FilterFunc? filterOut = null)
    {
        return Hash(File.ReadAllBytes(path), filterOut);
    }

    /// <summary>Port of Murmur2::FourBytes_MurmurHash2 (little-endian block mixing).</summary>
    public static void FourBytesMurmurHash2(ReadOnlySpan<byte> data, ref uint h, ref uint len)
    {
        if (len >= 4)
        {
            // Not the final mix
            var k = (uint)(data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24));

            k *= M;
            k ^= k >> R;
            k *= M;

            h *= M;
            h ^= k;

            len -= 4;
        }
        else
        {
            // The final mix

            // Handle the last few bytes of the input array
            switch (len)
            {
                case 3:
                    h ^= (uint)data[2] << 16;
                    goto case 2;
                case 2:
                    h ^= (uint)data[1] << 8;
                    goto case 1;
                case 1:
                    h ^= data[0];
                    h *= M;
                    break;
            }

            // Do a few final mixes of the hash to ensure the last few
            // bytes are well-incorporated.
            h ^= h >> 13;
            h *= M;
            h ^= h >> 15;

            len = 0;
        }
    }

    /// <summary>bool(char) predicate over bytes (C++ std::function&lt;bool(char)&gt;).</summary>
    public delegate bool FilterFunc(byte c);

    private static bool NullFilter(byte _) => false;
}
