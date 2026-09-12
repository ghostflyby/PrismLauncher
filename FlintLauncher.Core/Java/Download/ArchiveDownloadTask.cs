// SPDX-License-Identifier: GPL-3.0-only
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
//    zip through MMCZip and tar.gz/plain tar through the hand-rolled ustar reader in
//    TarArchive.cs (no new packages), selected by gzip magic bytes like libarchive does.
//  - Unix permission bits and symlinks stored in zip entries are not restored (the MMCZip
//    port's known limitation; the tar.gz path preserves the execute bits and symlinks).
using System.IO.Compression;
using System.Security.Cryptography;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Tasks;

namespace FlintLauncher.Core.Java;

public sealed class ArchiveDownloadTask : FlintTask
{
    private readonly Uri _url;
    private readonly string _finalPath;
    private readonly string _checksumType;
    private readonly string _checksumHash;
    private readonly HttpMetaCache? _cache;

    public ArchiveDownloadTask(Uri url, string finalPath, string checksumType = "", string checksumHash = "",
                               HttpMetaCache? cache = null)
    {
        _url = url;
        _finalPath = finalPath;
        _checksumType = checksumType;
        _checksumHash = checksumHash;
        _cache = cache;
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        // JRE found ! download the zip
        EmitStatus("Downloading Java");

        string fullPath;
        NetRequest action;
        var tempDownload = false;
        if (_cache is not null)
        {
            var entry = _cache.ResolveEntry("java", FileName());
            action = NetRequest.MakeCached(_cache, _url, entry);
            fullPath = entry.GetFullPath();
        }
        else
        {
            fullPath = Path.Combine(Path.GetTempPath(), $"flint-java-{Guid.NewGuid():N}-{FileName()}");
            action = NetRequest.MakeFile(_url, fullPath);
            tempDownload = true;
        }
        if (_checksumHash.Length != 0 && _checksumType.Length != 0)
        {
            action.AddValidator(new ChecksumValidator(HashType(_checksumType), Convert.FromHexString(_checksumHash)));
        }

        try
        {
            await action.StartAsync(cancellationToken).ConfigureAwait(false);
            if (action.State != TaskState.Succeeded)
            {
                EmitFailed(action.FailReason);
                return;
            }

            ExtractJava(fullPath);
        }
        finally
        {
            if (tempDownload && File.Exists(fullPath))
            {
                try
                {
                    File.Delete(fullPath);
                }
                catch (IOException)
                {
                    // best effort cleanup of the scratch download
                }
            }
        }
    }

    private void ExtractJava(string input)
    {
        EmitStatus("Extracting Java");

        // determine which top-level folder to strip (C++: files[0].split('/').value(0));
        // null = unreadable archive, "" = readable but empty
        var firstFolder = FirstTopLevelFolder(input);
        if (firstFolder is null)
        {
            EmitFailed(IsTarGz(input)
                ? "Unable to open supplied archive file."
                : "Unable to open supplied zip file.");
            return;
        }
        if (firstFolder.Length == 0)
        {
            EmitFailed(IsTarGz(input)
                ? "No files were found in the supplied archive file."
                : "No files were found in the supplied zip file.");
            return;
        }

        EmitDetails("Extracting files...");
        List<string>? extracted;
        if (IsTarGz(input))
        {
            extracted = TarArchive.ExtractTarGzFile(input, _finalPath, firstFolder, out var error);
            if (extracted is null)
            {
                EmitFailed(error);
                return;
            }
            if (extracted.Count == 0)
            {
                EmitFailed("No files were found in the supplied archive file.");
                return;
            }
        }
        else
        {
            try
            {
                using var zip = ZipFile.OpenRead(input);
                extracted = MMCZip.ExtractSubDir(zip, firstFolder, _finalPath);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException
                                     or ArgumentException or NotSupportedException)
            {
                extracted = null;
            }
            if (extracted is null)
            {
                EmitFailed("Unable to open supplied zip file.");
                return;
            }
            if (extracted.Count == 0)
            {
                EmitFailed("No files were found in the supplied zip file.");
                return;
            }
        }

        // This should do all of the extracting and creating folders
        EmitSucceeded();
    }

    /// <summary>
    /// The first path component of the archive's first entry; null when the archive cannot be
    /// read, empty when it holds no entries.
    /// </summary>
    private string? FirstTopLevelFolder(string input)
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

    private string FileName()
    {
        // QUrl::fileName() keeps the file name without the query part
        var path = _url.AbsolutePath;
        var idx = path.LastIndexOf('/');
        var name = idx >= 0 ? path[(idx + 1)..] : path;
        return name.Length != 0 ? name : "java-archive";
    }

    private static HashAlgorithmName HashType(string checksumType) =>
        checksumType == "sha256" ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA1;
}
