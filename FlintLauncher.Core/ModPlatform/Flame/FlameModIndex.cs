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
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
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

// ------------------------------------------------------------------ wire probes
//
// The CurseForge project/file shapes as parsed DTOs; every member is camel-conforming
// (id/name/slug/links/logo/authors/classId/gameVersions/fileDate/downloadUrl/...), so the
// context-wide policy carries the naming with zero explicit annotations.

internal sealed record FlameProjectJson(
    int? Id,
    string? Name,
    string? Slug,
    FlameLinksJson? Links,
    string? Summary,
    FlameLogoJson? Logo,
    List<FlameAuthorJson>? Authors,
    int? ClassId);

internal sealed record FlameLinksJson(
    string? WebsiteUrl,
    string? IssuesUrl,
    string? SourceUrl,
    string? WikiUrl);

internal sealed record FlameLogoJson(
    string? Title,
    string? ThumbnailUrl,
    string? Url);

internal sealed record FlameAuthorJson(
    string? Name,
    string? Url);

internal sealed record FlameModFileJson(
    JsonElement? GameVersions,
    int? ModId,
    int? Id,
    string? FileDate,
    string? DisplayName,
    string? DownloadUrl,
    string? FileName,
    int? ReleaseType,
    List<FlameHashJson>? Hashes,
    List<FlameDependencyJson>? Dependencies);

internal sealed record FlameHashJson(
    int? Algo,
    string? Value);

internal sealed record FlameDependencyJson(
    int? ModId,
    int? RelationType);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FlameProjectJson))]
[JsonSerializable(typeof(FlameModFileJson))]
internal sealed partial class FlameModJsonContext : JsonSerializerContext;

// https://docs.curseforge.com/#tocS_Mod
public static class FlameMod
{
    /// <summary>The probe read over a node: shape failures become JsonParseException like
    /// the former Require* reads.</summary>
    private static T Probe<T>(JsonNode node, JsonTypeInfo<T> typeInfo) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(node, typeInfo)
                   ?? throw new JsonParseException("Value is not an object");
        }
        catch (JsonException e)
        {
            throw new JsonParseException(e.Message);
        }
    }

    private static string Require(string? value, string key) =>
        value ?? throw new JsonParseException("'" + key + "' is not a string");

    private static int Require(int? value, string key) =>
        value ?? throw new JsonParseException("'" + key + "' is not an integer");

    /// <summary>Port of FlameMod::loadIndexedPack.</summary>
    public static void LoadIndexedPack(IndexedPack pack, JsonNode obj)
    {
        var dto = Probe(obj, FlameModJsonContext.Default.FlameProjectJson);

        pack.AddonId = Require(dto.Id, "id").ToString(CultureInfo.InvariantCulture);
        pack.Provider = ResourceProvider.Flame;
        pack.Name = Require(dto.Name, "name");
        pack.Slug = Require(dto.Slug, "slug");
        pack.WebsiteUrl = dto.Links?.WebsiteUrl ?? "";
        pack.Description = dto.Summary ?? "";

        // a missing/junk logo reads as the empty-logo fallback chain (title/thumbnail/url)
        var logo = dto.Logo;
        pack.LogoName = logo?.Title ?? "";
        pack.LogoUrl = logo?.ThumbnailUrl ?? "";
        if (pack.LogoUrl.Length == 0)
        {
            pack.LogoUrl = logo?.Url ?? "";
        }

        if (dto.Authors is { Count: > 0 } authors)
        {
            pack.Authors.Clear();
            foreach (var author in authors)
            {
                pack.Authors.Add(new ModpackAuthor
                {
                    Name = Require(author.Name, "name"),
                    Url = Require(author.Url, "url"),
                });
            }
        }

        pack.ResourceType = FlameApi.GetResourceType(dto.ClassId ?? 0);
        pack.ExtraDataLoaded = false;
        MapUrls(dto.Links, pack);
    }

    /// <summary>Port of FlameMod::loadURLs.</summary>
    public static void LoadUrLs(IndexedPack pack, JsonNode obj)
    {
        var dto = Probe(obj, FlameModJsonContext.Default.FlameProjectJson);
        MapUrls(dto.Links, pack);
    }

    private static void MapUrls(FlameLinksJson? links, IndexedPack pack)
    {
        pack.ExtraData.IssuesUrl = ChopTrailingSlash(links?.IssuesUrl ?? "");
        pack.ExtraData.SourceUrl = ChopTrailingSlash(links?.SourceUrl ?? "");
        pack.ExtraData.WikiUrl = ChopTrailingSlash(links?.WikiUrl ?? "");

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
            if (versionIter is null)
            {
                throw new JsonParseException("file entry is not an object");
            }

            var file = LoadIndexedPackVersion(versionIter);
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
        var dto = Probe(obj, FlameModJsonContext.Default.FlameModFileJson);

        // a missing gameVersions member is a require failure; a non-string element reads as
        // "" like the former AsString fallback (and simply misses every branch below)
        if (dto.GameVersions is not { ValueKind: JsonValueKind.Array } gameVersionsNode)
        {
            throw new JsonParseException("'gameVersions' is not an array");
        }

        var file = new IndexedVersion();
        foreach (var mcVer in gameVersionsNode.EnumerateArray())
        {
            var str = mcVer.ValueKind == JsonValueKind.String ? mcVer.GetString() ?? "" : "";

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

        file.AddonId = Require(dto.ModId, "modId").ToString(CultureInfo.InvariantCulture);
        file.FileId = Require(dto.Id, "id").ToString(CultureInfo.InvariantCulture);
        file.Date = Require(dto.FileDate, "fileDate");
        file.Version = Require(dto.DisplayName, "displayName");
        file.DownloadUrl = dto.DownloadUrl ?? "";
        file.FileName = FileSystem.RemoveInvalidPathChars(Require(dto.FileName, "fileName"));

        file.VersionType = Require(dto.ReleaseType, "releaseType") switch
        {
            1 => new IndexedVersionType(IndexedVersionTypeValue.Release),
            2 => new IndexedVersionType(IndexedVersionTypeValue.Beta),
            3 => new IndexedVersionType(IndexedVersionTypeValue.Alpha),
            _ => IndexedVersionType.Invalid,
        };

        foreach (var hashEntry in dto.Hashes ?? [])
        {
            var hashTypes = ProviderCapabilities.HashType(ResourceProvider.Flame);
            // a missing/junk algo member defaults to sha1 (1), like the former Int read
            var hashAlgo = EnumToString(hashEntry?.Algo ?? 1);
            if (hashTypes.Contains(hashAlgo))
            {
                file.Hash = Require(hashEntry?.Value, "value");
                file.HashType = hashAlgo;
                break;
            }
        }

        foreach (var dep in dto.Dependencies ?? [])
        {
            var dependency = new Dependency
            {
                AddonId = Require(dep?.ModId, "modId").ToString(CultureInfo.InvariantCulture),
                Type = Require(dep?.RelationType, "relationType") switch
                {
                    1 => new DependencyType(DependencyTypeValue.Embedded),     // EmbeddedLibrary
                    2 => new DependencyType(DependencyTypeValue.Optional),     // OptionalDependency
                    3 => new DependencyType(DependencyTypeValue.Required),     // RequiredDependency
                    4 => new DependencyType(DependencyTypeValue.Tool),         // Tool
                    5 => new DependencyType(DependencyTypeValue.Incompatible), // Incompatible
                    6 => new DependencyType(DependencyTypeValue.Include),      // Include
                    _ => DependencyType.Invalid,
                }
            };
            file.Dependencies.Add(dependency);
        }

        return file;
    }
}
