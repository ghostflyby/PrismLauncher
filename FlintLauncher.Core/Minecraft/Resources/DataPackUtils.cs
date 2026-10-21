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
//    significant digits (lowercase exponent); NumberToString below mirrors that.

using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FlintLauncher.Core.Json;
using TruePath;

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
public static class DataPackUtils
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

        var mcmetaFileInfo = new FileInfo(Path.Combine(pack.Fileinfo.FullName, "pack.mcmeta"));
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

            var mcmetaResult = ProcessMcMeta(pack, data);
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

        var imageFileInfo = new FileInfo(Path.Combine(pack.Fileinfo.FullName, "pack.png"));
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

            var packPngResult = ProcessPackPng(pack, data);
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

                    mcmetaResult = ProcessMcMeta(pack, Encoding.UTF8.GetString(data));

                    if (!mcmetaResult)
                    {
                        return true; // mcmeta invalid (breakControl)
                    }
                }

                if (!iconParsed && level != DataPackProcessingLevel.BasicInfoOnly && filename == "pack.png")
                {
                    iconParsed = true;
                    var data = ReadEntry(entry);

                    packPngResult = ProcessPackPng(pack, data);
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

    // https://minecraft.wiki/w/Data_pack#pack.mcmeta
    // https://minecraft.wiki/w/Raw_JSON_text_format
    // https://minecraft.wiki/w/Tutorials/Creating_a_resource_pack#Formatting_pack.mcmeta
    public static bool ProcessMcMeta(DataPack pack, string rawData)
    {
        PackMcmetaJson? mcmeta;
        try
        {
            mcmeta = JsonSerializer.Deserialize(rawData, DataPackJsonContext.Default.PackMcmetaJson);
        }
        catch (JsonException)
        {
            // qWarning() << "Failed to parse JSON" / the former RequireObject throw path
            return false;
        }

        var section = mcmeta?.Pack;
        if (section is null)
        {
            // the former Json::requireObject(root, "pack") throw (a missing pack member
            // required an object) — invalid mcmeta
            return false;
        }

        pack.SetPackFormat(section.PackFormat,
            section.MinFormat is { } min ? (min.Major, min.Minor) : default,
            section.MaxFormat is { } max ? (max.Major, max.Minor) : default);
        pack.SetDescription(ProcessComponent(section.Description));
        return true;
    }

    private static string BuildStyle(ComponentObjectJson obj)
    {
        var styles = new List<string>();
        if (obj.Color.Length != 0)
        {
            styles.Add($"color: {obj.Color};");
        }

        if (obj.Bold is { } bold)
        {
            styles.Add($"font-weight: {(bold ? "bold" : "normal")};");
        }

        if (obj.Italic is { } italic)
        {
            styles.Add($"font-style: {(italic ? "italic" : "normal")};");
        }

        return styles.Count == 0 ? "" : $"style=\"{string.Join(" ", styles)}\"";
    }

    private static string ProcessComponent(List<ChatComponentJson> value, bool strikethrough, bool underline)
    {
        var result = "";
        foreach (var current in value)
        {
            result += ProcessComponent(current, strikethrough, underline);
        }

        return result;
    }

    private static string ProcessComponent(ComponentObjectJson obj, bool strikethrough, bool underline)
    {
        underline = obj.Underlined ?? underline;
        strikethrough = obj.Strikethrough ?? strikethrough;

        var result = obj.Text;
        if (underline)
        {
            result = $"<u>{result}</u>";
        }

        if (strikethrough)
        {
            result = $"<s>{result}</s>";
        }

        // the extra needs to be an array
        if (obj.Extra is { } extra)
        {
            result += ProcessComponent(extra, strikethrough, underline);
        }

        var style = BuildStyle(obj);
        if (style.Length != 0)
        {
            result = $"<span {style}>{result}</span>";
        }

        if (obj.ClickEvent is { } clickEvent && clickEvent.Action == "open_url"
                                             && clickEvent.Value is { Length: > 0 } value)
        {
            result = $"<a href=\"{value}\">{result}</a>";
        }

        return result;
    }

    internal static string ProcessComponent(ChatComponentJson? value, bool strikethrough = false, bool underline = false)
    {
        return value switch
        {
            { Text: { } text } => text,
            { BoolValue: { } b } => b ? "true" : "false",
            { NumberValue: { } number } => NumberToString(number),
            { Array: { } array } => ProcessComponent(array, strikethrough, underline),
            { Object: { } obj } => ProcessComponent(obj, strikethrough, underline),
            _ => "", // qWarning() << "Invalid component type!";
        };
    }

    /// <summary>QJsonValue::toDouble() as written by QString::number(double) ('g', 6 digits,
    /// lowercase exponent) — the Qt number rendering the C++ component walker used.</summary>
    private static string NumberToString(double value)
    {
        var text = value.ToString("G6", CultureInfo.InvariantCulture);
        return text.Replace('E', 'e');
    }

    /// <summary>Port of DataPackUtils::processPackPNG(pack, data).</summary>
    public static bool ProcessPackPng(DataPack pack, byte[] rawData)
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
    public static bool ProcessPackPng(DataPack pack)
    {
        switch (pack.Type)
        {
            case ResourceType.Folder:
            {
                var imageFileInfo = new FileInfo(Path.Combine(pack.Fileinfo.FullName, "pack.png"));
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

                    if (!ProcessPackPng(pack, data))
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

                    if (!ProcessPackPng(pack, ReadEntry(entry)))
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

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }
}

// ----------------------------------------------------------------------------------------
// The pack.mcmeta wire (typed reads; nothing here is ever written). The description member
// is the Minecraft raw-JSON text component (string | bool | number | array | object tree) —
// the ChatComponentConverter unifies those forms and the coercing converters keep the
// per-member junk degradation of the former StringOr/BoolOr probes.

internal sealed record PackMcmetaJson
{
    public PackMetaSectionJson? Pack { get; set; }
}

internal sealed record PackMetaSectionJson
{
    [JsonPropertyName("pack_format")]
    [JsonConverter(typeof(QtCoercingIntConverter))]
    public int PackFormat { get; set; }

    [JsonPropertyName("min_format")]
    [JsonConverter(typeof(PackVersionConverter))]
    public PackVersionJson? MinFormat { get; set; }

    [JsonPropertyName("max_format")]
    [JsonConverter(typeof(PackVersionConverter))]
    public PackVersionJson? MaxFormat { get; set; }

    [JsonConverter(typeof(ChatComponentConverter))]
    public ChatComponentJson? Description { get; set; }
}

/// <summary>The [major, minor] pack-format pair (a bare number reads [major, 0]).</summary>
internal sealed record PackVersionJson
{
    public int Major { get; set; }

    public int Minor { get; set; }
}

internal sealed class PackVersionConverter : JsonConverter<PackVersionJson?>
{
    public override PackVersionJson? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var single))
        {
            return new PackVersionJson { Major = (int)single };
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var node = JsonNode.Parse(ref reader) as JsonArray;
            var version = new PackVersionJson();
            if (node is { Count: >= 1 } array)
            {
                version.Major = array[0] is JsonValue first && first.TryGetValue<double>(out var major)
                    ? (int)major
                    : 0;
            }

            if (node is { Count: >= 2 } arr2)
            {
                version.Minor = arr2[1] is JsonValue second && second.TryGetValue<double>(out var minor)
                    ? (int)minor
                    : 0;
            }

            return version;
        }

        return null; // a wrong-kind member is no version (the former Contains-guard skip)
    }

    public override void Write(Utf8JsonWriter writer, PackVersionJson? value, JsonSerializerOptions options) =>
        throw new NotSupportedException("the pack.mcmeta records are read-only");
}

