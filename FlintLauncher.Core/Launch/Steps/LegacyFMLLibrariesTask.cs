// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/update/LegacyFMLLibrariesTask.{h,cpp}: download the extra FML
// libraries old Forge versions need and copy them into the instance's lib folder.
//
// Application-singleton reach-ins resolve through the instance (see Launch/LaunchController.cs
// for the same pattern): the global settings override (LegacyFMLLibsURLOverride) comes from
// the instance-stamped LaunchServices (Services.LegacyFmllibsUrlOverride) and the metacache
// from the instance; when the cache is unset a throwaway cache is used so instances with the
// libraries already in place behave identically.
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Net;

using FlintLauncher.Core.IO;
using FlintLauncher.Core.Tasks;
using FlintLauncher.Core.Minecraft;

namespace FlintLauncher.Core.Launch.Steps;

public sealed class LegacyFMLLibrariesTask : FlintTask
{
    private readonly MinecraftInstance _inst;
    private readonly List<FmlLib> _fmlLibsToProcess = [];
    private NetJob? _downloadJob;

    public LegacyFMLLibrariesTask(MinecraftInstance inst)
    {
        _inst = inst;
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        // Get the mod list
        var inst = _inst;
        var components = inst.GetPackProfile();
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
            var libInfo = new FileInfo(FileSystem.PathCombine(inst.LibDir(), lib.Filename));
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
        var metacache = RequireCache();
        var dlJob = new NetJob("FML libraries");
        _downloadJob = dlJob;
        foreach (var lib in _fmlLibsToProcess)
        {
            var entry = metacache.ResolveEntry("fmllibs", lib.Filename);
            entry.SetStale(true);
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

    /// <summary>Port of baseUrl(): the LegacyFMLLibsURLOverride setting (the instance-stamped
    /// LaunchServices override) or the build default.</summary>
    private string BaseUrl() => _inst.LaunchServices?.LegacyFmllibsUrlOverride is { Length: > 0 } urlOverride
        ? urlOverride
        : FlintConfig.Default.LegacyFmllibsBaseUrl;

    public bool CanAbort() => true;

    private void FmllibsFinished()
    {
        _downloadJob = null;
        if (_fmlLibsToProcess.Count != 0)
        {
            EmitStatus("Copying FML libraries into the instance...");
            var inst = _inst;
            var metacache = RequireCache();
            var index = 0;
            foreach (var lib in _fmlLibsToProcess)
            {
                SetProgress(index, _fmlLibsToProcess.Count);
                var entry = metacache.ResolveEntry("fmllibs", lib.Filename);
                var path = FileSystem.PathCombine(inst.LibDir(), lib.Filename);
                if (!FileSystem.EnsureFilePathExists(path))
                {
                    EmitFailed("Failed creating FML library folder inside the instance.");
                    return;
                }
                try
                {
                    File.Copy(entry.GetFullPath(), FileSystem.PathCombine(inst.LibDir(), lib.Filename), overwrite: true);
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
        else
        {
            // Prematurely aborted LegacyFMLLibrariesTask
        }
        base.Abort();
    }

    private HttpMetaCache RequireCache()
    {
        // the composition root stamps the instance with the runtime cache; the throwaway
        // fallback (no shared ledger) is for bare fixtures
        if (_inst.MetaCache is { } shared)
        {
            return shared;
        }
        var cache = new HttpMetaCache();
        cache.AddBase("fmllibs", _inst.Paths?.FmllibsCacheDir
            ?? FileSystem.PathCombine(Directory.GetCurrentDirectory(), "cache", "fmllibs"));
        return cache;
    }
}
