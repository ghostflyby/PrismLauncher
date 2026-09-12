// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/mod/DataPack.{h,cpp}.
//
// Deviations from the C++:
//  - The pack image (QImage/QPixmap + QPixmapCache) is replaced by the PngImage header
//    info (see PngImage.cs); the mutexes are dropped (synchronous parse).
//  - Version maps to FlintLauncher.Core.Versioning.McVersion.
using FlintLauncher.Core.Versioning;

namespace FlintLauncher.Core.Minecraft.Resources;

/// <summary>Port of the DataPack class: a Resource carrying the pack.mcmeta basic info.</summary>
public class DataPack : Resource
{
    /* The 'version' of a data pack, as defined in the pack.mcmeta file.
     * See https://minecraft.wiki/w/Data_pack#pack.mcmeta
     */
    private int _packFormat;
    private (int Major, int Minor) _minFormat;
    private (int Major, int Minor) _maxFormat;

    /** The data pack's description, as defined in the pack.mcmeta file. */
    private string _description = "";

    /* The pack image header info (port of the pixmap cache slot). */
    private PngImage _image;

    public DataPack(string filePath) : this(new FileInfo(filePath)) { }

    public DataPack(FileInfo fileInfo) : base(fileInfo) { }

    /// <summary>Gets the numerical ID of the pack format.</summary>
    public int PackFormat => _packFormat;

    /// <summary>Gets the description of the data pack.</summary>
    public string Description => _description;

    /// <summary>The pack image header info, when a valid pack.png has been processed.</summary>
    public PngImage Image => _image;

    public bool HasImage => !_image.IsNull;

    /// <summary>Thread-safe in the C++; the parse here is synchronous.</summary>
    public void SetPackFormat(int newFormatId, (int Major, int Minor) minFormat, (int Major, int Minor) maxFormat)
    {
        _packFormat = newFormatId;
        _minFormat = minFormat;
        _maxFormat = maxFormat;
    }

    public void SetDescription(string newDescription)
    {
        _description = newDescription;
    }

    public void SetImage(PngImage newImage)
    {
        _image = newImage;
    }

    public override bool Valid => _packFormat != 0 || (_minFormat.Major != 0 && _maxFormat.Major != 0);

    public override int Compare(Resource other, SortType type = SortType.Name)
    {
        var castOther = (DataPack)other;
        if (type == SortType.PackFormat)
        {
            var thisVer = PackFormat;
            var otherVer = castOther.PackFormat;

            if (thisVer > otherVer)
            {
                return 1;
            }
            if (thisVer < otherVer)
            {
                return -1;
            }
        }
        else
        {
            return base.Compare(other, type);
        }
        return 0;
    }

    public override bool ApplyFilter(System.Text.RegularExpressions.Regex filter)
    {
        if (filter.IsMatch(Description))
        {
            return true;
        }

        if (filter.IsMatch(_packFormat.ToString()))
        {
            return true;
        }

        var mappings = Mappings();
        var versions = new[] { Map((_packFormat, 0), mappings), Map(_minFormat, mappings), Map(_maxFormat, mappings) };
        foreach (var version in versions)
        {
            if (!version.Min.IsEmpty)
            {
                if (filter.IsMatch(version.Min.ToString()))
                {
                    return true;
                }
                if (filter.IsMatch(version.Max.ToString()))
                {
                    return true;
                }
            }
        }
        return base.ApplyFilter(filter);
    }

    /// <summary>Port of DataPack::packFormatStr (the display string of the format column).</summary>
    public string PackFormatStr()
    {
        var mappings = Mappings();
        if (_packFormat != 0)
        {
            var versionBounds = Map((_packFormat, 0), mappings);
            if (versionBounds.Min.ToString().Length == 0)
            {
                return _packFormat.ToString();
            }
            return $"{_packFormat} ({versionBounds.Min} - {versionBounds.Max})";
        }
        var minBound = Map(_minFormat, mappings);
        var maxBound = Map(_maxFormat, mappings);
        var minVersion = minBound.Min;
        var maxVersion = maxBound.Max;
        if (minVersion.IsEmpty || maxVersion.IsEmpty)
        {
            return "Unrecognized";
        }
        var str = "[" + _minFormat.Major.ToString();
        if (_minFormat.Minor != 0)
        {
            str += "." + _minFormat.Minor.ToString();
        }

        str += " - " + _maxFormat.Major.ToString();
        if (_maxFormat.Minor != 0)
        {
            str += "." + _maxFormat.Minor.ToString();
        }

        return str + $" ({minVersion} - {maxVersion})";
    }

    /// <summary>Port of DataPack::mappings (the pack_format → version range table).</summary>
    protected virtual IReadOnlyList<PackFormatRange> Mappings() => DataPackFormatVersions;

