// SPDX-License-Identifier: GPL-3.0-only
// Port of modplatform/modrinth/ModrinthPackIndex.{h,cpp}: load Modrinth project/version
// JSON documents into the provider-neutral ModIndex models. The .mrpack (modrinth.index.json)
// parsing lives in ModrinthInstanceCreationTask.ParseManifest, like upstream.
using System.Text.Json;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Json;

namespace FlintLauncher.Core.ModPlatform.Modrinth;

// https://docs.modrinth.com/api/operations/getproject/
public static class ModrinthPackIndex
{
    private static bool ShouldDownloadOnSide(string side) => side is "required" or "optional";

    /// <summary>Port of Modrinth::loadIndexedPack.</summary>
    public static void LoadIndexedPack(IndexedPack pack, JsonElement obj)
    {
        pack.AddonId = obj.GetPropertySafe("project_id").GetString() ?? "";
        if (pack.AddonId.Length == 0)
        {
            pack.AddonId = obj.RequireString("id");
        }

        pack.Provider = ResourceProvider.MODRINTH;
        pack.Name = obj.RequireString("title");
        pack.ResourceType = ModrinthApi.GetResourceType(obj.GetPropertySafe("project_type").GetString() ?? "");
        if ((obj.TryGetArrayContains("loaders", "datapack")) ||
            (obj.TryGetArrayContains("all_project_types", "datapack")))
        {
            pack.ResourceType = ResourceType.DataPack;
        }

        pack.Slug = obj.GetPropertySafe("slug").GetString() ?? "";
        pack.WebsiteUrl = pack.Slug.Length != 0 ? "https://modrinth.com/mod/" + pack.Slug : "";

        pack.Description = obj.GetPropertySafe("description").GetString() ?? "";

        pack.LogoUrl = obj.GetPropertySafe("icon_url").GetString() ?? "";
        pack.LogoName = $"{obj.GetPropertySafe("slug").GetString() ?? ""}.{UrlPathSuffix(pack.LogoUrl)}";

        if (obj.TryGetProperty("author", out var author))
        {
            var modAuthor = new ModpackAuthor
            {
                Name = author.GetString() ?? "",
                Url = ModrinthApi.GetAuthorURL(author.GetString() ?? ""),
            };
            pack.Authors = [modAuthor];
        }

        var client = ShouldDownloadOnSide(obj.GetPropertySafe("client_side").GetString() ?? "");
        var server = ShouldDownloadOnSide(obj.GetPropertySafe("server_side").GetString() ?? "");

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

    /// <summary>Port of Modrinth::loadExtraPackData.</summary>
    public static void LoadExtraPackData(IndexedPack pack, JsonElement obj)
    {
        pack.ExtraData.IssuesUrl = ChopTrailingSlash(obj.GetPropertySafe("issues_url").GetString() ?? "");
        pack.ExtraData.SourceUrl = ChopTrailingSlash(obj.GetPropertySafe("source_url").GetString() ?? "");
        pack.ExtraData.WikiUrl = ChopTrailingSlash(obj.GetPropertySafe("wiki_url").GetString() ?? "");
        pack.ExtraData.DiscordUrl = ChopTrailingSlash(obj.GetPropertySafe("discord_url").GetString() ?? "");

        if (obj.TryGetProperty("donation_urls", out var donateArr) && donateArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in donateArr.EnumerateArray())
            {
                var donate = new DonationData
                {
                    Id = d.GetPropertySafe("id").GetString() ?? "",
                    Platform = d.GetPropertySafe("platform").GetString() ?? "",
                    Url = d.GetPropertySafe("url").GetString() ?? "",
                };
                pack.ExtraData.Donate.Add(donate);
            }
        }

        pack.ExtraData.Status = obj.GetPropertySafe("status").GetString() ?? "";

        pack.ExtraData.Body = (obj.GetPropertySafe("body").GetString() ?? "").Replace("<br>", "");

        pack.ExtraDataLoaded = true;
    }

