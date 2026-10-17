// SPDX-License-Identifier: GPL-3.0-only
// Port of modplatform/flame/FlameModIndex.{h,cpp}: load CurseForge project/version JSON
// documents into the provider-neutral ModIndex models.
//
// Deviations:
//  - loadIndexedPackVersion's `loadChangelog` parameter is dropped: every in-repo caller
//    passes the default (false) and fetches changelogs explicitly through
//    FlameApi.GetModFileChangelog (the C++ made it synchronous through a QEventLoop).
//  - loadBody (the description fetch) becomes LoadBodyAsync for the same reason.
//  - the upstream quirk that resets file.side to NoSide on every gameVersions iteration is
//    kept verbatim (the "both sides" branch is unreachable, exactly like the C++).

using System.Globalization;
using System.Text.Json.Nodes;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Json;
using FlintLauncher.Core.Minecraft;

namespace FlintLauncher.Core.ModPlatform.Flame;

/// <summary>Small QJsonObject-style accessors shared by the Flame loaders (explicit calls,
/// to stay unambiguous next to the Modrinth extension methods). JsonNode-based — the single
/// read model; see the JsonUtils node family for the semantics contract.</summary>
internal static class FlameJson
{
    /// <summary>obj["key"] equivalent: null node when missing.</summary>
    public static JsonNode? Ptr(JsonNode? element, string name) => element is JsonObject obj ? obj[name] : null;

    /// <summary>obj["key"].toString(default) equivalent.</summary>
    public static string Str(JsonNode? element, string name, string def = "")
    {
        return JsonUtils.EnsureString(Ptr(element, name), def);
    }

    public static int Int(JsonNode? element, string name, int def = 0)
    {
        return JsonUtils.EnsureInteger(Ptr(element, name), def);
    }

    /// <summary>QJsonValue::toInteger() equivalent over a raw node.</summary>
    public static long Long(JsonNode? node)
    {
        return JsonUtils.EnsureLong(node);
    }

    /// <summary>obj["key"].asString() equivalent for string-typed sub-reads.</summary>
    public static string? AsString(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;
    }

    public static JsonObject Obj(JsonNode? element, string name)
    {
        return Ptr(element, name) as JsonObject ?? new JsonObject();
    }

    public static JsonArray Arr(JsonNode? element, string name)
    {
        return Ptr(element, name) as JsonArray ?? new JsonArray();
    }

    public static string RequireString(JsonNode? parent, string key)
        => JsonUtils.RequireString(Ptr(parent, key), "'" + key + "'");

    public static int RequireInteger(JsonNode? parent, string key)
        => JsonUtils.RequireInteger(Ptr(parent, key), "'" + key + "'");

    public static JsonObject RequireObject(JsonNode? parent, string key)
        => JsonUtils.RequireObject(Ptr(parent, key), "'" + key + "'");

    public static JsonArray RequireArray(JsonNode? parent, string key)
        => JsonUtils.RequireArray(Ptr(parent, key), "'" + key + "'");
}

// https://docs.curseforge.com/#tocS_Mod
public static class FlameMod
{
    /// <summary>Port of FlameMod::loadIndexedPack.</summary>
    public static void LoadIndexedPack(IndexedPack pack, JsonNode obj)
    {
        pack.AddonId = FlameJson.RequireInteger(obj, "id").ToString(CultureInfo.InvariantCulture);
        pack.Provider = ResourceProvider.Flame;
        pack.Name = FlameJson.RequireString(obj, "name");
        pack.Slug = FlameJson.RequireString(obj, "slug");
        pack.WebsiteUrl = FlameJson.Str(FlameJson.Obj(obj, "links"), "websiteUrl");
        pack.Description = FlameJson.Str(obj, "summary");

        var logo = FlameJson.Obj(obj, "logo");
        pack.LogoName = FlameJson.Str(logo, "title");
        pack.LogoUrl = FlameJson.Str(logo, "thumbnailUrl");
        if (pack.LogoUrl.Length == 0)
        {
            pack.LogoUrl = FlameJson.Str(logo, "url");
        }

        var authors = FlameJson.Arr(obj, "authors");
        if (authors.Count != 0)
        {
            pack.Authors.Clear();
            foreach (var author in authors)
            {
                pack.Authors.Add(new ModpackAuthor
                {
                    Name = FlameJson.RequireString(author, "name"),
                    Url = FlameJson.RequireString(author, "url"),
                });
            }
        }

        pack.ResourceType = FlameApi.GetResourceType(FlameJson.Int(obj, "classId"));
        pack.ExtraDataLoaded = false;
        LoadUrLs(pack, obj);
    }

    /// <summary>Port of FlameMod::loadURLs.</summary>
    public static void LoadUrLs(IndexedPack pack, JsonNode obj)
    {
        var linksObj = FlameJson.Obj(obj, "links");

        pack.ExtraData.IssuesUrl = ChopTrailingSlash(FlameJson.Str(linksObj, "issuesUrl"));
        pack.ExtraData.SourceUrl = ChopTrailingSlash(FlameJson.Str(linksObj, "sourceUrl"));
        pack.ExtraData.WikiUrl = ChopTrailingSlash(FlameJson.Str(linksObj, "wikiUrl"));

        if (pack.ExtraData.Body.Length != 0)
        {
            pack.ExtraDataLoaded = true;
        }
    }

    /// <summary>Port of FlameMod::loadBody (the description fetch; see the header note).</summary>
    public static async Task LoadBodyAsync(IndexedPack pack, FlameApi flame, CancellationToken cancellationToken = default)
    {
        pack.ExtraData.Body = await flame.GetModDescription(
            int.Parse(pack.AddonId, CultureInfo.InvariantCulture), cancellationToken);

        if (pack.ExtraData.IssuesUrl.Length != 0 || pack.ExtraData.SourceUrl.Length != 0
            || pack.ExtraData.WikiUrl.Length != 0)
        {
            pack.ExtraDataLoaded = true;
        }
    }

