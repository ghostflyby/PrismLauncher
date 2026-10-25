// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2023-2024 Trial97 <alexandru.tripon97@gmail.com>
//
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
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Tasks;
using TruePath;

namespace FlintLauncher.Core.Java.Download;

/// <summary>The Mojang java runtime manifest: a "files" object of directory/link/file
/// entries keyed by their relative path under the install root.</summary>
public sealed record JavaRuntimeManifestJson(
    [property: JsonPropertyName("files")] Dictionary<string, JavaRuntimeFileJson>? Files);

public sealed record JavaRuntimeFileJson(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("target")] string? Target,
    [property: JsonPropertyName("executable")]
    bool? Executable,
    [property: JsonPropertyName("downloads")]
    JavaRuntimeDownloadsJson? Downloads);

public sealed record JavaRuntimeDownloadsJson(
    [property: JsonPropertyName("raw")] JavaRuntimeRawJson? Raw);

public sealed record JavaRuntimeRawJson(
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("sha1")] string? Sha1);

[JsonSerializable(typeof(JavaRuntimeManifestJson))]
internal sealed partial class JavaManifestJsonContext : JsonSerializerContext;

public sealed class ManifestDownloadTask(Uri url, AbsolutePath finalPath, HttpClient http, NetSettings settings, string checksumType = "", string checksumHash = "")
    : FlintTask
{
    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        EmitStatus("Downloading Java");
        byte[] manifestBytes;
        try
        {
            manifestBytes = await FlintHttp.SendAsync(http, settings,
                new HttpRequestMessage(HttpMethod.Get, url), cancellationToken).ConfigureAwait(false);
        }
        catch (FlintHttpException e)
        {
            EmitFailed(e.Reason);
            return;
        }

        if (checksumHash.Length != 0 && checksumType.Length != 0)
        {
            var validator = new ChecksumValidator(HashType(checksumType), Convert.FromHexString(checksumHash));
            var spec = new NetRequestSpec();
            if (!validator.Init(spec) || !validator.Write(manifestBytes) ||
                !validator.Validate(new NetResponseInfo { StatusCode = 200 }))
            {
                EmitFailed($"checksum verification failed for {url}");
                return;
            }
        }

        JavaRuntimeManifestJson? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(Encoding.UTF8.GetString(manifestBytes),
                JavaManifestJsonContext.Default.JavaRuntimeManifestJson);
        }
        catch (Exception e) when (e is JsonException)
        {
            // qWarning() << "Error while parsing JSON response ..."
            EmitFailed(e.Message);
            return;
        }

        if (manifest is null)
        {
            EmitFailed("Manifest root is not a JSON object");
            return;
        }

        await DownloadJava(manifest, cancellationToken).ConfigureAwait(false);
    }

    private async Task DownloadJava(JavaRuntimeManifestJson manifest, CancellationToken cancellationToken)
    {
        // valid json doc, begin making jre spot
        FileOps.EnsureFolderPathExists(new AbsolutePath(finalPath));
        var toDownload = new List<ManifestFile>();
        if (manifest.Files is { } list)
        {
            foreach (var (paths, meta) in list)
            {
                if (meta is null)
                {
                    continue; // toObject() on a non-object yields an empty object with no type
                }

                var file = (AbsolutePath)finalPath / paths;

                var type = meta.Type ?? "";
                if (type == "directory")
                {
                    FileOps.EnsureFolderPathExists(new AbsolutePath(file));
                }
                else if (type == "link")
                {
                    // this is *nix only !
                    var path = meta.Target ?? "";
                    if (path.Length != 0)
                    {
                        CreateLink(path, file.Value);
                    }
                }
                else if (type == "file")
                {
                    // TODO download compressed version if it exists ?
                    var raw = meta.Downloads?.Raw;
                    var isExec = meta.Executable ?? false;
                    var rawUrl = raw?.Url ?? "";
                    if (rawUrl.Length != 0 && Uri.TryCreate(rawUrl, UriKind.Absolute, out var parsedUrl))
                    {
                        toDownload.Add(new ManifestFile
                        {
                            Path = file,
                            Url = parsedUrl,
                            Hash = raw?.Sha1 is { Length: > 0 } sha1 ? Convert.FromHexString(sha1) : [],
                            IsExec = isExec,
                        });
                    }
                }
            }
        }

        var elementDownload = new NetJob("JRE::FileDownload");
        foreach (var file in toDownload)
        {
            var dl = NetRequest.MakeFile(file.Url, file.Path, http, settings);
            if (file.Hash.Length != 0)
            {
                dl.AddValidator(new ChecksumValidator(HashAlgorithmName.SHA1, file.Hash));
            }

            var target = file.Path;
            if (file.IsExec)
            {
                dl.Succeeded += _ => SetExecutable(target.Value);
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

            FileOps.EnsureFilePathExists(new AbsolutePath(linkPath));
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
        public AbsolutePath Path { get; init; }

        public Uri Url { get; init; } = new("about:blank");

        public byte[] Hash { get; init; } = [];

        public bool IsExec { get; init; }
    }
}
