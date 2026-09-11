// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/MojangVersionFormat.{h,cpp}.
//
// QJsonDocument parameters become JsonDocument/JsonObject; the QObject::tr() strings are kept
// as literal (untranslated) format strings. The upstream typo of a missing space in
// "<filename>contains a library that doesn't have a 'name' field" is preserved.
using System.Text.Json;
using System.Text.Json.Nodes;
using Flint.Core.Json;
using Flint.Core.Meta;

namespace Flint.Core.Minecraft;

public static class MojangVersionFormat
{
    private const int CurrentMinimumLauncherVersion = 18;

    // ------------------------------------------------------------------ Bits namespace

    private static string ReadString(JsonObject root, string key, string current)
    {
        return root.Contains(key) ? JsonUtils.RequireString(root, key, "Value") : current;
    }

    /// <summary>root.value(key) as a JsonElement (for the require*(QJsonValue) overload shape).</summary>
    private static JsonElement ValueOf(JsonObject root, string key)
    {
        return root[key].ToElement();
    }

    private static void ReadDownloadInfo(MojangDownloadInfo outInfo, JsonObject obj)
    {
        // optional, not used
        if (obj.Contains("path"))
        {
            outInfo.Path = JsonUtils.RequireString(obj, "path", "Value");
        }
        // required!
        outInfo.Sha1 = JsonUtils.RequireString(obj, "sha1");
        outInfo.Url = JsonUtils.RequireString(obj, "url");
        outInfo.Size = JsonUtils.RequireInteger(obj, "size");
    }

    private static void ReadAssetIndex(MojangAssetIndexInfo outInfo, JsonObject obj)
    {
        outInfo.TotalSize = JsonUtils.RequireInteger(obj, "totalSize");
        outInfo.Id = JsonUtils.RequireString(obj, "id");
        // outInfo.Known = true;
    }

    // ------------------------------------------------------------------ download info

    public static MojangDownloadInfo DownloadInfoFromJson(JsonObject obj)
    {
        var outInfo = new MojangDownloadInfo();
        ReadDownloadInfo(outInfo, obj);
        return outInfo;
    }

    public static MojangAssetIndexInfo AssetIndexFromJson(JsonObject obj)
    {
        var outInfo = new MojangAssetIndexInfo();
        ReadDownloadInfo(outInfo, obj);
        ReadAssetIndex(outInfo, obj);
        return outInfo;
    }

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

    public static MojangLibraryDownloadInfo LibDownloadInfoFromJson(JsonObject libObj)
    {
        var outInfo = new MojangLibraryDownloadInfo();
        var dlObj = JsonUtils.RequireObject(ValueOf(libObj, "downloads")).ToJsonObject();
        if (dlObj.Contains("artifact"))
        {
            outInfo.Artifact = DownloadInfoFromJson(JsonUtils.RequireObject(dlObj, "artifact").ToJsonObject());
        }
        if (dlObj.Contains("classifiers"))
        {
            var classifiersObj = JsonUtils.RequireObject(dlObj, "classifiers").ToJsonObject();
            foreach (var iter in classifiersObj)
            {
                var classifierObj = iter.Value as JsonObject
                    ?? throw new JsonParseException("Value is not an object");
                outInfo.Classifiers[iter.Key] = DownloadInfoFromJson(classifierObj);
            }
        }
        return outInfo;
    }

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
    public static void ReadVersionProperties(JsonObject inObj, VersionFile outInfo)
    {
        outInfo.MinecraftVersion = ReadString(inObj, "id", outInfo.MinecraftVersion);
        outInfo.MainClass = ReadString(inObj, "mainClass", outInfo.MainClass);
        outInfo.MinecraftArguments = ReadString(inObj, "minecraftArguments", outInfo.MinecraftArguments);
        outInfo.Type = ReadString(inObj, "type", outInfo.Type);

        outInfo.Assets = ReadString(inObj, "assets", outInfo.Assets);
        if (inObj.Contains("assetIndex"))
        {
            outInfo.MojangAssetIndex = AssetIndexFromJson(JsonUtils.RequireObject(inObj, "assetIndex").ToJsonObject());
        }
        else if (outInfo.Assets.Length != 0)
        {
            outInfo.MojangAssetIndex = new MojangAssetIndexInfo(outInfo.Assets);
        }

        outInfo.ReleaseTime = ParseUtils.TimeFromS3Time(inObj.StringOr("releaseTime"));
        outInfo.UpdateTime = ParseUtils.TimeFromS3Time(inObj.StringOr("time"));

        if (inObj.Contains("minimumLauncherVersion"))
        {
            outInfo.MinimumLauncherVersion = JsonUtils.RequireInteger(inObj, "minimumLauncherVersion", "Value");
            if (outInfo.MinimumLauncherVersion > CurrentMinimumLauncherVersion)
            {
                outInfo.AddProblem(ProblemSeverity.Warning,
                    $"The 'minimumLauncherVersion' value of this version ({outInfo.MinimumLauncherVersion}) is higher than " +
                    $"supported by {FlintConfig.Default.LauncherDisplayName} ({CurrentMinimumLauncherVersion}). It might not work properly!");
            }
        }

        if (inObj.Contains("compatibleJavaMajors"))
        {
            foreach (var compatible in JsonUtils.RequireArray(ValueOf(inObj, "compatibleJavaMajors")).EnumerateArray())
            {
                outInfo.CompatibleJavaMajors.Add(JsonUtils.RequireInteger(compatible));
            }
        }
        if (inObj.Contains("compatibleJavaName"))
        {
            outInfo.CompatibleJavaName = JsonUtils.RequireString(inObj, "compatibleJavaName", "Value");
        }

        if (inObj.Contains("downloads"))
        {
            var downloadsObj = JsonUtils.RequireObject(inObj, "downloads").ToJsonObject();
            foreach (var iter in downloadsObj)
            {
                var classifierObj = iter.Value as JsonObject
                    ?? throw new JsonParseException("Value is not an object");
                outInfo.MojangDownloads[iter.Key] = DownloadInfoFromJson(classifierObj);
            }
        }
    }

