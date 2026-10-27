// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
//
// Port of modplatform/flame/FlameAPI.{h,cpp}: the CurseForge v1 API surface (search,
// project/file endpoints, fingerprint matching, categories and the date-based
// latest-version matching) on top of the persistent client.
//
// The Application-singleton reach-ins become the static FlameConfig hooks (BaseUrl,
// ApiKeyProvider — see FlameConfig.cs for the API key handling notes).
//
// Deviations:
//  - getModFileChangelog/getModDescription return Tasks instead of blocking a QEventLoop.
//  - searchFilter goes through Uri.EscapeDataString (the C++ relies on QUrl's TolerantMode
//    to encode it when the request is built).

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Versioning;

namespace FlintLauncher.Core.ModPlatform.Flame;

/// <summary>The CurseForge batch request bodies — the id lists are strings of decimal
/// numbers, the C++ compatibility quirk kept verbatim.</summary>
public sealed record FlameFingerprintsRequest(
    [property: JsonPropertyName("fingerprints")]
    List<string> Fingerprints);

public sealed record FlameModsRequest(
    [property: JsonPropertyName("modIds")] List<string> ModIds);

public sealed record FlameFilesRequest(
    [property: JsonPropertyName("fileIds")]
    List<string> FileIds);

/// <summary>The {"data": "&lt;string&gt;"} envelope of the changelog/description endpoints.</summary>
public sealed record FlameStringEnvelopeJson(
    [property: JsonPropertyName("data")] string? Data);

[JsonSerializable(typeof(FlameFingerprintsRequest))]
[JsonSerializable(typeof(FlameModsRequest))]
[JsonSerializable(typeof(FlameFilesRequest))]
[JsonSerializable(typeof(FlameStringEnvelopeJson))]
internal sealed partial class FlameJsonContext : JsonSerializerContext;

public sealed class FlameApi(HttpClient http) : ResourceApi
{
    /// <summary>BuildConfig.FLAME_BASE_URL (per-instance; tests construct a client pointed
    /// at their stub).</summary>
    public string BaseUrl { get; set; } = FlintConfig.Default.FlameBaseUrl;

    /// <summary>BuildConfig.FLAME_DOWNLOAD_HOST (the mod file CDN).</summary>
    public string DownloadHost { get; set; } = FlintConfig.Default.FlameDownloadHost;

    /// <summary>
    /// APPLICATION->getFlameAPIKey(). Flint ships no CurseForge key: the default reads
    /// FlintConfig.FlameApiKey, which is empty unless the user overrides it.
    /// </summary>
    public Func<string> ApiKeyProvider { get; set; } = static () => FlintConfig.Default.FlameApiKey;

    /// <summary>The network stack this client's requests go through.</summary>
    public override HttpClient Http { get; } = http;


    // ------------------------------------------------------------------ requests

    /// <summary>Port of FlameAPI::matchFingerprints (POST /fingerprints, batch murmur2);
    /// failures surface as <see cref="FlintHttpException"/> (the old NetRequest status
    /// gate).</summary>
    public Task<string> MatchFingerprintsAsync(IReadOnlyList<uint> fingerprints,
        CancellationToken cancellationToken = default)
    {
        var body = new FlameFingerprintsRequest(
            [.. fingerprints.Select(fp => fp.ToString(CultureInfo.InvariantCulture))]);
        var bodyRaw = JsonSerializer.SerializeToUtf8Bytes(body, FlameJsonContext.Default.FlameFingerprintsRequest);
        return FlintHttp.GetStringAsync(Http,
            BuildRequest(HttpMethod.Post, $"{BaseUrl}/fingerprints", bodyRaw), cancellationToken);
    }

