// SPDX-License-Identifier: GPL-3.0-only
// Port of modplatform/modrinth/ModrinthAPI.{h,cpp}: the Modrinth v2 API surface (search
// with facets, project/version endpoints, hash-based version lookup and updates) on top of
// the Net stack. The Application-singleton reach-ins became injected instance state:
//  - BuildConfig.MODRINTH_PROD_URL / _STAGING_URL -> BaseUrl / StagingBaseUrl (per-instance,
//    tests construct their own client pointed at a stub)
//  - APPLICATION->getModrinthAPIToken() -> ApiToken (optional PAT, sent as Authorization)
//  - the net/ApiHeaderProxy header logic -> AddApiHeaders (PAT + modrinth-download-meta)
//  - the QNAM -> the injected NetStack
// Pure helpers (URL builders, loaders/facets mapping, response shaping) stay static — no
// configuration behind them.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.Json;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Versioning;

namespace FlintLauncher.Core.ModPlatform.Modrinth;

public sealed class ModrinthApi : ResourceApi
{
    public string BaseUrl { get; set; } = "https://api.modrinth.com/v2";
    public string StagingBaseUrl { get; set; } = "https://staging-api.modrinth.com/v2";
    public static IReadOnlyList<string> MrpackHosts { get; } = ["cdn.modrinth.com", "github.com", "raw.githubusercontent.com", "gitlab.com"];
    public string DownloadHost { get; set; } = "cdn.modrinth.com";

    /// <summary>Optional user PAT (APPLICATION->getModrinthAPIToken()); "" disables the header.</summary>
    public string ApiToken { get; set; } = "";

    /// <summary>The network stack this client's requests go through.</summary>
    public NetStack Net { get; }

    public ModrinthApi(NetStack? net = null)
    {
        Net = net ?? NetStack.Default;
    }

    /// <summary>Port of ModrinthDownloadMeta (net/ApiHeaderProxy.h).</summary>
    public readonly record struct DownloadMeta(string Reason, string GameVersion, string Loader, string DependentOn)
    {
        // default(DownloadMeta) produces null fields; treat them like the C++ empty strings
        public bool IsEmpty => string.IsNullOrEmpty(Reason);

        public string ToJson()
        {
            var obj = new JsonObject();
            if (!string.IsNullOrEmpty(Reason))
            {
                obj["reason"] = JsonValue.Create(Reason);
            }
            if (!string.IsNullOrEmpty(GameVersion))
            {
                obj["game_version"] = JsonValue.Create(GameVersion);
            }
            if (!string.IsNullOrEmpty(Loader))
            {
                obj["loader"] = JsonValue.Create(Loader);
            }
            if (!string.IsNullOrEmpty(DependentOn))
            {
                obj["dependent_on"] = JsonValue.Create(DependentOn);
            }
            return obj.ToJsonString();
        }
    }

    /// <summary>Port of the ApiHeaderProxy host checks (PAT + download-meta headers).</summary>
    public void AddApiHeaders(NetRequest request, DownloadMeta meta = default)
    {
        var host = request.Url.Host;
        if (host == new Uri(BaseUrl).Host || host == new Uri(StagingBaseUrl).Host)
        {
            if (ApiToken.Length != 0)
            {
                request.AddHeader("Authorization", ApiToken);
            }
        }

        if (host == new Uri(DownloadHost.StartsWith("http", StringComparison.Ordinal) ? DownloadHost : "https://" + DownloadHost).Host
            && !meta.IsEmpty)
        {
            request.AddHeader("modrinth-download-meta", meta.ToJson());
        }
    }

    // ------------------------------------------------------------------ requests

