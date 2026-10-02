// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/mod/tasks/LocalDataPackParseTask.{h,cpp} (the DataPackUtils
// namespace) together with the LocalDataPackParseTask-adjacent parsing used by resource
// packs. This file absorbs the P2a partial port that lived in
// FlintLauncher.Core/Minecraft/DataPackUtils.cs (the processComponent JSON chat component
// renderer); it has been moved here to complete the resource parsing layer.
//
// Deviations from the C++:
//  - LocalDataPackParseTask (the Task/abort plumbing) is not ported; callers wrap the
//    synchronous Process* calls in a task themselves.
//  - Zip inspection uses System.IO.Compression (like MMCZip.cs) instead of libarchive;
//    the ArchiveReader::parse callback loop is mirrored with a plain entry iteration.
//  - pack.png validation reads the PNG header (PngImage.cs) where the C++ decodes any
//    QImage-supported format.
//  - QJsonValue::isDouble()/QString::number(double) formats numbers like Qt's 'g' with 6
//    significant digits (lowercase exponent); QtJson.NumberToString mirrors that.
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Json;

namespace FlintLauncher.Core.Minecraft.Resources;

/// <summary>Port of DataPackUtils::ProcessingLevel.</summary>
public enum DataPackProcessingLevel
{
    Full,
    BasicInfoOnly,
}

/// <summary>
/// Port of the DataPackUtils namespace: pack.mcmeta/pack.png processing for data packs
/// and resource packs.
/// </summary>
public static partial class DataPackUtils
{
    /// <summary>Port of DataPackUtils::process (dispatch on the resource type).</summary>
    public static bool Process(DataPack pack, DataPackProcessingLevel level = DataPackProcessingLevel.Full)
    {
        switch (pack.Type)
        {
            case ResourceType.Folder:
                return ProcessFolder(pack, level);
            case ResourceType.ZipFile:
                return ProcessZip(pack, level);
            default:
                // qWarning() << "Invalid type for data pack parse task!";
                return false;
        }
    }

    /// <summary>Port of DataPackUtils::processFolder.</summary>
    public static bool ProcessFolder(DataPack pack, DataPackProcessingLevel level = DataPackProcessingLevel.Full)
    {
        bool McmetaInvalid()
        {
            // qWarning() << "Data pack at" << ... << "does not have a valid pack.mcmeta";
            return false; // the mcmeta is not optional
        }

        var mcmetaFileInfo = new FileInfo(FileSystem.PathCombine(pack.Fileinfo.FullName, "pack.mcmeta"));
        if (mcmetaFileInfo.Exists && !mcmetaFileInfo.Attributes.HasFlag(FileAttributes.Directory))
        {
            string data;
            try
            {
                data = File.ReadAllText(mcmetaFileInfo.FullName);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                     or NotSupportedException)
            {
                return McmetaInvalid(); // can't open mcmeta file
            }

            var mcmetaResult = ProcessMCMeta(pack, data);
            if (!mcmetaResult)
            {
                return McmetaInvalid(); // mcmeta invalid
            }
        }
        else
        {
            return McmetaInvalid(); // mcmeta file isn't a valid file
        }

        if (level == DataPackProcessingLevel.BasicInfoOnly)
        {
            return true; // only need basic info already checked
        }

        bool PngInvalid()
        {
            // qWarning() << "Data pack at" << ... << "does not have a valid pack.png";
            return true; // the png is optional
        }

        var imageFileInfo = new FileInfo(FileSystem.PathCombine(pack.Fileinfo.FullName, "pack.png"));
        if (imageFileInfo.Exists && !imageFileInfo.Attributes.HasFlag(FileAttributes.Directory))
        {
            byte[] data;
            try
            {
                data = File.ReadAllBytes(imageFileInfo.FullName);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                     or NotSupportedException)
            {
                return PngInvalid(); // can't open pack.png file
            }

            var packPngResult = ProcessPackPNG(pack, data);
            if (!packPngResult)
            {
                return PngInvalid(); // pack.png invalid
            }
        }
        else
        {
            return PngInvalid(); // pack.png does not exists or is not a valid file.
        }

        return true; // all tests passed
    }

