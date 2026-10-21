// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/MojangVersionFormat.{h,cpp}.
//
// QJsonDocument parameters become JsonObject/JsonNode reads (single-DOM model); the
// QObject::tr() strings are kept as literal (untranslated) format strings. The upstream typo
// of a missing space in "<filename>contains a library that doesn't have a 'name' field" is
// preserved.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace FlintLauncher.Core.Minecraft;

public static class MojangVersionFormat
{
    internal const int CurrentMinimumLauncherVersion = 18;

    // ------------------------------------------------------------------ read side
    //
    // The read side deserializes the VersionFormatJson probes (VersionFormatJson.cs) and maps
    // them onto the domain objects; a malformed document fails whole with the collapsed kind
    // texts.

    /// <summary>The probe read over a node: shape failures keep the "{what}: Error parsing
    /// JSON: ..." wrapper family of the former RequireDocument reads.</summary>
    private static T Probe<T>(JsonNode node, string what, JsonTypeInfo<T> typeInfo) where T : class
    {
        T? dto;
        try
        {
            dto = node.Deserialize(typeInfo);
        }
        catch (JsonException e)
        {
            throw new JsonException(what + ": Error parsing JSON: " + e.Message);
        }

        return dto ?? throw new JsonException(what + ": Error parsing JSON: document is null");
    }

    // ------------------------------------------------------------------ download info

    public static MojangDownloadInfo DownloadInfoFromJson(JsonNode obj) =>
        VersionFormatMapper.MapDownloadInfo(Probe(obj, "download info", VersionFormatJsonContext.Default.MojangDownloadInfoJson));

    public static MojangAssetIndexInfo AssetIndexFromJson(JsonNode obj) =>
        VersionFormatMapper.MapAssetIndex(Probe(obj, "asset index", VersionFormatJsonContext.Default.MojangAssetIndexJson));

    public static MojangLibraryDownloadInfo LibDownloadInfoFromJson(JsonNode libObj) =>
        VersionFormatMapper.MapLibDownloads(
            Probe(libObj, "library downloads", VersionFormatJsonContext.Default.MojangLibraryDownloadsJson));

    // ------------------------------------------------------------------ write side
    //
    // The write side maps the domain objects onto the *OutJson records (the typed wire) and
    // serializes them through the shared context — the member conditions of the former
    // node writes become null members (WhenWritingNull skips them).

    internal static DownloadInfoOutJson DownloadInfoToJson(MojangDownloadInfo info)
    {
        return new DownloadInfoOutJson
        {
            Path = info.Path,
            Sha1 = info.Sha1,
            Size = info.Size,
            Url = info.Url,
        };
    }

    internal static LibDownloadsOutJson LibDownloadInfoToJson(MojangLibraryDownloadInfo libinfo)
    {
        var classifiers = libinfo.Classifiers.Count != 0
            ? libinfo.Classifiers.ToDictionary(pair => pair.Key, pair => DownloadInfoToJson(pair.Value))
            : null;
        return new LibDownloadsOutJson
        {
            Artifact = libinfo.Artifact is { } artifact ? DownloadInfoToJson(artifact) : null,
            Classifiers = classifiers,
        };
    }

    internal static AssetIndexOutJson AssetIndexToJson(MojangAssetIndexInfo info)
    {
        return new AssetIndexOutJson
        {
            Path = info.Path,
            Sha1 = info.Sha1,
            Size = info.Size,
            Url = info.Url,
            TotalSize = info.TotalSize,
            Id = info.Id,
        };
    }

    // ------------------------------------------------------------------ version file

    /// <summary>does not include libraries</summary>
    public static void ReadVersionProperties(JsonNode inObj, VersionFile outInfo) =>
        VersionFormatMapper.MapVersionProperties(Probe(inObj, "version file", VersionFormatJsonContext.Default.VersionFileJson), outInfo);

    public static VersionFile VersionFileFromJson(JsonNode? root, string filename)
    {
        if (root is null)
        {
            throw new JsonException(filename + " is empty or null");
        }

        if (root is not JsonObject obj)
        {
            throw new JsonException(filename + " is not an object");
        }

        return VersionFileFromJsonObject(obj, filename);
    }

