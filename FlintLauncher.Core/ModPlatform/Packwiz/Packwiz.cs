// SPDX-License-Identifier: GPL-3.0-only
// Port of modplatform/packwiz/Packwiz.{h,cpp} (the MetadataHandler storage backend): the
// .pw.toml per-mod metadata index. The toml++ dependency is replaced by MiniToml (see the
// subset documentation there); the writer emits the same key layout toml++ produces for
// the equivalent table.
//
// Deviations:
//  - QDir/QFile become string paths + System.IO; the C++ qWarning/qCritical paths become
//    no-ops except where they change control flow (they only log upstream).
//  - QVariant ids are strings ("" = null); the Flame write path parses them back to
//    integers exactly like the C++ toInt() checks.
using System.Text;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Versioning;

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
        public Minecraft.ModLoaderType Loaders { get; set; } = Minecraft.ModLoaderType.None;
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

        if (modPack.Provider == ResourceProvider.FLAME)
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
    public static void UpdateModIndex(string indexDir, Mod mod)
    {
        if (!mod.IsValid)
        {
            // "Tried to update metadata of an invalid mod!"
            return;
        }

        // Ensure the corresponding mod's info exists, and create it if not
        var normalizedFname = IndexFileName(mod.Slug);
        var realFname = GetRealIndexName(indexDir, normalizedFname);
        var realPath = System.IO.Path.Combine(indexDir, realFname);
        var normalizedPath = System.IO.Path.Combine(indexDir, normalizedFname);

        if (realFname != normalizedFname && File.Exists(realPath))
        {
            File.Move(realPath, normalizedPath);
        }

        // There's already data on there!
        // TODO: We should do more stuff here, as the user is likely trying to
        // override a file. In this case, check versions and ask the user what
        // they want to do!
        if (File.Exists(normalizedPath))
        {
            File.Delete(normalizedPath);
        }
        else
        {
            FileSystem.EnsureFilePathExists(normalizedPath);
        }

        string update;
        switch (mod.Provider)
        {
            case ResourceProvider.FLAME:
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
            case ResourceProvider.MODRINTH:
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

        File.WriteAllText(normalizedPath, toml.ToString());
    }

    /// <summary>
    /// Port of V1::deleteModIndex: deletes the metadata for the mod with the given slug.
    /// If the metadata doesn't exist, it does nothing.
    /// </summary>
    public static void DeleteModIndex(string indexDir, string modSlug)
    {
        var normalizedFname = IndexFileName(modSlug);
        var realFname = GetRealIndexName(indexDir, normalizedFname);
        if (realFname.Length == 0)
        {
            return;
        }

        var path = System.IO.Path.Combine(indexDir, realFname);
        if (!File.Exists(path))
        {
            // "Tried to delete non-existent mod metadata for {slug}!"
            return;
        }

        File.Delete(path);
    }

    /// <summary>
    /// Port of V1::getIndexForMod(index_dir, slug): gets the metadata for a mod with a
    /// particular slug. If the mod doesn't have metadata, an empty Mod is returned.
    /// </summary>
    public static Mod GetIndexForMod(string indexDir, string slug)
    {
        var mod = new Mod();

        var normalizedFname = IndexFileName(slug);
        var realFname = GetRealIndexName(indexDir, normalizedFname, shouldFindMatch: true);
        if (realFname.Length == 0)
        {
            return mod;
        }

        var path = System.IO.Path.Combine(indexDir, realFname);
        TomlTable table;
        try
        {
            table = MiniToml.ParseFile(path);
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
            mod.Name = table.GetString("name");
            mod.Filename = table.GetString("filename");
            mod.Side = SideType.FromString(table.GetString("side"));
            mod.ReleaseType = IndexedVersionType.FromString(table.GetString("x-prismlauncher-release-type"));
            foreach (var loader in table.GetStringArray("x-prismlauncher-loaders"))
            {
                mod.Loaders |= ModIndex.GetModLoaderFromString(loader);
            }
            var versions = table.GetStringArray("x-prismlauncher-mc-versions");
            mod.McVersions.AddRange(versions.Where(v => v.Length != 0));
            RemoveDuplicates(mod.McVersions);
            mod.McVersions.Sort(SortMcVersions);
        }
        mod.VersionNumber = table.GetString("x-prismlauncher-version-number");
        mod.LockUpdate = table.GetBoolean("x-prismlauncher-lock-update");

        // [download] info
        {
            var downloadTable = table.GetTable("download");
            if (downloadTable is null)
            {
                // "No [download] section found on mod metadata!"
                return new Mod();
            }

            mod.Mode = downloadTable.GetString("mode");
            mod.Url = downloadTable.GetString("url");
            mod.HashFormat = downloadTable.GetString("hash-format");
            mod.Hash = downloadTable.GetString("hash");
        }

        // [update] info
        {
            var updateTable = table.GetTable("update");
            if (updateTable is null)
            {
                // "No [update] section found on mod metadata!"
                return new Mod();
            }

            var flameTable = updateTable.GetTable(ProviderCapabilities.Name(ResourceProvider.FLAME));
            if (flameTable is not null)
            {
                mod.Provider = ResourceProvider.FLAME;
                mod.FileId = flameTable.GetInteger("file-id").ToString(System.Globalization.CultureInfo.InvariantCulture);
                mod.ProjectId = flameTable.GetInteger("project-id").ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                var modrinthTable = updateTable.GetTable(ProviderCapabilities.Name(ResourceProvider.MODRINTH));
                if (modrinthTable is not null)
                {
                    mod.Provider = ResourceProvider.MODRINTH;
                    mod.ProjectId = modrinthTable.GetString("mod-id");
                    mod.FileId = modrinthTable.GetString("version");
                }
                else
                {
                    // "No mod provider on mod metadata!"
                    return new Mod();
                }
            }
        }

        // dependencies
        {
            foreach (var depTable in table.GetTableArray("x-prismlauncher-dependencies"))
            {
                var dep = new Dependency
                {
                    AddonId = depTable.GetString("addonId"),
                };
                if (depTable.ContainsKey("version"))
                {
                    dep.Version = depTable.GetString("version");
                }
                dep.Type = DependencyType.FromString(depTable.GetString("type"));
                mod.Dependencies.Add(dep);
            }
        }

        return mod;
    }

    /// <summary>
    /// Port of V1::getIndexForMod(index_dir, mod_id): scans the index directory for the
    /// metadata of a mod with the given provider id. Empty Mod when not found.
    /// </summary>
    public static Mod GetIndexForModById(string indexDir, string modId)
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
    private static string GetRealIndexName(string indexDir, string normalizedFname, bool shouldFindMatch = false)
    {
        var realFname = normalizedFname;
        if (!File.Exists(System.IO.Path.Combine(indexDir, normalizedFname)))
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

    private static List<string> ListFiles(string indexDir)
    {
        try
        {
            return Directory.EnumerateFiles(indexDir).Select(Path.GetFileName).OfType<string>().ToList();
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