    /// <summary>Port of DataPackUtils::processZIP.</summary>
    public static bool ProcessZip(DataPack pack, DataPackProcessingLevel level = DataPackProcessingLevel.Full)
    {
        ZipArchive zip;
        try
        {
            zip = ZipFile.OpenRead(pack.Fileinfo.FullName);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException
                                 or ArgumentException or NotSupportedException)
        {
            return false; // can't open zip file
        }

        using (zip)
        {
            var metaParsed = false;
            var iconParsed = false;
            var mcmetaResult = false;
            var packPngResult = false;

            foreach (var entry in zip.Entries)
            {
                var filename = entry.FullName;
                if (!metaParsed && filename == "pack.mcmeta")
                {
                    metaParsed = true;
                    var data = ReadEntry(entry);

                    mcmetaResult = ProcessMCMeta(pack, Encoding.UTF8.GetString(data));

                    if (!mcmetaResult)
                    {
                        return true; // mcmeta invalid (breakControl)
                    }
                }
                if (!iconParsed && level != DataPackProcessingLevel.BasicInfoOnly && filename == "pack.png")
                {
                    iconParsed = true;
                    var data = ReadEntry(entry);

                    packPngResult = ProcessPackPNG(pack, data);
                    if (!packPngResult)
                    {
                        return true; // pack.png invalid (breakControl)
                    }
                }
                if (metaParsed && (level == DataPackProcessingLevel.BasicInfoOnly || iconParsed))
                {
                    break;
                }
            }

            if (!mcmetaResult)
            {
                // qWarning() << "Data pack at" << ... << "does not have a valid pack.mcmeta";
                return false; // the mcmeta is not optional
            }

            if (level == DataPackProcessingLevel.BasicInfoOnly)
            {
                return true; // only need basic info already checked
            }

            if (!packPngResult)
            {
                // qWarning() << "Data pack at" << ... << "does not have a valid pack.png";
                return true; // the png is optional
            }

            return true;
        }
    }

    /// <summary>Port of DataPackUtils::parseVersion: a number, or a [major, minor] array.</summary>
    public static (int Major, int Minor) ParseVersion(JsonNode? value)
    {
        if (value is JsonValue numberValue && numberValue.TryGetValue<double>(out var d))
        {
            // Single integer -> [major, 0]
            return ((int)d, 0);
        }
        (int Major, int Minor) version = default;
        if (value is JsonArray arr)        {
            if (arr.Count >= 1)
            {
                version.Major = QJsonValueToInt(arr[0]);
            }
            if (arr.Count >= 2)
            {
                version.Minor = QJsonValueToInt(arr[1]);
            }
        }
        return version;
    }

    // https://minecraft.wiki/w/Data_pack#pack.mcmeta
    // https://minecraft.wiki/w/Raw_JSON_text_format
    // https://minecraft.wiki/w/Tutorials/Creating_a_resource_pack#Formatting_pack.mcmeta
    public static bool ProcessMCMeta(DataPack pack, string rawData)
    {
        var jsonRoot = JsonUtils.ParseNodeUntilGarbage(rawData, out _);
        if (jsonRoot is null)
        {
            // qWarning() << "Failed to parse JSON:" << parse_error.errorString();
            return false;
        }

        try
        {
            // The C++ calls Json::requireObject(json_doc.object(), "pack", {}): the
            // parent+key overload. QJsonDocument::object() yields an empty object for
            // non-object roots, and the "pack" member is then required to be an
            // object — otherwise a JsonException (→ false below).
            var root = jsonRoot as JsonObject ?? new JsonObject();
            var packObj = JsonUtils.RequireObject(root, "pack", "");

            var packFormat = 0;
            (int Major, int Minor) minFormat = default;
            (int Major, int Minor) maxFormat = default;
            if (packObj.Contains("pack_format"))
            {
                packFormat = QJsonValueToInt(packObj["pack_format"]);
            }
            if (packObj.Contains("min_format"))
            {
                minFormat = ParseVersion(packObj["min_format"]);
            }
            if (packObj.Contains("max_format"))
            {
                maxFormat = ParseVersion(packObj["max_format"]);
            }
            pack.SetPackFormat(packFormat, minFormat, maxFormat);
            pack.SetDescription(ProcessComponent(packObj["description"]));
        }
        catch (JsonParseException)
        {
            // qWarning() << "JsonException:" << e.what() << e.cause();
            return false;
        }
        return true;
    }

    private static string BuildStyle(JsonObject obj)
    {
        var styles = new List<string>();
        var color = obj.StringOr("color");
        if (color.Length != 0)
        {
            styles.Add($"color: {color};");
        }
        if (obj.Contains("bold"))
        {
            var weight = obj.BoolOr("bold") ? "bold" : "normal";
            styles.Add($"font-weight: {weight};");
        }
        if (obj.Contains("italic"))
        {
            var style = obj.BoolOr("italic") ? "italic" : "normal";
            styles.Add($"font-style: {style};");
        }

        return styles.Count == 0 ? "" : $"style=\"{string.Join(" ", styles)}\"";
    }

    private static string ProcessComponent(JsonArray value, bool strikethrough, bool underline)
    {
        var result = "";
        foreach (var current in value)
        {
            result += ProcessComponent(current, strikethrough, underline);
        }
        return result;
    }

