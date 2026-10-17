// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/update/AssetUpdateTask.{h,cpp}: download the assets index for
// the profile's asset version, then fetch the asset objects into assets/objects/<first2>/<hash>.
//
// APPLICATION->metacache() reaches the task through the instance (MinecraftInstance.MetaCache,
// stamped by the composition root); the ResourceURLOverride setting comes from the
// instance-stamped LaunchServices (Services.ResourceUrlOverride).

using System.Security.Cryptography;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Tasks;

namespace FlintLauncher.Core.Launch.Steps;

public sealed class AssetUpdateTask : FlintTask
{
    private readonly MinecraftInstance _inst;
    private NetJob? downloadJob;

    public AssetUpdateTask(MinecraftInstance inst)
    {
        _inst = inst;
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        EmitStatus("Updating assets index...");
        var components = _inst.GetPackProfile();
        var profile = components.GetProfile();
        if (profile is null)
        {
            EmitFailed("The instance profile is missing.");
            return;
        }

        var assets = profile.GetMinecraftAssets();
        var indexUrl = assets.Url;
        var localPath = assets.Id + ".json";

        // E6: the stamped runtime cache is the only source — the bare-fixture throwaway is
        // gone; an unstamped instance fails the step naming the missing wire
        var metacache = _inst.MetaCache ?? throw new InvalidOperationException(
            $"no meta cache stamped on instance '{_inst.Name()}' — the composition root must inject it");
        var entry = metacache.ResolveEntry("asset_indexes", localPath);
        entry.SetStale(true);
        var job = new NetJob($"Asset index for {_inst.Name()}");

        // Net::ApiRequest::makeCached(indexUrl, entry) — no extra options upstream
        var dl = NetRequest.MakeCached(metacache, new Uri(indexUrl), entry);
        if (assets.Sha1.Length != 0)
        {
            dl.AddValidator(ChecksumValidator.FromHex(HashAlgorithmName.SHA1, assets.Sha1));
        }

        job.AddNetRequest(dl);

        downloadJob = job;
        await job.StartAsync(cancellationToken).ConfigureAwait(false);
        if (!job.WasSuccessful)
        {
            EmitFailed($"Failed to download the assets index:\n{job.FailReason}");
            return;
        }

        await AssetIndexFinished(cancellationToken).ConfigureAwait(false);
    }

    private async Task AssetIndexFinished(CancellationToken cancellationToken)
    {
        var components = _inst.GetPackProfile();
        var profile = components.GetProfile();
        if (profile is null)
        {
            EmitFailed("The instance profile is missing.");
            return;
        }

        var assets = profile.GetMinecraftAssets();

        var assetFname = FileSystem.PathCombine(
            _inst.Paths?.AssetIndexesDir ?? FileSystem.PathCombine("assets", "indexes"),
            assets.Id + ".json");
        var index = new AssetsIndex();
        // FIXME: this looks like a job for a generic validator based on json schema?
        if (!AssetsUtils.LoadAssetsIndexJson(assets.Id, assetFname, index))
        {
            var metacache = _inst.MetaCache ?? throw new InvalidOperationException(
                $"no meta cache stamped on instance '{_inst.Name()}' — the composition root must inject it");
            var entry = metacache.ResolveEntry("asset_indexes", assets.Id + ".json");
            metacache.EvictEntry(entry);
            EmitFailed("Failed to read the assets index!");
            return;
        }

        var job = index.GetDownloadJob(ResourceUrl(), _inst.Paths?.AssetsRoot
                                                      ?? FileSystem.PathCombine("assets", ""));
        if (job is not null)
        {
            var resourceUrl = ResourceUrl();
            var source = "Mojang";
            if (resourceUrl != FlintConfig.Default.DefaultResourceBase)
            {
                source = new Uri(resourceUrl).Host;
            }

            EmitStatus($"Getting the asset files from {source}...");
            downloadJob = job;
            await job.StartAsync(cancellationToken).ConfigureAwait(false);
            if (!job.WasSuccessful)
            {
                EmitFailed($"Failed to download assets:\n{job.FailReason}");
                return;
            }
        }

        EmitSucceeded();
    }

    /// <summary>Port of resourceUrl().</summary>
    private string ResourceUrl()
    {
        var urlOverride = _inst.LaunchServices?.ResourceUrlOverride;
        if (!string.IsNullOrEmpty(urlOverride))
        {
            return urlOverride;
        }

        return FlintConfig.Default.DefaultResourceBase;
    }

    public bool CanAbort() => true;

    public override void Abort()
    {
        if (downloadJob is not null && downloadJob.IsRunning)
        {
            downloadJob.Abort();
        }

        // Prematurely aborted AssetUpdateTask
        base.Abort();
    }
}
