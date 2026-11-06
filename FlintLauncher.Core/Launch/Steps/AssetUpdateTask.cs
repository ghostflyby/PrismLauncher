// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
//
// Port of launcher/minecraft/update/AssetUpdateTask.{h,cpp}: download the assets index for
// the profile's asset version, then fetch the asset objects into assets/objects/<first2>/<hash>.
//
// APPLICATION->metacache() reaches the task through the instance (MinecraftInstance.MetaCache,
// stamped by the composition root); the ResourceURLOverride setting arrives as the explicit
// resourceUrlOverride parameter (LaunchFactory.Options.ResourceUrlOverride, threaded through
// the update task closures).

using System.Security.Cryptography;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Tasks;

namespace FlintLauncher.Core.Launch.Steps;

public static class AssetUpdateTask
{
    /// <summary>Downloads the assets index and the objects it lists. Returning is success;
    /// any other exception is a failure whose message is the reason.</summary>
    public static async Task RunAsync(MinecraftInstance inst, string? resourceUrlOverride,
        CancellationToken cancellationToken)
    {
        var components = inst.GetPackProfile();
        var profile = components.GetProfile();
        if (profile is null)
        {
            throw new InvalidOperationException("The instance profile is missing.");
        }

        var assets = profile.GetMinecraftAssets();
        var indexUrl = assets.Url;
        var localPath = assets.Id + ".json";

        // the stamped runtime cache is the only source — the bare-fixture throwaway is
        // gone; an unstamped instance fails the step naming the missing wire
        var metacache = inst.MetaCache ?? throw new InvalidOperationException(
            $"no meta cache stamped on instance '{inst.Name()}' — the composition root must inject it");
        var entry = metacache.ResolveEntry("asset_indexes", localPath);
        entry.IsStale = true;

        // Net::ApiRequest::makeCached(indexUrl, entry) — no extra options upstream
        var dl = HttpDownloadTask.Cached(metacache, new Uri(indexUrl), entry);
        if (assets.Sha1.Length != 0)
        {
            dl.RequireChecksum(HashAlgorithmName.SHA1, assets.Sha1);
        }

        await dl.DownloadAsync(null, cancellationToken).ConfigureAwait(false);

        await AssetIndexFinished(inst, resourceUrlOverride, cancellationToken).ConfigureAwait(false);
    }

    private static async Task AssetIndexFinished(MinecraftInstance inst, string? resourceUrlOverride,
        CancellationToken cancellationToken)
    {
        var components = inst.GetPackProfile();
        var profile = components.GetProfile();
        if (profile is null)
        {
            throw new InvalidOperationException("The instance profile is missing.");
        }

        var assets = profile.GetMinecraftAssets();

        string a1 = assets.Id + ".json";
        var assetFname = inst.AssetIndexesDir / a1;
        var index = new AssetsIndex();
        // FIXME: this looks like a job for a generic validator based on json schema?
        if (!AssetsUtils.LoadAssetsIndexJson(assets.Id, assetFname, index))
        {
            var metacache = inst.MetaCache ?? throw new InvalidOperationException(
                $"no meta cache stamped on instance '{inst.Name()}' — the composition root must inject it");
            var entry = metacache.ResolveEntry("asset_indexes", assets.Id + ".json");
            metacache.EvictEntry(entry);
            throw new InvalidOperationException("Failed to read the assets index!");
        }

        // the object downloads ride the instance's cache plane — the client the index itself
        // came through
        var cache = inst.MetaCache ?? throw new InvalidOperationException(
            $"no meta cache stamped on instance '{inst.Name()}' — the composition root must inject it");
        var downloads = index.GetDownloadJob(ResourceUrl(resourceUrlOverride), inst.AssetsRoot, cache.Http);
        if (downloads is not null)
        {
            await TaskWork.RunAllAsync(downloads,
                    (download, token) => download.DownloadAsync(null, token), maxConcurrent: 6,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Port of resourceUrl().</summary>
    private static string ResourceUrl(string? urlOverride)
    {
        if (!string.IsNullOrEmpty(urlOverride))
        {
            return urlOverride;
        }

        return FlintConfig.Default.DefaultResourceBase;
    }
}