    private static string ProcessComponent(JsonObject obj, bool strikethrough, bool underline)
    {
        underline = obj.BoolOr("underlined", underline);
        strikethrough = obj.BoolOr("strikethrough", strikethrough);

        var result = obj.StringOr("text");
        if (underline)
        {
            result = $"<u>{result}</u>";
        }
        if (strikethrough)
        {
            result = $"<s>{result}</s>";
        }
        // the extra needs to be an array
        if (obj.TryGetPropertyValue("extra", out var extra) && extra is JsonArray extraArray)
        {
            result += ProcessComponent(extraArray, strikethrough, underline);
        }
        var style = BuildStyle(obj);
        if (style.Length != 0)
        {
            result = $"<span {style}>{result}</span>";
        }
        if (obj.TryGetPropertyValue("clickEvent", out var clickEvent) && clickEvent is JsonObject clickEventObj)
        {
            var action = clickEventObj.StringOr("action");
            var value = clickEventObj.StringOr("value");
            if (action == "open_url" && value.Length != 0)
            {
                result = $"<a href=\"{value}\">{result}</a>";
            }
        }
        return result;
    }

    public static string ProcessComponent(JsonNode? value, bool strikethrough = false, bool underline = false)
    {
        switch (value)
        {
            case JsonValue stringValue when stringValue.TryGetValue<string>(out _):
                return JsonObjectExtensions.StringOr(stringValue);
            case JsonValue boolValue when boolValue.TryGetValue<bool>(out var b):
                return b ? "true" : "false";
            case JsonValue numberValue when numberValue.TryGetValue<double>(out var d):
                return QtJson.NumberToString(d);
            case JsonArray array:
                return ProcessComponent(array, strikethrough, underline);
            case JsonObject obj:
                return ProcessComponent(obj, strikethrough, underline);
            default:
                // qWarning() << "Invalid component type!";
                return "";
        }
    }

    /// <summary>Port of DataPackUtils::processPackPNG(pack, data).</summary>
    public static bool ProcessPackPNG(DataPack pack, byte[] rawData)
    {
        if (PngImageReader.TryReadPng(rawData, out var img))
        {
            pack.SetImage(img);
        }
        else
        {
            // qWarning() << "Failed to parse pack.png.";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Port of DataPackUtils::processPackPNG(pack) (the reload path). NOTE: upstream
    /// returns false even on success ("not processed correctly",
    /// https://github.com/PrismLauncher/PrismLauncher/issues/1740); the only caller
    /// (DataPack::image) ignores the result.
    /// </summary>
    public static bool ProcessPackPNG(DataPack pack)
    {
        switch (pack.Type)
        {
            case ResourceType.Folder:
            {
                var imageFileInfo = new FileInfo(FileSystem.PathCombine(pack.Fileinfo.FullName, "pack.png"));
                if (imageFileInfo.Exists && !imageFileInfo.Attributes.HasFlag(FileAttributes.Directory))
                {
                    byte[] data;
                    try
                    {
                        data = File.ReadAllBytes(imageFileInfo.FullName);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                             or NotSupportedException)
                    {
                        return false; // can't open pack.png file
                    }

                    if (!ProcessPackPNG(pack, data))
                    {
                        return false; // pack.png invalid
                    }
                }
                else
                {
                    return false; // pack.png does not exists or is not a valid file.
                }
                return false; // not processed correctly; https://github.com/PrismLauncher/PrismLauncher/issues/1740
            }
            case ResourceType.ZipFile:
            {
                try
                {
                    using var zip = ZipFile.OpenRead(pack.Fileinfo.FullName);
                    var entry = zip.GetEntry("pack.png");
                    if (entry is null)
                    {
                        return false; // png_invalid
                    }
                    if (!ProcessPackPNG(pack, ReadEntry(entry)))
                    {
                        return false; // pack.png invalid
                    }
                }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException
                                         or ArgumentException or NotSupportedException)
                {
                    return false;
                }
                return false; // not processed correctly; https://github.com/PrismLauncher/PrismLauncher/issues/1740
            }
            default:
                // qWarning() << "Invalid type for data pack parse task!";
                return false;
        }
    }

    /// <summary>Port of DataPackUtils::validate.</summary>
    public static bool Validate(string file)
    {
        var dp = new DataPack(file);
        return Process(dp, DataPackProcessingLevel.BasicInfoOnly) && dp.Valid;
    }

    /// <summary>Port of DataPackUtils::validateResourcePack.</summary>
    public static bool ValidateResourcePack(string file)
    {
        var rp = new ResourcePack(file);
        return Process(rp, DataPackProcessingLevel.BasicInfoOnly) && rp.Valid;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>QJsonValue::toInt() (default 0, C-cast truncation of doubles).</summary>
    private static int QJsonValueToInt(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<double>(out var d) ? (int)d : 0;
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }
}
