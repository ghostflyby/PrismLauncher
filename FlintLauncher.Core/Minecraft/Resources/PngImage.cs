// SPDX-License-Identifier: GPL-3.0-only
// Stand-in for the QImage::fromData validation the C++ parse tasks perform on pack.png.
// Qt decodes the bytes with an image codec; this port reads the PNG structure directly
// (signature + IHDR chunk) to validate the data and extract the dimensions, with no
// native/image-codec dependency.
//
// Deviation: only PNG data is recognized as a valid pack image, while QImage::fromData
// would also decode other formats (pack.png is PNG by Minecraft convention, so all real
// inputs are covered).
namespace FlintLauncher.Core.Minecraft.Resources;

/// <summary>The parsed header info of a pack image (PNG).</summary>
public readonly record struct PngImage(int Width, int Height)
{
    public bool IsNull => Width == 0 || Height == 0;
}

public static class PngImageReader
{
    /// <summary>89 50 4E 47 0D 0A 1A 0A.</summary>
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // offset 8: chunk length (4), offset 12: "IHDR" (4), offset 16: width (4), offset 20: height (4)
    private const int WidthOffset = 16;
    private const int HeaderLength = 24;

    /// <summary>
    /// Validates raw image bytes as a PNG and reads the dimensions from the IHDR chunk.
    /// Returns false when the data is not a PNG (the QImage::fromData null-image case).
    /// </summary>
    public static bool TryReadPng(ReadOnlySpan<byte> data, out PngImage image)
    {
        image = default;
        if (data.Length < HeaderLength || !data[..Signature.Length].SequenceEqual(Signature))
        {
            return false;
        }
        if (data[12] != (byte)'I' || data[13] != (byte)'H' || data[14] != (byte)'D' || data[15] != (byte)'R')
        {
            return false;
        }

        var width = ReadBigEndianUInt32(data[WidthOffset..]);
        var height = ReadBigEndianUInt32(data[(WidthOffset + 4)..]);
        if (width is 0 or > int.MaxValue || height is 0 or > int.MaxValue)
        {
            return false;
        }

        image = new PngImage((int)width, (int)height);
        return true;
    }

    private static uint ReadBigEndianUInt32(ReadOnlySpan<byte> data)
    {
        return ((uint)data[0] << 24) | ((uint)data[1] << 16) | ((uint)data[2] << 8) | data[3];
    }
}