    /// <summary>
    /// Port of ModrinthAPI::getModpackIdFromUrl: the slug of a modrinth://modpack/&lt;slug&gt;
    /// link. The scheme and host compare case-insensitively; the (percent-decoded) path must
    /// hold exactly one segment. Any other link yields "".
    /// </summary>
    public static string GetModpackIdFromUrl(string url)
    {
        Uri? uri;
        try
        {
            uri = new Uri(url);
        }
        catch (UriFormatException)
        {
            return "";
        }

        // QUrl::scheme()/host() are case-insensitive comparisons in the C++
        if (!string.Equals(uri.Scheme, "modrinth", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "modpack", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        // QUrl::fromPercentEncoding + split('/', SkipEmptyParts)
        var decoded = Uri.UnescapeDataString(uri.AbsolutePath);
        var segments = decoded.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length != 1)
        {
            return "";
        }

        return segments[0].Trim();
    }

    /// <summary>Port of ModrinthAPI::currentVersion (GET /version_file/{hash}?algorithm=).</summary>
    public (NetJob Job, NetRequest Request) CurrentVersion(string hash, string hashFormat)
    {
        var netJob = new NetJob("Modrinth::GetCurrentVersion");
        var request = MakeByteArrayRequest($"{BaseUrl}/version_file/{hash}?algorithm={hashFormat}");
        netJob.AddNetRequest(request);
        return (netJob, request);
    }

    /// <summary>Port of ModrinthAPI::currentVersions (POST /version_files, multiple hashes).</summary>
    public (NetJob Job, NetRequest Request) CurrentVersions(IReadOnlyList<string> hashes, string hashFormat)
    {
        var netJob = new NetJob("Modrinth::GetCurrentVersions");

        var bodyObj = new JsonObject();
        JsonUtils.WriteStringList(bodyObj, "hashes", hashes);
        JsonUtils.WriteString(bodyObj, "algorithm", hashFormat);
        var bodyRaw = Encoding.UTF8.GetBytes(bodyObj.ToJsonString());

        var request = MakeByteArrayRequest($"{BaseUrl}/version_files", bodyRaw);
        netJob.AddNetRequest(request);
        return (netJob, request);
    }

    /// <summary>Port of ModrinthAPI::latestVersion (POST /version_file/{hash}/update); the
    /// optional release types become the body's "version_types" list (#3404).</summary>
    public (NetJob Job, NetRequest Request) LatestVersion(string hash,
                                                           string hashFormat,
                                                           IReadOnlyList<McVersion>? mcVersions,
                                                           ModLoaderType? loaders,
                                                           IReadOnlyList<IndexedVersionType>? releaseTypes = null)
    {
        var netJob = new NetJob("Modrinth::GetLatestVersion");

        var bodyObj = new JsonObject();
        if (loaders is { } loaderValue)
        {
            JsonUtils.WriteStringList(bodyObj, "loaders", GetModLoaderStrings(loaderValue));
        }
        if (mcVersions is not null)
        {
            JsonUtils.WriteStringList(bodyObj, "game_versions",
                mcVersions.Select(MapMcVersionToModrinth).ToList());
        }
        AddVersionTypes(bodyObj, releaseTypes);

        var bodyRaw = Encoding.UTF8.GetBytes(bodyObj.ToJsonString());

        var request = MakeByteArrayRequest($"{BaseUrl}/version_file/{hash}/update?algorithm={hashFormat}", bodyRaw);
        netJob.AddNetRequest(request);
        return (netJob, request);
    }

    /// <summary>Port of ModrinthAPI::latestVersions (POST /version_files/update); the
    /// optional release types become the body's "version_types" list (#3404).</summary>
    public (NetJob Job, NetRequest Request) LatestVersions(IReadOnlyList<string> hashes,
                                                           string hashFormat,
                                                           IReadOnlyList<McVersion>? mcVersions,
                                                           ModLoaderType? loaders,
                                                           IReadOnlyList<IndexedVersionType>? releaseTypes = null)
    {
        var netJob = new NetJob("Modrinth::GetLatestVersions");

        var bodyObj = new JsonObject();
        JsonUtils.WriteStringList(bodyObj, "hashes", hashes);
        JsonUtils.WriteString(bodyObj, "algorithm", hashFormat);
        if (loaders is { } loaderValue)
        {
            JsonUtils.WriteStringList(bodyObj, "loaders", GetModLoaderStrings(loaderValue));
        }
        if (mcVersions is not null)
        {
            JsonUtils.WriteStringList(bodyObj, "game_versions",
                mcVersions.Select(MapMcVersionToModrinth).ToList());
        }
        AddVersionTypes(bodyObj, releaseTypes);

        var bodyRaw = Encoding.UTF8.GetBytes(bodyObj.ToJsonString());

        var request = MakeByteArrayRequest($"{BaseUrl}/version_files/update", bodyRaw);
        netJob.AddNetRequest(request);
        return (netJob, request);
    }

    /// <summary>Port of the addVersionTypes helper: write "version_types" when the release
    /// types carry at least one valid entry.</summary>
    private static void AddVersionTypes(JsonObject bodyObj, IReadOnlyList<IndexedVersionType>? releaseTypes)
    {
        if (releaseTypes is not { Count: > 0 })
        {
            return;
        }
        var versionTypes = IndexedVersionType.ToModrinthList(releaseTypes);
        if (versionTypes.Count != 0)
        {
            JsonUtils.WriteStringList(bodyObj, "version_types", versionTypes);
        }
    }

    /// <summary>Port of ModrinthAPI::getProjects (GET /projects?ids=[...]).</summary>
    public (NetJob Job, NetRequest Request) GetProjects(IReadOnlyList<string> addonIds)
    {
        var netJob = new NetJob("Modrinth::GetProjects");
        var searchUrl = GetMultipleModInfoUrl(addonIds);
        var request = MakeByteArrayRequest(searchUrl);
        netJob.AddNetRequest(request);
        return (netJob, request);
    }

    /// <summary>Port of ModrinthAPI::getProject — the single-project GET (ResourceAPI::getProject).</summary>
    public (NetJob? Job, NetRequest? Request) GetProject(string addonId)
    {
        var url = GetInfoUrl(addonId);
        if (url is null)
        {
            return (null, null);
        }
        var netJob = new NetJob("Modrinth::GetProject");
        var request = MakeByteArrayRequest(url);
        netJob.AddNetRequest(request);
        return (netJob, request);
    }

    /// <summary>Port of ModrinthAPI::getModCategories (GET /tag/category).</summary>
    public (NetJob Job, NetRequest Request) GetModCategories()
    {
        var netJob = new NetJob("Modrinth::GetCategories");
        var request = MakeByteArrayRequest($"{BaseUrl}/tag/category");
        netJob.AddNetRequest(request);
        return (netJob, request);
    }

    /// <summary>Port of ModrinthAPI::loadCategories (project_type filter).</summary>
    public static List<Category> LoadCategories(string response, string projectType)
    {
        var categories = new List<Category>();
        JsonArray? arr;
        try
        {
            arr = JsonNode.Parse(response) as JsonArray;
        }
        catch (JsonException)
        {
            return categories;
        }

        if (arr is null)
        {
            return categories;
        }
        foreach (var cat in arr)
        {
            if (cat is not JsonObject)
            {
                continue;
            }
            var name = JsonUtils.EnsureString(cat.GetPropertySafe("name"));
            if (cat.GetPropertySafe("project_type").AsString() == projectType)
            {
                categories.Add(new Category(name, name));
            }
        }
        return categories;
    }

    public List<Category> LoadModCategories(string response) => LoadCategories(response, "mod");

    /// <summary>Port of the GET /project/{id}/description endpoint: the project body as
    /// plain text (Modrinth has no per-file changelog endpoint — changelogs ride on the
    /// version objects). Returns "" when the request fails, like the Flame API helpers.</summary>
    public async Task<string> GetProjectDescription(string projectId,
        CancellationToken cancellationToken = default)
    {
        var request = MakeByteArrayRequest($"{BaseUrl}/project/{projectId}/description");
        await request.StartAsync(cancellationToken);
        if (!request.WasSuccessful)
        {
            return "";
        }
        return request.BodyAsString;
    }

    // ------------------------------------------------------------------ URL builders

    public static string GetAuthorUrl(string name) => "https://modrinth.com/user/" + name;

    /// <summary>Port of ModrinthAPI::getModLoaderStrings.</summary>
    public static List<string> GetModLoaderStrings(ModLoaderType types)
    {
        List<ModLoaderType> all =
        [
            ModLoaderType.NeoForge, ModLoaderType.Forge, ModLoaderType.Fabric,
            ModLoaderType.Quilt, ModLoaderType.LiteLoader, ModLoaderType.DataPack,
            ModLoaderType.Babric, ModLoaderType.Bta, ModLoaderType.LegacyFabric,
            ModLoaderType.Ornithe, ModLoaderType.Rift,
        ];
        var l = new List<string>();
        foreach (var loader in all)
        {
            if ((types & loader) != 0)
            {
                l.Add(ModIndex.GetModLoaderAsString(loader));
            }
        }
        return l;
    }

    /// <summary>Port of ModrinthAPI::getModLoaderFilters.</summary>
    public static string GetModLoaderFilters(ModLoaderType types)
    {
        var l = GetModLoaderStrings(types).Select(loader => $"\"categories:{loader}\"");
        return string.Join(",", l);
    }

    /// <summary>Port of ModrinthAPI::getSideFilters.</summary>
    public static string GetSideFilters(SideType side) => side.Value switch
    {
        SideTypeValue.ClientSide =>
            "\"environment:client_only\",\"environment:client_only_server_optional\",\"environment:singleplayer_only\",\"environment:client_or_server\",\"environment:client_or_server_prefers_both\"",
        SideTypeValue.ServerSide =>
            "\"environment:server_only\",\"environment:server_only_client_optional\",\"environment:dedicated_server_only\",\"environment:client_or_server\",\"environment:client_or_server_prefers_both\"",
        SideTypeValue.UniversalSide => "\"environment:client_and_server\",\"client_or_server_prefers_both\"",
        _ => "",
    };

    /// <summary>Port of ModrinthAPI::mapMCVersionFromModrinth.</summary>
    public static string MapMcVersionFromModrinth(string v)
    {
        const string preString = " Pre-Release ";
        var pre = v.Contains("-pre", StringComparison.Ordinal);
        if (pre)
        {
            v = v.Replace("-pre", preString);
        }
        v = v.Replace("-", " ");
        if (pre)
        {
            v = v.Replace(" Pre Release ", preString);
        }
        return v;
    }

    /// <summary>Port of ModrinthAPI::getResourceType.</summary>
    public static ResourceType GetResourceType(string param) => param switch
    {
        "mod" => ResourceType.Mod,
        "resourcepack" => ResourceType.ResourcePack,
        "shader" => ResourceType.ShaderPack,
        "datapack" => ResourceType.DataPack,
        "modpack" => ResourceType.Modpack,
        _ => ResourceType.Unknown,
    };

    private static string ResourceParameter(ResourceType type) => type switch
    {
        ResourceType.Mod => "mod",
        ResourceType.ResourcePack => "resourcepack",
        ResourceType.ShaderPack => "shader",
        ResourceType.DataPack => "datapack",
        ResourceType.Modpack => "modpack",
        _ => "",
    };

    /// <summary>Port of ModrinthAPI::createFacets.</summary>
    private static string CreateFacets(SearchArgs args)
    {
        var facetsList = new List<string>();

        if (args.Loaders is { } loaders && loaders != 0)
        {
            facetsList.Add($"[{GetModLoaderFilters(loaders)}]");
        }
        if (args.Versions is { } versions && versions.Count != 0)
        {
            facetsList.Add($"[{GetGameVersionsArray(versions)}]");
        }
        if (args.Side is { } side)
        {
            var sideFilter = GetSideFilters(side);
            if (sideFilter.Length != 0)
            {
                facetsList.Add($"[{sideFilter}]");
            }
        }
        if (args.CategoryIds is { } categoryIds && categoryIds.Count != 0)
        {
            // #6099: every search category gets a facet array of its own (a category list is
            // an OR within one array; separate arrays AND them together like the UI expects)
            foreach (var category in categoryIds)
            {
                facetsList.Add($"[\"categories:{category}\"]");
            }
        }
        foreach (var d in args.ExcludeDisclosureTypes)
        {
            facetsList.Add($"[\"disclosure_types!={d}\"]");
        }
        if (args.OpenSource)
        {
            facetsList.Add("[\"open_source:true\"]");
        }

        facetsList.Add($"[\"project_type:{ResourceParameter(args.Type)}\"]");

        return $"[{string.Join(",", facetsList)}]";
    }

    public override string? GetSearchUrl(SearchArgs args)
    {
        if (args.Loaders is { } loaders && loaders != 0)
        {
            if (!ValidateModLoaders(loaders))
            {
                // "Modrinth - or our interface - does not support any the provided mod loaders!"
                return null;
            }
        }

        var getArguments = new List<string>
        {
            $"offset={args.Offset}",
            "limit=25",
        };
        if (args.Search is { } search)
        {
            getArguments.Add($"query={Uri.EscapeDataString(search)}");
        }
        if (args.Sorting is { } sorting)
        {
            getArguments.Add($"index={sorting.Name}");
        }
        getArguments.Add($"facets={Uri.EscapeDataString(CreateFacets(args))}");

        return BaseUrl + "/search?" + string.Join("&", getArguments);
    }

    public override string? GetInfoUrl(string id) => BaseUrl + "/project/" + id;

    /// <summary>Port of ModrinthAPI::getMultipleModInfoURL.</summary>
    public string GetMultipleModInfoUrl(IReadOnlyList<string> ids)
    {
        return BaseUrl + $"/projects?ids=[\"{string.Join("\",\"", ids)}\"]";
    }

    public override string? GetVersionsUrl(VersionSearchArgs args)
    {
        var getArguments = new List<string>();
        if (args.McVersions is { } mcVersions)
        {
            getArguments.Add($"game_versions=[{GetGameVersionsString(mcVersions)}]");
        }
        if (args.Loaders is { } loaders)
        {
            getArguments.Add($"loaders=[\"{string.Join("\",\"", GetModLoaderStrings(loaders))}\"]");
        }
        getArguments.Add($"include_changelog={(args.IncludeChangelog ? "true" : "false")}");

        return $"{BaseUrl}/project/{args.Pack.AddonId}/version{(getArguments.Count != 0 ? "?" : "")}{string.Join("&", getArguments)}";
    }

    /// <summary>Port of ModrinthAPI::getGameVersionsArray.</summary>
    public static string GetGameVersionsArray(IReadOnlyList<McVersion> mcVersions)
    {
        var s = new StringBuilder();
        foreach (var ver in mcVersions)
        {
            s.Append("\"versions:").Append(MapMcVersionToModrinth(ver)).Append("\",");
        }
        if (s.Length == 0)
        {
            return "";
        }
        s.Length -= 1; // remove last comma
        return s.ToString();
    }

    /// <summary>Port of ModrinthAPI::validateModLoaders.</summary>
    public static bool ValidateModLoaders(ModLoaderType loaders)
    {
        return (loaders & (ModLoaderType.NeoForge | ModLoaderType.Forge | ModLoaderType.Fabric
                           | ModLoaderType.Quilt | ModLoaderType.LiteLoader | ModLoaderType.DataPack
                           | ModLoaderType.Babric | ModLoaderType.Bta | ModLoaderType.LegacyFabric
                           | ModLoaderType.Ornithe | ModLoaderType.Rift)) != 0;
    }

    public override string? GetDependencyUrl(DependencySearchArgs args)
    {
        return args.Dependency.Version.Length != 0
            ? $"{BaseUrl}/version/{args.Dependency.Version}"
            : $"{BaseUrl}/project/{args.Dependency.AddonId}/version?game_versions=[\"{MapMcVersionToModrinth(args.McVersion)}\"]" +
              $"&loaders=[\"{string.Join("\",\"", GetModLoaderStrings(args.Loader))}\"]&include_changelog={(args.IncludeChangelog ? "true" : "false")}";
    }

    public override List<SortingMethod> GetSortingMethods()
    {
        // https://docs.modrinth.com/api-spec/#tag/projects/operation/searchProjects
        return
        [
            new SortingMethod { Index = 1, Name = "relevance", ReadableName = "Sort by Relevance" },
            new SortingMethod { Index = 2, Name = "downloads", ReadableName = "Sort by Downloads" },
            new SortingMethod { Index = 3, Name = "follows", ReadableName = "Sort by Follows" },
            new SortingMethod { Index = 4, Name = "newest", ReadableName = "Sort by Newest" },
            new SortingMethod { Index = 5, Name = "updated", ReadableName = "Sort by Last Updated" },
        ];
    }

    // ------------------------------------------------------------------ plumbing

    private NetRequest MakeByteArrayRequest(string url)
    {
        var request = NetRequest.MakeByteArray(new Uri(url), stack: Net);
        AddApiHeaders(request);
        return request;
    }

    private NetRequest MakeByteArrayRequest(string url, byte[] postData)
    {
        var request = NetRequest.MakeByteArray(new Uri(url), postData, stack: Net);
        AddApiHeaders(request);
        return request;
    }

    /// <summary>makeFile() with the Modrinth header proxy applied (downloads from the cdn).</summary>
    public NetRequest MakeFileRequest(string url, string path, DownloadMeta meta = default)
    {
        var request = NetRequest.MakeFile(new Uri(url), path, stack: Net);
        AddApiHeaders(request, meta);
        return request;
    }

}

