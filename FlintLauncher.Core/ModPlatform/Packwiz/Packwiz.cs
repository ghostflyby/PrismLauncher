// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022 flowln <flowlnlnln@gmail.com>
// SPDX-FileCopyrightText: 2023 Trial97 <alexandru.tripon97@gmail.com>
//
// Port of modplatform/packwiz/Packwiz.{h,cpp} (the MetadataHandler storage backend): the
// .pw.toml per-mod metadata index. The toml++ read path is replaced by Tomlyn's typed DTOs
// (TOML 1.1 on input); the writer still emits TOML 1.0 syntax for the same key layout.
//
// Deviations:
//  - QDir/QFile become string paths + System.IO; the C++ qWarning/qCritical paths become
//    no-ops except where they change control flow (they only log upstream).
//  - QVariant ids are nullable strings; absence is null, and the Flame write path parses
//    present ids back to integers exactly like the C++ toInt() checks.

using System.Globalization;
using System.Text;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Toml;
using FlintLauncher.Core.Versioning;
using Tomlyn;
using TruePath;
using TruePath.SystemIo;

namespace FlintLauncher.Core.ModPlatform.Packwiz;

public static class Packwiz
{
    /// <summary>Port of Packwiz::V1::Mod.</summary>
    public class Mod
    {
        public required string Slug { get; init; }
        public required string Name { get; init; }
        public required string Filename { get; init; }
        public SideType Side { get; set; } = new(SideTypeValue.UniversalSide);
        public ModLoaderType Loaders { get; set; } = ModLoaderType.None;
        public List<string> McVersions { get; } = [];
        public IndexedVersionType ReleaseType { get; set; } = IndexedVersionType.Invalid;

        // [download]
        public string? Mode { get; set; }
        public string? Url { get; set; }
        public string? HashFormat { get; set; }
        public string? Hash { get; set; }

        // [update] — absent when the metadata has no update provider
        public ResourceProvider? Provider { get; set; }
        public string? FileId { get; set; }
        public string? ProjectId { get; set; }
        public string? VersionNumber { get; set; }

        public List<Dependency> Dependencies { get; } = [];

        // "x-prismlauncher-lock-update": updates for the resource are locked (#3404's
        // update-lock column)
        public bool LockUpdate { get; set; }

        public bool IsValid => Slug.Length != 0 && Name.Length != 0 && Filename.Length != 0
                               && (Mode == "metadata:curseforge" || Url is not null);

        // Different providers can use different names for the same thing — Modrinth-specific
        public string? ModId() => ProjectId;

        public string? Version() => FileId;
    }

    /// <summary>
    /// Port of V1::createModFormat: generates the object representing a mod's .pw.toml file
    /// from its common in-launcher representation.
    /// </summary>
    public static Mod? CreateModFormat(IndexedPack modPack, IndexedVersion modVersion)
    {
        var slug = NullIfEmpty(modPack.Slug);
        var name = NullIfEmpty(modPack.Name);
        var filename = NullIfEmpty(modVersion.FileName);
        if (slug is null || name is null || filename is null)
        {
            return null;
        }

        var mod = new Mod
        {
            Slug = slug,
            Name = name,
            Filename = filename,
            HashFormat = NullIfEmpty(modVersion.HashType),
            Hash = NullIfEmpty(modVersion.Hash),
            Provider = modPack.Provider,
            FileId = NullIfEmpty(modVersion.FileId),
            ProjectId = NullIfEmpty(modPack.AddonId),
            VersionNumber = NullIfEmpty(modVersion.VersionNumber) ?? NullIfEmpty(modVersion.Version),
        };

        if (modPack.Provider == ResourceProvider.Flame)
        {
            mod.Mode = "metadata:curseforge";
        }
        else
        {
            mod.Mode = "url";
            mod.Url = NullIfEmpty(modVersion.DownloadUrl);
            if (mod.Url is null)
            {
                return null;
            }
        }

        mod.Side = modVersion.Side.IsValid ? modVersion.Side : modPack.Side;
        mod.Loaders = modVersion.Loaders;
        mod.McVersions.AddRange(modVersion.McVersion);
        RemoveDuplicates(mod.McVersions);
        mod.McVersions.Sort(SortMcVersions);
        mod.ReleaseType = modVersion.VersionType;
        mod.Dependencies.AddRange(modVersion.Dependencies);
        return mod;
    }

