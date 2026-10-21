// SPDX-License-Identifier: GPL-3.0-only
// Port of the file-level parts of launcher/icons/: IconUtils.{h,cpp}
// (findBestIconIn/getIconFilter/isIconSuffix) and the file operations of IconList.{h,cpp}
// (installIcon, deleteIcon, the recursive icon file scan with its key derivation and the
// display-name helper). No Qt models, no QIcon/QPixmap theming.
//
// Deviations:
//  - IconList is a live Qt model over MMCIcon entries; the port derives the same
//    information from the icons directory on demand (the icon *file* for a key, whether it
//    exists, the key/file listings). The key lookup keeps upstream's "default" -> "grass"
//    alias of IconList::getIconIndex.
//  - installIcon() returned void upstream (failures silently ignored); the port returns
//    bool. Like QFile::copy, it does not overwrite an existing target.

using FlintLauncher.Core.IO;
using TruePath;

namespace FlintLauncher.Core.Icons;

public static class IconFiles
{
    // IconUtils.cpp's validIconExtensions
    private static readonly string[] ValidIconExtensions = ["svg", "png", "ico", "gif", "jpg", "jpeg", "webp"];

    /// <summary>Port of IconUtils::isIconSuffix.</summary>
    public static bool IsIconSuffix(string suffix) => ValidIconExtensions.Contains(suffix);

    /// <summary>Port of IconUtils::getIconFilter (file-dialog filter string).</summary>
    public static string GetIconFilter() => "(*." + string.Join(" *.", ValidIconExtensions) + ")";

    /// <summary>
    /// Port of IconUtils::findBestIconIn: given a folder and an icon key, find the icon with
    /// the given key in there and return its path ("" when there is none).
    /// </summary>
    public static string FindBestIconIn(string folder, string iconKey)
    {
        if (!Directory.Exists(folder))
        {
            return "";
        }

        // QDirIterator(folder, NoDotAndDotDot | Files, NoIteratorFlags): files, non-recursive
        var options = new EnumerationOptions
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
        };
        foreach (var file in Directory.EnumerateFiles(folder, "*", options))
        {
            var fileName = Path.GetFileName(file);
            if ((CompleteBaseName(fileName) == iconKey || fileName == iconKey) && IsIconSuffix(Suffix(fileName)))
            {
                return file;
            }
        }

        return "";
    }

    /// <summary>
    /// Port of IconList::getIconFilePaths: every icon file in the directory tree,
    /// breadth-first (directories first, files collected per level).
    /// </summary>
    public static List<string> GetIconFilePaths(string directory)
    {
        var iconFiles = new List<string>();
        var directories = new Queue<string>();
        directories.Enqueue(directory);
        var options = new EnumerationOptions
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
        };
        while (directories.Count != 0)
        {
            var first = directories.Dequeue();
            if (!Directory.Exists(first))
            {
                continue;
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(first, "*", options)
                         .OrderBy(p => p, StringComparer.Ordinal))
            {
                if (Directory.Exists(entry) && !File.Exists(entry))
                {
                    directories.Enqueue(entry);
                }
                else
                {
                    iconFiles.Add(entry);
                }
            }
        }

        return iconFiles;
    }

    /// <summary>
    /// Port of the IconList key derivation: QFileInfo(dir.relativeFilePath(file))
    /// .completeBaseName() — the file name up to the last dot, without any directory parts.
    /// </summary>
    public static string IconKeyForFile(string iconsDir, string filePath)
    {
        var relative = Path.GetRelativePath(iconsDir, filePath).Replace('\\', '/');
        return CompleteBaseName(Path.GetFileName(relative));
    }

    /// <summary>
    /// Port of the formatName() helper (display label of an icon file): the base name at the
    /// root, or "&lt;relative dir&gt; » &lt;base name&gt;" for icons in subdirectories.
    /// </summary>
    public static string IconDisplayName(string iconsDir, string filePath)
    {
        const string delimiter = " » ";
        var baseName = CompleteBaseName(Path.GetFileName(filePath));
        var iconsDirFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(iconsDir));
        var dir = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? "");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(dir, iconsDirFull, comparison))
        {
            return baseName;
        }

        var relativeDir = Path.GetRelativePath(iconsDirFull, dir).Replace('\\', '/');
        return relativeDir + delimiter + baseName;
    }

    /// <summary>
    /// Port of IconList::installIcon: copy an icon file into the icons directory (named
    /// <paramref name="name"/>, or its own file name when empty). Returns false when the
    /// source is not a readable icon file, when the suffix is not supported, or when the
    /// target already exists (QFile::copy semantics).
    /// </summary>
    public static bool InstallIcon(string iconsDir, string file, string name = "")
    {
        if (!File.Exists(file) || Directory.Exists(file))
        {
            return false; // QFileInfo::isFile()/isReadable()
        }

        if (!IsIconSuffix(Suffix(Path.GetFileName(file))))
        {
            return false;
        }

        if (!FileOps.EnsureFolderPathExists(new LocalPath(iconsDir)))
        {
            return false;
        }

        var target = Path.Combine(iconsDir, name.Length == 0 ? Path.GetFileName(file) : name);
        try
        {
            File.Copy(file, target, overwrite: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Port of IconList::getIcon/getIconIndex reduced to file-based icons: the icon *file*
    /// for the given key ("" when there is none). Like the upstream model, the lookup scans
    /// the whole icon tree recursively (subdirectory icons carry their base-name key) and a
    /// later duplicate key replaces an earlier one. The "default" key aliases to "grass",
    /// like the upstream fallback.
    /// </summary>
    public static string GetIconFile(string iconsDir, string key)
    {
        var resolvedKey = key == "default" ? "grass" : key;
        var match = "";
        foreach (var file in GetIconFilePaths(iconsDir))
        {
            if (IconKeyForFile(iconsDir, file) == resolvedKey && IsIconSuffix(Suffix(Path.GetFileName(file))))
            {
                match = file; // addIcon() replaces the entry of an existing key
            }
        }

        return match;
    }

    /// <summary>Port of IconList::iconFileExists (file-based).</summary>
    public static bool IconFileExists(string iconsDir, string key) => GetIconFile(iconsDir, key).Length != 0;

    /// <summary>Port of IconList::deleteIcon.</summary>
    public static bool DeleteIcon(string iconsDir, string key)
    {
        var file = GetIconFile(iconsDir, key);
        return file.Length != 0 && FileOps.DeletePath(new LocalPath(file));
    }

    // ------------------------------------------------------------------------ helpers

    /// <summary>QFileInfo::completeBaseName(): the file name up to the last dot.</summary>
    private static string CompleteBaseName(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        return dot > 0 ? fileName[..dot] : fileName;
    }

    /// <summary>QFileInfo::suffix(): the part after the last dot ("" when there is none).</summary>
    private static string Suffix(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        return dot > 0 ? fileName[(dot + 1)..] : "";
    }
}
