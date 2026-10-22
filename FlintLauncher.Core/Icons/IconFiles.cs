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
using TruePath.SystemIo;

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
    public static AbsolutePath? FindBestIconIn(AbsolutePath folder, string iconKey)
    {
        if (!folder.ExistsDirectory())
        {
            return null;
        }

        // QDirIterator(folder, NoDotAndDotDot | Files, NoIteratorFlags): files, non-recursive
        var options = new EnumerationOptions
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
        };
        foreach (var file in folder.EnumerateFiles("*", options))
        {
            if ((file.GetFilenameWithoutExtension() == iconKey || file.FileName == iconKey) && IsIconSuffix(file.GetExtensionWithoutDot()))
            {
                return file;
            }
        }

        return null;
    }

    /// <summary>
    /// Port of IconList::getIconFilePaths: every icon file in the directory tree,
    /// breadth-first (directories first, files collected per level).
    /// </summary>
    public static IEnumerable<AbsolutePath> GetIconFilePaths(AbsolutePath directory)
    {
        var iconFiles = new List<AbsolutePath>();
        var directories = new Queue<AbsolutePath>();
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
            if (!first.ExistsDirectory())
            {
                continue;
            }

            foreach (var entry in Directory
                         .EnumerateFileSystemEntries(first.Value, "*", options)
                         .OrderBy(p => p, StringComparer.Ordinal)
                         .Select(p => new AbsolutePath(p)))
            {
                if (entry.ExistsDirectory() && !entry.ExistsFile())
                {
                    directories.Enqueue(entry);
                    continue;
                }

                iconFiles.Add(entry);
            }
        }

        return iconFiles;
    }

    /// <summary>
    /// Port of the IconList key derivation: QFileInfo(dir.relativeFilePath(file))
    /// .completeBaseName() — the file name up to the last dot, without any directory parts.
    /// </summary>
    public static string IconKeyForFile(AbsolutePath iconsDir, AbsolutePath filePath)
    {
        // the C++ derived the key from the relative path's completeBaseName — the directory
        // part never survives GetFileName, so the basename alone is the whole story
        return filePath.GetFilenameWithoutExtension();
    }

    /// <summary>
    /// Port of the formatName() helper (display label of an icon file): the base name at the
    /// root, or "&lt;relative dir&gt; » &lt;base name&gt;" for icons in subdirectories.
    /// </summary>
    public static string IconDisplayName(AbsolutePath iconsDir, AbsolutePath filePath)
    {
        const string delimiter = " » ";
        var baseName = filePath.GetFilenameWithoutExtension();
        var iconsDirFull = iconsDir.Canonicalize();
        var dir = filePath.Parent?.Canonicalize();
        if (dir == iconsDirFull)
        {
            return baseName;
        }
        var relativeDir = dir?.RelativeTo(iconsDirFull);
        return relativeDir + delimiter + baseName;
    }

    /// <summary>
    /// Port of IconList::installIcon: copy an icon file into the icons directory (named
    /// <paramref name="name"/>, or its own file name when empty). Returns false when the
    /// source is not a readable icon file, when the suffix is not supported, or when the
    /// target already exists (QFile::copy semantics).
    /// </summary>
    public static bool InstallIcon(AbsolutePath iconsDir, AbsolutePath file, string name = "")
    {
        if (!file.ExistsFile() || file.ExistsDirectory())
        {
            return false; // QFileInfo::isFile()/isReadable()
        }

        if (!IsIconSuffix(file.GetExtensionWithoutDot()))
        {
            return false;
        }

        if (!FileOps.EnsureFolderPathExists(iconsDir))
        {
            return false;
        }

        string a1 = name.Length == 0 ? file.FileName : name;
        var target = iconsDir / a1;
        try
        {
            file.Copy(target, overwrite: false);
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
    public static AbsolutePath? GetIconFile(AbsolutePath iconsDir, string key)
    {
        var resolvedKey = key == "default" ? "grass" : key;
        AbsolutePath? match = null;
        foreach (var file in GetIconFilePaths(iconsDir))
        {
            if (IconKeyForFile(iconsDir, file) == resolvedKey && IsIconSuffix(file.GetExtensionWithoutDot()))
            {
                match = file; // addIcon() replaces the entry of an existing key
            }
        }

        return match;
    }

    /// <summary>Port of IconList::iconFileExists (file-based).</summary>
    public static bool IconFileExists(AbsolutePath iconsDir, string key) => GetIconFile(iconsDir, key) is not null;

    /// <summary>Port of IconList::deleteIcon.</summary>
    public static bool DeleteIcon(AbsolutePath iconsDir, string key)
    {
        return GetIconFile(iconsDir, key) is { } file && FileOps.DeletePath(file);
    }
}
