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
using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Json;

namespace FlintLauncher.Core.ModPlatform.Flame;

/// <summary>Small QJsonObject-style accessors shared by the Flame loaders (explicit calls,
/// to stay unambiguous next to the Modrinth extension methods).</summary>
internal static class FlameJson
{
    /// <summary>obj["key"] equivalent: null element when missing.</summary>
    public static JsonElement Ptr(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return JsonUtils.NullElement;
        }
        return element.TryGetProperty(name, out var value) ? value : JsonUtils.NullElement;
    }

    /// <summary>obj["key"].toString(default) equivalent.</summary>
    public static string Str(JsonElement element, string name, string def = "")
    {
        return JsonUtils.EnsureString(Ptr(element, name), def);
    }

    public static int Int(JsonElement element, string name, int def = 0)
    {
        return JsonUtils.EnsureInteger(Ptr(element, name), def);
    }

    /// <summary>QJsonValue::toInteger() equivalent over a raw node.</summary>
    public static long Long(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<double>(out var d) ? (long)d : 0;
    }

    /// <summary>QJsonValue::toInteger() equivalent over an element.</summary>
    public static long Long(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.Number ? (long)element.GetDouble() : 0;
    }

    public static JsonElement Obj(JsonElement element, string name)
    {
        var value = Ptr(element, name);
        return value.ValueKind == JsonValueKind.Object ? value : JsonUtils.EmptyObjectElement;
    }

    public static JsonElement Arr(JsonElement element, string name)
    {
        var value = Ptr(element, name);
        return value.ValueKind == JsonValueKind.Array ? value : JsonUtils.EmptyArrayElement;
    }

    public static string RequireString(JsonElement parent, string key)
        => JsonUtils.RequireString(Ptr(parent, key), "'" + key + "'");

    public static int RequireInteger(JsonElement parent, string key)
        => JsonUtils.RequireInteger(Ptr(parent, key), "'" + key + "'");

    public static JsonElement RequireObject(JsonElement parent, string key)
        => JsonUtils.RequireObject(Ptr(parent, key), "'" + key + "'");

    public static JsonElement RequireArray(JsonElement parent, string key)
        => JsonUtils.RequireArray(Ptr(parent, key), "'" + key + "'");
}

// https://docs.curseforge.com/#tocS_Mod
public static class FlameMod
{
    /// <summary>Port of FlameMod::loadIndexedPack.</summary>
    public static void LoadIndexedPack(IndexedPack pack, JsonElement obj)
    {
        pack.AddonId = FlameJson.RequireInteger(obj, "id").ToString(CultureInfo.InvariantCulture);
        pack.Provider = ResourceProvider.FLAME;
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
        if (authors.GetArrayLength() != 0)
        {
            pack.Authors.Clear();
            foreach (var authorIter in authors.EnumerateArray())
            {
                var author = JsonUtils.RequireObject(authorIter);
                pack.Authors.Add(new ModpackAuthor
                {
                    Name = FlameJson.RequireString(author, "name"),
                    Url = FlameJson.RequireString(author, "url"),
                });
            }
        }

        pack.ResourceType = FlameApi.GetResourceType(FlameJson.Int(obj, "classId"));
        pack.ExtraDataLoaded = false;
        LoadURLs(pack, obj);
    }

    /// <summary>Port of FlameMod::loadURLs.</summary>
    public static void LoadURLs(IndexedPack pack, JsonElement obj)
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
    public static void LoadIndexedPackVersions(IndexedPack pack, JsonElement arr)
    {
        var unsortedVersions = new List<IndexedVersion>();
        foreach (var versionIter in arr.EnumerateArray())
        {
            var obj = JsonUtils.RequireObject(versionIter);

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
    public static IndexedVersion LoadIndexedPackVersion(JsonElement obj)
    {
        var versionArray = FlameJson.RequireArray(obj, "gameVersions");

        var file = new IndexedVersion();
        foreach (var mcVer in versionArray.EnumerateArray())
        {
            var str = mcVer.GetString() ?? "";

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
                file.Loaders |= Minecraft.ModLoaderType.NeoForge;
            }
            else if (loader == "forge")
            {
                file.Loaders |= Minecraft.ModLoaderType.Forge;
            }
            else if (loader == "cauldron")
            {
                file.Loaders |= Minecraft.ModLoaderType.Cauldron;
            }
            else if (loader == "liteloader")
            {
                file.Loaders |= Minecraft.ModLoaderType.LiteLoader;
            }
            else if (loader == "fabric")
            {
                file.Loaders |= Minecraft.ModLoaderType.Fabric;
            }
            else if (loader == "quilt")
            {
                file.Loaders |= Minecraft.ModLoaderType.Quilt;
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
        foreach (var h in hashList.EnumerateArray())
        {
            var hashEntry = JsonUtils.RequireObject(h);
            var hashTypes = ProviderCapabilities.HashType(ResourceProvider.FLAME);
            var hashAlgo = EnumToString(FlameJson.Int(hashEntry, "algo", 1));
            if (hashTypes.Contains(hashAlgo))
            {
                file.Hash = FlameJson.RequireString(hashEntry, "value");
                file.HashType = hashAlgo;
                break;
            }
        }

        var dependencies = FlameJson.Arr(obj, "dependencies");
        foreach (var d in dependencies.EnumerateArray())
        {
            var dep = JsonUtils.RequireObject(d);
            var dependency = new Dependency
            {
                AddonId = FlameJson.RequireInteger(dep, "modId").ToString(CultureInfo.InvariantCulture),
            };
            dependency.Type = FlameJson.RequireInteger(dep, "relationType") switch
            {
                1 => new DependencyType(DependencyTypeValue.EMBEDDED), // EmbeddedLibrary
                2 => new DependencyType(DependencyTypeValue.OPTIONAL), // OptionalDependency
                3 => new DependencyType(DependencyTypeValue.REQUIRED), // RequiredDependency
                4 => new DependencyType(DependencyTypeValue.TOOL), // Tool
                5 => new DependencyType(DependencyTypeValue.INCOMPATIBLE), // Incompatible
                6 => new DependencyType(DependencyTypeValue.INCLUDE), // Include
                _ => DependencyType.Invalid,
            };
            file.Dependencies.Add(dependency);
        }

        return file;
    }
}
