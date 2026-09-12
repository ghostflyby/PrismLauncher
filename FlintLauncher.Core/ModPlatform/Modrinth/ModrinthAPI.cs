// SPDX-License-Identifier: GPL-3.0-only
// Port of modplatform/modrinth/ModrinthAPI.{h,cpp}: the Modrinth v2 API surface (search
// with facets, project/version endpoints, hash-based version lookup and updates) on top of
// the Net stack. The Application-singleton reach-ins become static hooks:
//  - BuildConfig.MODRINTH_PROD_URL / _STAGING_URL -> BaseUrl / StagingBaseUrl (settable,
//    see ModrinthApiTests for the [Collection] pattern used to restore it)
//  - APPLICATION->getModrinthAPIToken() -> ApiToken (optional PAT, sent as Authorization)
//  - the net/ApiHeaderProxy header logic -> AddApiHeaders (PAT + modrinth-download-meta)
using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.Json;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Versioning;

namespace FlintLauncher.Core.ModPlatform.Modrinth;

public sealed class ModrinthApi : ResourceApi
{
    // BuildConfig.MODRINTH_* (settable hooks; tests restore them through [Collection])
    public static string BaseUrl { get; set; } = "https://api.modrinth.com/v2";
    public static string StagingBaseUrl { get; set; } = "https://staging-api.modrinth.com/v2";
    public static IReadOnlyList<string> MrpackHosts { get; } = ["cdn.modrinth.com", "github.com", "raw.githubusercontent.com", "gitlab.com"];
    public static string DownloadHost { get; set; } = "cdn.modrinth.com";

    /// <summary>Optional user PAT (APPLICATION->getModrinthAPIToken()); "" disables the header.</summary>
    public static string ApiToken { get; set; } = "";

    private ModrinthApi()
    {
    }

    private static ModrinthApi? _instance;
    public static ModrinthApi Instance => _instance ??= new ModrinthApi();

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
    public static void AddApiHeaders(NetRequest request, DownloadMeta meta = default)
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

    /// <summary>Port of ModrinthAPI::currentVersion (GET /version_file/{hash}?algorithm=).</summary>
    public static (NetJob Job, NetRequest Request) CurrentVersion(string hash, string hashFormat)
    {
        var netJob = new NetJob("Modrinth::GetCurrentVersion");
        var request = MakeByteArrayRequest($"{BaseUrl}/version_file/{hash}?algorithm={hashFormat}");
        netJob.AddNetRequest(request);
        return (netJob, request);
    }

    /// <summary>Port of ModrinthAPI::currentVersions (POST /version_files, multiple hashes).</summary>
    public static (NetJob Job, NetRequest Request) CurrentVersions(IReadOnlyList<string> hashes, string hashFormat)
    {
        var netJob = new NetJob("Modrinth::GetCurrentVersions");

        var bodyObj = new JsonObject();
        JsonUtils.WriteStringList(bodyObj, "hashes", hashes);
        JsonUtils.WriteString(bodyObj, "algorithm", hashFormat);
        var bodyRaw = System.Text.Encoding.UTF8.GetBytes(bodyObj.ToJsonString());

        var request = MakeByteArrayRequest($"{BaseUrl}/version_files", bodyRaw);
        netJob.AddNetRequest(request);
        return (netJob, request);
    }

    /// <summary>Port of ModrinthAPI::latestVersion (POST /version_file/{hash}/update).</summary>
    public (NetJob Job, NetRequest Request) LatestVersion(string hash,
                                                           string hashFormat,
                                                           IReadOnlyList<McVersion>? mcVersions,
                                                           Minecraft.ModLoaderType? loaders)
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

        var bodyRaw = System.Text.Encoding.UTF8.GetBytes(bodyObj.ToJsonString());

