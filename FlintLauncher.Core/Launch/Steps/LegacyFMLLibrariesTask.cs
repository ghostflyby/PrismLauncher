// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/update/LegacyFMLLibrariesTask.{h,cpp}: download the extra FML
// libraries old Forge versions need and copy them into the instance's lib folder.
//
// Application-singleton reach-ins resolve through the instance (see Launch/LaunchController.cs
// for the same pattern): the download base is the build default (the LegacyFMLLibsURLOverride
// setting had no wired consumer and retired with the settings world) and the metacache comes
// from the instance ("fmllibs" base — registered once at the composition root, E6; an
// unstamped instance fails the step instead of silently re-downloading).

using FlintLauncher.Core.Instances;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Tasks;
using TruePath;

namespace FlintLauncher.Core.Launch.Steps;

public sealed class LegacyFmlLibrariesTask(MinecraftInstance inst) : FlintTask
{
    private readonly List<FmlLib> _fmlLibsToProcess = [];
    private NetJob? _downloadJob;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        // Get the mod list
        var inst1 = inst;
        var components = inst1.GetPackProfile();
        var profile = components.GetProfile();
        if (profile is null)
        {
            EmitFailed("The instance profile is missing.");
            return;
        }

        if (!profile.HasTrait("legacyFML"))
        {
            EmitSucceeded();
            return;
        }

        var version = components.GetComponentVersion("net.minecraft");
        var fmlLibsMapping = VersionFilterData.Instance.FmlLibsMapping;
        if (!fmlLibsMapping.TryGetValue(version, out var libList))
        {
            EmitSucceeded();
            return;
        }

        // determine if we need some libs for FML or forge
        EmitStatus("Checking for FML libraries...");
        if (components.GetComponent("net.minecraftforge") is null)
        {
            EmitSucceeded();
            return;
        }

        // now check the lib folder inside the instance for files.
        foreach (var lib in libList)
        {
            var libInfo = new FileInfo(Path.Combine(inst1.LibDir(), lib.Filename));
            if (libInfo.Exists)
            {
                continue;
            }

            _fmlLibsToProcess.Add(lib);
        }

        // if everything is in place, there's nothing to do here...
        if (_fmlLibsToProcess.Count == 0)
        {
            EmitSucceeded();
            return;
        }

        // download missing libs to our place
        EmitStatus("Downloading FML libraries...");
        // E6: the stamped runtime cache is the only source — the bare-fixture throwaway is
        // gone; an unstamped instance fails the step naming the missing wire
        var metacache = inst.MetaCache ?? throw new InvalidOperationException(
            $"no meta cache stamped on instance '{inst.Name()}' — the composition root must inject it");
        var dlJob = new NetJob("FML libraries");
        _downloadJob = dlJob;
        foreach (var lib in _fmlLibsToProcess)
        {
            var entry = metacache.ResolveEntry("fmllibs", lib.Filename);
            entry.IsStale = true;
            var urlString = BaseUrl() + lib.Filename;
            dlJob.AddNetRequest(NetRequest.MakeCached(metacache, new Uri(urlString), entry, RequestOptions.MakeEternal));
        }

        await dlJob.StartAsync(cancellationToken).ConfigureAwait(false);
        if (!dlJob.WasSuccessful)
        {
            FmllibsFailed(dlJob.FailReason);
            return;
        }

        FmllibsFinished();
    }

    /// <summary>Port of baseUrl(): the build default base (the override setting retired with
    /// the settings world — nothing wired it through the launch services).</summary>
    private string BaseUrl() => FlintConfig.Default.LegacyFmllibsBaseUrl;

    public bool CanAbort() => true;

    private void FmllibsFinished()
    {
        _downloadJob = null;
        if (_fmlLibsToProcess.Count != 0)
        {
            EmitStatus("Copying FML libraries into the instance...");
            var inst1 = inst;
            var metacache = inst.MetaCache ?? throw new InvalidOperationException(
                $"no meta cache stamped on instance '{inst.Name()}' — the composition root must inject it");
            var index = 0;
            foreach (var lib in _fmlLibsToProcess)
            {
                SetProgress(index, _fmlLibsToProcess.Count);
                var entry = metacache.ResolveEntry("fmllibs", lib.Filename);
                var path = new AbsolutePath(inst1.LibDir()) / lib.Filename;
                if (!FileOps.EnsureFilePathExists(path))
                {
                    EmitFailed("Failed creating FML library folder inside the instance.");
                    return;
                }

                try
                {
                    File.Copy(entry.GetFullPath(), Path.Combine(inst1.LibDir(), lib.Filename), overwrite: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    EmitFailed($"Failed copying Forge/FML library: {lib.Filename}.");
                    return;
                }

                index++;
            }

            SetProgress(index, _fmlLibsToProcess.Count);
        }

        EmitSucceeded();
    }

    private void FmllibsFailed(string reason)
    {
        // (the C++ lists the failed files from the job; the NetJob carries the reason instead)
        EmitFailed($"Failed to download the following files:\n\nReason:{reason}\nPlease try again.");
    }

    public override void Abort()
    {
        if (_downloadJob is not null && _downloadJob.IsRunning)
        {
            _downloadJob.Abort();
        }

        // Prematurely aborted LegacyFMLLibrariesTask
        base.Abort();
    }
}
