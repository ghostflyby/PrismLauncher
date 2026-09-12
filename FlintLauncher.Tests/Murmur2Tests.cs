// SPDX-License-Identifier: GPL-3.0-only
// Tests for the murmur2 port (libraries/murmur2) and the HashUtils helpers.
//
// Vector provenance: libraries/murmur2 forces seed 1 and is byte-identical to the canonical
// SMHasher MurmurHash2 with seed = 1; the expected values below were verified against an
// independent reference implementation of that algorithm (empty input = 1540447798,
// "hello" = 2788266382, ...). The CurseForge fingerprint filter (drops tab/LF/CR/space)
// is covered by the "hello world" cases: filtered("hello world") == murmur2("helloworld").
using System.Security.Cryptography;
using FlintLauncher.Core.ModPlatform;
using FlintLauncher.Core.ModPlatform.Hashing;
using Xunit;

namespace Flint.Tests;

public class Murmur2Tests
{
    private static void AssertVector(byte[] data, uint expected, Murmur2.FilterFunc? filter = null)
    {
        Assert.Equal(expected, Murmur2.Hash(data, filter));
    }

    [Theory]
    [InlineData(1540447798u, "")]           // empty input: h = 1 ^ 0, final mix only
    [InlineData(626045324u, "a")]           // tail cases 1..4 bytes
    [InlineData(1692487918u, "ab")]
    [InlineData(1621425345u, "abc")]
    [InlineData(3376380438u, "abcd")]       // exact multiple of 4
    [InlineData(3469237630u, "abcde")]
    [InlineData(2788266382u, "hello")]
    [InlineData(2824650221u, "helloworld")]
    [InlineData(2213174766u, "hello world")]
    [InlineData(504383975u, "The quick brown fox jumps over the lazy dog")]
    public void Murmur2_MatchesCanonicalSeed1Vectors(uint expected, string input)
    {
        AssertVector(System.Text.Encoding.UTF8.GetBytes(input), expected);
    }

    [Fact]
    public void Murmur2_ByteRange()
    {
        var data = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        AssertVector(data, 253525554u);
    }

    [Fact]
    public void Murmur2_CurseForgeFilter_DropsWhitespace()
    {
        var cfFilter = (Murmur2.FilterFunc)(c => c is 9 or 10 or 13 or 32);

        // the CF fingerprint hashes the stream without tab/LF/CR/space
        AssertVector(System.Text.Encoding.UTF8.GetBytes("hello world"), 2824650221u, cfFilter);
        AssertVector(System.Text.Encoding.UTF8.GetBytes("The quick brown fox jumps over the lazy dog"),
            3751777527u, cfFilter);

        // whitespace-only input hashes like the empty input
        AssertVector(System.Text.Encoding.UTF8.GetBytes(" \t\r\n"), 1540447798u, cfFilter);

        // CRLF/line-ending differences vanish under the filter
        var lf = System.Text.Encoding.UTF8.GetBytes("line1\nline2");
        var crlf = System.Text.Encoding.UTF8.GetBytes("line1\r\nline2");
        Assert.Equal(Murmur2.Hash(lf, cfFilter), Murmur2.Hash(crlf, cfFilter));
    }

    [Fact]
    public void Murmur2_IncrementalBlocks_MatchOneShot()
    {
        // FourBytes_MurmurHash2 is exported for incremental use: mixing the stream through
        // the incremental state must reproduce the one-shot hash.
        var data = System.Text.Encoding.UTF8.GetBytes("The quick brown fox jumps over the lazy dog");
        var oneShot = Murmur2.Hash(data);

        var h = 1u ^ (uint)data.Length;
        var len = (uint)data.Length;
        Span<byte> block = [0, 0, 0, 0];
        var index = 0;
        foreach (var c in data)
        {
            block[index] = c;
            index = (index + 1) % 4;
            if (index == 0)
            {
                Murmur2.FourBytesMurmurHash2(block, ref h, ref len);
            }
        }
        Murmur2.FourBytesMurmurHash2(block, ref h, ref len);

        Assert.Equal(oneShot, h);
    }

    // ------------------------------------------------------------------ HashUtils

    [Fact]
    public void HashUtils_Murmur2_Algorithms()
    {
        var data = "hello world"u8.ToArray();

        // like the C++ Hashing::hash(..., Murmur2), the helper always applies the
        // CurseForge whitespace filter
        Murmur2.FilterFunc cf = c => c is 9 or 10 or 13 or 32;
        Assert.Equal(Murmur2.Hash(data, cf).ToString(), HashUtils.Hash(data, Algorithm.Murmur2));
        Assert.Equal("murmur2", HashUtils.AlgorithmToString(Algorithm.Murmur2));
        Assert.Equal(Algorithm.Murmur2, HashUtils.AlgorithmFromString("murmur2"));
        Assert.Equal(Algorithm.Unknown, HashUtils.AlgorithmFromString("nope"));
    }

    [Fact]
    public void HashUtils_Sha1_Sha512_MatchCryptoStack()
    {
        var data = "hash me please"u8.ToArray();

        Assert.Equal(Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant(),
            HashUtils.Hash(data, Algorithm.Sha1));
        Assert.Equal(Convert.ToHexString(SHA512.HashData(data)).ToLowerInvariant(),
            HashUtils.Hash(data, Algorithm.Sha512));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(),
            HashUtils.Hash(data, Algorithm.Sha256));
    }

    [Fact]
    public async Task HashUtils_HashFile_And_HasherTask_Async()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"flint-hash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "mod.jar");
        var data = "file content"u8.ToArray();
        File.WriteAllBytes(path, data);

        Murmur2.FilterFunc cf = c => c is 9 or 10 or 13 or 32;
        Assert.Equal(Murmur2.Hash(data, cf).ToString(), HashUtils.HashFile(path, Algorithm.Murmur2));

        var hasher = HashUtils.CreateHasher(path, "sha512");
        await hasher.StartAsync();
        Assert.True(hasher.WasSuccessful);
        Assert.Equal(HashUtils.Hash(data, Algorithm.Sha512), hasher.GetResult());

        // the provider-based factory maps FLAME to the CF murmur2 variant
        var cfHasher = HashUtils.CreateHasher(path, ResourceProvider.FLAME);
        await cfHasher.StartAsync();
        Assert.Equal(HashUtils.Hash(data, Algorithm.Murmur2), cfHasher.GetResult());

        Directory.Delete(dir, recursive: true);
    }
}
