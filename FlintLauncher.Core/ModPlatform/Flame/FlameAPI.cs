// SPDX-License-Identifier: GPL-3.0-only
// Port of modplatform/flame/FlameAPI.{h,cpp}: the CurseForge v1 API surface (search,
// project/file endpoints, fingerprint matching, categories and the date-based
// latest-version matching) on top of the Net stack.
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
using FlintLauncher.Core.Json;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Versioning;

namespace FlintLauncher.Core.ModPlatform.Flame;

using FlintLauncher.Core.Minecraft;

public sealed class FlameApi : ResourceApi
{
    private FlameApi()
    {
    }

    private static FlameApi? _instance;
    public static FlameApi Instance => _instance ??= new FlameApi();

    // ------------------------------------------------------------------ requests

    /// <summary>Port of FlameAPI::matchFingerprints (POST /fingerprints, batch murmur2).</summary>
    public static (NetJob Job, NetRequest Request) MatchFingerprints(IReadOnlyList<uint> fingerprints)
    {
        var netJob = new NetJob("Flame::MatchFingerprints");

        var bodyObj = new JsonObject();
        var fingerprintsArr = new JsonArray();
        foreach (var fp in fingerprints)
        {
            fingerprintsArr.Add((JsonNode?)JsonValue.Create(fp.ToString(CultureInfo.InvariantCulture)));
        }
        bodyObj["fingerprints"] = fingerprintsArr;

        var bodyRaw = System.Text.Encoding.UTF8.GetBytes(bodyObj.ToJsonString());
        var request = MakeByteArrayRequest($"{FlameConfig.BaseUrl}/fingerprints", bodyRaw);
        netJob.AddNetRequest(request);
        return (netJob, request);
    }

