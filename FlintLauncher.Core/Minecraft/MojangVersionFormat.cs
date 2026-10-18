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
using FlintLauncher.Core.Java;
using FlintLauncher.Core.Json;

namespace FlintLauncher.Core.Minecraft;

public static class MojangVersionFormat
{
    internal const int CurrentMinimumLauncherVersion = 18;

    // ------------------------------------------------------------------ read side
    //
    // The read side deserializes the VersionFormatJson probes (VersionFormatJson.cs) and maps
    // them onto the domain objects; a malformed document fails whole with the collapsed kind
    // texts. The write side below is the byte-tested Qt tail (sorted keys, 4-space indent).

    /// <summary>The probe read over a node: shape failures keep the "{what}: Error parsing
    /// JSON: ..." wrapper family of the former RequireDocument reads.</summary>
    private static T Probe<T>(JsonNode node, string what, JsonTypeInfo<T> typeInfo) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(node, typeInfo)
                   ?? throw new JsonParseException(what + ": Error parsing JSON: document is null");
        }
        catch (JsonException e)
        {
            throw new JsonParseException(what + ": Error parsing JSON: " + e.Message);
        }
    }

    // ------------------------------------------------------------------ download info

    public static MojangDownloadInfo DownloadInfoFromJson(JsonNode obj) =>
        VersionFormatMapper.MapDownloadInfo(Probe(obj, "download info", VersionFormatJsonContext.Default.MojangDownloadInfoJson));

    public static MojangAssetIndexInfo AssetIndexFromJson(JsonNode obj) =>
        VersionFormatMapper.MapAssetIndex(Probe(obj, "asset index", VersionFormatJsonContext.Default.MojangAssetIndexJson));

    public static JsonObject DownloadInfoToJson(MojangDownloadInfo info)
    {
        var outObj = new JsonObject();
        if (info.Path is not null)
        {
            outObj["path"] = info.Path;
        }

        outObj["sha1"] = info.Sha1;
        outObj["size"] = info.Size;
        outObj["url"] = info.Url;
        return outObj;
    }

    public static MojangLibraryDownloadInfo LibDownloadInfoFromJson(JsonNode libObj) =>
        VersionFormatMapper.MapLibDownloads(
            Probe(libObj, "library downloads", VersionFormatJsonContext.Default.MojangLibraryDownloadsJson));

    public static JsonObject LibDownloadInfoToJson(MojangLibraryDownloadInfo libinfo)
    {
        var outObj = new JsonObject();
        if (libinfo.Artifact is not null)
        {
            outObj["artifact"] = DownloadInfoToJson(libinfo.Artifact);
        }

        if (libinfo.Classifiers.Count != 0)
        {
            var classifiersOut = new JsonObject();
            foreach (var (classifier, info) in libinfo.Classifiers)
            {
                classifiersOut[classifier] = DownloadInfoToJson(info);
            }

            outObj["classifiers"] = classifiersOut;
        }

        return outObj;
    }

    public static JsonObject AssetIndexToJson(MojangAssetIndexInfo info)
    {
        var outObj = new JsonObject();
        if (info.Path is not null)
        {
            outObj["path"] = info.Path;
        }

        outObj["sha1"] = info.Sha1;
        outObj["size"] = info.Size;
        outObj["url"] = info.Url;
        outObj["totalSize"] = info.TotalSize;
        outObj["id"] = info.Id;
        return outObj;
    }

    // ------------------------------------------------------------------ version file

    /// <summary>does not include libraries</summary>
    public static void ReadVersionProperties(JsonNode inObj, VersionFile outInfo) =>
        VersionFormatMapper.MapVersionProperties(Probe(inObj, "version file", VersionFormatJsonContext.Default.VersionFileJson), outInfo);

    public static VersionFile VersionFileFromJson(JsonNode? root, string filename)
    {
        if (root is null)
        {
            throw new JsonParseException(filename + " is empty or null");
        }

        if (root is not JsonObject obj)
        {
            throw new JsonParseException(filename + " is not an object");
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

    /// <summary>does not include libraries</summary>
    public static void WriteVersionProperties(VersionFile inInfo, JsonObject outObj)
    {
        JsonUtils.WriteString(outObj, "id", inInfo.MinecraftVersion);
        JsonUtils.WriteString(outObj, "mainClass", inInfo.MainClass);
        JsonUtils.WriteString(outObj, "minecraftArguments", inInfo.MinecraftArguments);
        JsonUtils.WriteString(outObj, "type", inInfo.Type);
        if (inInfo.ReleaseTime is not null)
        {
            JsonUtils.WriteString(outObj, "releaseTime", ParseUtils.TimeToS3Time(inInfo.ReleaseTime.Value));
        }

        if (inInfo.UpdateTime is not null)
        {
            JsonUtils.WriteString(outObj, "time", ParseUtils.TimeToS3Time(inInfo.UpdateTime.Value));
        }

        if (inInfo.MinimumLauncherVersion != -1)
        {
            outObj["minimumLauncherVersion"] = inInfo.MinimumLauncherVersion;
        }

        JsonUtils.WriteString(outObj, "assets", inInfo.Assets);
        if (inInfo.MojangAssetIndex is { Known: true } assetIndex)
        {
            outObj["assetIndex"] = AssetIndexToJson(assetIndex);
        }

        if (inInfo.MojangDownloads.Count != 0)
        {
            var downloadsOut = new JsonObject();
            foreach (var (classifier, info) in inInfo.MojangDownloads)
            {
                downloadsOut[classifier] = DownloadInfoToJson(info);
            }

            outObj["downloads"] = downloadsOut;
        }

        if (inInfo.CompatibleJavaMajors.Count != 0)
        {
            var compatibleJavaMajorsOut = new JsonArray();
            foreach (var compatibleJavaMajor in inInfo.CompatibleJavaMajors)
            {
                compatibleJavaMajorsOut.Add((JsonNode)JsonValue.Create(compatibleJavaMajor));
            }

            outObj["compatibleJavaMajors"] = compatibleJavaMajorsOut;
        }

        if (inInfo.CompatibleJavaName.Length != 0)
        {
            JsonUtils.WriteString(outObj, "compatibleJavaName", inInfo.CompatibleJavaName);
        }
    }

    /// <summary>Port of MojangVersionFormat::versionFileToJson (QJsonDocument -> JsonObject).</summary>
    public static JsonObject VersionFileToJson(VersionFile patch)
    {
        var root = new JsonObject();
        WriteVersionProperties(patch, root);
        if (patch.Libraries.Count != 0)
        {
            var array = new JsonArray();
            foreach (var value in patch.Libraries)
            {
                array.Add((JsonNode)LibraryToJson(value));
            }

            root["libraries"] = array;
        }

        return root;
    }

    // ------------------------------------------------------------------ libraries

    public static Library LibraryFromJson(ProblemContainer problems, JsonNode libObj, string filename) =>
        VersionFormatMapper.MapLibrary(
            Probe(libObj, filename, VersionFormatJsonContext.Default.LibraryJson), problems, filename);

    public static JsonObject LibraryToJson(Library library)
    {
        var libRoot = new JsonObject
        {
            ["name"] = library.Name.Serialize(),
        };
        if (library.RepositoryUrl.Length != 0)
        {
            libRoot["url"] = library.RepositoryUrl;
        }

        if (library.IsNative())
        {
            var nativeList = new JsonObject();
            foreach (var (os, classifier) in library.NativeClassifiers)
            {
                nativeList[os] = classifier;
            }

            libRoot["natives"] = nativeList;
            if (library.ExtractExcludes.Count != 0)
            {
                var excludes = new JsonArray();
                foreach (var exclude in library.ExtractExcludes)
                {
                    excludes.Add((JsonNode)JsonValue.Create(exclude));
                }

                libRoot["extract"] = new JsonObject
                {
                    ["exclude"] = excludes,
                };
            }
        }

        if (library.Rules.Count != 0)
        {
            var allRules = new JsonArray();
            foreach (var rule in library.Rules)
            {
                allRules.Add((JsonNode)rule.ToJson());
            }

            libRoot["rules"] = allRules;
        }

        if (library.MojangDownloads is not null)
        {
            libRoot["downloads"] = LibDownloadInfoToJson(library.MojangDownloads);
        }

        return libRoot;
    }
}
