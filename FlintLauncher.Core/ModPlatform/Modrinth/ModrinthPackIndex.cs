// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
// SPDX-FileCopyrightText: 2022 flowln <flowlnlnln@gmail.com>
// SPDX-FileCopyrightText: 2023 Trial97 <alexandru.tripon97@gmail.com>
//
// Port of modplatform/modrinth/ModrinthPackIndex.{h,cpp}: load Modrinth project/version
// payloads into the provider-neutral ModIndex models. The wire shapes arrive as the
// source-generated DTOs in ModrinthJson (ModrinthProject / ModrinthVersion); these
// loaders are the DTO -> model mapping, keeping the require/fallback semantics of the
// former node readers. The .mrpack (modrinth.index.json) parsing lives in
// ModrinthInstanceCreationTask.ParseManifest, like upstream.

using FlintLauncher.Core.IO;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.ExternalApi;
using FlintLauncher.ExternalApi.Modrinth;

namespace FlintLauncher.Core.ModPlatform.Modrinth;

// https://docs.modrinth.com/api/operations/getproject/
public static class ModrinthPackIndex
{
    private static bool ShouldDownloadOnSide(string side) => side is "required" or "optional";

    /// <summary>Port of Modrinth::loadIndexedPack. The search-only members (author,
    /// all_project_types) are not on the full Project shape and no longer read; the datapack
    /// detection rides on the project's loader list alone.</summary>
    public static void LoadIndexedPack(IndexedPack pack, ModrinthProject dto)
    {
        pack.AddonId = dto.Id;
        pack.Provider = ResourceProvider.Modrinth;
        pack.Name = dto.Title;
        pack.ResourceType = GetResourceType(dto.ProjectType);
        if (dto.Loaders?.Contains("datapack") == true)
        {
            pack.ResourceType = ResourceType.DataPack;
        }

        pack.Slug = dto.Slug;
        pack.WebsiteUrl = pack.Slug.Length != 0 ? "https://modrinth.com/mod/" + pack.Slug : "";

        pack.Description = dto.Description;

        pack.LogoUrl = dto.IconUrl ?? "";
        pack.LogoName = $"{dto.Slug}.{UrlPathSuffix(pack.LogoUrl)}";

        var client = ShouldDownloadOnSide(dto.ClientSide);
        var server = ShouldDownloadOnSide(dto.ServerSide);

        if (server && client)
        {
            pack.Side = new SideType(SideTypeValue.UniversalSide);
        }
        else if (server)
        {
            pack.Side = new SideType(SideTypeValue.ServerSide);
        }
        else if (client)
        {
            pack.Side = new SideType(SideTypeValue.ClientSide);
        }

        // Modrinth can have more data than what's provided by the basic search :)
        pack.ExtraDataLoaded = false;
    }

    /// <summary>The wire project_type → the launcher resource type (the C++
    /// ModrinthAPI::getResourceType map; unknown types stay Unknown).</summary>
    private static ResourceType GetResourceType(string param) => param switch
    {
        "mod" => ResourceType.Mod,
        "resourcepack" => ResourceType.ResourcePack,
        "shader" => ResourceType.ShaderPack,
        "datapack" => ResourceType.DataPack,
        "modpack" => ResourceType.Modpack,
        _ => ResourceType.Unknown,
    };

    /// <summary>Port of Modrinth::loadExtraPackData (the donation list, discord url and
    /// status members have no reader and are not ported).</summary>
    public static void LoadExtraPackData(IndexedPack pack, ModrinthProject dto)
    {
        pack.ExtraData.IssuesUrl = ChopTrailingSlash(dto.IssuesUrl ?? "");
        pack.ExtraData.SourceUrl = ChopTrailingSlash(dto.SourceUrl ?? "");
        pack.ExtraData.WikiUrl = ChopTrailingSlash(dto.WikiUrl ?? "");

        pack.ExtraData.Body = dto.Body.Replace("<br>", "");

        pack.ExtraDataLoaded = true;
    }

