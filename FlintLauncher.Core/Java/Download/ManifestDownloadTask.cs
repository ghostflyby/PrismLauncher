// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/java/download/ManifestDownloadTask.{h,cpp}: install a JDK from a Mojang
// runtime manifest (a "files" object of directory/link/file entries; file entries carry a
// downloads.raw url + sha1 and an executable flag).
//
// Deviations from the C++ original:
//  - The QJsonDocument/QJsonObject dance becomes System.Text.Json via the Qt shims.
//  - QFile::link becomes File.CreateSymbolicLink (Unix symlinks; on Windows the link entry is
//    silently skipped like upstream's QFile::link returning false without surfacing an error).
//  - Relative manifest file URLs are rejected (upstream hands them to QUrl/QNetworkAccessManager
//    which would fail the request later); the meta server only issues absolute URLs.

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Json;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Tasks;

namespace FlintLauncher.Core.Java.Download;

public sealed class ManifestDownloadTask : FlintTask
{
    private readonly Uri _url;
    private readonly string _finalPath;
    private readonly string _checksumType;
    private readonly string _checksumHash;

    public ManifestDownloadTask(Uri url, string finalPath, string checksumType = "", string checksumHash = "")
    {
        _url = url;
        _finalPath = finalPath;
        _checksumType = checksumType;
        _checksumHash = checksumHash;
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        EmitStatus("Downloading Java");
        var download = NetRequest.MakeByteArray(_url);
        if (_checksumHash.Length != 0 && _checksumType.Length != 0)
        {
            download.AddValidator(new ChecksumValidator(HashType(_checksumType), Convert.FromHexString(_checksumHash)));
        }

        await download.StartAsync(cancellationToken).ConfigureAwait(false);
        if (download.State != TaskState.Succeeded)
        {
            EmitFailed(download.FailReason);
            return;
        }

        JsonObject doc;
        try
        {
            doc = JsonNode.Parse(download.BodyAsString) as JsonObject
                ?? throw new JsonParseException("Manifest root is not a JSON object");
        }
        catch (Exception e) when (e is JsonParseException or JsonException)
        {
            // qWarning() << "Error while parsing JSON response ..."
            EmitFailed(e.Message);
            return;
        }
        await DownloadJava(doc, cancellationToken).ConfigureAwait(false);
    }

    private async Task DownloadJava(JsonObject doc, CancellationToken cancellationToken)
    {
        // valid json doc, begin making jre spot
        FileSystem.EnsureFolderPathExists(_finalPath);
        var toDownload = new List<ManifestFile>();
        if (doc["files"] is JsonObject list)
        {
            foreach (var (paths, node) in list)
            {
                var file = FileSystem.PathCombine(_finalPath, paths);

                if (node is not JsonObject meta)
                {
                    continue; // toObject() on a non-object yields an empty object with no type
                }
                var type = meta.StringOr("type");
                if (type == "directory")
                {
                    FileSystem.EnsureFolderPathExists(file);
                }
                else if (type == "link")
                {
                    // this is *nix only !
                    var path = meta.StringOr("target");
                    if (path.Length != 0)
                    {
                        CreateLink(path, file);
                    }
                }
                else if (type == "file")
                {
                    // TODO download compressed version if it exists ?
                    var raw = (meta["downloads"] as JsonObject)?["raw"] as JsonObject;
                    var isExec = meta.BoolOr("executable");
                    var url = raw?.StringOr("url") ?? "";
                    if (url.Length != 0 && Uri.TryCreate(url, UriKind.Absolute, out var parsedUrl))
                    {
                        toDownload.Add(new ManifestFile
                        {
                            Path = file,
                            Url = parsedUrl,
                            Hash = raw?.StringOr("sha1") is { Length: > 0 } sha1 ? Convert.FromHexString(sha1) : [],
                            IsExec = isExec,
                        });
                    }
                }
            }
        }

        var elementDownload = new NetJob("JRE::FileDownload");
        foreach (var file in toDownload)
        {
            var dl = NetRequest.MakeFile(file.Url, file.Path);
            if (file.Hash.Length != 0)
            {
                dl.AddValidator(new ChecksumValidator(HashAlgorithmName.SHA1, file.Hash));
            }
            var target = file.Path;
            if (file.IsExec)
            {
                dl.Succeeded += _ => SetExecutable(target);
            }
            elementDownload.AddNetRequest(dl);
        }

        await elementDownload.StartAsync(cancellationToken).ConfigureAwait(false);
        if (elementDownload.State != TaskState.Succeeded)
        {
            EmitFailed(elementDownload.FailReason);
            return;
        }
        EmitSucceeded();
    }

    /// <summary>Port of the QFile(file.path).setPermissions(... | 0x1111) success handler.</summary>
    private static void SetExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return; // no unix permission bits to apply
        }
        try
        {
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path,
                mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // upstream ignores QFile::setPermissions failures the same way
        }
    }

    private void CreateLink(string target, string linkPath)
    {
        try
        {
            if (File.Exists(linkPath) || Directory.Exists(linkPath))
            {
                return;
            }
            FileSystem.EnsureFilePathExists(linkPath);
            if (!OperatingSystem.IsWindows())
            {
                File.CreateSymbolicLink(linkPath, target);
            }
            // on Windows QFile::link would create a shortcut; the manifest link type is
            // documented as *nix only, so nothing is written there.
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // QFile::link reports failure only through its return value, which upstream ignores
        }
    }

    private static HashAlgorithmName HashType(string checksumType) =>
        checksumType == "sha256" ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA1;

    private sealed class ManifestFile
    {
        public string Path { get; init; } = "";

        public Uri Url { get; init; } = new("about:blank");

        public byte[] Hash { get; init; } = [];

        public bool IsExec { get; init; }
    }
}
