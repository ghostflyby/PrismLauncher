// SPDX-License-Identifier: GPL-3.0-only
// Port of tests/GZip_test.cpp (test_Through) plus focused round-trip cases.

using FlintLauncher.Core.IO;
using Xunit;

namespace FlintLauncher.Tests;

public class GZipTests
{
    private static void Fib(ref int prev, ref int cur)
    {
        var ret = prev + cur;
        prev = cur;
        cur = ret;
    }

    /// <summary>Port of GZipTest::test_Through: fibonacci-length random buffers up to 10 MB.</summary>
    [Fact]
    public void Test_Through()
    {
        // test up to 10 MB
        const int size = 10 * 1024 * 1024;
        var random = new byte[size];
        Random.Shared.NextBytes(random);

        // initialize fibonacci
        var prev = 1;
        var cur = 1;

        // test if fibonacci long random buffers pass through GZip
        do
        {
            var copy = random[..cur];
            Assert.True(GZip.Compress(copy, out var compressed));
            Assert.True(GZip.Decompress(compressed, out var decompressed));
            Assert.Equal(copy, decompressed);
            Fib(ref prev, ref cur);
        } while (cur < size);
    }

    [Fact]
    public void Empty_Buffer_Echoes_Through()
    {
        var empty = Array.Empty<byte>();
        Assert.True(GZip.Compress(empty, out var compressed));
        Assert.Empty(compressed);
        Assert.True(GZip.Decompress(empty, out var decompressed));
        Assert.Empty(decompressed);
    }

    [Fact]
    public void Invalid_Data_Fails()
    {
        var garbage = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        Assert.False(GZip.Decompress(garbage, out _));
    }

    [Fact]
    public void ReadGzFileByBlocks_RoundTrip()
    {
        var data = new byte[50_000];
        Random.Shared.NextBytes(data);
        Assert.True(GZip.Compress(data, out var compressed));

        using var source = new MemoryStream(compressed, writable: false);
        using var output = new MemoryStream();
        var error = GZip.ReadGzFileByBlocks(source, block =>
        {
            output.Write(block, 0, block.Length);
            return true;
        });
        Assert.Null(error); // empty string/null means success (Z_OK)
        Assert.Equal(data, output.ToArray());
    }

    [Fact]
    public void ReadGzFileByBlocks_EarlyStop_Is_Success()
    {
        var data = new byte[100_000];
        Random.Shared.NextBytes(data);
        Assert.True(GZip.Compress(data, out var compressed));

        using var source = new MemoryStream(compressed, writable: false);
        var blocks = 0;
        var error = GZip.ReadGzFileByBlocks(source, _ =>
        {
            blocks++;
            // C++ inf(): a handler returning false stops the decompression early (Z_OK)
            return false;
        });
        Assert.Null(error); // early stop is still a success
        Assert.Equal(1, blocks);
    }
}