        var request = MakeByteArrayRequest($"{BaseUrl}/version_file/{hash}/update?algorithm={hashFormat}", bodyRaw);
        netJob.AddNetRequest(request);
        return (netJob, request);
    }

    /// <summary>Port of ModrinthAPI::latestVersions (POST /version_files/update).</summary>
    public (NetJob Job, NetRequest Request) LatestVersions(IReadOnlyList<string> hashes,
                                                           string hashFormat,
                                                           IReadOnlyList<McVersion>? mcVersions,
                                                           Minecraft.ModLoaderType? loaders)
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

        var bodyRaw = System.Text.Encoding.UTF8.GetBytes(bodyObj.ToJsonString());

        var request = MakeByteArrayRequest($"{BaseUrl}/version_files/update", bodyRaw);
        netJob.AddNetRequest(request);
        return (netJob, request);
    }

    /// <summary>Port of ModrinthAPI::getProjects (GET /projects?ids=[...]).</summary>
    public (NetJob Job, NetRequest Request) GetProjects(IReadOnlyList<string> addonIds)
    {
        var netJob = new NetJob("Modrinth::GetProjects");
        var searchUrl = GetMultipleModInfoURL(addonIds);
        var request = MakeByteArrayRequest(searchUrl);
        netJob.AddNetRequest(request);
        return (netJob, request);
    }

    /// <summary>Port of ModrinthAPI::getProject — the single-project GET (ResourceAPI::getProject).</summary>
    public (NetJob? Job, NetRequest? Request) GetProject(string addonId)
    {
        var url = GetInfoURL(addonId);
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
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return categories;
            }
            foreach (var cat in doc.RootElement.EnumerateArray())
            {
                if (cat.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                var name = JsonUtils.EnsureString(cat.GetPropertySafe("name"));
                if (cat.GetPropertySafe("project_type").GetString() == projectType)
                {
                    categories.Add(new Category(name, name));
                }
            }
        }
        return categories;
    }

    public List<Category> LoadModCategories(string response) => LoadCategories(response, "mod");

    // ------------------------------------------------------------------ URL builders

    public static string GetAuthorURL(string name) => "https://modrinth.com/user/" + name;

    /// <summary>Port of ModrinthAPI::getModLoaderStrings.</summary>
    public static List<string> GetModLoaderStrings(Minecraft.ModLoaderType types)
    {
        List<Minecraft.ModLoaderType> all =
        [
            Minecraft.ModLoaderType.NeoForge, Minecraft.ModLoaderType.Forge, Minecraft.ModLoaderType.Fabric,
            Minecraft.ModLoaderType.Quilt, Minecraft.ModLoaderType.LiteLoader, Minecraft.ModLoaderType.DataPack,
            Minecraft.ModLoaderType.Babric, Minecraft.ModLoaderType.BTA, Minecraft.ModLoaderType.LegacyFabric,
            Minecraft.ModLoaderType.Ornithe, Minecraft.ModLoaderType.Rift,
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
    public static string GetModLoaderFilters(Minecraft.ModLoaderType types)
    {
        var l = GetModLoaderStrings(types).Select(loader => $"\"categories:{loader}\"");
        return string.Join(",", l);
    }

    /// <summary>Port of ModrinthAPI::getCategoriesFilters.</summary>
    public static string GetCategoriesFilters(IReadOnlyList<string> categories)
    {
        return string.Join(",", categories.Select(cat => $"\"categories:{cat}\""));
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
        var pre = v.Contains("-pre");
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
            facetsList.Add($"[{GetCategoriesFilters(categoryIds)}]");
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

    public override string? GetSearchURL(SearchArgs args)
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

    public override string? GetInfoURL(string id) => BaseUrl + "/project/" + id;

    /// <summary>Port of ModrinthAPI::getMultipleModInfoURL.</summary>
    public static string GetMultipleModInfoURL(IReadOnlyList<string> ids)
    {
        return BaseUrl + $"/projects?ids=[\"{string.Join("\",\"", ids)}\"]";
    }

    public override string? GetVersionsURL(VersionSearchArgs args)
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
        var s = new System.Text.StringBuilder();
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
    public static bool ValidateModLoaders(Minecraft.ModLoaderType loaders)
    {
        return (loaders & (Minecraft.ModLoaderType.NeoForge | Minecraft.ModLoaderType.Forge | Minecraft.ModLoaderType.Fabric
                           | Minecraft.ModLoaderType.Quilt | Minecraft.ModLoaderType.LiteLoader | Minecraft.ModLoaderType.DataPack
                           | Minecraft.ModLoaderType.Babric | Minecraft.ModLoaderType.BTA | Minecraft.ModLoaderType.LegacyFabric
                           | Minecraft.ModLoaderType.Ornithe | Minecraft.ModLoaderType.Rift)) != 0;
    }

    public override string? GetDependencyURL(DependencySearchArgs args)
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

    private static NetRequest MakeByteArrayRequest(string url)
    {
        var request = NetRequest.MakeByteArray(new Uri(url));
        AddApiHeaders(request);
        return request;
    }

    private static NetRequest MakeByteArrayRequest(string url, byte[] postData)
    {
        var request = NetRequest.MakeByteArray(new Uri(url), postData);
        AddApiHeaders(request);
        return request;
    }

    /// <summary>makeFile() with the Modrinth header proxy applied (downloads from the cdn).</summary>
    public static NetRequest MakeFileRequest(string url, string path, DownloadMeta meta = default)
    {
        var request = NetRequest.MakeFile(new Uri(url), path);
        AddApiHeaders(request, meta);
        return request;
    }

    /// <summary>Port of ResourceAPI::documentToArray for Modrinth (the "hits" array).</summary>
    public static JsonElement DocumentToArray(JsonDocument obj) => JsonUtils.RequireObject(obj).GetPropertySafe("hits");
}

/// <summary>Small QJsonObject-style accessor helpers shared by the Modrinth loaders.</summary>
internal static class ModrinthJsonExtensions
{
    /// <summary>obj["key"] equivalent: Undefined (empty) element when missing.</summary>
    public static JsonElement GetPropertySafe(this JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return JsonDocument.Parse("null").RootElement;
        }
        return element.TryGetProperty(name, out var value) ? value : JsonDocument.Parse("null").RootElement;
    }

    /// <summary>Port of the Json::requireX(parent, key) pattern over a JsonElement parent.</summary>
    private static JsonElement Child(JsonElement parent, string key)
    {
        var localWhat = "'" + key + "'";
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(key, out var value))
        {
            throw new JsonParseException(localWhat + "s parent does not contain " + localWhat);
        }
        return value;
    }

    public static string RequireString(this JsonElement parent, string key)
        => JsonUtils.RequireString(Child(parent, key), "'" + key + "'");

    public static bool RequireBoolean(this JsonElement parent, string key)
        => JsonUtils.RequireBoolean(Child(parent, key), "'" + key + "'");

    public static int RequireInteger(this JsonElement parent, string key)
        => JsonUtils.RequireInteger(Child(parent, key), "'" + key + "'");

    public static JsonElement RequireObject(this JsonElement parent, string key)
        => JsonUtils.RequireObject(Child(parent, key), "'" + key + "'");

    public static JsonElement RequireArray(this JsonElement parent, string key)
        => JsonUtils.RequireArray(Child(parent, key), "'" + key + "'");
}