    /// <summary>
    /// Port of V1::updateModIndex: updates (creates if needed) the mod's .pw.toml index file.
    /// </summary>
    public static void UpdateModIndex(AbsolutePath indexDir, Mod mod)
    {
        if (!mod.IsValid)
        {
            // "Tried to update metadata of an invalid mod!"
            return;
        }

        // Ensure the corresponding mod's info exists, and create it if not
        var normalizedFname = IndexFileName(mod.Slug);
        var realFname = GetRealIndexName(indexDir, normalizedFname);
        var realPath = indexDir / realFname;
        var normalizedPath = indexDir / normalizedFname;

        if (realFname != normalizedFname && realPath.ExistsFile())
        {
            realPath.Move(normalizedPath);
        }

        // There's already data on there!
        // TODO: We should do more stuff here, as the user is likely trying to
        // override a file. In this case, check versions and ask the user what
        // they want to do!
        if (normalizedPath.Exists())
        {
            normalizedPath.Delete();
        }
        else
        {
            FileOps.EnsureFilePathExists(new AbsolutePath(normalizedPath));
        }

        string? update = null;
        switch (mod.Provider)
        {
            case ResourceProvider.Flame:
                if (mod.FileId is { } fileIdText && mod.ProjectId is { } projectIdText
                    && long.TryParse(fileIdText, out var fileId)
                    && long.TryParse(projectIdText, out var projectId)
                    && fileId != 0 && projectId != 0)
                {
                    update = $"file-id = {fileIdText}\nproject-id = {projectIdText}";
                }

                break;
            case ResourceProvider.Modrinth:
                if (mod.ProjectId is { } modId && mod.FileId is { } version)
                {
                    update = $"mod-id = {T(modId)}\nversion = {T(version)}";
                }

                break;
        }

        var loaders = ModIndex.ModLoaderTypesToList(mod.Loaders).Select(l => T(ModIndex.GetModLoaderAsString(l)));
        var mcVersions = mod.McVersions.Select(T);

        var deps = new StringBuilder();
        deps.Append('[');
        for (var i = 0; i < mod.Dependencies.Count; i++)
        {
            var dep = mod.Dependencies[i];
            if (i != 0)
            {
                deps.Append(", ");
            }

            deps.Append("{ addonId = ").Append(T(dep.AddonId));
            deps.Append(", type = ").Append(T(dep.Type.ToString()));
            if (dep.Version is { } depVersion)
            {
                deps.Append(", version = ").Append(T(depVersion));
            }

            deps.Append(" }");
        }

        deps.Append(']');

        var toml = new StringBuilder();
        toml.Append("name = ").AppendLine(T(mod.Name));
        toml.Append("filename = ").AppendLine(T(mod.Filename));
        if (mod.Side.IsValid)
        {
            toml.Append("side = ").AppendLine(T(SideType.ToString(mod.Side.Value)));
        }

        toml.Append("x-prismlauncher-loaders = [").Append(string.Join(", ", loaders)).AppendLine("]");
        toml.Append("x-prismlauncher-mc-versions = [").Append(string.Join(", ", mcVersions)).AppendLine("]");
        if (mod.ReleaseType.IsValid)
        {
            toml.Append("x-prismlauncher-release-type = ").AppendLine(T(IndexedVersionType.ToString(mod.ReleaseType.Value)));
        }
        if (mod.VersionNumber is { } versionNumber)
        {
            toml.Append("x-prismlauncher-version-number = ").AppendLine(T(versionNumber));
        }

        toml.Append("x-prismlauncher-dependencies = ").AppendLine(deps.ToString());
        toml.Append("x-prismlauncher-lock-update = ").AppendLine(mod.LockUpdate ? "true" : "false");
        toml.AppendLine("[download]");
        if (mod.Mode is { } mode)
        {
            toml.Append("mode = ").AppendLine(T(mode));
        }

        if (mod.Url is { } url)
        {
            toml.Append("url = ").AppendLine(T(url));
        }

        if (mod.HashFormat is { } hashFormat)
        {
            toml.Append("hash-format = ").AppendLine(T(hashFormat));
        }

        if (mod.Hash is { } hash)
        {
            toml.Append("hash = ").AppendLine(T(hash));
        }

        if (update is not null && mod.Provider is { } provider)
        {
            toml.AppendLine("[update]");
            toml.Append('[').Append("update.").Append(ProviderCapabilities.Name(provider)).AppendLine("]");
            toml.AppendLine(update);
        }
        // trailing newline like toml++'s formatted output
        toml.AppendLine("");

        normalizedPath.WriteAllText(toml.ToString());
    }

