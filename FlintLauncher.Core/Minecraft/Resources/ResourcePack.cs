// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/mod/ResourcePack.{h,cpp}: a DataPack whose pack_format maps
// through the resource pack format table.

using FlintLauncher.Core.Versioning;

namespace FlintLauncher.Core.Minecraft.Resources;

/// <summary>Port of the ResourcePack class.</summary>
public class ResourcePack : DataPack
{
    public ResourcePack(string filePath) : this(new FileInfo(filePath)) { }

    public ResourcePack(FileInfo fileInfo) : base(fileInfo) { }

    /// <summary>Port of ResourcePack::mappings: the pack_format → version range table.</summary>
    protected override IReadOnlyList<PackFormatRange> Mappings() => ResourcePackFormatVersions;

    // Values taken from:
    // https://minecraft.wiki/w/Pack_format#List_of_resource_pack_formats
    private static readonly PackFormatRange[] ResourcePackFormatVersions =
    [
        new((1, 0), new McVersion("1.6.1"), new McVersion("1.8.9")),
        new((2, 0), new McVersion("1.9"), new McVersion("1.10.2")),
        new((3, 0), new McVersion("1.11"), new McVersion("1.12.2")),
        new((4, 0), new McVersion("1.13"), new McVersion("1.14.4")),
        new((5, 0), new McVersion("1.15"), new McVersion("1.16.1")),
        new((6, 0), new McVersion("1.16.2"), new McVersion("1.16.5")),
        new((7, 0), new McVersion("1.17"), new McVersion("1.17.1")),
        new((8, 0), new McVersion("1.18"), new McVersion("1.18.2")),
        new((9, 0), new McVersion("1.19"), new McVersion("1.19.2")),
        new((11, 0), new McVersion("22w42a"), new McVersion("22w44a")),
        new((12, 0), new McVersion("1.19.3"), new McVersion("1.19.3")),
        new((13, 0), new McVersion("1.19.4"), new McVersion("1.19.4")),
        new((14, 0), new McVersion("23w14a"), new McVersion("23w16a")),
        new((15, 0), new McVersion("1.20"), new McVersion("1.20.1")),
        new((16, 0), new McVersion("23w31a"), new McVersion("23w31a")),
        new((17, 0), new McVersion("23w32a"), new McVersion("1.20.2-pre1")),
        new((18, 0), new McVersion("1.20.2"), new McVersion("1.20.2")),
        new((19, 0), new McVersion("23w42a"), new McVersion("23w42a")),
        new((20, 0), new McVersion("23w43a"), new McVersion("23w44a")),
        new((21, 0), new McVersion("23w45a"), new McVersion("23w46a")),
        new((22, 0), new McVersion("1.20.3"), new McVersion("1.20.4")),
        new((24, 0), new McVersion("24w03a"), new McVersion("24w04a")),
        new((25, 0), new McVersion("24w05a"), new McVersion("24w05b")),
        new((26, 0), new McVersion("24w06a"), new McVersion("24w07a")),
        new((28, 0), new McVersion("24w09a"), new McVersion("24w10a")),
        new((29, 0), new McVersion("24w11a"), new McVersion("24w11a")),
        new((30, 0), new McVersion("24w12a"), new McVersion("24w12a")),
        new((31, 0), new McVersion("24w13a"), new McVersion("1.20.5-pre3")),
        new((32, 0), new McVersion("1.20.5"), new McVersion("1.20.6")),
        new((33, 0), new McVersion("24w18a"), new McVersion("24w20a")),
        new((34, 0), new McVersion("1.21"), new McVersion("1.21.1")),
        new((35, 0), new McVersion("24w33a"), new McVersion("24w33a")),
        new((36, 0), new McVersion("24w34a"), new McVersion("24w35a")),
        new((37, 0), new McVersion("24w36a"), new McVersion("24w36a")),
        new((38, 0), new McVersion("24w37a"), new McVersion("24w37a")),
        new((39, 0), new McVersion("24w38a"), new McVersion("24w39a")),
        new((40, 0), new McVersion("24w40a"), new McVersion("24w40a")),
        new((41, 0), new McVersion("1.21.2-pre1"), new McVersion("1.21.2-pre2")),
        new((42, 0), new McVersion("1.21.2"), new McVersion("1.21.3")),
        new((43, 0), new McVersion("24w44a"), new McVersion("24w44a")),
        new((44, 0), new McVersion("24w45a"), new McVersion("24w45a")),
        new((45, 0), new McVersion("24w46a"), new McVersion("24w46a")),
        new((46, 0), new McVersion("1.21.4"), new McVersion("1.21.4")),
        new((47, 0), new McVersion("25w02a"), new McVersion("25w02a")),
        new((48, 0), new McVersion("25w03a"), new McVersion("25w03a")),
        new((49, 0), new McVersion("25w04a"), new McVersion("25w04a")),
        new((50, 0), new McVersion("25w05a"), new McVersion("25w05a")),
        new((51, 0), new McVersion("25w06a"), new McVersion("25w06a")),
        new((52, 0), new McVersion("25w07a"), new McVersion("25w07a")),
        new((53, 0), new McVersion("25w08a"), new McVersion("25w09b")),
        new((54, 0), new McVersion("25w10a"), new McVersion("25w10a")),
        new((55, 0), new McVersion("1.21.5"), new McVersion("1.21.5")),
        new((56, 0), new McVersion("25w15a"), new McVersion("25w15a")),
        new((57, 0), new McVersion("25w16a"), new McVersion("25w16a")),
        new((58, 0), new McVersion("25w17a"), new McVersion("25w17a")),
        new((59, 0), new McVersion("25w18a"), new McVersion("25w18a")),
        new((60, 0), new McVersion("25w19a"), new McVersion("25w19a")),
        new((61, 0), new McVersion("25w20a"), new McVersion("25w20a")),
        new((62, 0), new McVersion("25w21a"), new McVersion("25w21a")),
        new((63, 0), new McVersion("1.21.6"), new McVersion("1.21.6")),
        new((64, 0), new McVersion("1.21.7"), new McVersion("1.21.8")),
        new((65, 0), new McVersion("25w31a"), new McVersion("25w31a")),
        new((65, 1), new McVersion("25w32a"), new McVersion("25w32a")),
        new((65, 2), new McVersion("25w33a"), new McVersion("25w33a")),
        new((66, 0), new McVersion("25w34a"), new McVersion("25w34b")),
        new((67, 0), new McVersion("25w35a"), new McVersion("25w35a")),
        new((68, 0), new McVersion("25w36a"), new McVersion("25w36b")),
        new((69, 0), new McVersion("1.21.9"), new McVersion("1.21.10")),
        new((70, 0), new McVersion("25w41a"), new McVersion("25w41a")),
        new((70, 1), new McVersion("25w42a"), new McVersion("25w42a")),
        new((71, 0), new McVersion("25w43a"), new McVersion("25w43a")),
        new((72, 0), new McVersion("25w44a"), new McVersion("25w44a")),
        new((73, 0), new McVersion("25w45a"), new McVersion("25w45a")),
        new((74, 0), new McVersion("25w46a"), new McVersion("25w46a")),
        new((75, 0), new McVersion("1.21.11"), new McVersion("1.21.11")),
        new((76, 0), new McVersion("26.1-snap1"), new McVersion("26.1-snap1")),
        new((77, 0), new McVersion("26.1-snap2"), new McVersion("26.1-snap2")),
        new((78, 0), new McVersion("26.1-snap3"), new McVersion("26.1-snap3")),
        new((78, 1), new McVersion("26.1-snap4"), new McVersion("26.1-snap4")),
        new((79, 0), new McVersion("26.1-snap5"), new McVersion("26.1-snap5")),
        new((80, 0), new McVersion("26.1-snap6"), new McVersion("26.1-snap6")),
        new((81, 0), new McVersion("26.1-snap7"), new McVersion("26.1-snap7")),
        new((81, 1), new McVersion("26.1-snap8"), new McVersion("26.1-snap9")),
        new((82, 0), new McVersion("26.1-snap10"), new McVersion("26.1-snap10")),
        new((83, 0), new McVersion("26.1-snap11"), new McVersion("26.1-snap11")),
    ];
}