/// <summary>Small QJsonObject-style accessor helpers shared by the Modrinth loaders
/// (JsonNode-based: the read side of the single-DOM model — see JsonUtils node family).</summary>
internal static class ModrinthJsonExtensions
{
    public static JsonNode? GetPropertySafe(this JsonNode? node, string name) => node is JsonObject obj ? obj[name] : null;

    public static string? AsString(this JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

    public static string RequireString(this JsonNode? parent, string key)
        => JsonUtils.RequireString(parent, key, "'" + key + "'");

    public static bool RequireBoolean(this JsonNode? parent, string key)
        => JsonUtils.RequireBoolean(parent, key, "'" + key + "'");

    public static int RequireInteger(this JsonNode? parent, string key)
        => JsonUtils.RequireInteger(parent, key, "'" + key + "'");

    public static JsonObject RequireObject(this JsonNode? parent, string key)
        => JsonUtils.RequireObject(parent, key, "'" + key + "'");

    public static JsonArray RequireArray(this JsonNode? parent, string key)
        => JsonUtils.RequireArray(parent, key, "'" + key + "'");

    // key + explicit what variants (the index parser labels every requirement with the file)

    public static string RequireString(this JsonNode? parent, string key, string what)
        => JsonUtils.RequireString(parent?[key], what);

    public static bool RequireBoolean(this JsonNode? parent, string key, string what)
        => JsonUtils.RequireBoolean(parent?[key], what);

    public static int RequireInteger(this JsonNode? parent, string key, string what)
        => JsonUtils.RequireInteger(parent?[key], what);

    public static JsonObject RequireObject(this JsonNode? parent, string key, string what)
        => JsonUtils.RequireObject(parent?[key], what);

    public static JsonArray RequireArray(this JsonNode? parent, string key, string what)
        => JsonUtils.RequireArray(parent?[key], what);
}
