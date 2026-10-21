// SPDX-License-Identifier: GPL-3.0-only
// Port of modplatform/technic/SingleZipPackInstallTask.{h,cpp}: download a Technic zip
// modpack, extract it into the staging folder's minecraft/ directory and run the
// TechnicPackProcessor on it.
//
// Deviations (headless port):
//  - the download goes through the meta cache when MetaCache is set ("general" base, like
//    the C++), otherwise a plain file download into the staging folder
//    (InstanceImportTask.DownloadFromUrl's fallback).
//  - the "fixing permissions" walk after extraction is not ported (no unix permission
//    model on the .NET zip layer; MMCZip writes plain files).

using FlintLauncher.Core.Instances;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Net;
using TruePath;

namespace FlintLauncher.Core.ModPlatform.Technic;

public sealed class SingleZipPackInstallTask(Uri sourceUrl, string minecraftVersion) : InstanceTask
{
    private string _archivePath = "";

    /// <summary>APPLICATION->metacache() hook ("general" base); null = plain download.</summary>
    public HttpMetaCache? MetaCache { get; set; }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        EmitStatus("Downloading modpack:\n" + sourceUrl);

        if (MetaCache is not null)
        {
            var path = sourceUrl.Host + '/' + sourceUrl.AbsolutePath.TrimStart('/');
            var entry = MetaCache.ResolveEntry("general", path);
            entry.IsStale = true;

            var filesNetJob = new NetJob("Modpack download");
            var request = NetRequest.MakeCached(MetaCache, sourceUrl, entry);
            filesNetJob.AddNetRequest(request);
            _archivePath = entry.GetFullPath();
            await filesNetJob.StartAsync(cancellationToken);
            if (!filesNetJob.WasSuccessful)
            {
                EmitFailed(filesNetJob.FailReason);
                return;
            }
        }
        else
        {
            // (the C++ downloads through the global cache — outside the instance folder; the
            //  headless fallback mirrors that with a temporary file)
            _archivePath = Path.Combine(Path.GetTempPath(), "flint-technic-" + Guid.NewGuid().ToString("N") +
                                                            Path.GetFileName(sourceUrl.AbsolutePath));
            FileOps.EnsureFilePathExists(new AbsolutePath(_archivePath));
            var request = NetRequest.MakeFile(sourceUrl, _archivePath);
            await request.StartAsync(cancellationToken);
            if (!request.WasSuccessful)
            {
                EmitFailed(request.FailReason);
                return;
            }
        }

        await DownloadSucceeded(cancellationToken);
    }

    private async Task DownloadSucceeded(CancellationToken cancellationToken)
    {
        EmitStatus("Extracting modpack");
        var extractDir = Path.Combine(StagingPath, "minecraft");
        FileOps.EnsureFolderPathExists(new AbsolutePath(extractDir));

        var extracted = MmcZip.ExtractDir(_archivePath, "", extractDir);
        if (extracted is null)
        {
            EmitFailed("Failed to extract modpack");
            return;
        }

        // (the "fixing permissions" walk is not ported — see the header note)

        var failure = await TechnicPackProcessor.RunAsync(
            Environment ?? throw new InvalidOperationException(
                "InstanceTask requires SetParentEnvironment before execution."),
            Name(), Icon, StagingPath, minecraftVersion, cancellationToken: cancellationToken);
        if (failure is not null)
        {
            EmitFailed(failure);
            return;
        }

        EmitSucceeded();
    }
}