/// <summary>The unified raw-JSON text component value: exactly one form is set.</summary>
[JsonConverter(typeof(ChatComponentConverter))]
internal sealed record ChatComponentJson
{
    public string? Text { get; set; }

    public bool? BoolValue { get; set; }

    public double? NumberValue { get; set; }

    public List<ChatComponentJson>? Array { get; set; }

    public ComponentObjectJson? Object { get; set; }
}

internal sealed class ChatComponentConverter : JsonConverter<ChatComponentJson?>
{
    public override ChatComponentJson? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return new ChatComponentJson { Text = reader.GetString() ?? "" };
            case JsonTokenType.True:
            case JsonTokenType.False:
                return new ChatComponentJson { BoolValue = reader.TokenType == JsonTokenType.True };
            case JsonTokenType.Number:
                return new ChatComponentJson { NumberValue = reader.TryGetDouble(out var d) ? d : 0 };
            case JsonTokenType.StartArray:
                return new ChatComponentJson
                {
                    Array = JsonSerializer.Deserialize(ref reader, DataPackJsonContext.Default.ListChatComponentJson),
                };
            case JsonTokenType.StartObject:
                return new ChatComponentJson
                {
                    Object = JsonSerializer.Deserialize(ref reader, DataPackJsonContext.Default.ComponentObjectJson),
                };
            default:
                return null; // qWarning() << "Invalid component type!";
        }
    }

    public override void Write(Utf8JsonWriter writer, ChatComponentJson? value, JsonSerializerOptions options) =>
        throw new NotSupportedException("the pack.mcmeta records are read-only");
}

