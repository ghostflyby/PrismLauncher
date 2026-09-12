// SPDX-License-Identifier: GPL-3.0-only
// Port of modplatform/helpers/OverrideUtils.{h,cpp}: the overrides bookkeeping used by the
// Modrinth pack install/update flows. Also hosts OverrideFolder (the FS::overrideFolder
// helper from launcher/FileSystem.{h,cpp} that the same flows call — IO/FileSystem.cs is
// outside this phase's edit scope).
using FlintLauncher.Core.IO;

namespace FlintLauncher.Core.ModPlatform;

public static class OverrideUtils
{
    /// <summary>
    /// Port of Override::createOverrides: creates a file in <paramref name="parentFolder"/>
    /// that holds information about which overrides are in <paramref name="overridePath"/>.
    /// If there's already an existing such file, it will be overwritten.
    /// </summary>
    public static void CreateOverrides(string name, string parentFolder, string overridePath)
    {
        var filePath = FileSystem.PathCombine(parentFolder, name + ".txt");
        if (File.Exists(filePath))
        {
            FileSystem.DeletePath(filePath);
        }

        FileSystem.EnsureFilePathExists(filePath);

        using var writer = new StreamWriter(filePath);
        foreach (var path in EnumerateOverrideFiles(overridePath))
        {
            // Absolute path with temp directory -> relative path
            writer.WriteLine(path);
        }
    }

    /// <summary>
    /// Port of Override::readOverrides: reads an existing overrides archive, returning a
    /// list of overrides. If there's no such file in <paramref name="parentFolder"/>, an
    /// empty list is returned.
    /// </summary>
    public static List<string> ReadOverrides(string name, string parentFolder)
    {
        var filePath = FileSystem.PathCombine(parentFolder, name + ".txt");
        if (!File.Exists(filePath))
        {
            return [];
        }

        var previousOverrides = new List<string>();
        using var reader = new StreamReader(filePath);
        while (reader.ReadLine() is { } entry)
        {
            previousOverrides.Add(entry.Trim());
        }
        return previousOverrides;
    }

    /// <summary>
    /// Port of FS::overrideFolder: copy the contents of <paramref name="overridePath"/>
    /// over <paramref name="overwrittenPath"/> without deleting files that already exist
    /// (recursive, overwrite-existing).
    /// </summary>
    public static bool OverrideFolder(string overwrittenPath, string overridePath)
    {
        if (!FileSystem.EnsureFolderPathExists(overwrittenPath))
        {
            return false;
        }

        try
        {
            foreach (var source in Directory.EnumerateFiles(overridePath, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(overridePath, source);
                var target = Path.GetFullPath(FileSystem.PathCombine(overwrittenPath, relative));
                FileSystem.EnsureFilePathExists(target);
                File.Copy(source, target, overwrite: true);
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    // QDirIterator(override_path, Subdirectories) visited files in path order; the C++
    // strips everything up to the last occurrence of the override folder's *directory
    // name* and drops one separator. Doing the same with plain relative paths is
    // equivalent for the actual callers (the folder name is the last component).
    private static IEnumerable<string> EnumerateOverrideFiles(string overridePath)
    {
        foreach (var path in Directory.EnumerateFiles(overridePath, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(overridePath, path).Replace('\\', '/');
            var splitIndex = relative.LastIndexOf(Path.GetFileName(overridePath.TrimEnd('/')), StringComparison.Ordinal);
            if (splitIndex >= 0)
            {
                // C++: split(name).last().remove(0, 1) -> drop the matched name and one separator
                var after = relative[(splitIndex + Path.GetFileName(overridePath.TrimEnd('/')).Length)..];
                yield return after.Length != 0 && after[0] == '/' ? after[1..] : after;
            }
            else
            {
                yield return relative;
            }
        }
    }
}
