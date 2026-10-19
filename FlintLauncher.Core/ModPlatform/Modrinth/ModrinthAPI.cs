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
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Versioning;

namespace FlintLauncher.Core.ModPlatform.Modrinth;

public sealed class ModrinthApi(NetStack? net = null) : ResourceApi
{
    public string BaseUrl { get; set; } = "https://api.modrinth.com/v2";
    public string StagingBaseUrl { get; set; } = "https://staging-api.modrinth.com/v2";
    public static IReadOnlyList<string> MrpackHosts { get; } = ["cdn.modrinth.com", "github.com", "raw.githubusercontent.com", "gitlab.com"];
    public string DownloadHost { get; set; } = "cdn.modrinth.com";

    /// <summary>Optional user PAT (APPLICATION->getModrinthAPIToken()); "" disables the header.</summary>
    public string ApiToken { get; set; } = "";

    /// <summary>The network stack this client's requests go through.</summary>
    public NetStack Net { get; } = net ?? NetStack.Default;

    /// <summary>Port of ModrinthDownloadMeta (net/ApiHeaderProxy.h).</summary>
    public readonly record struct DownloadMeta(string Reason, string GameVersion, string Loader, string DependentOn)
    {
        // default(DownloadMeta) produces null fields; treat them like the C++ empty strings
        public bool IsEmpty => string.IsNullOrEmpty(Reason);

        public string ToJson()
        {
            var dto = new ModrinthDownloadMetaJson(
                Reason: string.IsNullOrEmpty(Reason) ? null : Reason,
                GameVersion: string.IsNullOrEmpty(GameVersion) ? null : GameVersion,
                Loader: string.IsNullOrEmpty(Loader) ? null : Loader,
                DependentOn: string.IsNullOrEmpty(DependentOn) ? null : DependentOn);
            return JsonSerializer.Serialize(dto, ModrinthJsonContext.Default.ModrinthDownloadMetaJson);
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

        var body = new ModrinthHashLookupRequest(
            Hashes: hashes.Count != 0 ? [.. hashes] : null,
            Algorithm: hashFormat.Length != 0 ? hashFormat : null);
        var bodyRaw = JsonSerializer.SerializeToUtf8Bytes(body, ModrinthJsonContext.Default.ModrinthHashLookupRequest);

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

        var body = new ModrinthVersionUpdateRequest(
            Hashes: null,
            Algorithm: null,
            Loaders: NonEmptyOrNull(loaders is { } loaderValue ? GetModLoaderStrings(loaderValue) : null),
            GameVersions: mcVersions is null ? null : NonEmptyOrNull(mcVersions.Select(MapMcVersionToModrinth).ToList()),
            VersionTypes: VersionTypesOrNull(releaseTypes));

        var bodyRaw = JsonSerializer.SerializeToUtf8Bytes(body, ModrinthJsonContext.Default.ModrinthVersionUpdateRequest);

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

        var body = new ModrinthVersionUpdateRequest(
            Hashes: hashes.Count != 0 ? [.. hashes] : null,
            Algorithm: hashFormat.Length != 0 ? hashFormat : null,
            Loaders: NonEmptyOrNull(loaders is { } loaderValue ? GetModLoaderStrings(loaderValue) : null),
            GameVersions: mcVersions is null ? null : NonEmptyOrNull(mcVersions.Select(MapMcVersionToModrinth).ToList()),
            VersionTypes: VersionTypesOrNull(releaseTypes));

        var bodyRaw = JsonSerializer.SerializeToUtf8Bytes(body, ModrinthJsonContext.Default.ModrinthVersionUpdateRequest);

        var request = MakeByteArrayRequest($"{BaseUrl}/version_files/update", bodyRaw);
        netJob.AddNetRequest(request);
        return (netJob, request);
    }

    /// <summary>WriteStringList semantics: an empty list is omitted.</summary>
    private static List<string>? NonEmptyOrNull(List<string>? values) =>
        values is { Count: > 0 } ? values : null;

    /// <summary>Port of the addVersionTypes helper: "version_types" is only written when the
    /// release types carry at least one valid entry.</summary>
    private static List<string>? VersionTypesOrNull(IReadOnlyList<IndexedVersionType>? releaseTypes)
    {
        if (releaseTypes is not { Count: > 0 })
        {
            return null;
        }

        return NonEmptyOrNull(IndexedVersionType.ToModrinthList(releaseTypes));
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
        var entries = ModrinthJson.ParseCategories(response);
        if (entries is null)
        {
            return categories;
        }

        foreach (var cat in entries)
        {
            if (cat.ProjectType == projectType)
            {
                var name = cat.Name ?? "";
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

        if (args.CategoryIds is { } categoryIds && categoryIds.Count != 0)
        {
            // #6099: every search category gets a facet array of its own (a category list is
            // an OR within one array; separate arrays AND them together like the UI expects)
            foreach (var category in categoryIds)
            {
                facetsList.Add($"[\"categories:{category}\"]");
            }
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

    public override string GetInfoUrl(string id) => BaseUrl + "/project/" + id;

    /// <summary>Port of ModrinthAPI::getMultipleModInfoURL.</summary>
    public string GetMultipleModInfoUrl(IReadOnlyList<string> ids)
    {
        return BaseUrl + $"/projects?ids=[\"{string.Join("\",\"", ids)}\"]";
    }

    public override string GetVersionsUrl(VersionSearchArgs args)
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

        // the port has no VersionSearchArgs.IncludeChangelog producer; the wire default is
        // true, so keep the upstream request shape (always false) explicit
        getArguments.Add("include_changelog=false");

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

    public override string GetDependencyUrl(DependencySearchArgs args)
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
            new SortingMethod { Index = 1, Name = "relevance" },
            new SortingMethod { Index = 2, Name = "downloads" },
            new SortingMethod { Index = 3, Name = "follows" },
            new SortingMethod { Index = 4, Name = "newest" },
            new SortingMethod { Index = 5, Name = "updated" },
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