    /// <summary>Port of FlameAPI::getModFileChangelog (GET /mods/{id}/files/{fid}/changelog);
    /// returns "" when the request or the parse fails, like the C++ event-loop version.</summary>
    public async Task<string> GetModFileChangelog(int modId, int fileId,
        CancellationToken cancellationToken = default)
    {
        string body;
        try
        {
            body = await FlintHttp.GetStringAsync(Http,
                BuildRequest(HttpMethod.Get, $"{BaseUrl}/mods/{modId}/files/{fileId}/changelog"),
                cancellationToken);
        }
        catch (FlintHttpException)
        {
            return "";
        }

        try
        {
            return JsonSerializer.Deserialize(body,
                FlameJsonContext.Default.FlameStringEnvelopeJson)?.Data ?? "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    /// <summary>Port of FlameAPI::getModDescription (GET /mods/{id}/description).</summary>
    public async Task<string> GetModDescription(int modId, CancellationToken cancellationToken = default)
    {
        string body;
        try
        {
            body = await FlintHttp.GetStringAsync(Http,
                BuildRequest(HttpMethod.Get, $"{BaseUrl}/mods/{modId}/description"), cancellationToken);
        }
        catch (FlintHttpException)
        {
            return "";
        }

        try
        {
            return JsonSerializer.Deserialize(body,
                FlameJsonContext.Default.FlameStringEnvelopeJson)?.Data ?? "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    /// <summary>Port of FlameAPI::getProjects (POST /mods, multiple project ids).</summary>
    public Task<string> GetProjectsAsync(IReadOnlyList<string> addonIds,
        CancellationToken cancellationToken = default)
    {
        var body = new FlameModsRequest([.. addonIds]);
        var bodyRaw = JsonSerializer.SerializeToUtf8Bytes(body, FlameJsonContext.Default.FlameModsRequest);
        return FlintHttp.GetStringAsync(Http,
            BuildRequest(HttpMethod.Post, $"{BaseUrl}/mods", bodyRaw), cancellationToken);
    }

    /// <summary>Port of ResourceAPI::getProject for Flame (GET /mods/{id}; the single-project
    /// variant the callers use when only one id is needed).</summary>
    public Task<string> GetProjectAsync(string addonId, CancellationToken cancellationToken = default) =>
        FlintHttp.GetStringAsync(Http,
            BuildRequest(HttpMethod.Get, GetInfoUrl(addonId)), cancellationToken);

    /// <summary>Port of FlameAPI::getFiles (POST /mods/files, multiple file ids).</summary>
    public Task<string> GetFilesAsync(IReadOnlyList<string> fileIds,
        CancellationToken cancellationToken = default)
    {
        var body = new FlameFilesRequest([.. fileIds]);
        var bodyRaw = JsonSerializer.SerializeToUtf8Bytes(body, FlameJsonContext.Default.FlameFilesRequest);
        return FlintHttp.GetStringAsync(Http,
            BuildRequest(HttpMethod.Post, $"{BaseUrl}/mods/files", bodyRaw), cancellationToken);
    }

    /// <summary>Port of FlameAPI::getFile (GET /mods/{addonId}/files/{fileId}).</summary>
    public Task<string> GetFileAsync(string addonId, string fileId,
        CancellationToken cancellationToken = default) =>
        FlintHttp.GetStringAsync(Http,
            BuildRequest(HttpMethod.Get, $"{BaseUrl}/mods/{addonId}/files/{fileId}"), cancellationToken);

    /// <summary>Port of FlameAPI::getCategories (GET /categories?gameId=432&amp;classId=...).</summary>
    public Task<string> GetCategoriesAsync(ResourceType type, CancellationToken cancellationToken = default)
    {
        return GetCategoriesForClassIdAsync(GetClassId(type), cancellationToken);
    }

    /// <summary>The raw-classId variant the categories endpoint uses (search hits carry a
    /// CurseForge classId that may not have a ResourceType in the map above).</summary>
    public Task<string> GetCategoriesForClassIdAsync(int classId,
        CancellationToken cancellationToken = default) =>
        FlintHttp.GetStringAsync(Http,
            BuildRequest(HttpMethod.Get, $"{BaseUrl}/categories?gameId=432&classId={classId}"),
            cancellationToken);

    public Task<string> GetModCategoriesAsync(CancellationToken cancellationToken = default) =>
        GetCategoriesAsync(ResourceType.Mod, cancellationToken);

    /// <summary>Port of FlameAPI::loadModCategories (parse the /categories response).</summary>
    public List<Category> LoadModCategories(string response)
    {
        var categories = new List<Category>();
        try
        {
            var doc = FlameMod.ParseResponse(response, FlameModJsonContext.Default.FlameCategoryListJson);
            foreach (var cat in doc.Data ?? [])
            {
                // the former per-entry Require pair failed the whole parse into the same
                // empty-list catch; a real CF response always carries both members
                categories.Add(new Category(cat.Name ?? "", (cat.Id ?? 0).ToString(CultureInfo.InvariantCulture)));
            }
        }
        catch (JsonException)
        {
            // "Failed to parse response from a version request." (logged upstream)
        }
        return categories;
    }

    // ------------------------------------------------------------------ helpers

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
    private static int GetMappedModLoader(ModLoaderType loaders) => loaders switch
    {
        ModLoaderType.Forge => 1,
        ModLoaderType.Cauldron => 2,
        ModLoaderType.LiteLoader => 3,
        ModLoaderType.Fabric => 4,
        ModLoaderType.Quilt => 5,
        ModLoaderType.NeoForge => 6,
        _ => 0, // DataPack/Babric/BTA/LegacyFabric/Ornithe/Rift/None: not supported
    };

    private static List<string> GetModLoaderStrings(ModLoaderType types)
    {
        List<ModLoaderType> all = [ModLoaderType.NeoForge, ModLoaderType.Forge, ModLoaderType.Fabric, ModLoaderType.Quilt];
        var l = new List<string>();
        foreach (var loader in all)
        {
            if ((types & loader) != 0)
            {
                l.Add(GetMappedModLoader(loader).ToString(CultureInfo.InvariantCulture));
            }
        }

        return l;
    }

    private static string GetModLoaderFilters(ModLoaderType types) => "[" + string.Join(",", GetModLoaderStrings(types)) + "]";

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

    // ------------------------------------------------------------ URL builders

    public override string GetSearchUrl(SearchArgs args)
    {
        var getArguments = new List<string>
        {
            $"classId={GetClassId(args.Type)}",
            $"index={args.Offset}",
            "pageSize=25",
        };
        if (args.Search is { } search)
        {
            getArguments.Add($"searchFilter={Uri.EscapeDataString(search)}");
        }

        if (args.Sorting is { } sorting)
        {
            getArguments.Add($"sortField={sorting.Index}");
        }

        getArguments.Add("sortOrder=desc");
        if (args.Loaders is { } loaders)
        {
            loaders &= ~ModLoaderType.DataPack;
            if (loaders != 0)
            {
                getArguments.Add($"modLoaderTypes={GetModLoaderFilters(loaders)}");
            }
        }

        if (args.CategoryIds is { } categoryIds && categoryIds.Count != 0)
        {
            getArguments.Add($"categoryIds=[{string.Join(",", categoryIds)}]");
        }

        if (args.Versions is { } versions && versions.Count != 0)
        {
            getArguments.Add($"gameVersion={versions[0]}");
        }

        return BaseUrl + "/mods/search?gameId=432&" + string.Join("&", getArguments);
    }

    public override string GetVersionsUrl(VersionSearchArgs args)
    {
        var addonId = args.Pack.AddonId;
        var url = $"{BaseUrl}/mods/{addonId}/files?pageSize=10000";

        if (args.McVersions is { } mcVersions)
        {
            url += $"&gameVersion={mcVersions[0]}";
        }

        if (args.Loaders is { } loaders && loaders != ModLoaderType.DataPack
                                        && ModIndex.HasSingleModLoaderSelected(loaders))
        {
            var mappedModLoader = GetMappedModLoader(loaders);
            url += $"&modLoaderType={mappedModLoader}";
        }

        return url;
    }

    public override string GetInfoUrl(string id) => $"{BaseUrl}/mods/{id}";

    public override string GetDependencyUrl(DependencySearchArgs args)
    {
        var addonId = args.Dependency.AddonId;
        var url = $"{BaseUrl}/mods/{addonId}/files?pageSize=10000&gameVersion={args.McVersion}";
        if (args.Loader != 0 && ModIndex.HasSingleModLoaderSelected(args.Loader))
        {
            var mappedModLoader = GetMappedModLoader(args.Loader);
            url += $"&modLoaderType={mappedModLoader}";
        }

        return url;
    }

    public override List<SortingMethod> GetSortingMethods()
    {
        // https://docs.curseforge.com/?python#tocS_ModsSearchSortField
        return
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

    // --------------------------------------------------------- response loading

    /// <summary>Port of the FlameAPI::loadIndexedPack override.</summary>
    public void LoadIndexedPack(IndexedPack pack, JsonNode obj) => FlameMod.LoadIndexedPack(pack, obj);

    /// <summary>Port of the FlameAPI::loadIndexedPackVersion override (with the texture-pack
    /// client-side version filter of the C++).</summary>
    public IndexedVersion LoadIndexedPackVersion(JsonNode obj, ResourceType resourceType)
    {
        var arr = FlameMod.LoadIndexedPackVersion(obj);
        if (resourceType != ResourceType.TexturePack)
        {
            return arr;
        }

        // FIXME upstream: client-side version filtering, ignoring user-selected filters
        foreach (var mcVersion in arr.McVersion)
        {
            if (new McVersion(mcVersion) <= new McVersion("1.6"))
            {
                return arr;
            }
        }

        return new IndexedVersion();
    }

    /// <summary>Port of the FlameAPI::loadExtraPackInfo override (see FlameMod.LoadBodyAsync).</summary>
    public Task LoadExtraPackInfoAsync(IndexedPack pack, CancellationToken cancellationToken = default)
        => FlameMod.LoadBodyAsync(pack, this, cancellationToken);

    // ---------------------------------------------------------------- plumbing

    /// <summary>The x-api-key header the C++ ApiHeaderProxy attaches to Flame API requests
    /// (only sent when a key is configured; see FlameConfig for the shipping story).</summary>
    public void AddApiKeyHeader(HttpRequestMessage message)
    {
        var key = ApiKeyProvider();
        if (key.Length != 0)
        {
            message.Headers.TryAddWithoutValidation("x-api-key", key);
        }
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, string url, byte[]? postData = null)
    {
        var message = new HttpRequestMessage(method, url);
        if (postData is not null)
        {
            message.Content = new ByteArrayContent(postData);
        }

        AddApiKeyHeader(message);
        return message;
    }
}
