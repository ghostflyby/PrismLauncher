// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// tar/tar.gz extraction for Java::ArchiveDownloadTask, on top of the in-box
// System.Formats.Tar reader. Upstream runs every archive through libarchive
// (MMCZip::ArchiveReader); this side covers the tar.gz flavour the Java runtime
// distributions ship in, while zip stays with MMCZip.
//
// Like the C++ flow (collectFiles + parse), consumers make two passes over the archive: a
// listing pass to find the first top-level folder and the extraction pass.

using System.Formats.Tar;
using System.IO.Compression;
using FlintLauncher.Core.IO;
using TruePath;
using TruePath.SystemIo;

namespace FlintLauncher.Core.Java;

public static class TarArchive
{
    /// <summary>
    /// Extracts a tar.gz archive into <paramref name="targetDir"/>, skipping entries that do
    /// not live under <paramref name="stripFirstComponent"/> and stripping that prefix (the
    /// ExtractZipTask behaviour). Returns the extracted paths, or null on failure.
    /// </summary>
    public static List<string>? ExtractTarGzFile(string archivePath, string targetDir, string? stripFirstComponent,
        out string error) =>
        Extract(archivePath, gzipped: true, targetDir, stripFirstComponent, out error);

    /// <summary>Extracts a plain (uncompressed) tar archive; same semantics as the .tar.gz variant.</summary>
    public static List<string>? ExtractTarFile(string archivePath, string targetDir, string? stripFirstComponent,
        out string error) =>
        Extract(archivePath, gzipped: false, targetDir, stripFirstComponent, out error);

    /// <summary>
    /// Lists the entry names of a tar.gz archive (the collectFiles() pass used to determine
    /// which top-level folder to strip). Returns null on failure.
    /// </summary>
    public static List<string>? ListTarGzFile(string archivePath, out string error) =>
        Extract(archivePath, gzipped: true, targetDir: "", stripFirstComponent: null, out error, listOnly: true);

    private static List<string>? Extract(string archivePath, bool gzipped, string targetDir,
        string? stripFirstComponent, out string error, bool listOnly = false)
    {
        try
        {
            using var source = File.OpenRead(archivePath);
            using var stream = gzipped ? new GZipStream(source, CompressionMode.Decompress) : (Stream)source;
            return Extract(stream, targetDir, stripFirstComponent, out error, listOnly);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            error = $"Unable to read {(gzipped ? "tar.gz" : "tar")} archive {archivePath}: {e.Message}";
            return null;
        }
    }

    /// <summary>Core extraction pass over an opened tar stream.</summary>
    public static List<string>? ExtractTarStream(Stream stream, string targetDir, string? stripFirstComponent,
        out string error, bool listOnly = false)
        => Extract(stream, targetDir, stripFirstComponent, out error, listOnly);

    private static List<string>? Extract(Stream stream, string targetDir, string? stripFirstComponent,
        out string error, bool listOnly)
    {
        error = "";
        var extracted = new List<string>();
        var targetTopDir = listOnly ? "" : Path.GetFullPath(targetDir);
        if (!listOnly)
        {
            FileOps.EnsureFolderPathExists(new AbsolutePath(targetTopDir));
        }

        var reader = new TarReader(stream, leaveOpen: true);
        while (reader.GetNextEntry() is { } entry)
        {
            if (listOnly)
            {
                extracted.Add(entry.Name);
                continue;
            }

            if (stripFirstComponent is not null && !entry.Name.StartsWith(stripFirstComponent, StringComparison.Ordinal))
            {
                continue;
            }

            var relativeName = stripFirstComponent is not null ? entry.Name[stripFirstComponent.Length..] : entry.Name;
            relativeName = relativeName.Replace('\\', '/');
            // Fix subdirs/files ending with a / getting transformed into absolute paths
            if (relativeName.StartsWith('/'))
            {
                relativeName = relativeName[1..];
            }

            // Fix weird "folders with a single file get squashed" thing
            string subPath;
            if (relativeName.Contains('/') && !relativeName.EndsWith('/'))
            {
                var sections = relativeName.Split('/');
                subPath = string.Join('/', sections[..^1]) + '/';
                relativeName = sections[^1];
                FileOps.EnsureFolderPathExists(new AbsolutePath(targetTopDir) / subPath);
            }
            else
            {
                subPath = "";
            }

            var targetFilePath = relativeName.Length == 0
                ? targetTopDir + '/'
                : (new AbsolutePath(targetTopDir) / subPath / relativeName).Value;

            // zip-slip check: the resolved target must stay inside the target directory
            if (!IsParentOf(targetTopDir, targetFilePath))
            {
                FileOps.RemoveFiles(extracted);
                error = $"Extracting {relativeName} was cancelled, because it was effectively outside of the target path {targetTopDir}";
                return null;
            }

            if (!ExtractEntry(entry, targetFilePath, extracted, out error))
            {
                FileOps.RemoveFiles(extracted);
                return null;
            }
        }

        return extracted;
    }

    private static bool ExtractEntry(TarEntry entry, string targetFilePath, List<string> extracted, out string error)
    {
        error = "";
        try
        {
            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                {
                    var dir = targetFilePath.TrimEnd('/');
                    if (!FileOps.EnsureFolderPathExists(new AbsolutePath(dir)))
                    {
                        error = $"Failed to create directory {dir}";
                        return false;
                    }

                    extracted.Add(targetFilePath);
                    return true;
                }
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                {
                    FileOps.EnsureFilePathExists(new AbsolutePath(targetFilePath));
                    using (var output = File.Create(targetFilePath))
                    {
                        entry.DataStream?.CopyTo(output);
                    }

                    if (!OperatingSystem.IsWindows() && (entry.Mode & AnyExecute) != 0)
                    {
                        File.SetUnixFileMode(targetFilePath,
                            File.GetUnixFileMode(targetFilePath) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute |
                            UnixFileMode.OtherExecute);
                    }

                    extracted.Add(targetFilePath);
                    return true;
                }
                case TarEntryType.SymbolicLink:
                {
                    try
                    {
                        if (File.Exists(targetFilePath) || Directory.Exists(targetFilePath))
                        {
                            // create_link upstream fails on existing targets; skip instead
                            return true;
                        }

                        FileOps.EnsureFilePathExists(new AbsolutePath(targetFilePath));
                        File.CreateSymbolicLink(targetFilePath, entry.LinkName);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        // best effort like the manifest link handling; never fatal
                    }

                    extracted.Add(targetFilePath);
                    return true;
                }
                case TarEntryType.HardLink:
                {
                    // best-effort copy from the already-extracted target
                    var sourcePath = new AbsolutePath(Path.GetDirectoryName(targetFilePath) ?? "") / entry.LinkName;
                    if (sourcePath.ExistsFile())
                    {
                        FileOps.EnsureFilePathExists(new AbsolutePath(targetFilePath));
                        File.Copy(sourcePath.Value, targetFilePath, overwrite: true);
                        extracted.Add(targetFilePath);
                    }

                    return true;
                }
                default:
                    return true; // devices, fifos and metadata entries carry nothing to extract
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException)
        {
            error = $"Failed to extract entry {targetFilePath}: {e.Message}";
            return false;
        }
    }

    private const UnixFileMode AnyExecute =
        UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    /// <summary>QUrl::isParentOf equivalent for local paths (same check as MMCZip.ExtractSubDir).</summary>
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
