// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
//
// Port of modplatform/flame/FlameModIndex.{h,cpp}: map the CurseForge wire models (the
// ExternalApi.Flame types) into the provider-neutral ModIndex models, plus the
// launcher-vocabulary translations the CurseForge wire needs (the class-id table, the
// loader-flag mapping and the date-based latest-version pick of FlameAPI).
//
// Deviations:
//  - loadIndexedPackVersion's `loadChangelog` parameter is dropped: every in-repo caller
//    passes the default (false) and fetches changelogs explicitly through the changelog
//    operation (the C++ made it synchronous through a QEventLoop).
//  - loadBody (the description fetch) is not ported: its only caller was the retired Core
//    adapter — the description consumers run the operation themselves.
//  - the upstream quirk that resets file.side to NoSide on every gameVersions iteration is
//    kept verbatim (the "both sides" branch is unreachable, exactly like the C++).

using System.Globalization;
using System.Text.Json;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.ExternalApi.Flame;

namespace FlintLauncher.Core.ModPlatform.Flame;

/// <summary>The CurseForge sort vocabulary (the sortField indexes of the search endpoint;
/// https://docs.curseforge.com/?python#tocS_ModsSearchSortField).</summary>
public sealed record SortingMethod
{
    /// <summary>The index used in the search request's sortField.</summary>
    public required uint Index { get; init; }

    /// <summary>The name as used in the API specification.</summary>
    public required string Name { get; init; }
}

// https://docs.curseforge.com/#tocS_Mod
public static class FlameMod
{
    private static string Require(string? value, string key) =>
        value ?? throw new JsonException("'" + key + "' is not a string");

    private static int Require(int? value, string key) =>
        value ?? throw new JsonException("'" + key + "' is not an integer");

    // ------------------------------------------------------------------ pack loading

    /// <summary>Port of FlameMod::loadIndexedPack.</summary>
    public static void LoadIndexedPack(IndexedPack pack, FlameProject dto)
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