internal sealed record ComponentObjectJson
{
    [JsonConverter(typeof(QtCoercingStringConverter))]
    public string Text { get; set; } = "";

    /// <summary>The inherit-chain flags: junk reads null and inherits (the old
    /// BoolOr(key, fallback)).</summary>
    [JsonConverter(typeof(QtCoercingNullableBoolConverter))]
    public bool? Underlined { get; set; }

    [JsonConverter(typeof(QtCoercingNullableBoolConverter))]
    public bool? Strikethrough { get; set; }

    /// <summary>The style flags are PRESENCE-checked (the old Contains+BoolOr pair): a
    /// present member of any kind reads its bool (junk → false), only absence stays null
    /// and emits no span.</summary>
    [JsonConverter(typeof(QtPresenceBoolConverter))]
    public bool? Bold { get; set; }

    [JsonConverter(typeof(QtPresenceBoolConverter))]
    public bool? Italic { get; set; }

    [JsonConverter(typeof(QtCoercingStringConverter))]
    public string Color { get; set; } = "";

    /// <summary>Only an array counts (the former `is JsonArray` probe); junk reads null.</summary>
    [JsonConverter(typeof(ChatExtraConverter))]
    public List<ChatComponentJson>? Extra { get; set; }

    [JsonConverter(typeof(ClickEventConverter))]
    public ClickEventJson? ClickEvent { get; set; }
}

internal sealed record ClickEventJson
{
    [JsonConverter(typeof(QtCoercingStringConverter))]
    public string Action { get; set; } = "";

    [JsonConverter(typeof(QtCoercingStringConverter))]
    public string Value { get; set; } = "";
}

/// <summary>A non-array extra reads null (skipped), like the former probe.</summary>
internal sealed class ChatExtraConverter : JsonConverter<List<ChatComponentJson>?>
{
    public override List<ChatComponentJson>? Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        return reader.TokenType == JsonTokenType.StartArray
            ? JsonSerializer.Deserialize(ref reader, DataPackJsonContext.Default.ListChatComponentJson)
            : null;
    }

    public override void Write(Utf8JsonWriter writer, List<ChatComponentJson>? value, JsonSerializerOptions options) =>
        throw new NotSupportedException("the pack.mcmeta records are read-only");
}

/// <summary>A non-object clickEvent reads null (skipped), like the former probe.</summary>
internal sealed class ClickEventConverter : JsonConverter<ClickEventJson?>
{
    public override ClickEventJson? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType == JsonTokenType.StartObject
            ? JsonSerializer.Deserialize(ref reader, DataPackJsonContext.Default.ClickEventJson)
            : null;
    }

    public override void Write(Utf8JsonWriter writer, ClickEventJson? value, JsonSerializerOptions options) =>
        throw new NotSupportedException("the pack.mcmeta records are read-only");
}

[JsonSourceGenerationOptions(RespectNullableAnnotations = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PackMcmetaJson))]
[JsonSerializable(typeof(ChatComponentJson))]
[JsonSerializable(typeof(List<ChatComponentJson>))]
[JsonSerializable(typeof(ComponentObjectJson))]
[JsonSerializable(typeof(ClickEventJson))]
internal sealed partial class DataPackJsonContext : JsonSerializerContext;
