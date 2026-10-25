// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022 Rachel Powers <508861+Ryex@users.noreply.github.com>
//
// Port of launcher/minecraft/mod/ShaderPack.{h,cpp}.
//
/* Info:
 * Currently For Optifine / Iris shader packs,
 * could be expanded to support others should they exist?
 *
 * This class and enum are mostly here as placeholders for validating
 * that a shaderpack exists and is in the right format,
 * namely that they contain a folder named 'shaders'.
 */

namespace FlintLauncher.Core.Minecraft.Resources;

/// <summary>Port of ShaderPackFormat.</summary>
public enum ShaderPackFormat
{
    Valid,
    Invalid,
}

/// <summary>Port of the ShaderPack class.</summary>
public class ShaderPack : Resource
{
    private ShaderPackFormat _packFormat = ShaderPackFormat.Invalid;

    public ShaderPack(string filePath) : this(new FileInfo(filePath)) { }

    public ShaderPack(FileInfo fileInfo) : base(fileInfo) { }

    public ShaderPackFormat PackFormat { get => _packFormat; set => _packFormat = value; }


    public override bool Valid => _packFormat != ShaderPackFormat.Invalid;
}