    public static VersionFile VersionFileFromJson(JsonDocument doc, string filename)
    {
        if (doc is null)
        {
            throw new JsonParseException(filename + " is empty or null");
        }
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonParseException(filename + " is not an object");
        }

        return VersionFileFromJsonObject(doc.RootElement.ToJsonObject(), filename);
    }

    /// <summary>
    /// The object-based entry point (the C++ version wraps the object in a QJsonDocument first;
    /// the "document is empty or null" branch cannot trigger for an object).
    /// </summary>
    public static VersionFile VersionFileFromJsonObject(JsonObject root, string filename)
    {
        var outInfo = new VersionFile();

        ReadVersionProperties(root, outInfo);

        outInfo.Name = "Minecraft";
        outInfo.Uid = "net.minecraft";
        outInfo.Version = outInfo.MinecraftVersion;

        if (root.Contains("libraries"))
        {
            foreach (var libVal in JsonUtils.RequireArray(ValueOf(root, "libraries")).EnumerateArray())
            {
                var libObj = JsonUtils.RequireObject(libVal);

                var lib = LibraryFromJson(outInfo, libObj.ToJsonObject(), filename);
                outInfo.Libraries.Add(lib);
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
        if (inInfo.MojangAssetIndex is { } assetIndex && assetIndex.Known)
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
                compatibleJavaMajorsOut.Add((JsonNode)JsonValue.Create(compatibleJavaMajor)!);
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

    public static Library LibraryFromJson(ProblemContainer problems, JsonObject libObj, string filename)
    {
        var outLib = new Library();
        if (!libObj.Contains("name"))
        {
            throw new JsonParseException(filename + "contains a library that doesn't have a 'name' field");
        }
        var rawName = libObj.StringOr("name");
        outLib.Name = new GradleSpecifier(rawName);
        if (!outLib.Name.IsValid)
        {
            problems.AddProblem(ProblemSeverity.Error, $"Library {rawName} name is broken and cannot be processed.");
        }

        outLib.RepositoryUrl = ReadString(libObj, "url", outLib.RepositoryUrl);
        if (libObj.Contains("extract"))
        {
            outLib.HasExcludes = true;
            var extractObj = JsonUtils.RequireObject(ValueOf(libObj, "extract"));
            extractObj.TryGetProperty("exclude", out var excludeNode);
            foreach (var excludeVal in JsonUtils.RequireArray(excludeNode).EnumerateArray())
            {
                outLib.ExtractExcludes.Add(JsonUtils.RequireString(excludeVal));
            }
        }
        if (libObj.Contains("natives"))
        {
            var nativesObj = JsonUtils.RequireObject(ValueOf(libObj, "natives"));
            foreach (var it in nativesObj.EnumerateObject())
            {
                if (it.Value.ValueKind != JsonValueKind.String)
                {
                    // qWarning() << filename << "contains an invalid native (skipping)";
                }
                // FIXME upstream: Skip unknown platforms
                outLib.NativeClassifiers[it.Name] = JsonUtils.EnsureString(it.Value);
            }
        }
        if (libObj.Contains("rules"))
        {
            outLib.ApplyRules = true;

            var rulesArray = JsonUtils.RequireArray(ValueOf(libObj, "rules"));
            foreach (var rule in rulesArray.EnumerateArray())
            {
                var ruleObj = JsonNode.Parse(rule.GetRawText()) as JsonObject
                    ?? throw new JsonParseException("Value is not an object");
                outLib.Rules.Add(Rule.FromJson(ruleObj));
            }
        }
        if (libObj.Contains("downloads"))
        {
            outLib.MojangDownloads = LibDownloadInfoFromJson(libObj);
        }
        return outLib;
    }

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
                    excludes.Add((JsonNode)JsonValue.Create(exclude)!);
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
