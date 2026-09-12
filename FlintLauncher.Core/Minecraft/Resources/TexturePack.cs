// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/mod/TexturePack.{h,cpp}.
//
// Deviation: the QPixmapCache image plumbing is replaced by the PngImage header info;
// `valid()` (C++ `m_description != nullptr`, i.e. the description was set at least once)
// is modeled with a nullable backing field.
namespace FlintLauncher.Core.Minecraft.Resources;

/// <summary>Port of the TexturePack class (the pre-pack.mcmeta texture pack model).</summary>
public class TexturePack : Resource
{
    /** The texture pack's description, as defined in the pack.txt file (null until set —
     *  the C++ null-QString check in valid()). */
    private string? _description;

    /* The pack image header info (port of the pixmap cache slot). */
    private PngImage _image;

    public TexturePack(string filePath) : this(new FileInfo(filePath)) { }

    public TexturePack(FileInfo fileInfo) : base(fileInfo) { }

    /// <summary>Gets the description of the texture pack.</summary>
    public string Description => _description ?? "";

    /// <summary>The pack image header info, when a valid pack.png has been processed.</summary>
    public PngImage Image => _image;

    public bool HasImage => !_image.IsNull;

    public void SetDescription(string newDescription)
    {
        _description = newDescription;
    }

    public void SetImage(PngImage newImage)
    {
        _image = newImage;
    }

    /// <summary>Port of TexturePack::valid (`m_description != nullptr`).</summary>
    public override bool Valid => _description != null;
}
