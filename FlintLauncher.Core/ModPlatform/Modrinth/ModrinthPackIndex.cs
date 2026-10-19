// SPDX-License-Identifier: GPL-3.0-only
// Port of modplatform/modrinth/ModrinthPackIndex.{h,cpp}: load Modrinth project/version
// payloads into the provider-neutral ModIndex models. The wire shapes arrive as the
// source-generated DTOs in ModrinthJson (ModrinthProjectJson / ModrinthVersionJson); these
// loaders are the DTO -> model mapping, keeping the require/fallback semantics of the
// former node readers. The .mrpack (modrinth.index.json) parsing lives in
// ModrinthInstanceCreationTask.ParseManifest, like upstream.

using FlintLauncher.Core.IO;
using FlintLauncher.Core.Minecraft;

namespace FlintLauncher.Core.ModPlatform.Modrinth;

// https://docs.modrinth.com/api/operations/getproject/
public static class ModrinthPackIndex
{
    private static bool ShouldDownloadOnSide(string side) => side is "required" or "optional";

    /// <summary>Port of Modrinth::loadIndexedPack.</summary>
    public static void LoadIndexedPack(IndexedPack pack, ModrinthProjectJson dto)
    {
        pack.AddonId = dto.ProjectId ?? "";
        if (pack.AddonId.Length == 0)
        {
            pack.AddonId = ModrinthRequire.String(dto.Id, "id");
        }

        pack.Provider = ResourceProvider.Modrinth;
        pack.Name = ModrinthRequire.String(dto.Title, "title");
        pack.ResourceType = ModrinthApi.GetResourceType(dto.ProjectType ?? "");
        if ((dto.Loaders?.Contains("datapack") ?? false) ||
            (dto.AllProjectTypes?.Contains("datapack") ?? false))
        {
            pack.ResourceType = ResourceType.DataPack;
        }

        pack.Slug = dto.Slug ?? "";
        pack.WebsiteUrl = pack.Slug.Length != 0 ? "https://modrinth.com/mod/" + pack.Slug : "";

        pack.Description = dto.Description ?? "";

        pack.LogoUrl = dto.IconUrl ?? "";
        pack.LogoName = $"{dto.Slug ?? ""}.{UrlPathSuffix(pack.LogoUrl)}";

        if (dto.Author is { } author)
        {
            var modAuthor = new ModpackAuthor
            {
                Name = author,
            };
            pack.Authors = [modAuthor];
        }

        var client = ShouldDownloadOnSide(dto.ClientSide ?? "");
        var server = ShouldDownloadOnSide(dto.ServerSide ?? "");

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

    /// <summary>Port of Modrinth::loadExtraPackData (the donation list, discord url and
    /// status members have no reader and are not ported).</summary>
    public static void LoadExtraPackData(IndexedPack pack, ModrinthProjectJson dto)
    {
        pack.ExtraData.IssuesUrl = ChopTrailingSlash(dto.IssuesUrl ?? "");
        pack.ExtraData.SourceUrl = ChopTrailingSlash(dto.SourceUrl ?? "");
        pack.ExtraData.WikiUrl = ChopTrailingSlash(dto.WikiUrl ?? "");

        pack.ExtraData.Body = (dto.Body ?? "").Replace("<br>", "");

        pack.ExtraDataLoaded = true;
    }

    /// <summary>Port of Modrinth::loadIndexedPackVersion.</summary>
    public static IndexedVersion LoadIndexedPackVersion(ModrinthVersionJson dto, string preferredHashType = "sha512",
        string preferredFileName = "")
    {
        var file = new IndexedVersion
        {
            AddonId = ModrinthRequire.String(dto.ProjectId, "project_id"),
            FileId = ModrinthRequire.String(dto.Id, "id"),
            Date = ModrinthRequire.String(dto.DatePublished, "date_published"),
        };
        var versionArray = ModrinthRequire.Array(dto.GameVersions, "game_versions");
        if (versionArray.Count == 0)
        {
            return new IndexedVersion();
        }

        foreach (var raw in versionArray)
        {
            // double this so we can check both strings when filtering
            file.McVersion.Add(ModrinthApi.MapMcVersionFromModrinth(raw));
            file.McVersion.Add(raw);
        }

        foreach (var loader in ModrinthRequire.Array(dto.Loaders, "loaders"))
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

        file.Version = ModrinthRequire.String(dto.Name, "name");
        file.VersionNumber = ModrinthRequire.String(dto.VersionNumber, "version_number");
        file.VersionType = IndexedVersionType.FromString(ModrinthRequire.String(dto.VersionType, "version_type"));

        // the wire's `changelog` member has no ported reader (UI-only display field), but a
        // present non-string value still fails the read like it did upstream
        if (dto.Changelog is { } changelog)
        {
            _ = ModrinthRequire.String(changelog, "changelog");
        }

        if (dto.Dependencies is { } dependencies)
        {
            foreach (var d in dependencies)
            {
                var dependency = new Dependency
                {
                    AddonId = d.ProjectId ?? "",
                    Version = d.VersionId ?? "",
                };

                dependency.Type = ModrinthRequire.String(d.DependencyType, "dependency_type") switch
                {
                    "required" => new DependencyType(DependencyTypeValue.Required),
                    "optional" => new DependencyType(DependencyTypeValue.Optional),
                    "incompatible" => new DependencyType(DependencyTypeValue.Incompatible),
                    "embedded" => new DependencyType(DependencyTypeValue.Embedded),
                    _ => new DependencyType(DependencyTypeValue.Unknown),
                };

                file.Dependencies.Add(dependency);
            }
        }

        var files = ModrinthRequire.Array(dto.Files, "files");
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
            var fileName = ModrinthRequire.String(candidate.Filename, "filename");

            if (preferredFileName.Length != 0 && fileName.Contains(preferredFileName, StringComparison.Ordinal))
            {
                file.IsPreferred = true;
                parent = candidate;
                break;
            }

            // Grab the primary file, if available
            if (ModrinthRequire.Boolean(candidate.Primary, "primary"))
            {
                parent = candidate;
                break;
            }
        }

        if (parent.Url is not null)
        {
            file.DownloadUrl = ModrinthRequire.String(parent.Url, "url");
            var fileName = ModrinthRequire.String(parent.Filename, "filename");
            file.FileName = FileSystem.RemoveInvalidPathChars(fileName);
            file.IsPreferred = ModrinthRequire.Boolean(parent.Primary, "primary") || fileCount == 1;
            var hashList = ModrinthRequire.Object(parent.Hashes, "hashes");

            // a JSON null hash value counts as absent (the former node model: null is not
            // { }), so the fallback chain keeps walking
            if (hashList.TryGetValue(preferredHashType, out var preferredHash))
            {
                file.Hash = ModrinthRequire.String(preferredHash, preferredHashType);
                file.HashType = preferredHashType;
            }
            else
            {
                var hashTypes = ProviderCapabilities.HashType(ResourceProvider.Modrinth);
                foreach (var hashType in hashTypes)
                {
                    if (!hashList.TryGetValue(hashType, out var hash)) continue;
                    file.Hash = ModrinthRequire.String(hash, hashType);
                    file.HashType = hashType;
                    break;
                }
            }

            return file;
        }

        return new IndexedVersion();
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