    /// <summary>Port of FlameAPI::getModFileChangelog (GET /mods/{id}/files/{fid}/changelog);
    /// returns "" when the request or the parse fails, like the C++ event-loop version.</summary>
    public static async Task<string> GetModFileChangelog(int modId, int fileId,
        CancellationToken cancellationToken = default)
    {
        var request = MakeByteArrayRequest($"{FlameConfig.BaseUrl}/mods/{modId}/files/{fileId}/changelog");
        await request.StartAsync(cancellationToken);
        if (!request.WasSuccessful)
        {
            return "";
        }
        try
        {
            return JsonNode.Parse(request.BodyAsString)?["data"]?.GetValue<string>() ?? "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    /// <summary>Port of FlameAPI::getModDescription (GET /mods/{id}/description).</summary>
    public static async Task<string> GetModDescription(int modId, CancellationToken cancellationToken = default)
    {
        var request = MakeByteArrayRequest($"{FlameConfig.BaseUrl}/mods/{modId}/description");
        await request.StartAsync(cancellationToken);
        if (!request.WasSuccessful)
        {
            return "";
        }
        try
        {
            return JsonNode.Parse(request.BodyAsString)?["data"]?.GetValue<string>() ?? "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    /// <summary>Port of FlameAPI::getProjects (POST /mods, multiple project ids).</summary>
    public (NetJob Job, NetRequest Request) GetProjects(IReadOnlyList<string> addonIds)
    {
        var netJob = new NetJob("Flame::GetProjects");

        var bodyObj = new JsonObject();
        var addonsArr = new JsonArray();
        foreach (var addonId in addonIds)
        {
            addonsArr.Add((JsonNode?)JsonValue.Create(addonId));
        }
        bodyObj["modIds"] = addonsArr;

        var bodyRaw = System.Text.Encoding.UTF8.GetBytes(bodyObj.ToJsonString());
        var request = MakeByteArrayRequest($"{FlameConfig.BaseUrl}/mods", bodyRaw);
        netJob.AddNetRequest(request);
        return (netJob, request);
    }

    /// <summary>Port of ResourceAPI::getProject for Flame (GET /mods/{id}; the single-project
    /// variant the callers use when only one id is needed).</summary>
    public (NetJob Job, NetRequest Request) GetProject(string addonId)
    {
        var netJob = new NetJob("Flame::GetProject");
        var request = MakeByteArrayRequest(GetInfoURL(addonId)!);
        netJob.AddNetRequest(request);
        return (netJob, request);
    }

    /// <summary>Port of FlameAPI::getFiles (POST /mods/files, multiple file ids).</summary>
    public static (NetJob Job, NetRequest Request) GetFiles(IReadOnlyList<string> fileIds)
    {
        var netJob = new NetJob("Flame::GetFiles");

        var bodyObj = new JsonObject();
        var filesArr = new JsonArray();
        foreach (var fileId in fileIds)
        {
            filesArr.Add((JsonNode?)JsonValue.Create(fileId));
        }
        bodyObj["fileIds"] = filesArr;

        var bodyRaw = System.Text.Encoding.UTF8.GetBytes(bodyObj.ToJsonString());
        var request = MakeByteArrayRequest($"{FlameConfig.BaseUrl}/mods/files", bodyRaw);
        netJob.AddNetRequest(request);
        return (netJob, request);
    }

    /// <summary>Port of FlameAPI::getFile (GET /mods/{addonId}/files/{fileId}).</summary>
    public static (NetJob Job, NetRequest Request) GetFile(string addonId, string fileId)
    {
        var netJob = new NetJob("Flame::GetFile");
        var request = MakeByteArrayRequest($"{FlameConfig.BaseUrl}/mods/{addonId}/files/{fileId}");
        netJob.AddNetRequest(request);
        return (netJob, request);
    }

    /// <summary>Port of FlameAPI::getCategories (GET /categories?gameId=432&classId=...).</summary>
    public static (NetJob Job, NetRequest Request) GetCategories(ResourceType type)
    {
        var netJob = new NetJob("Flame::GetCategories");
        var request = MakeByteArrayRequest($"{FlameConfig.BaseUrl}/categories?gameId=432&classId={GetClassId(type)}");
        netJob.AddNetRequest(request);
        return (netJob, request);
    }

    public (NetJob Job, NetRequest Request) GetModCategories() => GetCategories(ResourceType.Mod);

    /// <summary>Port of FlameAPI::loadModCategories (parse the /categories response).</summary>
    public List<Category> LoadModCategories(string response)
    {
        var categories = new List<Category>();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(response);
        }
        catch (JsonException)
        {
            return categories;
        }

        using (doc)
        {
            try
            {
                var obj = JsonUtils.RequireObject(doc);
                var arr = JsonUtils.RequireArray(FlameJson.Ptr(obj, "data"), "'data'");

                foreach (var val in arr.EnumerateArray())
                {
                    var cat = JsonUtils.RequireObject(val);
                    var id = FlameJson.RequireInteger(cat, "id");
                    var name = FlameJson.RequireString(cat, "name");
                    categories.Add(new Category(name, id.ToString(CultureInfo.InvariantCulture)));
                }
            }
            catch (JsonParseException)
            {
                // "Failed to parse response from a version request." (logged upstream)
            }
        }
        return categories;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Port of FlameAPI::getLatestVersion: pick the newest file for the instance's
    /// loaders (with the no-loader and stale-unspecified-loader tie-breaks of the C++).</summary>
    public static IndexedVersion? GetLatestVersion(IReadOnlyList<IndexedVersion> versions,
                                                   IReadOnlyList<ModLoaderType> instanceLoaders,
                                                   ModLoaderType fallback,
                                                   bool checkLoaders)
    {
        const ModLoaderType noLoader = ModLoaderType.None;
        if (!checkLoaders)
        {
            IndexedVersion? ver = null;
            foreach (var fileTmp in versions)
            {
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

    public override string? GetSearchURL(SearchArgs args)
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

        return FlameConfig.BaseUrl + "/mods/search?gameId=432&" + string.Join("&", getArguments);
    }

    public override string? GetVersionsURL(VersionSearchArgs args)
    {
        var addonId = args.Pack.AddonId;
        var url = $"{FlameConfig.BaseUrl}/mods/{addonId}/files?pageSize=10000";

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

    public override string? GetInfoURL(string id) => $"{FlameConfig.BaseUrl}/mods/{id}";

    public override string? GetDependencyURL(DependencySearchArgs args)
    {
        var addonId = args.Dependency.AddonId;
        var url = $"{FlameConfig.BaseUrl}/mods/{addonId}/files?pageSize=10000&gameVersion={args.McVersion}";
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
            new SortingMethod { Index = 1, Name = "Featured", ReadableName = "Sort by Featured" },
            new SortingMethod { Index = 2, Name = "Popularity", ReadableName = "Sort by Popularity" },
            new SortingMethod { Index = 3, Name = "LastUpdated", ReadableName = "Sort by Last Updated" },
            new SortingMethod { Index = 4, Name = "Name", ReadableName = "Sort by Name" },
            new SortingMethod { Index = 5, Name = "Author", ReadableName = "Sort by Author" },
            new SortingMethod { Index = 6, Name = "TotalDownloads", ReadableName = "Sort by Downloads" },
            new SortingMethod { Index = 7, Name = "Category", ReadableName = "Sort by Category" },
            new SortingMethod { Index = 8, Name = "GameVersion", ReadableName = "Sort by Game Version" },
        ];
    }

    // --------------------------------------------------------- response loading

    /// <summary>Port of ResourceAPI::documentToArray for Flame (the "data" array).</summary>
    public static JsonElement DocumentToArray(JsonDocument obj) => FlameJson.Arr(JsonUtils.RequireObject(obj), "data");

    /// <summary>Port of the FlameAPI::loadIndexedPack override.</summary>
    public void LoadIndexedPack(IndexedPack pack, JsonElement obj) => FlameMod.LoadIndexedPack(pack, obj);

    /// <summary>Port of the FlameAPI::loadIndexedPackVersion override (with the texture-pack
    /// client-side version filter of the C++).</summary>
    public IndexedVersion LoadIndexedPackVersion(JsonElement obj, ResourceType resourceType)
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
        => FlameMod.LoadBodyAsync(pack, cancellationToken);

    // ---------------------------------------------------------------- plumbing

    /// <summary>The x-api-key header the C++ ApiHeaderProxy attaches to Flame API requests
    /// (only sent when a key is configured; see FlameConfig for the shipping story).</summary>
    public static void AddApiKeyHeader(NetRequest request)
    {
        var key = FlameConfig.ApiKeyProvider();
        if (key.Length != 0)
        {
            request.AddHeader("x-api-key", key);
        }
    }

    private static NetRequest MakeByteArrayRequest(string url)
    {
        var request = NetRequest.MakeByteArray(new Uri(url));
        AddApiKeyHeader(request);
        return request;
    }

    private static NetRequest MakeByteArrayRequest(string url, byte[] postData)
    {
        var request = NetRequest.MakeByteArray(new Uri(url), postData);
        AddApiKeyHeader(request);
        return request;
    }
}
