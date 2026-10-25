// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
//
// Port of modplatform/modrinth/ModrinthAPI.{h,cpp}: the launcher-domain adapter over
// ModrinthClient. URL building (search facets, version/dependency URLs), the
// McVersion/ModLoaderType/release-type mappings and the download path live
// here; the executed queries and the wire models live in the ExternalApi client. The
// Application-singleton reach-ins became injected instance state:
//  - BuildConfig.MODRINTH_PROD_URL / _STAGING_URL -> BaseUrl / StagingBaseUrl (per-instance,
//    tests construct their own client pointed at a stub)
//  - APPLICATION->getModrinthAPIToken() -> ApiToken (optional PAT, sent as Authorization)
//  - the net/ApiHeaderProxy header logic -> AddApiHeaders (PAT + modrinth-download-meta)
// Pure helpers (URL builders, loaders/facets mapping, response shaping) stay static — no
// configuration behind them.

using System.Text;
using System.Text.Json;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.ModPlatform.Hashing;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Versioning;
using FlintLauncher.ExternalApi;
using TruePath;

namespace FlintLauncher.Core.ModPlatform.Modrinth;

public sealed class ModrinthApi(ModrinthClient client, HttpClient http, NetSettings settings) : ResourceApi
{
    /// <summary>The executed client behind every query; its Options carry the hosts and PAT
    /// this adapter forwards.</summary>
    public ModrinthClient Client { get; } = client;

    public string BaseUrl { get => Client.Options.BaseUrl; set => Client.Options.BaseUrl = value; }
    public string ApiToken { get => Client.Options.ApiToken; set => Client.Options.ApiToken = value; }

    /// <summary>The CDN host the mrpack import validates download links against.</summary>
    public string DownloadHost { get; set; } = "cdn.modrinth.com";

    public static IReadOnlyList<string> MrpackHosts { get; } = ["cdn.modrinth.com", "github.com", "raw.githubusercontent.com", "gitlab.com"];

    /// <summary>The client behind the raw download paths (the pinned-version and
    /// dependency-version fetches that are not ModrinthClient queries).</summary>
    public override HttpClient Http { get; } = http;

    public override NetSettings Settings { get; } = settings;

    // ------------------------------------------------------------------ domain queries

    /// <summary>Domain-arg forwarding to <see cref="ModrinthClient.GetLatestVersionsAsync"/>:
    /// the batch update check for mod update scanning (the optional release types become the
    /// body's "version_types" list, #3404).</summary>
    public Task<Dictionary<string, ModrinthVersion>> GetLatestVersionsAsync(IReadOnlyList<string> hashes,
        string hashFormat,
        IReadOnlyList<McVersion>? mcVersions,
        ModLoaderType? loaders,
        IReadOnlyList<IndexedVersionType>? releaseTypes = null,
        CancellationToken cancellationToken = default)
    {
        return Client.GetLatestVersionsAsync(hashes, hashFormat,
            loaders is { } loaderValue ? NonEmptyOrNull(GetModLoaderStrings(loaderValue)) : null,
            mcVersions is null ? null : NonEmptyOrNull([.. mcVersions.Select(MapMcVersionToModrinth)]),
            VersionTypesOrNull(releaseTypes),
            cancellationToken);
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

    /// <summary>Port of ModrinthAPI::loadCategories (project_type filter).</summary>
    public static List<Category> LoadCategories(string response, string projectType)
    {
        var categories = new List<Category>();
        List<ModrinthCategory>? entries;
        try
        {
            entries = JsonSerializer.Deserialize(response, ModrinthJsonContext.Default.ListModrinthCategory);
        }
        catch (JsonException)
        {
            entries = null;
        }

        if (entries is null)
        {
            return categories;
        }

        foreach (var cat in entries)
        {
            if (cat.ProjectType == projectType)
            {
                var name = cat.Name;
                categories.Add(new Category(name, name));
            }
        }

        return categories;
    }

    public List<Category> LoadModCategories(string response) => LoadCategories(response, "mod");

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

    // ------------------------------------------------------------------ downloads

    /// <summary>The mod download path: stream the URL to disk through the client's
    /// checksum-gated temp-file dance. Hash types are the launcher-domain spellings the
    /// metadata files use (md4 degrades to md5 — no managed MD4 in .NET, see HashUtils);
    /// unknown types download without a check, like the C++ default branch.</summary>
    public Task DownloadModFileAsync(string url, AbsolutePath path, ModrinthDownloadMeta? meta = null,
        string? hashType = null, string? hash = null, CancellationToken cancellationToken = default) =>
        Client.DownloadFileAsync(url, path.Value, meta, MapChecksum(hashType, hash), cancellationToken);

    /// <summary>Port of the ChecksumValidator switch: md4/md5 -> MD5, sha1/sha256/sha512 map
    /// directly; anything else (murmur2, unknown) downloads unchecked.</summary>
    private static (string Algorithm, string Hash)? MapChecksum(string? hashType, string? hash) =>
        hash is { Length: > 0 }
        ? HashUtils.AlgorithmFromString(hashType ?? "") switch
        {
            Algorithm.Md4 => ("md5", hash),
            Algorithm.Md5 => ("md5", hash),
            Algorithm.Sha1 => ("sha1", hash),
            Algorithm.Sha256 => ("sha256", hash),
            Algorithm.Sha512 => ("sha512", hash),
            _ => null,
        }
        : null;
}
