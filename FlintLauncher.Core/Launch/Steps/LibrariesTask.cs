// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
//
// Port of launcher/minecraft/update/LibrariesTask.{h,cpp}: download the profile's libraries
// (and jar mods) through TaskWork.RunAllAsync over the downloads from Library.getDownloads.

using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Tasks;

namespace FlintLauncher.Core.Launch.Steps;

public static class LibrariesTask
{
    /// <summary>Downloads the profile's libraries and jar mods. Returning is success; any
    /// other exception is a failure whose message is the reason.</summary>
    public static async Task RunAsync(MinecraftInstance inst, CancellationToken cancellationToken)
    {
        // Build a list of URLs that will need to be downloaded.
        var components = inst.GetPackProfile();
        var profile = components.GetProfile();
        if (profile is null)
        {
            throw new InvalidOperationException("The instance profile is missing.");
        }

        // the stamped runtime cache is the only source — the bare-fixture throwaway is
        // gone; an unstamped instance fails the step naming the missing wire
        var metacache = inst.MetaCache ?? throw new InvalidOperationException(
            $"no meta cache stamped on instance '{inst.Name()}' — the composition root must inject it");

        var failedLocalLibraries = new List<string>();
        var libArtifactPool = new List<Library?>();
        libArtifactPool.AddRange(profile.GetLibraries());
        libArtifactPool.AddRange(profile.GetNativeLibraries());
        libArtifactPool.AddRange(profile.GetMavenFiles());
        foreach (var agent in profile.GetAgents())
        {
            libArtifactPool.Add(agent.Library);
        }

        libArtifactPool.Add(profile.GetMainJar());
        var downloads = new List<HttpDownloadTask>();
        ProcessArtifactPool(inst, libArtifactPool, failedLocalLibraries, inst.GetLocalLibraryPath().Value,
            metacache, downloads);

        var failedLocalJarMods = new List<string>();
        ProcessArtifactPool(inst, profile.GetJarMods().Cast<Library?>(), failedLocalJarMods, inst.JarModsDir.Value,
            metacache, downloads);

        if (failedLocalJarMods.Count != 0 || failedLocalLibraries.Count != 0)
        {
            var failedAll = string.Join("\n", failedLocalLibraries.Concat(failedLocalJarMods));
            throw new InvalidOperationException(
                "Some artifacts marked as 'local' are missing their files:\n" + failedAll +
                "\n\nYou need to either add the files, or removed the packages that require them." +
                "\nYou'll have to correct this problem manually.");
        }

        try
        {
            await TaskWork.RunAllAsync(downloads,
                    (dl, token) => dl.DownloadAsync(null, token), maxConcurrent: 6, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Game update failed: it was impossible to fetch the required libraries.\nReason:\n{TaskWork.ReasonOf(e)}");
        }
    }


    private static void ProcessArtifactPool(MinecraftInstance inst, IEnumerable<Library?> pool,
        List<string> errors, string localPath, HttpMetaCache metacache, List<HttpDownloadTask> downloads)
    {
        foreach (var lib in pool)
        {
            if (lib is null)
            {
                throw new InvalidOperationException("Null jar is specified in the metadata, aborting.");
            }

            downloads.AddRange(lib.GetDownloads(inst.RuntimeContext, metacache, errors, localPath));
        }
    }
}