    /// <summary>Port of Modrinth::loadIndexedPackVersion.</summary>
    public static IndexedVersion LoadIndexedPackVersion(JsonElement obj, string preferredHashType = "sha512",
                                                        string preferredFileName = "")
    {
        var file = new IndexedVersion
        {
            AddonId = obj.RequireString("project_id"),
            FileId = obj.RequireString("id"),
            Date = obj.RequireString("date_published"),
        };
        var versionArray = obj.RequireArray("game_versions");
        if (!versionArray.EnumerateArray().Any())
        {
            return new IndexedVersion();
        }
        foreach (var mcVer in versionArray.EnumerateArray())
        {
            var raw = mcVer.GetString() ?? "";
            // double this so we can check both strings when filtering
            file.McVersion.Add(ModrinthApi.MapMcVersionFromModrinth(raw));
            file.McVersion.Add(raw);
        }
        var loaders = obj.RequireArray("loaders");
        foreach (var loader in loaders.EnumerateArray())
        {
            switch (loader.GetString())
            {
                case "neoforge": file.Loaders |= Minecraft.ModLoaderType.NeoForge; break;
                case "forge": file.Loaders |= Minecraft.ModLoaderType.Forge; break;
                case "cauldron": file.Loaders |= Minecraft.ModLoaderType.Cauldron; break;
                case "liteloader": file.Loaders |= Minecraft.ModLoaderType.LiteLoader; break;
                case "fabric": file.Loaders |= Minecraft.ModLoaderType.Fabric; break;
                case "quilt": file.Loaders |= Minecraft.ModLoaderType.Quilt; break;
            }
        }
        file.Version = obj.RequireString("name");
        file.VersionNumber = obj.RequireString("version_number");
        file.VersionType = IndexedVersionType.FromString(obj.RequireString("version_type"));

        if (obj.TryGetProperty("changelog", out var changelog))
        {
            file.Changelog = JsonUtils.RequireString(changelog);
        }

        var dependencies = obj.GetPropertySafe("dependencies");
        if (dependencies.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in dependencies.EnumerateArray())
            {
                var dependency = new Dependency
                {
                    AddonId = d.GetPropertySafe("project_id").GetString() ?? "",
                    Version = d.GetPropertySafe("version_id").GetString() ?? "",
                };
                var depType = d.RequireString("dependency_type");

                dependency.Type = depType switch
                {
                    "required" => new DependencyType(DependencyTypeValue.REQUIRED),
                    "optional" => new DependencyType(DependencyTypeValue.OPTIONAL),
                    "incompatible" => new DependencyType(DependencyTypeValue.INCOMPATIBLE),
                    "embedded" => new DependencyType(DependencyTypeValue.EMBEDDED),
                    _ => new DependencyType(DependencyTypeValue.UNKNOWN),
                };

                file.Dependencies.Add(dependency);
            }
        }

        var files = obj.RequireArray("files");
        var fileCount = files.GetArrayLength();

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
            var fileName = candidate.RequireString("filename");

            if (preferredFileName.Length != 0 && fileName.Contains(preferredFileName, StringComparison.Ordinal))
            {
                file.IsPreferred = true;
                parent = candidate;
                break;
            }

            // Grab the primary file, if available
            if (candidate.RequireBoolean("primary"))
            {
                parent = candidate;
                break;
            }
        }

        if (parent.TryGetProperty("url", out _))
        {
            file.DownloadUrl = parent.RequireString("url");
            file.FileName = parent.RequireString("filename");
            file.FileName = FileSystem.RemoveInvalidPathChars(file.FileName);
            file.IsPreferred = parent.RequireBoolean("primary") || fileCount == 1;
            var hashList = parent.RequireObject("hashes");

            if (hashList.TryGetProperty(preferredHashType, out var preferred))
            {
                file.Hash = JsonUtils.RequireString(preferred);
                file.HashType = preferredHashType;
            }
            else
            {
                var hashTypes = ProviderCapabilities.HashType(ResourceProvider.MODRINTH);
                foreach (var hashType in hashTypes)
                {
                    if (hashList.TryGetProperty(hashType, out var hash))
                    {
                        file.Hash = JsonUtils.RequireString(hash);
                        file.HashType = hashType;
                        break;
                    }
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

    private static bool TryGetArrayContains(this JsonElement obj, string arrayKey, string value)
    {
        if (!obj.TryGetProperty(arrayKey, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            // obj.value(key).toArray({}) semantics: missing/other -> empty
            return false;
        }
        foreach (var item in arr.EnumerateArray())
        {
            if (item.GetString() == value)
            {
                return true;
            }
        }
        return false;
    }
}