    /// <summary>
    /// The object-based entry point (the C++ version wraps the object in a QJsonDocument first;
    /// the "document is empty or null" branch cannot trigger for an object).
    /// </summary>
    public static VersionFile VersionFileFromJsonObject(JsonObject root, string filename)
    {
        var dto = Probe(root, filename, VersionFormatJsonContext.Default.VersionFileJson);
        var outInfo = new VersionFile();

        VersionFormatMapper.MapVersionProperties(dto, outInfo);

        outInfo.Name = "Minecraft";
        outInfo.Uid = "net.minecraft";
        outInfo.Version = outInfo.MinecraftVersion;

        if (dto.Libraries is { } libraries)
        {
            foreach (var libDto in libraries)
            {
                outInfo.Libraries.Add(VersionFormatMapper.MapLibrary(libDto, outInfo, filename));
            }
        }

        return outInfo;
    }

    /// <summary>does not include libraries — the Mojang half of the write wire.</summary>
    internal static VersionFileOutJson WriteVersionProperties(VersionFile inInfo)
    {
        var wire = new VersionFileOutJson
        {
            Id = inInfo.MinecraftVersion.Length != 0 ? inInfo.MinecraftVersion : null,
            MainClass = inInfo.MainClass.Length != 0 ? inInfo.MainClass : null,
            MinecraftArguments = inInfo.MinecraftArguments.Length != 0 ? inInfo.MinecraftArguments : null,
            Type = inInfo.Type.Length != 0 ? inInfo.Type : null,
            ReleaseTime = inInfo.ReleaseTime is { } releaseTime ? ParseUtils.TimeToS3Time(releaseTime) : null,
            Time = inInfo.UpdateTime is { } updateTime ? ParseUtils.TimeToS3Time(updateTime) : null,
            MinimumLauncherVersion = inInfo.MinimumLauncherVersion != -1 ? inInfo.MinimumLauncherVersion : null,
            Assets = inInfo.Assets.Length != 0 ? inInfo.Assets : null,
            AssetIndex = inInfo.MojangAssetIndex is { Known: true } assetIndex ? AssetIndexToJson(assetIndex) : null,
            CompatibleJavaMajors = inInfo.CompatibleJavaMajors.Count != 0 ? [.. inInfo.CompatibleJavaMajors] : null,
            CompatibleJavaName = inInfo.CompatibleJavaName.Length != 0 ? inInfo.CompatibleJavaName : null,
        };
        if (inInfo.MojangDownloads.Count != 0)
        {
            wire = wire with
            {
                Downloads = inInfo.MojangDownloads.ToDictionary(
                    pair => pair.Key, pair => DownloadInfoToJson(pair.Value)),
            };
        }

        return wire;
    }

    /// <summary>Port of MojangVersionFormat::versionFileToJson: the Mojang member half plus
    /// the libraries, as the typed write wire.</summary>
    internal static VersionFileOutJson VersionFileToJson(VersionFile patch)
    {
        var wire = WriteVersionProperties(patch);
        if (patch.Libraries.Count != 0)
        {
            wire = wire with { Libraries = patch.Libraries.Select(LibraryToJson).ToList() };
        }

        return wire;
    }

    // ------------------------------------------------------------------ libraries

    public static Library LibraryFromJson(ProblemContainer problems, JsonNode libObj, string filename) =>
        VersionFormatMapper.MapLibrary(
            Probe(libObj, filename, VersionFormatJsonContext.Default.LibraryJson), problems, filename);

    internal static LibraryOutJson LibraryToJson(Library library)
    {
        return new LibraryOutJson
        {
            Name = library.Name.Serialize(),
            Url = library.RepositoryUrl.Length != 0 ? library.RepositoryUrl : null,
            Natives = library.IsNative()
                ? new Dictionary<string, string>(library.NativeClassifiers, StringComparer.Ordinal)
                : null,
            Extract = library.IsNative() && library.ExtractExcludes.Count != 0
                ? new LibraryExtractJson([.. library.ExtractExcludes])
                : null,
            Rules = library.Rules.Count != 0 ? library.Rules.Select(RuleToJson).ToList() : null,
            Downloads = library.MojangDownloads is { } downloads ? LibDownloadInfoToJson(downloads) : null,
        };
    }

    /// <summary>The C++ Rule::ToJson shape as the typed wire.</summary>
    internal static RuleOutJson RuleToJson(Rule rule) => rule.ToWire();
}
