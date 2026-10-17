// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/update/LibrariesTask.{h,cpp}: download the profile's libraries
// (and jar mods) through a NetJob built from Library.getDownloads.

using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Tasks;

namespace FlintLauncher.Core.Launch.Steps;

public sealed class LibrariesTask : FlintTask
{
    private readonly MinecraftInstance _inst;
    private NetJob? downloadJob;

    public LibrariesTask(MinecraftInstance inst)
    {
        _inst = inst;
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        EmitStatus("Downloading required library files...");
        var inst = _inst;

        // Build a list of URLs that will need to be downloaded.
        var components = inst.GetPackProfile();
        var profile = components.GetProfile();
        if (profile is null)
        {
            EmitFailed("The instance profile is missing.");
            return;
        }

        var job = new NetJob($"Libraries for instance {inst.Name()}");
        downloadJob = job;

        // E6: the stamped runtime cache is the only source — the bare-fixture throwaway is
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
        // (upstream ignores the processArtifactPool return value; the port stops before
        // starting a NetJob that would be useless after the task already failed)
        if (!ProcessArtifactPool(libArtifactPool, failedLocalLibraries, inst.GetLocalLibraryPath(), metacache, job))
        {
            downloadJob = null;
            return;
        }

        var failedLocalJarMods = new List<string>();
        if (!ProcessArtifactPool(profile.GetJarMods().Cast<Library?>(), failedLocalJarMods, inst.JarModsDir(), metacache, job))
        {
            downloadJob = null;
            return;
        }

        if (failedLocalJarMods.Count != 0 || failedLocalLibraries.Count != 0)
        {
            downloadJob = null;
            var failedAll = string.Join("\n", failedLocalLibraries.Concat(failedLocalJarMods));
            EmitFailed("Some artifacts marked as 'local' are missing their files:\n" + failedAll +
                       "\n\nYou need to either add the files, or removed the packages that require them." +
                       "\nYou'll have to correct this problem manually.");
            return;
        }

        await job.StartAsync(cancellationToken).ConfigureAwait(false);
        if (!job.WasSuccessful)
        {
            EmitFailed($"Game update failed: it was impossible to fetch the required libraries.\nReason:\n{job.FailReason}");
            return;
        }
        EmitSucceeded();
    }

    public bool CanAbort() => true;

    public override void Abort()
    {
        if (downloadJob is not null && downloadJob.IsRunning)
        {
            downloadJob.Abort();
        }

        // Prematurely aborted LibrariesTask
        base.Abort();
    }

    private bool ProcessArtifactPool(IEnumerable<Library?> pool, List<string> errors, string localPath,
                                     HttpMetaCache metacache, NetJob job)
    {
        foreach (var lib in pool)
        {
            if (lib is null)
            {
                EmitFailed("Null jar is specified in the metadata, aborting.");
                return false;
            }
            var dls = lib.GetDownloads(_inst.RuntimeContext, metacache, errors, localPath);
            foreach (var dl in dls)
            {
                job.AddNetRequest(dl);
            }
        }
        return true;
    }
}