    /// <summary>
    /// Port of V1::deleteModIndex: deletes the metadata for the mod with the given slug.
    /// If the metadata doesn't exist, it does nothing.
    /// </summary>
    public static void DeleteModIndex(AbsolutePath indexDir, string modSlug)
    {
        var normalizedFname = IndexFileName(modSlug);
        var realFname = GetRealIndexName(indexDir, normalizedFname);
        if (realFname.Length == 0)
        {
            return;
        }

        var path = indexDir / realFname;
        if (!path.ExistsFile())
        {
            // "Tried to delete non-existent mod metadata for {slug}!"
            return;
        }

        path.Delete();
    }

    /// <summary>
    /// Port of V1::getIndexForMod(index_dir, slug): the metadata for a mod with the given
    /// slug, or null when the file is missing or is not usable metadata.
    /// </summary>
    public static Mod? GetIndexForMod(AbsolutePath indexDir, string slug)
    {
        var normalizedFname = IndexFileName(slug);
        var realFname = GetRealIndexName(indexDir, normalizedFname, shouldFindMatch: true);
        if (realFname.Length == 0)
        {
            return null;
        }

        var path = indexDir / realFname;
        PackwizTomlFile? parsed;
        try
        {
            parsed = TomlSerializer.Deserialize<PackwizTomlFile>(
                path.ReadAllText().TrimStart('\uFEFF'), FlintTomlContext.Default);
        }
        catch (Exception e) // upstream #6047: any failure here means "no usable metadata", not a crash
        {
            // "Could not open file {name}!" + reason
            _ = e;
            return null;
        }

        if (parsed is null)
        {
            return null;
        }

        var name = NullIfEmpty(parsed.Name);
        var filename = NullIfEmpty(parsed.Filename);
        var mode = NullIfEmpty(parsed.Download.Mode);
        var url = NullIfEmpty(parsed.Download.Url);
        // metadata:curseforge is a Prism extension mode: the URL is resolved at download
        // time, so only the normal url mode requires the download.url key.
        if (name is null || filename is null || (mode != "metadata:curseforge" && url is null))
        {
            return null;
        }

        var mod = new Mod
        {
            Slug = slug,
            Name = name,
            Filename = filename,
            HashFormat = NullIfEmpty(parsed.Download.HashFormat),
            Hash = NullIfEmpty(parsed.Download.Hash),
            Mode = mode,
            Url = url,
            VersionNumber = NullIfEmpty(parsed.VersionNumber),
            LockUpdate = parsed.LockUpdate,
        };

        mod.Side = parsed.Side is { } side ? SideType.FromString(side) : SideType.Invalid;
        mod.ReleaseType = parsed.ReleaseType is { } releaseType
            ? IndexedVersionType.FromString(releaseType)
            : IndexedVersionType.Invalid;
        foreach (var loader in parsed.Loaders ?? [])
        {
            mod.Loaders |= ModIndex.GetModLoaderFromString(loader);
        }

        var versions = parsed.McVersions ?? [];
        mod.McVersions.AddRange(versions.Where(v => v.Length != 0));
        RemoveDuplicates(mod.McVersions);
        mod.McVersions.Sort(SortMcVersions);

        // [update] info: optional in packwiz; a metadata file without a provider simply
        // has no update source, so the download fields above stay usable.
        if (parsed.Update is { } update)
        {
            if (update.CurseForge is { } flame)
            {
                mod.Provider = ResourceProvider.Flame;
                mod.FileId = flame.FileId.ToString(CultureInfo.InvariantCulture);
                mod.ProjectId = flame.ProjectId.ToString(CultureInfo.InvariantCulture);
            }
            else if (update.Modrinth is { } modrinth)
            {
                mod.Provider = ResourceProvider.Modrinth;
                mod.ProjectId = NullIfEmpty(modrinth.ModId);
                mod.FileId = NullIfEmpty(modrinth.Version);
            }
        }

        foreach (var depFile in parsed.Dependencies ?? [])
        {
            mod.Dependencies.Add(new Dependency
            {
                AddonId = depFile.AddonId,
                Version = depFile.Version,
                Type = depFile.Type is { } type ? DependencyType.FromString(type) : DependencyType.Invalid,
            });
        }

        return mod;
    }

