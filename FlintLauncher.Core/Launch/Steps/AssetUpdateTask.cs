// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/update/AssetUpdateTask.{h,cpp}: download the assets index for
// the profile's asset version, then fetch the asset objects into assets/objects/<first2>/<hash>.
//
// Application-singleton reach-ins become hooks: the metacache (APPLICATION->metacache()) and
// the ResourceURLOverride setting (APPLICATION->settings()) — see LegacyFMLLibrariesTask.
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Net;

using FlintLauncher.Core.IO;
using FlintLauncher.Core.Tasks;

namespace FlintLauncher.Core.Launch.Steps;

public sealed class AssetUpdateTask : FlintTask
{
    /// <summary>C++ APPLICATION->metacache(); a missing hook creates a throwaway cache rooted
    /// at the cwd (the "asset_indexes" base then resolves next to the working directory).</summary>
    public static Func<HttpMetaCache?>? MetaCacheHook { get; set; }

    /// <summary>C++ APPLICATION->settings()->get("ResourceURLOverride").</summary>
    public static Func<string>? ResourceUrlOverride { get; set; }

    private readonly MinecraftInstance _inst;
    private NetJob? _downloadJob;

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

        var metacache = RequireCache();
        var entry = metacache.ResolveEntry("asset_indexes", localPath);
        entry.SetStale(true);
        var job = new NetJob($"Asset index for {_inst.Name()}");

        // Net::ApiRequest::makeCached(indexUrl, entry) — no extra options upstream
        var dl = NetRequest.MakeCached(metacache, new Uri(indexUrl), entry);
        if (assets.Sha1.Length != 0)
        {
            dl.AddValidator(ChecksumValidator.FromHex(System.Security.Cryptography.HashAlgorithmName.SHA1, assets.Sha1));
        }
        job.AddNetRequest(dl);

        _downloadJob = job;
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

        var assetFname = FileSystem.PathCombine("assets", "indexes", assets.Id + ".json");
        var index = new AssetsIndex();
        // FIXME: this looks like a job for a generic validator based on json schema?
        if (!AssetsUtils.LoadAssetsIndexJson(assets.Id, assetFname, index))
        {
            var metacache = RequireCache();
            var entry = metacache.ResolveEntry("asset_indexes", assets.Id + ".json");
            metacache.EvictEntry(entry);
            EmitFailed("Failed to read the assets index!");
            return;
        }

        var job = index.GetDownloadJob(ResourceUrl());
        if (job is not null)
        {
            var resourceUrl = ResourceUrl();
            var source = "Mojang";
            if (resourceUrl != FlintConfig.Default.DefaultResourceBase)
            {
                source = new Uri(resourceUrl).Host;
            }
            EmitStatus($"Getting the asset files from {source}...");
            _downloadJob = job;
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
        var urlOverride = ResourceUrlOverride?.Invoke();
        if (!string.IsNullOrEmpty(urlOverride))
        {
            return urlOverride;
        }
        return FlintConfig.Default.DefaultResourceBase;
    }

    public bool CanAbort() => true;

    public override void Abort()
    {
        if (_downloadJob is not null && _downloadJob.IsRunning)
        {
            _downloadJob.Abort();
        }
        else
        {
            // Prematurely aborted AssetUpdateTask
        }
        base.Abort();
    }

    private HttpMetaCache RequireCache()
    {
        if (MetaCacheHook?.Invoke() is { } shared)
        {
            return shared;
        }
        var cache = new HttpMetaCache();
        cache.AddBase("asset_indexes", FileSystem.PathCombine(Directory.GetCurrentDirectory(), "assets", "indexes"));
        return cache;
    }
}
