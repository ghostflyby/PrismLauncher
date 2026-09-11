// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/MMCZip.{h,cpp} on top of System.IO.Compression.ZipArchive.
//
// Deviations from the C++ (libarchive based) implementation:
//  - Qt zip symlink handling and unix permission bits are not preserved (entries are
//    written as plain files/directories); the C++ ArchiveWriter has the same limitation
//    for most callers.
//  - CompressDir creates explicit directory entries for folders (the C++ compressDirFiles
//    only adds file entries); this matches what jars/Modrinth packs expect.
//  - createModdedJar is not ported: it depends on the minecraft/mod Mod model.
//  - findFiles does not exist in the current C++ MMCZip; nothing to port.
using System.IO.Compression;

namespace Flint.Core.IO;

public static class MMCZip
{
    /// <summary>FilterFileFunction/FilterFunction equivalent: return true to exclude (collect) or to allow (merge).</summary>
    public delegate bool FilterFunction(string relativeName);

    /// <summary>
    /// Port of MMCZip::extractDir(fileCompressed, dir): extract a whole archive.
    /// Returns the full paths of extracted entries, or null on failure.
    /// </summary>
    public static List<string>? ExtractDir(string fileCompressed, string dir)
    {
        // check if this is a minimum size empty zip file...
        if (new FileInfo(fileCompressed).Length == 22)
        {
            return [];
        }
        try
        {
            using var zip = ZipFile.OpenRead(fileCompressed);
            return ExtractSubDir(zip, subdir: "", target: dir);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException
                                 or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Port of MMCZip::extractDir(fileCompressed, subdir, dir): extract a subdirectory of
    /// an archive. Returns the full paths of extracted entries, or null on failure.
    /// </summary>
    public static List<string>? ExtractDir(string fileCompressed, string subdir, string dir)
    {
        if (new FileInfo(fileCompressed).Length == 22)
        {
            return [];
        }
        try
        {
            using var zip = ZipFile.OpenRead(fileCompressed);
            return ExtractSubDir(zip, subdir, dir);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException
                                 or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Port of MMCZip::extractFile(fileCompressed, file, dir/target): extract a single
    /// file from an archive to the given target path.
    /// </summary>
    public static bool ExtractFile(string fileCompressed, string file, string target)
    {
        if (new FileInfo(fileCompressed).Length == 22)
        {
            return true;
        }
        try
        {
            using var zip = ZipFile.OpenRead(fileCompressed);
            var entry = zip.GetEntry(file);
            if (entry == null)
            {
                return false;
            }
            FileSystem.EnsureFilePathExists(target);
            using var source = entry.Open();
            using var output = File.Create(target);
            source.CopyTo(output);
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException
                                 or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Port of MMCZip::extractSubDir: extract every entry under <paramref name="subdir"/>
    /// into <paramref name="target"/>. Includes zip-slip protection matching the C++
    /// QUrl::isParentOf check. Returns null on failure (after removing what was written).
    /// </summary>
    public static List<string>? ExtractSubDir(ZipArchive zip, string subdir, string target)
    {
        var extracted = new List<string>();
        var entries = zip.Entries;
        if (entries.Count == 0)
        {
            // Extracting empty archives seems odd...
            return extracted;
        }

        var targetTopDir = Path.GetFullPath(target);
        FileSystem.EnsureFolderPathExists(targetTopDir);

        foreach (var entry in entries)
        {
            var fileName = FileSystem.RemoveInvalidPathChars(entry.FullName.Replace('\\', '/'));
            if (!fileName.StartsWith(subdir, StringComparison.Ordinal))
            {
                continue;
            }

            var relativeFileName = fileName[subdir.Length..];

            // Fix subdirs/files ending with a / getting transformed into absolute paths
            if (relativeFileName.StartsWith('/'))
            {
                relativeFileName = relativeFileName[1..];
            }

            // Fix weird "folders with a single file get squashed" thing
            string subPath;
            if (relativeFileName.Contains('/') && !relativeFileName.EndsWith('/'))
            {
                var sections = relativeFileName.Split('/');
                subPath = string.Join('/', sections[..^1]) + '/';
                FileSystem.EnsureFolderPathExists(FileSystem.PathCombine(target, subPath));
                relativeFileName = sections[^1];
            }
            else
            {
                subPath = "";
            }

            string targetFilePath;
            if (relativeFileName.Length == 0)
            {
                targetFilePath = targetTopDir + '/';
            }
            else
            {
                targetFilePath = FileSystem.PathCombine(targetTopDir, subPath, relativeFileName);
                if (relativeFileName.EndsWith('/') && !targetFilePath.EndsWith('/'))
                {
                    targetFilePath += '/';
                }
            }

            // zip-slip check: the resolved target must stay inside the target directory
            if (!IsParentOf(targetTopDir, targetFilePath))
            {
                // Extracting was cancelled, because it was effectively outside of the target path
                FileSystem.RemoveFiles(extracted);
                return null;
            }

            if (relativeFileName.Length == 0 || relativeFileName.EndsWith('/'))
            {
                // directory entry
                if (!FileSystem.EnsureFolderPathExists(targetFilePath.TrimEnd('/')))
                {
                    FileSystem.RemoveFiles(extracted);
                    return null;
                }
                extracted.Add(targetFilePath);
                continue;
            }

            try
            {
                FileSystem.EnsureFilePathExists(targetFilePath);
                using var source = entry.Open();
                using var output = File.Create(targetFilePath);
                source.CopyTo(output);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                     or NotSupportedException)
            {
                // Failed to extract file
                FileSystem.RemoveFiles(extracted);
                return null;
            }
            extracted.Add(targetFilePath);
        }
        return extracted;
    }

    /// <summary>
    /// Convenience wrapper combining the C++ collectFileListRecursively + compressDirFiles
    /// (used by createModdedJar/exporters): zip the whole <paramref name="dir"/> tree into
    /// <paramref name="zipFile"/>. The optional filter receives paths relative to dir and
    /// returns true to exclude a file. Folder entries are preserved.
    /// </summary>
    public static bool CompressDir(string zipFile, string dir, FilterFunction? excludeFilter = null)
    {
        var files = new List<string>();
        if (!CollectFileListRecursively(dir, null, files,
                excludeFilter != null ? (Func<FileSystemInfo, bool>)(f => excludeFilter(GetRelativePath(dir, f.FullName))) : null))
        {
            return false;
        }
        using var stream = File.Create(zipFile);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        return CompressDirFiles(zip, dir, files) && AddDirectoryEntries(zip, dir, excludeFilter);
    }

    /// <summary>
    /// Port of MMCZip::compressDirFiles: add the given files (absolute paths) to the zip,
    /// stored under their paths relative to <paramref name="dir"/>.
    /// </summary>
    public static bool CompressDirFiles(ZipArchive zip, string dir, IReadOnlyList<string> files)
    {
        if (!Directory.Exists(dir))
        {
            return false;
        }
        foreach (var file in files)
        {
            var filePath = GetRelativePath(dir, file);
            try
            {
                zip.CreateEntryFromFile(file, filePath, CompressionLevel.Optimal);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                     or NotSupportedException)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Port of MMCZip::mergeZipFiles: copy entries of <paramref name="from"/> into
    /// <paramref name="into"/>, skipping duplicates (tracked in contained) and entries the
    /// filter rejects (filter returns true to keep).
    /// </summary>
    public static bool MergeZipFiles(ZipArchive into, string from, HashSet<string> contained,
                                     Func<string, bool>? filter = null)
    {
        try
        {
            using var source = ZipFile.OpenRead(from);
            foreach (var entry in source.Entries)
            {
                var filename = entry.FullName;
                if (filter != null && !filter(filename))
                {
                    // Skipping file - filtered
                    continue;
                }
                if (!contained.Add(filename))
                {
                    // Skipping already contained file
                    continue;
                }
                var newEntry = into.CreateEntry(filename);
                using var input = entry.Open();
                using var output = newEntry.Open();
                input.CopyTo(output);
            }
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException
                                 or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Port of MMCZip::collectFileListRecursively: populate <paramref name="files"/> with
    /// absolute file paths under subDir (recursing first into directories, like the C++).
    /// The exclude filter receives the FileInfo and returns true to skip the file.
    /// </summary>
    public static bool CollectFileListRecursively(string rootDir, string? subDir, List<string> files,
                                                  Func<FileSystemInfo, bool>? excludeFilter = null)
    {
        if (!Directory.Exists(rootDir))
        {
            return false;
        }
        var directory = subDir ?? rootDir;
        if (!Directory.Exists(directory))
        {
            return false; // shouldn't ever happen
        }

        // recurse directories
        foreach (var dir in Directory.EnumerateDirectories(directory))
        {
            if (!CollectFileListRecursively(rootDir, dir, files, excludeFilter))
            {
                return false;
            }
        }

        // collect files
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            if (excludeFilter != null && excludeFilter(new FileInfo(file)))
            {
                continue; // Skipping file
            }
            files.Add(file); // we want the original paths for CompressDirFiles
        }
        return true;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Create explicit entries for every folder under dir (see header note).</summary>
    private static bool AddDirectoryEntries(ZipArchive zip, string dir, FilterFunction? excludeFilter)
    {
        foreach (var sub in Directory.EnumerateDirectories(dir, "*", new EnumerationOptions
                 {
                     RecurseSubdirectories = true,
                     IgnoreInaccessible = true,
                 }))
        {
            var relative = GetRelativePath(dir, sub);
            if (excludeFilter != null && excludeFilter(relative))
            {
                continue;
            }
            zip.CreateEntry(relative.EndsWith('/') ? relative : relative + '/');
        }
        return true;
    }

    private static string GetRelativePath(string dir, string file)
    {
        var relative = Path.GetRelativePath(dir, file).Replace('\\', '/');
        return relative == "." ? "" : relative;
    }

    /// <summary>QUrl::isParentOf equivalent for local paths (case-insensitive on Windows).</summary>
    private static bool IsParentOf(string parent, string child)
    {
        var parentFull = Path.GetFullPath(parent);
        var childFull = Path.GetFullPath(child.TrimEnd('/'));
        if (parentFull.Length == 0)
        {
            return true;
        }
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!childFull.StartsWith(parentFull, comparison))
        {
            return false;
        }
        return childFull.Length == parentFull.Length || childFull[parentFull.Length] == Path.DirectorySeparatorChar
            || childFull[parentFull.Length] == '/';
    }
}
