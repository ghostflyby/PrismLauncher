// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
//
// Port of launcher/minecraft/update/LegacyFMLLibrariesTask.{h,cpp}: download the extra FML
// libraries old Forge versions need and copy them into the instance's lib folder.
//
// Application-singleton reach-ins resolve through the instance (see Launch/LaunchController.cs
// for the same pattern): the download base is the build default (the LegacyFMLLibsURLOverride
// setting had no wired consumer and retired with the settings world) and the metacache comes
// from the instance ("fmllibs" base — registered once at the composition root; an
// unstamped instance fails the step instead of silently re-downloading).

using FlintLauncher.Core.Instances;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Tasks;
using TruePath;
using TruePath.SystemIo;

namespace FlintLauncher.Core.Launch.Steps;

public static class LegacyFmlLibrariesTask
{
    /// <summary>Downloads the extra FML libraries and copies them into the instance's lib
    /// folder. Returning is success; any other exception is a failure whose message is the
    /// reason. <paramref name="progress"/> null runs without reporting.</summary>
    public static async Task RunAsync(MinecraftInstance inst, IProgress<TaskProgress>? progress,
        CancellationToken cancellationToken)
    {
        var components = inst.GetPackProfile();
        var profile = components.GetProfile();
        if (profile is null)
        {
            throw new InvalidOperationException("The instance profile is missing.");
        }

        if (!profile.HasTrait("legacyFML"))
        {
            return;
        }

        var version = components.GetComponentVersion("net.minecraft");
        var fmlLibsMapping = VersionFilterData.Instance.FmlLibsMapping;
        if (!fmlLibsMapping.TryGetValue(version, out var libList))
        {
            return;
        }

        // determine if we need some libs for FML or forge
        if (components.GetComponent("net.minecraftforge") is null)
        {
            return;
        }

        // now check the lib folder inside the instance for files.
        var fmlLibsToProcess = new List<FmlLib>();
        foreach (var lib in libList)
        {
            var libInfo = new FileInfo((inst.LibDir() / lib.Filename).Value);
            if (libInfo.Exists)
            {
                continue;
            }

            fmlLibsToProcess.Add(lib);
        }

        // if everything is in place, there's nothing to do here...
        if (fmlLibsToProcess.Count == 0)
        {
            return;
        }

        // download missing libs to our place
        // the stamped runtime cache is the only source — the bare-fixture throwaway is
        // gone; an unstamped instance fails the step naming the missing wire
        var metacache = inst.MetaCache ?? throw new InvalidOperationException(
            $"no meta cache stamped on instance '{inst.Name()}' — the composition root must inject it");
        var downloads = new List<HttpDownloadTask>();
        foreach (var lib in fmlLibsToProcess)
        {
            var entry = metacache.ResolveEntry("fmllibs", lib.Filename);
            entry.IsStale = true;
            // baseUrl(): the build default base (the override setting retired with the
            // settings world — nothing wired it through the launch services)
            var urlString = FlintConfig.Default.LegacyFmllibsBaseUrl + lib.Filename;
            downloads.Add(HttpDownloadTask.Cached(metacache, new Uri(urlString), entry, RequestOptions.MakeEternal));
        }

        try
        {
            await TaskWork.RunAllAsync(downloads,
                    (download, token) => download.DownloadAsync(progress, token), maxConcurrent: 6,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // (the C++ lists the failed files from the job; the batch carries the reason instead)
            throw new InvalidOperationException(
                $"Failed to download the following files:\n\nReason:{TaskWork.ReasonOf(e)}\nPlease try again.");
        }

        // Port of fmllibsFinished(): copy the downloaded libs into the instance's lib folder.
        var index = 0;
        foreach (var lib in fmlLibsToProcess)
        {
            progress?.Report(new TaskProgress(index, fmlLibsToProcess.Count));
            var entry = metacache.ResolveEntry("fmllibs", lib.Filename);
            var path = new AbsolutePath(inst.LibDir()) / lib.Filename;
            if (!FileOps.EnsureFilePathExists(path))
            {
                throw new InvalidOperationException("Failed creating FML library folder inside the instance.");
            }

            try
            {
                entry.GetFullPath().Copy(inst.LibDir() / lib.Filename, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"Failed copying Forge/FML library: {lib.Filename}.");
            }

            index++;
        }

        progress?.Report(new TaskProgress(index, fmlLibsToProcess.Count));
    }
}