    /// <summary>Port of Modrinth::loadIndexedPackVersion. The wire's required members are
    /// enforced by the model itself (missing keys fail the decode), so the former
    /// require-is-type checks are gone; a JSON null hash value still counts as absent and
    /// the sha512 -> sha1 fallback keeps walking.</summary>
    public static IndexedVersion LoadIndexedPackVersion(ModrinthVersion dto, string preferredHashType = "sha512",
        string preferredFileName = "")
    {
        var file = new IndexedVersion
        {
            AddonId = dto.ProjectId,
            FileId = dto.Id,
            Date = dto.DatePublished,
        };
        if (dto.GameVersions.Count == 0)
        {
            return new IndexedVersion();
        }

        foreach (var raw in dto.GameVersions)
        {
            // double this so we can check both strings when filtering
            file.McVersion.Add(ResourceApi.MapMcVersionFromModrinth(raw));
            file.McVersion.Add(raw);
        }

        foreach (var loader in dto.Loaders)
        {
            switch (loader)
            {
                case "neoforge": file.Loaders |= ModLoaderType.NeoForge; break;
                case "forge": file.Loaders |= ModLoaderType.Forge; break;
                case "cauldron": file.Loaders |= ModLoaderType.Cauldron; break;
                case "liteloader": file.Loaders |= ModLoaderType.LiteLoader; break;
                case "fabric": file.Loaders |= ModLoaderType.Fabric; break;
                case "quilt": file.Loaders |= ModLoaderType.Quilt; break;
            }
        }

        file.Version = dto.Name;
        file.VersionNumber = dto.VersionNumber;
        file.VersionType = IndexedVersionType.FromString(dto.VersionType);

        foreach (var d in dto.Dependencies ?? [])
        {
            var dependency = new Dependency
            {
                AddonId = d.ProjectId ?? "",
                Version = d.VersionId ?? "",
            };

            dependency.Type = d.DependencyType switch
            {
                "required" => new DependencyType(DependencyTypeValue.Required),
                "optional" => new DependencyType(DependencyTypeValue.Optional),
                "incompatible" => new DependencyType(DependencyTypeValue.Incompatible),
                "embedded" => new DependencyType(DependencyTypeValue.Embedded),
                _ => new DependencyType(DependencyTypeValue.Unknown),
            };

            file.Dependencies.Add(dependency);
        }

        var files = dto.Files;
        var fileCount = files.Count;

        if (fileCount == 0)
        {
            // This should not happen normally, but check just in case
            return new IndexedVersion();
        }

        // Find correct file (needed in cases where one version may have multiple files)
        // Will default to the last one if there's no primary (though I think Modrinth requires that
        // at least one file is primary, idk)
        var parent = files[fileCount - 1];
        for (var i = 0; i < fileCount - 1; i++)
        {
            var candidate = files[i];

            if (preferredFileName.Length != 0 &&
                candidate.Filename.Contains(preferredFileName, StringComparison.Ordinal))
            {
                file.IsPreferred = true;
                parent = candidate;
                break;
            }

            // Grab the primary file, if available
            if (candidate.Primary)
            {
                parent = candidate;
                break;
            }
        }

        file.DownloadUrl = parent.Url;
        file.FileName = FileNaming.RemoveInvalidPathChars(parent.Filename);
        file.IsPreferred = parent.Primary || fileCount == 1;
        var hashList = parent.Hashes;

        // a JSON null hash value counts as absent (the former node model: null is not
        // { }), so the fallback chain keeps walking
        if (hashList.TryGetValue(preferredHashType, out var preferredHash))
        {
            file.Hash = preferredHash;
            file.HashType = preferredHashType;
        }
        else
        {
            var hashTypes = ProviderCapabilities.HashType(ResourceProvider.Modrinth);
            foreach (var hashType in hashTypes)
            {
                if (!hashList.TryGetValue(hashType, out var hash)) continue;
                file.Hash = hash;
                file.HashType = hashType;
                break;
            }
        }

        return file;
    }

    // ------------------------------------------------------------------ helpers

    private static string ChopTrailingSlash(string url) => url.EndsWith('/') ? url[..^1] : url;

    /// <summary>QFileInfo(QUrl(url).fileName()).suffix(): extension of the URL's file name.</summary>
    private static string UrlPathSuffix(string url)
    {
        var slash = url.LastIndexOf('/');
        var fileName = slash >= 0 ? url[(slash + 1)..] : url;
        var query = fileName.IndexOf('?');
        if (query >= 0)
        {
            fileName = fileName[..query];
        }

        var dot = fileName.LastIndexOf('.');
        return dot >= 0 ? fileName[(dot + 1)..] : "";
    }
}
