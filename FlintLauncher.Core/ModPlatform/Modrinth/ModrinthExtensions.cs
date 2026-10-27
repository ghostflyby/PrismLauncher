// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors

using System.Text;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.ModPlatform.Hashing;
using FlintLauncher.Core.Versioning;
using FlintLauncher.ExternalApi;
using TruePath;

namespace FlintLauncher.Core.ModPlatform.Modrinth;

/// <summary>The launcher-domain surface over the ModrinthClient operation values: the URL
/// builders (search facets, version/dependency URLs), the McVersion/ModLoaderType mappings
/// and the download entry. The executed queries and the wire models live in the ExternalApi
/// client; the executor is handed per send.</summary>
public static class ModrinthExtensions
{
    // ------------------------------------------------------------------ domain forwarding

    /// <summary>The batch update check for mod update scanning (the optional release types
    /// become the body's "version_types" list, #3404).</summary>
    public static Task<Dictionary<string, ModrinthVersion>> GetLatestVersionsAsync(
        this ModrinthClient client, HttpClient http, IReadOnlyList<string> hashes, string hashFormat,
        IReadOnlyList<McVersion>? mcVersions, ModLoaderType? loaders,
        IReadOnlyList<IndexedVersionType>? releaseTypes = null, CancellationToken cancellationToken = default)
    {
        return client.Versions.Latest(hashes, hashFormat,
            loaders is { } loaderValue ? NonEmptyOrNull(GetModLoaderStrings(loaderValue)) : null,
            mcVersions is null ? null : NonEmptyOrNull([.. mcVersions.Select(ResourceApi.MapMcVersionToModrinth)]),
            VersionTypesOrNull(releaseTypes)).SendTo(http, cancellationToken);
    }

    /// <summary>The mod download path: stream the URL to disk through the download
    /// operation's checksum-gated temp-file commit. Hash types are the launcher-domain
    /// spellings the metadata files use (md4 degrades to md5 — no managed MD4 in .NET, see
    /// HashUtils); unknown types download without a check, like the C++ default branch.</summary>
    public static Task DownloadModFileAsync(this ModrinthClient client, HttpClient http, string url,
        AbsolutePath path, ModrinthDownloadMeta? meta = null, string? hashType = null, string? hash = null,
        CancellationToken cancellationToken = default) =>
        client.Downloads.File(url, meta, MapChecksum(hashType, hash))
            .SendTo(http, HttpCompletionOption.ResponseHeadersRead, path.Value, cancellationToken);

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

    public static string? GetSearchUrl(this ModrinthClient client, SearchArgs args)
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

        return client.Options.BaseUrl + "/search?" + string.Join("&", getArguments);
    }

    public static string GetVersionsUrl(this ModrinthClient client, VersionSearchArgs args)
    {
        var getArguments = new List<string>();
        if (args.McVersions is { } mcVersions)
        {
            getArguments.Add($"game_versions=[{ResourceApi.GetGameVersionsString(mcVersions)}]");
        }

        if (args.Loaders is { } loaders)
        {
            getArguments.Add($"loaders=[\"{string.Join("\",\"", GetModLoaderStrings(loaders))}\"]");
        }

        // the port has no VersionSearchArgs.IncludeChangelog producer; the wire default is
        // true, so keep the upstream request shape (always false) explicit
        getArguments.Add("include_changelog=false");

        return $"{client.Options.BaseUrl}/project/{args.Pack.AddonId}/version{(getArguments.Count != 0 ? "?" : "")}{string.Join("&", getArguments)}";
    }

    public static string GetDependencyUrl(this ModrinthClient client, DependencySearchArgs args)
    {
        return args.Dependency.Version.Length != 0
            ? $"{client.Options.BaseUrl}/version/{args.Dependency.Version}"
            : $"{client.Options.BaseUrl}/project/{args.Dependency.AddonId}/version?game_versions=[\"{ResourceApi.MapMcVersionToModrinth(args.McVersion)}\"]" +
              $"&loaders=[\"{string.Join("\",\"", GetModLoaderStrings(args.Loader))}\"]&include_changelog={(args.IncludeChangelog ? "true" : "false")}";
    }

    /// <summary>Port of ModrinthAPI::getGameVersionsArray.</summary>
    private static string GetGameVersionsArray(IReadOnlyList<McVersion> mcVersions)
    {
        var s = new StringBuilder();
        foreach (var ver in mcVersions)
        {
            s.Append("\"versions:").Append(ResourceApi.MapMcVersionToModrinth(ver)).Append("\",");
        }

        if (s.Length == 0)
        {
            return "";
        }

        s.Length -= 1; // remove last comma
        return s.ToString();
    }

    /// <summary>Port of ModrinthAPI::validateModLoaders.</summary>
    private static bool ValidateModLoaders(ModLoaderType loaders)
    {
        return (loaders & (ModLoaderType.NeoForge | ModLoaderType.Forge | ModLoaderType.Fabric
                           | ModLoaderType.Quilt | ModLoaderType.LiteLoader | ModLoaderType.DataPack
                           | ModLoaderType.Babric | ModLoaderType.Bta | ModLoaderType.LegacyFabric
                           | ModLoaderType.Ornithe | ModLoaderType.Rift)) != 0;
    }
}