        pack.ResourceType = GetResourceType(dto.ClassId ?? 0);
        pack.ExtraDataLoaded = false;
        MapUrls(dto.Links, pack);
    }

    /// <summary>Port of FlameMod::loadURLs.</summary>
    private static void MapUrls(FlameLinks? links, IndexedPack pack)
    {
        pack.ExtraData.IssuesUrl = ChopTrailingSlash(links?.IssuesUrl ?? "");
        pack.ExtraData.SourceUrl = ChopTrailingSlash(links?.SourceUrl ?? "");
        pack.ExtraData.WikiUrl = ChopTrailingSlash(links?.WikiUrl ?? "");

        if (pack.ExtraData.Body.Length != 0)
        {
            pack.ExtraDataLoaded = true;
        }
    }

    private static string ChopTrailingSlash(string url) => url.EndsWith('/') ? url[..^1] : url;

    // --------------------------------------------------------------- version loading

    /// <summary>The typed version-list load: fills the pack from the {"data": [...]}
    /// response (validated files, newest first). Returns false when the data member is
    /// absent (the caller's "no data array" face).</summary>
    public static bool LoadIndexedPackVersionsFromResponse(IndexedPack pack, FlameFileList doc)
    {
        if (doc.Data is null)
        {
            return false;
        }

        LoadIndexedPackVersions(pack, doc.Data);
        return true;
    }

    /// <summary>Port of FlameMod::loadIndexedPackVersions (sorts by file date, newest first).</summary>
    public static void LoadIndexedPackVersions(IndexedPack pack, List<FlameModFile>? files)
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

    /// <summary>Port of FlameMod::loadIndexedPackVersion.</summary>
    public static IndexedVersion LoadIndexedPackVersion(FlameModFile dto)
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
                },
            };
            file.Dependencies.Add(dependency);
        }

        return file;
    }

    /// <summary>enumToString helper: the CF hash algorithm ids (1=sha1, 2=md5).</summary>
    private static string EnumToString(int hashAlgorithm) => hashAlgorithm switch
    {
        2 => "md5",
        _ => "sha1",
    };

    // --------------------------------------------------------------- category loading

    /// <summary>Port of FlameAPI::loadModCategories (the /categories response).</summary>
    public static List<Category> LoadModCategories(FlameCategoryList doc)
    {
        var categories = new List<Category>();
        foreach (var cat in doc.Data ?? [])
        {
            // the former per-entry Require pair failed the whole parse into the same
            // empty-list catch; a real CF response always carries both members
            categories.Add(new Category(cat.Name ?? "", (cat.Id ?? 0).ToString(CultureInfo.InvariantCulture)));
        }

        return categories;
    }

    // -------------------------------------------------- latest-version & loader vocab

    /// <summary>Port of FlameAPI::getLatestVersion: pick the newest file for the instance's
    /// loaders (with the no-loader and stale-unspecified-loader tie-breaks of the C++); when
    /// release types are given, files on other channels are skipped (#3404).</summary>
    public static IndexedVersion? GetLatestVersion(IReadOnlyList<IndexedVersion> versions,
        IReadOnlyList<ModLoaderType> instanceLoaders,
        ModLoaderType fallback,
        bool checkLoaders,
        IReadOnlyList<IndexedVersionType>? releaseTypes = null)
    {
        const ModLoaderType noLoader = ModLoaderType.None;
        if (!checkLoaders)
        {
            IndexedVersion? ver = null;
            foreach (var fileTmp in versions)
            {
                if (releaseTypes is { Count: > 0 } && !releaseTypes.Contains(fileTmp.VersionType))
                {
                    continue;
                }

                if (ver is null || string.CompareOrdinal(fileTmp.Date, ver.Date) > 0)
                {
                    ver = fileTmp;
                }
            }

            return ver;
        }

        var bestMatch = new Dictionary<ModLoaderType, IndexedVersion>();

        void CheckVersion(IndexedVersion version, ModLoaderType loader)
        {
            if (bestMatch.TryGetValue(loader, out var best))
            {
                if (string.CompareOrdinal(version.Date, best.Date) > 0)
                {
                    bestMatch[loader] = version;
                }
            }
            else
            {
                bestMatch[loader] = version;
            }
        }

        foreach (var fileTmp in versions)
        {
            if (releaseTypes is { Count: > 0 } && !releaseTypes.Contains(fileTmp.VersionType))
            {
                continue;
            }

            var loaders = ModIndex.ModLoaderTypesToList(fileTmp.Loaders);
            if (loaders.Count == 0)
            {
                CheckVersion(fileTmp, noLoader);
            }
            else
            {
                foreach (var loader in loaders)
                {
                    CheckVersion(fileTmp, loader);
                }
            }
        }

        // edge case: mod has installed for forge but the instance is fabric => fabric version
        // will be prioritized on update
        var currentLoaders = new List<ModLoaderType>(instanceLoaders);
        currentLoaders.AddRange(ModIndex.ModLoaderTypesToList(fallback));
        currentLoaders.Add(noLoader); // add a fallback in case the versions do not define a loader

        foreach (var loader in currentLoaders)
        {
            if (bestMatch.TryGetValue(loader, out var bestForLoader))
            {
                // awkward case where the mod has only two loaders and one of them is not specified
                if (loader != noLoader && bestMatch.TryGetValue(noLoader, out var bestForNoLoader)
                                       && bestMatch.Count == 2
                                       && string.CompareOrdinal(bestForNoLoader.Date, bestForLoader.Date) > 0)
                {
                    return bestForNoLoader;
                }

                return bestForLoader;
            }
        }

        return null;
    }

    /// <summary>Port of FlameAPI::validateModLoaders.</summary>
    public static bool ValidateModLoaders(ModLoaderType loaders)
    {
        return (loaders & (ModLoaderType.NeoForge | ModLoaderType.Forge | ModLoaderType.Fabric | ModLoaderType.Quilt)) != 0;
    }

    // https://docs.curseforge.com/?http#tocS_ModLoaderType
    public static int GetMappedModLoader(ModLoaderType loaders) => loaders switch
    {
        ModLoaderType.Forge => 1,
        ModLoaderType.Cauldron => 2,
        ModLoaderType.LiteLoader => 3,
        ModLoaderType.Fabric => 4,
        ModLoaderType.Quilt => 5,
        ModLoaderType.NeoForge => 6,
        _ => 0, // DataPack/Babric/BTA/LegacyFabric/Ornithe/Rift/None: not supported
    };

    /// <summary>The loader ids the search endpoint's modLoaderTypes filter takes for a
    /// loader-flag set.</summary>
    public static List<int> GetModLoaderFilterIds(ModLoaderType types)
    {
        List<ModLoaderType> all = [ModLoaderType.NeoForge, ModLoaderType.Forge, ModLoaderType.Fabric, ModLoaderType.Quilt];
        var l = new List<int>();
        foreach (var loader in all)
        {
            if ((types & loader) != 0)
            {
                l.Add(GetMappedModLoader(loader));
            }
        }

        return l;
    }

    // --------------------------------------------------------------- class ids

    private static readonly (ResourceType Type, int ClassId)[] ClassIdMappings =
    [
        (ResourceType.Mod, 6),
        (ResourceType.ResourcePack, 12),
        (ResourceType.World, 17),
        (ResourceType.ShaderPack, 6552),
        (ResourceType.Modpack, 4471),
        (ResourceType.DataPack, 6945),
    ];

    public static int GetClassId(ResourceType type)
    {
        foreach (var (t, classId) in ClassIdMappings)
        {
            if (t == type)
            {
                return classId;
            }
        }

        return 0;
    }

    public static ResourceType GetResourceType(int classId)
    {
        foreach (var (type, c) in ClassIdMappings)
        {
            if (c == classId)
            {
                return type;
            }
        }

        return ResourceType.Unknown;
    }

    // ------------------------------------------------------------- sorting methods

    public static List<SortingMethod> GetSortingMethods() =>
    [
        new SortingMethod { Index = 1, Name = "Featured" },
        new SortingMethod { Index = 2, Name = "Popularity" },
        new SortingMethod { Index = 3, Name = "LastUpdated" },
        new SortingMethod { Index = 4, Name = "Name" },
        new SortingMethod { Index = 5, Name = "Author" },
        new SortingMethod { Index = 6, Name = "TotalDownloads" },
        new SortingMethod { Index = 7, Name = "Category" },
        new SortingMethod { Index = 8, Name = "GameVersion" },
    ];
}
