// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/MojangVersionFormat.{h,cpp}.
//
// QJsonDocument parameters become JsonObject/JsonNode reads (single-DOM model); the
// QObject::tr() strings are kept as literal (untranslated) format strings. The upstream typo
// of a missing space in "<filename>contains a library that doesn't have a 'name' field" is
// preserved.
using System.Text.Json.Nodes;
using FlintLauncher.Core.Json;
using FlintLauncher.Core.Meta;

namespace FlintLauncher.Core.Minecraft;

public static class MojangVersionFormat
{
    private const int CurrentMinimumLauncherVersion = 18;

    // ------------------------------------------------------------------ Bits namespace
    //
    // Node-side member readers (FlameJson convention): the helper parameters are JsonNode? so
    // JsonObject arguments upcast at the call site instead of re-binding the (JsonObject, key,
    // what) element-bridge overloads — see the JsonUtils node-family migration rule.

    /// <summary>obj["key"] equivalent: null node when missing or not an object.</summary>
    private static JsonNode? Ptr(JsonNode? node, string key) => node is JsonObject obj ? obj[key] : null;

    /// <summary>QJsonObject::contains(key) equivalent: true even when the member holds JSON null.</summary>
    private static bool Has(JsonNode? node, string key) => node is JsonObject obj && obj.Contains(key);

    /// <summary>root.value(key).toString(default) equivalent.</summary>
    private static string Str(JsonNode? node, string key, string def = "") =>
        JsonObjectExtensions.StringOr(Ptr(node, key), def);

    private static string ReadString(JsonNode root, string key, string current)
    {
        return Has(root, key) ? JsonUtils.RequireString(Ptr(root, key), "Value") : current;
    }

    private static void ReadDownloadInfo(MojangDownloadInfo outInfo, JsonNode obj)
    {
        // optional, not used
        if (Has(obj, "path"))
        {
            outInfo.Path = JsonUtils.RequireString(Ptr(obj, "path"), "Value");
        }
        // required!
        outInfo.Sha1 = JsonUtils.RequireString(obj, "sha1", "'sha1'");
        outInfo.Url = JsonUtils.RequireString(obj, "url", "'url'");
        outInfo.Size = JsonUtils.RequireInteger(obj, "size", "'size'");
    }

    private static void ReadAssetIndex(MojangAssetIndexInfo outInfo, JsonNode obj)
    {
        outInfo.TotalSize = JsonUtils.RequireInteger(obj, "totalSize", "'totalSize'");
        outInfo.Id = JsonUtils.RequireString(obj, "id", "'id'");
        // outInfo.Known = true;
    }

    // ------------------------------------------------------------------ download info

    public static MojangDownloadInfo DownloadInfoFromJson(JsonNode obj)
    {
        var outInfo = new MojangDownloadInfo();
        ReadDownloadInfo(outInfo, obj);
        return outInfo;
    }

    public static MojangAssetIndexInfo AssetIndexFromJson(JsonNode obj)
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

