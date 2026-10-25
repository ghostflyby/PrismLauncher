// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022 Sefa Eyeoglu <contact@scrumplex.net>
// SPDX-FileCopyrightText: 2023-2024 Trial97 <alexandru.tripon97@gmail.com>
//
// This file incorporates work covered by the following copyright and
// permission notice:
//
//     Copyright 2013-2021 MultiMC Contributors
//
//     Licensed under the Apache License, Version 2.0 (the "License");
//     you may not use this file except in compliance with the License.
//     You may obtain a copy of the License at
//
//         http://www.apache.org/licenses/LICENSE-2.0
//
//     Unless required by applicable law or agreed to in writing, software
//     distributed under the License is distributed on an "AS IS" BASIS,
//     WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//     See the License for the specific language governing permissions and
//     limitations under the License.
//
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
using TruePath;
using TruePath.SystemIo;

namespace FlintLauncher.Core.IO;

public static class MmcZip
{
    /// <summary>FilterFileFunction/FilterFunction equivalent: return true to exclude (collect) or to allow (merge).</summary>
    public delegate bool FilterFunction(string relativeName);

    /// <summary>
    /// Port of MMCZip::extractDir(fileCompressed, dir): extract a whole archive.
    /// Returns the full paths of extracted entries, or null on failure.
    /// </summary>
    public static List<string>? ExtractDir(string fileCompressed, AbsolutePath dir)
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
    public static List<string>? ExtractDir(AbsolutePath fileCompressed, string subdir, AbsolutePath dir)
    {
        if (new FileInfo(fileCompressed.Value).Length == 22)
        {
            return [];
        }

        try
        {
            using var zip = ZipFile.OpenRead(fileCompressed.Value);
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

            FileOps.EnsureFilePathExists(new AbsolutePath(target));
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
    public static List<string>? ExtractSubDir(ZipArchive zip, string subdir, AbsolutePath target)
    {
        var extracted = new List<string>();
        var entries = zip.Entries;
        if (entries.Count == 0)
        {
            // Extracting empty archives seems odd...
            return extracted;
        }

        FileOps.EnsureFolderPathExists(target);

        foreach (var entry in entries)
        {
            var fileName = FileNaming.RemoveInvalidPathChars(entry.FullName.Replace('\\', '/'));
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
                FileOps.EnsureFolderPathExists((new AbsolutePath(target) / subPath));
                relativeFileName = sections[^1];
            }
            else
            {
                subPath = "";
            }

            AbsolutePath targetFilePath;
            if (relativeFileName.Length == 0)
            {
                targetFilePath = target;
            }
            else
            {
                targetFilePath = target / subPath / relativeFileName;
            }

            // zip-slip check: the resolved target must stay inside the target directory
            if (!IsParentOf(target.Value, targetFilePath.Value))
            {
                // Extracting was cancelled, because it was effectively outside of the target path
                FileOps.RemoveFiles(extracted);
                return null;
            }

            if (relativeFileName.Length == 0 || relativeFileName.EndsWith('/'))
            {
                // directory entry
                if (!FileOps.EnsureFolderPathExists(targetFilePath))
                {
                    FileOps.RemoveFiles(extracted);
                    return null;
                }

                extracted.Add(targetFilePath.Value + "/"); // the trailing '/' marks directory entries
                continue;
            }

            try
            {
                FileOps.EnsureFilePathExists(targetFilePath);
                using var source = entry.Open();
                using var output = File.Create(targetFilePath.Value);
                source.CopyTo(output);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                          or NotSupportedException)
            {
                // Failed to extract file
                FileOps.RemoveFiles(extracted);
                return null;
            }

            extracted.Add(targetFilePath.Value);
        }

        return extracted;
    }

    /// <summary>
    /// Convenience wrapper combining the C++ collectFileListRecursively + compressDirFiles
    /// (used by createModdedJar/exporters): zip the whole <paramref name="dir"/> tree into
    /// <paramref name="zipFile"/>. The optional filter receives paths relative to dir and
    /// returns true to exclude a file. Folder entries are preserved.
    /// </summary>
    public static bool CompressDir(AbsolutePath zipFile, AbsolutePath dir, FilterFunction? excludeFilter = null)
    {
        var files = new List<string>();
        if (!CollectFileListRecursively(dir, null, files,
                excludeFilter != null ? (Func<FileSystemInfo, bool>)(f => excludeFilter(GetRelativePath(dir.Value, f.FullName))) : null))
        {
            return false;
        }

        using var stream = File.Create(zipFile.Value);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        return CompressDirFiles(zip, dir.Value, files) && AddDirectoryEntries(zip, dir.Value, excludeFilter);
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
    public static bool CollectFileListRecursively(AbsolutePath rootDir, string? subDir, List<string> files,
        Func<FileSystemInfo, bool>? excludeFilter = null)
    {
        if (!rootDir.ExistsDirectory())
        {
            return false;
        }

        var directory = subDir ?? rootDir.Value;
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
        var subdirectories = Directory.EnumerateDirectories(dir, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
        });
        foreach (var sub in subdirectories)
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