    private static string ChopTrailingSlash(string url) => url.EndsWith('/') ? url[..^1] : url;

    /// <summary>enumToString helper: the CF hash algorithm ids (1=sha1, 2=md5).</summary>
    private static string EnumToString(int hashAlgorithm) => hashAlgorithm switch
    {
        2 => "md5",
        _ => "sha1",
    };

    /// <summary>Port of FlameMod::loadIndexedPackVersions (sorts by file date, newest first).</summary>
    public static void LoadIndexedPackVersions(IndexedPack pack, JsonArray arr)
    {
        var unsortedVersions = new List<IndexedVersion>();
        foreach (var versionIter in arr)
        {
            var obj = versionIter as JsonObject
                ?? throw new JsonParseException("file entry is not an object");

            var file = LoadIndexedPackVersion(obj);
            if (file.AddonId.Length == 0)
            {
                file.AddonId = pack.AddonId;
            }

            if (file.FileId.Length != 0)
            {
                // Heuristic to check if the returned value is valid
                unsortedVersions.Add(file);
            }
        }

        // dates are in RFC 3339 format
        unsortedVersions.Sort((a, b) => string.CompareOrdinal(b.Date, a.Date));
        pack.Versions.Clear();
        pack.Versions.AddRange(unsortedVersions);
        pack.VersionsLoaded = true;
    }

    /// <summary>Port of FlameMod::loadIndexedPackVersion.</summary>
    public static IndexedVersion LoadIndexedPackVersion(JsonNode obj)
    {
        var versionArray = FlameJson.RequireArray(obj, "gameVersions");

        var file = new IndexedVersion();
        foreach (var mcVer in versionArray)
        {
            var str = FlameJson.AsString(mcVer) ?? "";

            if (str.Contains('.'))
            {
                file.McVersion.Add(str);
            }

            // NOTE: upstream resets the side on every iteration (kept verbatim; the
            // "both sides" merge branch below is therefore unreachable there too)
            file.Side = SideType.Invalid;
            var loader = str.ToLowerInvariant();
            if (loader == "neoforge")
            {
                file.Loaders |= ModLoaderType.NeoForge;
            }
            else if (loader == "forge")
            {
                file.Loaders |= ModLoaderType.Forge;
            }
            else if (loader == "cauldron")
            {
                file.Loaders |= ModLoaderType.Cauldron;
            }
            else if (loader == "liteloader")
            {
                file.Loaders |= ModLoaderType.LiteLoader;
            }
            else if (loader == "fabric")
            {
                file.Loaders |= ModLoaderType.Fabric;
            }
            else if (loader == "quilt")
            {
                file.Loaders |= ModLoaderType.Quilt;
            }
            else if (loader is "server" or "client")
            {
                if (!file.Side.IsValid)
                {
                    file.Side = SideType.FromString(loader);
                }
                else if (file.Side.Value != SideType.FromString(loader).Value)
                {
                    file.Side = new SideType(SideTypeValue.UniversalSide);
                }
            }
        }

        file.AddonId = FlameJson.RequireInteger(obj, "modId").ToString(CultureInfo.InvariantCulture);
        file.FileId = FlameJson.RequireInteger(obj, "id").ToString(CultureInfo.InvariantCulture);
        file.Date = FlameJson.RequireString(obj, "fileDate");
        file.Version = FlameJson.RequireString(obj, "displayName");
        file.DownloadUrl = FlameJson.Str(obj, "downloadUrl");
        file.FileName = FileSystem.RemoveInvalidPathChars(FlameJson.RequireString(obj, "fileName"));

        file.VersionType = FlameJson.RequireInteger(obj, "releaseType") switch
        {
            1 => new IndexedVersionType(IndexedVersionTypeValue.Release),
            2 => new IndexedVersionType(IndexedVersionTypeValue.Beta),
            3 => new IndexedVersionType(IndexedVersionTypeValue.Alpha),
            _ => IndexedVersionType.Invalid,
        };

        var hashList = FlameJson.Arr(obj, "hashes");
        foreach (var hashEntry in hashList)
        {
            var hashTypes = ProviderCapabilities.HashType(ResourceProvider.Flame);
            var hashAlgo = EnumToString(FlameJson.Int(hashEntry, "algo", 1));
            if (hashTypes.Contains(hashAlgo))
            {
                file.Hash = FlameJson.RequireString(hashEntry, "value");
                file.HashType = hashAlgo;
                break;
            }
        }

        var dependencies = FlameJson.Arr(obj, "dependencies");
        foreach (var dep in dependencies)
        {
            var dependency = new Dependency
            {
                AddonId = FlameJson.RequireInteger(dep, "modId").ToString(CultureInfo.InvariantCulture),
            };
            dependency.Type = FlameJson.RequireInteger(dep, "relationType") switch
            {
                1 => new DependencyType(DependencyTypeValue.Embedded), // EmbeddedLibrary
                2 => new DependencyType(DependencyTypeValue.Optional), // OptionalDependency
                3 => new DependencyType(DependencyTypeValue.Required), // RequiredDependency
                4 => new DependencyType(DependencyTypeValue.Tool), // Tool
                5 => new DependencyType(DependencyTypeValue.Incompatible), // Incompatible
                6 => new DependencyType(DependencyTypeValue.Include), // Include
                _ => DependencyType.Invalid,
            };
            file.Dependencies.Add(dependency);
        }

        return file;
    }
}
