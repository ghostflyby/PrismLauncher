// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2023-2024 Trial97 <alexandru.tripon97@gmail.com>
//
// Port of launcher/java/download/ArchiveDownloadTask.{h,cpp}: download a JDK archive (optionally
// checksum-verified) and extract it into the target folder with the first top-level directory
// stripped (ExtractZipTask semantics: "jdk-17.0.9/..." lands directly in the final path).
//
// Deviations from the C++ original:
//  - Upstream goes through the meta cache (APPLICATION->metacache()->resolveEntry("java", ...))
//    + Net::Request::makeCached. The port takes an optional HttpMetaCache: when one is supplied
//    the same resolveEntry("java", <filename>) + MakeCached flow is used, otherwise the archive
//    downloads to a temporary file that is removed after extraction.
//  - MMCZip::ArchiveReader is libarchive-based and reads zip and tar alike; the port handles
//    zip through MMCZip and tar.gz/plain tar through System.Formats.Tar (TarArchive.cs),
//    selected by gzip magic bytes like libarchive does.
//  - Unix permission bits and symlinks stored in zip entries are not restored (the MMCZip
//    port's known limitation; the tar.gz path preserves the execute bits and symlinks).

using System.IO.Compression;
using System.Security.Cryptography;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Tasks;
using TruePath;
using TruePath.SystemIo;

namespace FlintLauncher.Core.Java.Download;

public static class ArchiveDownloadTask
{
    /// <summary>Downloads the archive and extracts it into <c>finalPath</c>. Returning is
    /// success; any other exception is a failure whose message is the reason.
    /// <paramref name="progress"/> null runs without reporting.</summary>
    public static async Task DownloadAsync(Uri url, AbsolutePath finalPath, HttpClient http,
        CancellationToken cancellationToken, IProgress<TaskProgress>? progress = null,
        string checksumType = "", string checksumHash = "", HttpMetaCache? cache = null)
    {
        // JRE found ! download the zip

        AbsolutePath fullPath;
        HttpDownloadTask action;
        var tempDownload = false;
        if (cache is not null)
        {
            var entry = cache.ResolveEntry("java", FileName(url));
            action = HttpDownloadTask.Cached(cache, url, entry);
            fullPath = entry.GetFullPath();
        }
        else
        {
            string a1 = $"flint-java-{Guid.NewGuid():N}-{FileName(url)}";
            fullPath = Temporary.CreateTempFolder() / a1;
            action = HttpDownloadTask.ToFile(url, fullPath, http);
            tempDownload = true;
        }

        if (checksumHash.Length != 0 && checksumType.Length != 0)
        {
            action.RequireChecksum(HashType(checksumType), checksumHash);
        }

        try
        {
            await action.DownloadAsync(progress, cancellationToken).ConfigureAwait(false);

            ExtractJava(fullPath.Value, finalPath, progress);
        }
        finally
        {
            if (tempDownload && fullPath.ExistsFile())
            {
                try
                {
                    fullPath.Delete();
                }
                catch (IOException)
                {
                    // best effort cleanup of the scratch download
                }
            }
        }
    }

    private static void ExtractJava(string input, AbsolutePath finalPath, IProgress<TaskProgress>? progress)
    {
        // determine which top-level folder to strip (C++: files[0].split('/').value(0));
        // null = unreadable archive, "" = readable but empty
        var firstFolder = FirstTopLevelFolder(input);
        if (firstFolder is null)
        {
            throw new InvalidOperationException(IsTarGz(input)
                ? "Unable to open supplied archive file."
                : "Unable to open supplied zip file.");
        }

        if (firstFolder.Length == 0)
        {
            throw new InvalidOperationException(IsTarGz(input)
                ? "No files were found in the supplied archive file."
                : "No files were found in the supplied zip file.");
        }

        List<string>? extracted;
        if (IsTarGz(input))
        {
            extracted = TarArchive.ExtractTarGzFile(input, finalPath.Value, firstFolder, out var error);
            if (extracted is null)
            {
                throw new InvalidOperationException(error);
            }

            if (extracted.Count == 0)
            {
                throw new InvalidOperationException("No files were found in the supplied archive file.");
            }
        }
        else
        {
            try
            {
                using var zip = ZipFile.OpenRead(input);
                extracted = MmcZip.ExtractSubDir(zip, firstFolder, finalPath);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException
                                          or ArgumentException or NotSupportedException)
            {
                extracted = null;
            }

            if (extracted is null)
            {
                throw new InvalidOperationException("Unable to open supplied zip file.");
            }

            if (extracted.Count == 0)
            {
                throw new InvalidOperationException("No files were found in the supplied zip file.");
            }
        }
    }

    /// <summary>
    /// The first path component of the archive's first entry; null when the archive cannot be
    /// read, empty when it holds no entries.
    /// </summary>
    private static string? FirstTopLevelFolder(string input)
    {
        if (IsTarGz(input))
        {
            var names = TarArchive.ListTarGzFile(input, out _);
            return names is null ? null : names.Count == 0 ? "" : FirstComponent(names[0]);
        }

        try
        {
            using var zip = ZipFile.OpenRead(input);
            var entries = zip.Entries;
            if (entries.Count == 0)
            {
                return "";
            }

            return FirstComponent(entries[0].FullName.Replace('\\', '/'));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>QString::split('/', Qt::SkipEmptyParts).value(0).</summary>
    private static string FirstComponent(string name)
    {
        var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length != 0 ? parts[0] : name;
    }

    private static bool IsTarGz(string path)
    {
        Span<byte> magic = stackalloc byte[2];
        try
        {
            using var stream = File.OpenRead(path);
            return stream.Read(magic) == 2 && magic[0] == 0x1f && magic[1] == 0x8b;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string FileName(Uri url)
    {
        // QUrl::fileName() keeps the file name without the query part
        var path = url.AbsolutePath;
        var idx = path.LastIndexOf('/');
        var name = idx >= 0 ? path[(idx + 1)..] : path;
        return name.Length != 0 ? name : "java-archive";
    }

    private static HashAlgorithmName HashType(string checksumType) =>
        checksumType == "sha256" ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA1;
}
