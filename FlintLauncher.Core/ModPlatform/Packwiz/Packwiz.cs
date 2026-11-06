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
//  - QVariant ids are strings ("" = null); the Flame write path parses them back to
//    integers exactly like the C++ toInt() checks.

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
        public string Slug { get; set; } = "";
        public string Name { get; set; } = "";
        public string Filename { get; set; } = "";
        public SideType Side { get; set; } = new(SideTypeValue.UniversalSide);
        public ModLoaderType Loaders { get; set; } = ModLoaderType.None;
        public List<string> McVersions { get; } = [];
        public IndexedVersionType ReleaseType { get; set; } = IndexedVersionType.Invalid;

        // [download]
        public string Mode { get; set; } = "";
        public string Url { get; set; } = "";
        public string HashFormat { get; set; } = "";
        public string Hash { get; set; } = "";

        // [update]
        public ResourceProvider Provider { get; set; }
        public string FileId { get; set; } = "";
        public string ProjectId { get; set; } = "";
        public string VersionNumber { get; set; } = "";

        public List<Dependency> Dependencies { get; } = [];

        // "x-prismlauncher-lock-update": updates for the resource are locked (#3404's
        // update-lock column)
        public bool LockUpdate { get; set; }

        // This is a totally heuristic, but should work for now.
        public bool IsValid => Slug.Length != 0 && ProjectId.Length != 0;

        // Different providers can use different names for the same thing — Modrinth-specific
        public string ModId() => ProjectId;

        public string Version() => FileId;
    }

    /// <summary>
    /// Port of V1::createModFormat: generates the object representing a mod's .pw.toml file
    /// from its common in-launcher representation.
    /// </summary>
    public static Mod CreateModFormat(IndexedPack modPack, IndexedVersion modVersion)
    {
        var mod = new Mod
        {
            Slug = modPack.Slug,
            Name = modPack.Name,
            Filename = modVersion.FileName,
        };

        if (modPack.Provider == ResourceProvider.Flame)
        {
            mod.Mode = "metadata:curseforge";
        }
        else
        {
            mod.Mode = "url";
            mod.Url = modVersion.DownloadUrl;
        }

        mod.HashFormat = modVersion.HashType;
        mod.Hash = modVersion.Hash;

        mod.Provider = modPack.Provider;
        mod.FileId = modVersion.FileId;
        mod.ProjectId = modPack.AddonId;
        mod.Side = modVersion.Side.IsValid ? modVersion.Side : modPack.Side;
        mod.Loaders = modVersion.Loaders;
        mod.McVersions.AddRange(modVersion.McVersion);
        RemoveDuplicates(mod.McVersions);
        mod.McVersions.Sort(SortMcVersions);
        mod.ReleaseType = modVersion.VersionType;

        mod.VersionNumber = modVersion.VersionNumber;
        if (mod.VersionNumber.Length == 0) // on CurseForge, there is only a version name - not a version number
        {
            mod.VersionNumber = modVersion.Version;
        }

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

        string update;
        switch (mod.Provider)
        {
            case ResourceProvider.Flame:
                if (long.TryParse(mod.FileId, out var fileId) && long.TryParse(mod.ProjectId, out var projectId))
                {
                    if (fileId == 0 || projectId == 0)
                    {
                        // "Did not write file {name} because missing information!"
                        return;
                    }
                }
                else
                {
                    // "Did not write file {name} because missing information!"
                    return;
                }

                update = $"file-id = {mod.FileId}\nproject-id = {mod.ProjectId}";
                break;
            case ResourceProvider.Modrinth:
                if (mod.ModId().Length == 0 || mod.Version().Length == 0)
                {
                    // "Did not write file {name} because missing information!"
                    return;
                }

                update = $"mod-id = {T(mod.ModId())}\nversion = {T(mod.Version())}";
                break;
            default:
                return;
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
            if (dep.Version.Length != 0)
            {
                deps.Append(", version = ").Append(T(dep.Version));
            }

            deps.Append(" }");
        }

        deps.Append(']');

        var toml = new StringBuilder();
        toml.Append("name = ").AppendLine(T(mod.Name));
        toml.Append("filename = ").AppendLine(T(mod.Filename));
        toml.Append("side = ").AppendLine(T(SideType.ToString(mod.Side.Value)));
        toml.Append("x-prismlauncher-loaders = [").Append(string.Join(", ", loaders)).AppendLine("]");
        toml.Append("x-prismlauncher-mc-versions = [").Append(string.Join(", ", mcVersions)).AppendLine("]");
        toml.Append("x-prismlauncher-release-type = ").AppendLine(T(IndexedVersionType.ToString(mod.ReleaseType.Value)));
        toml.Append("x-prismlauncher-version-number = ").AppendLine(T(mod.VersionNumber));
        toml.Append("x-prismlauncher-dependencies = ").AppendLine(deps.ToString());
        toml.Append("x-prismlauncher-lock-update = ").AppendLine(mod.LockUpdate ? "true" : "false");
        toml.AppendLine("[download]");
        toml.Append("mode = ").AppendLine(T(mod.Mode));
        toml.Append("url = ").AppendLine(T(mod.Url));
        toml.Append("hash-format = ").AppendLine(T(mod.HashFormat));
        toml.Append("hash = ").AppendLine(T(mod.Hash));
        toml.AppendLine("[update]");
        toml.Append('[').Append("update.").Append(ProviderCapabilities.Name(mod.Provider)).AppendLine("]");
        toml.AppendLine(update);
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
    /// Port of V1::getIndexForMod(index_dir, slug): gets the metadata for a mod with a
    /// particular slug. If the mod doesn't have metadata, an empty Mod is returned.
    /// </summary>
    public static Mod GetIndexForMod(AbsolutePath indexDir, string slug)
    {
        var mod = new Mod();

        var normalizedFname = IndexFileName(slug);
        var realFname = GetRealIndexName(indexDir, normalizedFname, shouldFindMatch: true);
        if (realFname.Length == 0)
        {
            return mod;
        }

        var path = indexDir / realFname;
        PackwizTomlFile file;
        try
        {
            file = TomlSerializer.Deserialize<PackwizTomlFile>(
                path.ReadAllText().TrimStart('\uFEFF'), FlintTomlContext.Default) ?? new PackwizTomlFile();
        }
        catch (Exception e) // upstream #6047: any failure here means "no usable metadata", not a crash
        {
            // "Could not open file {name}!" + reason
            _ = e;
            return mod;
        }

        mod.Slug = slug;

        // Basic info
        {
            mod.Name = file.Name ?? "";
            mod.Filename = file.Filename ?? "";
            mod.Side = SideType.FromString(file.Side ?? "");
            mod.ReleaseType = IndexedVersionType.FromString(file.ReleaseType ?? "");
            foreach (var loader in file.Loaders ?? [])
            {
                mod.Loaders |= ModIndex.GetModLoaderFromString(loader);
            }

            var versions = file.McVersions ?? [];
            mod.McVersions.AddRange(versions.Where(v => v.Length != 0));
            RemoveDuplicates(mod.McVersions);
            mod.McVersions.Sort(SortMcVersions);
        }
        mod.VersionNumber = file.VersionNumber ?? "";
        mod.LockUpdate = file.LockUpdate;

        // [download] info
        {
            var download = file.Download;
            if (download is null)
            {
                // "No [download] section found on mod metadata!"
                return new Mod();
            }

            mod.Mode = download.Mode ?? "";
            mod.Url = download.Url ?? "";
            mod.HashFormat = download.HashFormat ?? "";
            mod.Hash = download.Hash ?? "";
        }

        // [update] info
        {
            var update = file.Update;
            if (update is null)
            {
                // "No [update] section found on mod metadata!"
                return new Mod();
            }

            if (update.CurseForge is { } flame)
            {
                mod.Provider = ResourceProvider.Flame;
                mod.FileId = flame.FileId.ToString(CultureInfo.InvariantCulture);
                mod.ProjectId = flame.ProjectId.ToString(CultureInfo.InvariantCulture);
            }
            else if (update.Modrinth is { } modrinth)
            {
                mod.Provider = ResourceProvider.Modrinth;
                mod.ProjectId = modrinth.ModId ?? "";
                mod.FileId = modrinth.Version ?? "";
            }
            else
            {
                // "No mod provider on mod metadata!"
                return new Mod();
            }
        }

        // dependencies
        {
            foreach (var depFile in file.Dependencies ?? [])
            {
                mod.Dependencies.Add(new Dependency
                {
                    AddonId = depFile.AddonId ?? "",
                    Version = depFile.Version ?? "",
                    Type = DependencyType.FromString(depFile.Type ?? ""),
                });
            }
        }

        return mod;
    }

    /// <summary>
    /// Port of V1::getIndexForMod(index_dir, mod_id): scans the index directory for the
    /// metadata of a mod with the given provider id. Empty Mod when not found.
    /// </summary>
    public static Mod GetIndexForModById(AbsolutePath indexDir, string modId)
    {
        foreach (var fileName in ListFiles(indexDir))
        {
            var mod = GetIndexForMod(indexDir, fileName);
            if (mod.ModId() == modId)
            {
                return mod;
            }
        }

        return new Mod();
    }

    // ------------------------------------------------------------------ helpers

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