    public static MojangLibraryDownloadInfo LibDownloadInfoFromJson(JsonNode libObj)
    {
        var outInfo = new MojangLibraryDownloadInfo();
        JsonNode dlObj = JsonUtils.RequireObject(Ptr(libObj, "downloads"));
        if (Has(dlObj, "artifact"))
        {
            outInfo.Artifact = DownloadInfoFromJson(JsonUtils.RequireObject(dlObj, "artifact", "'artifact'"));
        }
        if (Has(dlObj, "classifiers"))
        {
            var classifiersObj = JsonUtils.RequireObject(dlObj, "classifiers", "'classifiers'");
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
    public static void ReadVersionProperties(JsonNode inObj, VersionFile outInfo)
    {
        outInfo.MinecraftVersion = ReadString(inObj, "id", outInfo.MinecraftVersion);
        outInfo.MainClass = ReadString(inObj, "mainClass", outInfo.MainClass);
        outInfo.MinecraftArguments = ReadString(inObj, "minecraftArguments", outInfo.MinecraftArguments);
        outInfo.Type = ReadString(inObj, "type", outInfo.Type);

        outInfo.Assets = ReadString(inObj, "assets", outInfo.Assets);
        if (Has(inObj, "assetIndex"))
        {
            outInfo.MojangAssetIndex = AssetIndexFromJson(JsonUtils.RequireObject(inObj, "assetIndex", "'assetIndex'"));
        }
        else if (outInfo.Assets.Length != 0)
        {
            outInfo.MojangAssetIndex = new MojangAssetIndexInfo(outInfo.Assets);
        }

        outInfo.ReleaseTime = ParseUtils.TimeFromS3Time(Str(inObj, "releaseTime"));
        outInfo.UpdateTime = ParseUtils.TimeFromS3Time(Str(inObj, "time"));

        if (Has(inObj, "minimumLauncherVersion"))
        {
            outInfo.MinimumLauncherVersion = JsonUtils.RequireInteger(Ptr(inObj, "minimumLauncherVersion"), "Value");
            if (outInfo.MinimumLauncherVersion > CurrentMinimumLauncherVersion)
            {
                outInfo.AddProblem(ProblemSeverity.Warning,
                    $"The 'minimumLauncherVersion' value of this version ({outInfo.MinimumLauncherVersion}) is higher than " +
                    $"supported by {FlintConfig.Default.LauncherDisplayName} ({CurrentMinimumLauncherVersion}). It might not work properly!");
            }
        }

        if (Has(inObj, "compatibleJavaMajors"))
        {
            foreach (var compatible in JsonUtils.RequireArray(Ptr(inObj, "compatibleJavaMajors")))
            {
                outInfo.CompatibleJavaMajors.Add(JsonUtils.RequireInteger(compatible));
            }
        }
        if (Has(inObj, "compatibleJavaName"))
        {
            outInfo.CompatibleJavaName = JsonUtils.RequireString(Ptr(inObj, "compatibleJavaName"), "Value");
        }

        if (Has(inObj, "downloads"))
        {
            var downloadsObj = JsonUtils.RequireObject(inObj, "downloads", "'downloads'");
            foreach (var iter in downloadsObj)
            {
                var classifierObj = iter.Value as JsonObject
                    ?? throw new JsonParseException("Value is not an object");
                outInfo.MojangDownloads[iter.Key] = DownloadInfoFromJson(classifierObj);
            }
        }
    }

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
        var outInfo = new VersionFile();

        ReadVersionProperties(root, outInfo);

        outInfo.Name = "Minecraft";
        outInfo.Uid = "net.minecraft";
        outInfo.Version = outInfo.MinecraftVersion;

        if (root.Contains("libraries"))
        {
            foreach (var libVal in JsonUtils.RequireArray(Ptr(root, "libraries")))
            {
                var libObj = JsonUtils.RequireObject(libVal);

                var lib = LibraryFromJson(outInfo, libObj, filename);
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

    public static Library LibraryFromJson(ProblemContainer problems, JsonNode libObj, string filename)
    {
        var outLib = new Library();
        if (!Has(libObj, "name"))
        {
            throw new JsonParseException(filename + "contains a library that doesn't have a 'name' field");
        }
        var rawName = Str(libObj, "name");
        outLib.Name = new GradleSpecifier(rawName);
        if (!outLib.Name.IsValid)
        {
            problems.AddProblem(ProblemSeverity.Error, $"Library {rawName} name is broken and cannot be processed.");
        }

        outLib.RepositoryUrl = ReadString(libObj, "url", outLib.RepositoryUrl);
        if (Has(libObj, "extract"))
        {
            outLib.HasExcludes = true;
            var extractObj = JsonUtils.RequireObject(Ptr(libObj, "extract"));
            extractObj.TryGetPropertyValue("exclude", out var excludeNode);
            foreach (var excludeVal in JsonUtils.RequireArray(excludeNode))
            {
                outLib.ExtractExcludes.Add(JsonUtils.RequireString(excludeVal));
            }
        }
        if (Has(libObj, "natives"))
        {
            var nativesObj = JsonUtils.RequireObject(Ptr(libObj, "natives"));
            foreach (var it in nativesObj)
            {
                if (it.Value is not JsonValue nativeValue || !nativeValue.TryGetValue<string>(out _))
                {
                    // qWarning() << filename << "contains an invalid native (skipping)";
                }
                // FIXME upstream: Skip unknown platforms
                outLib.NativeClassifiers[it.Key] = JsonUtils.EnsureString(it.Value);
            }
        }
        if (Has(libObj, "rules"))
        {
            outLib.ApplyRules = true;

            var rulesArray = JsonUtils.RequireArray(Ptr(libObj, "rules"));
            foreach (var rule in rulesArray)
            {
                var ruleObj = rule as JsonObject
                    ?? throw new JsonParseException("Value is not an object");
                outLib.Rules.Add(Rule.FromJson(ruleObj));
            }
        }
        if (Has(libObj, "downloads"))
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