    /// <summary>
    /// Port of V1::getIndexForMod(index_dir, mod_id): scans the index directory for the
    /// metadata of a mod with the given provider id, or null when not found.
    /// </summary>
    public static Mod? GetIndexForModById(AbsolutePath indexDir, string modId)
    {
        foreach (var fileName in ListFiles(indexDir))
        {
            var mod = GetIndexForMod(indexDir, fileName);
            if (mod?.ModId() == modId)
            {
                return mod;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ helpers

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>Port of the indexFileName helper.</summary>
    public static string IndexFileName(string modSlug)
    {
        if (modSlug.EndsWith(".pw.toml", StringComparison.Ordinal))
        {
            return modSlug;
        }

        return $"{modSlug}.pw.toml";
    }

    /// <summary>Port of the getRealIndexName helper.</summary>
    private static string GetRealIndexName(AbsolutePath indexDir, string normalizedFname, bool shouldFindMatch = false)
    {
        var realFname = normalizedFname;
        if (!(indexDir / normalizedFname).ExistsFile())
        {
            // Tries to get similar entries
            foreach (var fileName in ListFiles(indexDir))
            {
                if (string.Equals(normalizedFname, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    realFname = fileName;
                    break;
                }
            }

            if (shouldFindMatch && string.Equals(normalizedFname, realFname, StringComparison.Ordinal))
            {
                // "Could not find a match for a valid metadata file!"
                return "";
            }
        }

        return realFname;
    }

    private static List<string> ListFiles(AbsolutePath indexDir)
    {
        try
        {
            return indexDir.EnumerateFiles().Select(path => path.FileName).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    /// <summary>Port of the sortMCVersions helper (Version order, ties broken by string).</summary>
    private static int SortMcVersions(string a, string b)
    {
        var cmp = new McVersion(a).CompareTo(new McVersion(b));
        if (cmp == 0)
        {
            return string.CompareOrdinal(a, b);
        }

        return cmp;
    }

    /// <summary>QStringList::removeDuplicates equivalent (keeps the first occurrence).</summary>
    private static void RemoveDuplicates(List<string> list)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        list.RemoveAll(v => !seen.Add(v));
    }

    /// <summary>Encode a TOML basic string (with the quotes).</summary>
    private static string T(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\t': sb.Append("\\t"); break;
                case '\n': sb.Append("\\n"); break;
                case '\f': sb.Append("\\f"); break;
                case '\r': sb.Append("\\r"); break;
                default:
                    if (char.IsControl(c))
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4"));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }
}
