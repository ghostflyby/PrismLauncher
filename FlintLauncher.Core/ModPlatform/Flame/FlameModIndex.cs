// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
//
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
using FlintLauncher.Core.Minecraft;

namespace FlintLauncher.Core.ModPlatform.Flame;

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
    List<FlameDependencyJson>? Dependencies,
    bool? IsAvailable,
    long? FileFingerprint);

internal sealed record FlameHashJson(
    int? Algo,
    string? Value);

internal sealed record FlameDependencyJson(
    int? ModId,
    int? RelationType);

/// <summary>The /categories response entry (FlameAPI::loadModCategories).</summary>
internal sealed record FlameCategoryJson(
    int? Id,
    string? Name);

internal sealed record FlameCategoryListJson(
    List<FlameCategoryJson>? Data);

/// <summary>The fingerprint-match response (EnsureMetadataTask's Flame flow).</summary>
internal sealed record FlameFingerprintResponseJson(
    FlameFingerprintDataJson? Data);

internal sealed record FlameFingerprintDataJson(
    List<FlameFingerprintMatchJson>? ExactMatches);

/// <summary>One fingerprint-search match; the fingerprint itself is the FILE object's
/// fileFingerprint member (the match level only carries "id", which nobody consumed).</summary>
internal sealed record FlameFingerprintMatchJson(
    FlameModFileJson? File);

/// <summary>The {data: [...]} envelope of the files list response.</summary>
internal sealed record FlameFileListJson(
    List<FlameModFileJson>? Data);

/// <summary>The {data: [...]} envelope of the projects list response. Elements stay
/// nullable: a JSON null project is tolerated by the readers, not silently re-typed.</summary>
internal sealed record FlameProjectListJson(
    List<FlameProjectJson?>? Data);

/// <summary>The {data: {...}} envelope of the single-project response.</summary>
internal sealed record FlameProjectEnvelopeJson(
    FlameProjectJson? Data);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FlameProjectJson))]
[JsonSerializable(typeof(FlameModFileJson))]
[JsonSerializable(typeof(FlameFingerprintResponseJson))]
[JsonSerializable(typeof(FlameFileListJson))]
[JsonSerializable(typeof(FlameProjectListJson))]
[JsonSerializable(typeof(FlameProjectEnvelopeJson))]
[JsonSerializable(typeof(FlameCategoryListJson))]
[JsonSerializable(typeof(List<FlameModFileJson>))]
internal sealed partial class FlameModJsonContext : JsonSerializerContext;

// https://docs.curseforge.com/#tocS_Mod
public static class FlameMod
{
    /// <summary>The probe read over a node: shape failures become JsonException like
    /// the former Require* reads.</summary>
    private static T Probe<T>(JsonNode node, JsonTypeInfo<T> typeInfo) where T : class
    {
        T? dto;
        try
        {
            dto = node.Deserialize(typeInfo);
        }
        catch (JsonException e)
        {
            throw new JsonException(e.Message);
        }

        return dto ?? throw new JsonException("Value is not an object");
    }

    /// <summary>The response-body parse for the typed flow entries (the same
    /// JsonException wrapper family).</summary>
    internal static T ParseResponse<T>(string body, JsonTypeInfo<T> typeInfo) where T : class
    {
        T? dto;
        try
        {
            dto = JsonSerializer.Deserialize(body, typeInfo);
        }
        catch (JsonException e)
        {
            throw new JsonException(e.Message);
        }

        return dto ?? throw new JsonException("the document is null");
    }

    private static string Require(string? value, string key) =>
        value ?? throw new JsonException("'" + key + "' is not a string");

    private static int Require(int? value, string key) =>
        value ?? throw new JsonException("'" + key + "' is not an integer");

    /// <summary>Port of FlameMod::loadIndexedPack (node entry).</summary>
    public static void LoadIndexedPack(IndexedPack pack, JsonNode obj) =>
        LoadIndexedPack(pack, Probe(obj, FlameModJsonContext.Default.FlameProjectJson));

    /// <summary>Port of FlameMod::loadIndexedPack (typed entry).</summary>
    internal static void LoadIndexedPack(IndexedPack pack, FlameProjectJson dto)
    {
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
                // upstream requires both name and url; the port has no reader for the
                // display-only url but keeps the parse-time validation
                var name = Require(author.Name, "name");
                _ = Require(author.Url, "url");
                pack.Authors.Add(new ModpackAuthor { Name = name });
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

    /// <summary>The Web-facing typed version-list load: parses the {"data": [...]} response
    /// body and fills the pack (validated files, newest first). Returns false when the data
    /// member is absent (the caller's "no data array" face); JsonException on a broken
    /// body.</summary>
    public static bool LoadIndexedPackVersionsFromResponse(IndexedPack pack, string responseBody)
    {
        var doc = ParseResponse(responseBody, FlameModJsonContext.Default.FlameFileListJson);
        if (doc.Data is null)
        {
            return false;
        }

        LoadIndexedPackVersions(pack, doc.Data);
        return true;
    }

    /// <summary>Port of FlameMod::loadIndexedPackVersions (sorts by file date, newest first).</summary>
    internal static void LoadIndexedPackVersions(IndexedPack pack, List<FlameModFileJson>? files)
    {
        var unsortedVersions = new List<IndexedVersion>();
        foreach (var versionIter in files ?? [])
        {
            if (versionIter is null)
            {
                throw new JsonException("file entry is not an object");
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

    /// <summary>Port of FlameMod::loadIndexedPackVersion (node entry).</summary>
    public static IndexedVersion LoadIndexedPackVersion(JsonNode obj) =>
        LoadIndexedPackVersion(Probe(obj, FlameModJsonContext.Default.FlameModFileJson));

    /// <summary>Port of FlameMod::loadIndexedPackVersion (typed entry).</summary>
    internal static IndexedVersion LoadIndexedPackVersion(FlameModFileJson dto)
    {
        // a missing gameVersions member is a require failure; a non-string element reads as
        // "" like the former AsString fallback (and simply misses every branch below)
        if (dto.GameVersions is not { ValueKind: JsonValueKind.Array } gameVersionsNode)
        {
            throw new JsonException("'gameVersions' is not an array");
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
        file.FileName = FileNaming.RemoveInvalidPathChars(Require(dto.FileName, "fileName"));

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
            var hashAlgo = EnumToString(hashEntry.Algo ?? 1);
            if (hashTypes.Contains(hashAlgo))
            {
                file.Hash = Require(hashEntry.Value, "value");
                file.HashType = hashAlgo;
                break;
            }
        }

        foreach (var dep in dto.Dependencies ?? [])
        {
            var dependency = new Dependency
            {
                AddonId = Require(dep.ModId, "modId").ToString(CultureInfo.InvariantCulture),
                Type = Require(dep.RelationType, "relationType") switch
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