    /// <summary>Port of the static map(format, versions) helpers from DataPack.cpp.</summary>
    protected static (McVersion Min, McVersion Max) Map((int Major, int Minor) format,
        IReadOnlyList<PackFormatRange> versions)
    {
        if (format.Major == 0)
        {
            return (EmptyVersion, EmptyVersion);
        }
        foreach (var range in versions)
        {
            if (range.Format.Major == format.Major && range.Format.Minor == format.Minor)
            {
                return (range.Min, range.Max);
            }
        }
        return (EmptyVersion, EmptyVersion);
    }

    protected static readonly McVersion EmptyVersion = new("");

    /// <summary>One entry of the pack_format → supported version range tables.</summary>
    public readonly record struct PackFormatRange((int Major, int Minor) Format, McVersion Min, McVersion Max);

    // Values taken from:
    // https://minecraft.wiki/w/Pack_format#List_of_data_pack_formats
    private static readonly PackFormatRange[] DataPackFormatVersions =
    [
        new((4, 0), new McVersion("1.13"), new McVersion("1.14.4")),
        new((5, 0), new McVersion("1.15"), new McVersion("1.16.1")),
        new((6, 0), new McVersion("1.16.2"), new McVersion("1.16.5")),
        new((7, 0), new McVersion("1.17"), new McVersion("1.17.1")),
        new((8, 0), new McVersion("1.18"), new McVersion("1.18.1")),
        new((9, 0), new McVersion("1.18.2"), new McVersion("1.18.2")),
        new((10, 0), new McVersion("1.19"), new McVersion("1.19.3")),
        new((11, 0), new McVersion("23w03a"), new McVersion("23w05a")),
        new((12, 0), new McVersion("1.19.4"), new McVersion("1.19.4")),
        new((13, 0), new McVersion("23w12a"), new McVersion("23w14a")),
        new((14, 0), new McVersion("23w16a"), new McVersion("23w17a")),
        new((15, 0), new McVersion("1.20"), new McVersion("1.20.1")),
        new((16, 0), new McVersion("23w31a"), new McVersion("23w31a")),
        new((17, 0), new McVersion("23w32a"), new McVersion("23w35a")),
        new((18, 0), new McVersion("1.20.2"), new McVersion("1.20.2")),
        new((19, 0), new McVersion("23w40a"), new McVersion("23w40a")),
        new((20, 0), new McVersion("23w41a"), new McVersion("23w41a")),
        new((21, 0), new McVersion("23w42a"), new McVersion("23w42a")),
        new((22, 0), new McVersion("23w43a"), new McVersion("23w43b")),
        new((23, 0), new McVersion("23w44a"), new McVersion("23w44a")),
        new((24, 0), new McVersion("23w45a"), new McVersion("23w45a")),
        new((25, 0), new McVersion("23w46a"), new McVersion("23w46a")),
        new((26, 0), new McVersion("1.20.3"), new McVersion("1.20.4")),
        new((27, 0), new McVersion("23w51a"), new McVersion("23w51b")),
        new((28, 0), new McVersion("24w03a"), new McVersion("24w03b")),
        new((29, 0), new McVersion("24w04a"), new McVersion("24w04a")),
        new((30, 0), new McVersion("24w05a"), new McVersion("24w05b")),
        new((31, 0), new McVersion("24w06a"), new McVersion("24w06a")),
        new((32, 0), new McVersion("24w07a"), new McVersion("24w07a")),
        new((33, 0), new McVersion("24w09a"), new McVersion("24w09a")),
        new((34, 0), new McVersion("24w10a"), new McVersion("24w10a")),
        new((35, 0), new McVersion("24w11a"), new McVersion("24w11a")),
        new((36, 0), new McVersion("24w12a"), new McVersion("24w12a")),
        new((37, 0), new McVersion("24w13a"), new McVersion("24w13a")),
        new((38, 0), new McVersion("24w14a"), new McVersion("24w14a")),
        new((39, 0), new McVersion("1.20.5-pre1"), new McVersion("1.20.5-pre1")),
        new((40, 0), new McVersion("1.20.5-pre2"), new McVersion("1.20.5-pre2")),
        new((41, 0), new McVersion("1.20.5"), new McVersion("1.20.6")),
        new((42, 0), new McVersion("24w18a"), new McVersion("24w18a")),
        new((43, 0), new McVersion("24w19a"), new McVersion("24w19b")),
        new((44, 0), new McVersion("24w20a"), new McVersion("24w20a")),
        new((45, 0), new McVersion("24w21a"), new McVersion("24w21b")),
        new((46, 0), new McVersion("1.21-pre1"), new McVersion("1.21-pre1")),
        new((47, 0), new McVersion("1.21-pre2"), new McVersion("1.21-pre2")),
        new((48, 0), new McVersion("1.21"), new McVersion("1.21.1")),
        new((49, 0), new McVersion("24w33a"), new McVersion("24w33a")),
        new((50, 0), new McVersion("24w34a"), new McVersion("24w34a")),
        new((51, 0), new McVersion("24w35a"), new McVersion("24w35a")),
        new((52, 0), new McVersion("24w36a"), new McVersion("24w36a")),
        new((53, 0), new McVersion("24w37a"), new McVersion("24w37a")),
        new((54, 0), new McVersion("24w38a"), new McVersion("24w38a")),
        new((55, 0), new McVersion("24w39a"), new McVersion("24w39a")),
        new((56, 0), new McVersion("24w40a"), new McVersion("24w40a")),
        new((57, 0), new McVersion("1.21.2"), new McVersion("1.21.3")),
        new((58, 0), new McVersion("24w44a"), new McVersion("24w44a")),
        new((59, 0), new McVersion("24w45a"), new McVersion("24w45a")),
        new((60, 0), new McVersion("24w46a"), new McVersion("1.21.4-pre1")),
        new((61, 0), new McVersion("1.21.4"), new McVersion("1.21.4")),
        new((62, 0), new McVersion("25w02a"), new McVersion("25w02a")),
        new((63, 0), new McVersion("25w03a"), new McVersion("25w03a")),
        new((64, 0), new McVersion("25w04a"), new McVersion("25w04a")),
        new((65, 0), new McVersion("25w05a"), new McVersion("25w05a")),
        new((66, 0), new McVersion("25w06a"), new McVersion("25w06a")),
        new((67, 0), new McVersion("25w07a"), new McVersion("25w07a")),
        new((68, 0), new McVersion("25w08a"), new McVersion("25w08a")),
        new((69, 0), new McVersion("25w09a"), new McVersion("25w09b")),
        new((70, 0), new McVersion("25w10a"), new McVersion("1.21.5-pre1")),
        new((71, 0), new McVersion("1.21.5"), new McVersion("1.21.5")),
        new((72, 0), new McVersion("25w15a"), new McVersion("25w15a")),
        new((73, 0), new McVersion("25w16a"), new McVersion("25w16a")),
        new((74, 0), new McVersion("25w17a"), new McVersion("25w17a")),
        new((75, 0), new McVersion("25w18a"), new McVersion("25w18a")),
        new((76, 0), new McVersion("25w19a"), new McVersion("25w19a")),
        new((77, 0), new McVersion("25w20a"), new McVersion("25w20a")),
        new((78, 0), new McVersion("25w21a"), new McVersion("25w21a")),
        new((79, 0), new McVersion("1.21.6-pre1"), new McVersion("1.21.6-pre2")),
        new((80, 0), new McVersion("1.21.6"), new McVersion("1.21.6")),
        new((81, 0), new McVersion("1.21.7"), new McVersion("1.21.8")),
        new((82, 0), new McVersion("25w31a"), new McVersion("25w31a")),
        new((83, 0), new McVersion("25w32a"), new McVersion("25w32a")),
        new((83, 1), new McVersion("25w33a"), new McVersion("25w33a")),
        new((84, 0), new McVersion("25w34a"), new McVersion("25w34b")),
        new((85, 0), new McVersion("25w35a"), new McVersion("25w35a")),
        new((86, 0), new McVersion("25w36a"), new McVersion("25w36b")),
        new((87, 0), new McVersion("25w37a"), new McVersion("1.21.9-pre1")),
        new((87, 1), new McVersion("1.21.9-pre1"), new McVersion("1.21.9-pre1")),
        new((88, 0), new McVersion("1.21.9"), new McVersion("1.21.10")),
        new((89, 0), new McVersion("25w41a"), new McVersion("25w41a")),
        new((90, 0), new McVersion("25w42a"), new McVersion("25w42a")),
        new((91, 0), new McVersion("25w43a"), new McVersion("25w43a")),
        new((92, 0), new McVersion("25w44a"), new McVersion("25w44a")),
        new((93, 0), new McVersion("25w45a"), new McVersion("25w45a")),
        new((93, 1), new McVersion("25w46a"), new McVersion("25w46a")),
        new((94, 0), new McVersion("1.21.11-pre1"), new McVersion("1.21.11-pre3")),
        new((94, 1), new McVersion("1.21.11-pre4"), new McVersion("1.21.11")),
        new((95, 0), new McVersion("26.1-snap1"), new McVersion("26.1-snap1")),
        new((96, 0), new McVersion("26.1-snap2"), new McVersion("26.1-snap2")),
        new((97, 0), new McVersion("26.1-snap3"), new McVersion("26.1-snap3")),
        new((97, 1), new McVersion("26.1-snap4"), new McVersion("26.1-snap4")),
        new((98, 0), new McVersion("26.1-snap5"), new McVersion("26.1-snap5")),
        new((99, 0), new McVersion("26.1-snap6"), new McVersion("26.1-snap6")),
        new((99, 1), new McVersion("26.1-snap7"), new McVersion("26.1-snap7")),
        new((99, 2), new McVersion("26.1-snap8"), new McVersion("26.1-snap9")),
        new((99, 3), new McVersion("26.1-snap10"), new McVersion("26.1-snap10")),
        new((100, 0), new McVersion("26.1-snap11"), new McVersion("26.1-snap11")),
    ];
}
