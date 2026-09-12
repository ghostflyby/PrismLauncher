// SPDX-License-Identifier: GPL-3.0-only
// Port of modplatform/ftb/FTBPackInstallTask.{h,cpp}: install an FTB App API pack version —
// fetch /modpack/{id}/{versionId}, resolve the CurseForge-only files through the Flame
// file resolver, create the instance (net.minecraft from the "game"/"minecraft" target,
// forge/fabric/neoforge/quilt from the "modloader" targets, optional jar mods) and download
// the version files (sha1-verified) into staging/.minecraft/<path>/<name>.
//
// Deviations (headless port):
//  - The BlockedModsDialog: blocked files (resolved without a download URL) are reported
//    through the BlockedMods property; the optional BlockedModLocalFileResolver hook stands
//    in for the dialog's match-a-local-file step. Files that stay unmatched after it fail
//    the install listing them (the C++ dialog defaults to rejecting -> abort).
//  - downloadFiles() (the P5 update pipeline) is not ported; creation succeeds without
//    fetching game files, like the other creation tasks.
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Tasks;

namespace FlintLauncher.Core.ModPlatform.Ftb;

public sealed class FtbPackInstallTask : InstanceTask
{
    public FtbPackInstallTask(FtbModpack pack, string versionName)
    {
        _pack = pack;
        _versionName = versionName;
    }

    private readonly FtbModpack _pack;
    private readonly string _versionName;

    private readonly FtbVersion _version = new();
    private readonly List<long> _fileIds = [];
    private readonly List<Flame.BlockedMod> _blockedMods = [];

    /// <summary>The blocked (browser-download) files reported when the resolver ran.</summary>
    public IReadOnlyList<Flame.BlockedMod> BlockedMods => _blockedMods;

    /// <summary>BlockedModsDialog stand-in: given a blocked mod, return a matching local file
    /// path (null when the user has none).</summary>
    public Func<Flame.BlockedMod, string?>? BlockedModLocalFileResolver { get; set; }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        EmitStatus("Getting the manifest...");

        // Find pack version
        FtbVersionInfo? versionInfo = null;
        foreach (var candidate in _pack.Versions)
        {
            if (candidate.Name == _versionName)
            {
                versionInfo = candidate;
                break;
            }
        }

        if (versionInfo is null)
        {
            EmitFailed($"Failed to find pack version {_versionName}");
            return;
        }

        var (job, request) = FtbApi.FetchVersion(_pack.Id, versionInfo.Id);
        await job.StartAsync(cancellationToken);
        if (!job.WasSuccessful)
        {
            EmitFailed(job.FailReason);
            return;
        }

        await OnManifestDownloadSucceeded(request.BodyAsString, cancellationToken);
    }

    private async Task OnManifestDownloadSucceeded(string response, CancellationToken cancellationToken)
    {
        try
        {
            var loaded = FtbApi.LoadVersion(response);
            CopyInto(_version, loaded);
        }
        catch (Json.JsonParseException e)
        {
            EmitFailed("Could not understand pack manifest:\n" + e.Message);
            return;
        }

        await ResolveMods(cancellationToken);
    }

    private static void CopyInto(FtbVersion target, FtbVersion source)
    {
        target.Id = source.Id;
        target.Parent = source.Parent;
        target.Name = source.Name;
        target.Type = source.Type;
        target.Installs = source.Installs;
        target.Plays = source.Plays;
        target.Updated = source.Updated;
        target.Refreshed = source.Refreshed;
        (target.Specs.Id, target.Specs.Minimum, target.Specs.Recommended) =
            (source.Specs.Id, source.Specs.Minimum, source.Specs.Recommended);
        target.Targets.AddRange(source.Targets);
        target.Files.AddRange(source.Files);
    }

    private async Task ResolveMods(CancellationToken cancellationToken)
    {
        EmitStatus("Resolving mods...");
        SetProgress(0, 100);

        _fileIds.Clear();

        var manifest = new Flame.FlameManifest();
        foreach (var file in _version.Files)
        {
            if (!file.ServerOnly && file.Url.Length == 0)
            {
                if (file.Curseforge.FileId <= 0)
                {
                    EmitFailed($"Invalid manifest: There's no information available to download the file '{file.Name}'!");
                    return;
                }

                var flameFile = new Flame.FlameFile
                {
                    ProjectId = file.Curseforge.ProjectId,
                    FileId = file.Curseforge.FileId,
                };

                manifest.Files.Add(flameFile.FileId, flameFile);
                _fileIds.Add(flameFile.FileId);
            }
            else
            {
                _fileIds.Add(-1);
            }
        }

        var modIdResolverTask = new Flame.FileResolvingTask(manifest);

        EmitStatus("Resolving mods...");
        await modIdResolverTask.StartAsync(cancellationToken);
        if (!modIdResolverTask.WasSuccessful)
        {
            EmitFailed(modIdResolverTask.FailReason);
            return;
        }

        await OnResolveModsSucceeded(modIdResolverTask, cancellationToken);
    }

    private async Task OnResolveModsSucceeded(Flame.FileResolvingTask modIdResolverTask,
        CancellationToken cancellationToken)
    {
        var results = modIdResolverTask.GetResults();
        for (var index = 0; index < _fileIds.Count; index++)
        {
            var fileId = _fileIds[index];
            if (fileId < 0)
            {
                continue;
            }

            var resultsFile = results.Files[fileId];
            var localFile = _version.Files[index];

            // First check for blocked mods
            if (resultsFile.Version.DownloadUrl.Length == 0)
            {
                _blockedMods.Add(new Flame.BlockedMod
                {
                    Name = resultsFile.Version.FileName,
                    WebsiteUrl = $"{resultsFile.Pack.WebsiteUrl}/download/{resultsFile.FileId}",
                    Hash = resultsFile.Version.Hash,
                    Matched = false,
                    LocalPath = "",
                    TargetFolder = resultsFile.TargetFolder,
                });
            }
            else
            {
                localFile.Url = resultsFile.Version.DownloadUrl;
            }
        }

        if (_blockedMods.Count != 0)
        {
            // (upstream opens the BlockedModsDialog here and aborts on reject; the headless
            //  flow runs the optional local-file resolver instead and fails listing the
            //  leftovers)
            foreach (var mod in _blockedMods)
            {
                var localPath = BlockedModLocalFileResolver?.Invoke(mod);
                if (!string.IsNullOrEmpty(localPath) && File.Exists(localPath))
                {
                    mod.Matched = true;
                    mod.LocalPath = localPath;
                }
            }

            if (_blockedMods.Any(mod => !mod.Matched))
            {
                EmitFailed("The following files are not available for download in third party launchers.\n" +
                           "You will need to manually download them and add them to the instance:\n" +
                           string.Join("\n", _blockedMods.Where(mod => !mod.Matched)
                               .Select(mod => $"{mod.Name} ({mod.WebsiteUrl})")));
                return;
            }
        }

        await CreateInstance(cancellationToken);
    }

    private async Task CreateInstance(CancellationToken cancellationToken)
    {
        EmitStatus("Creating the instance...");
        var instanceConfigPath = FileSystem.PathCombine(StagingPath, "instance.cfg");
        var instanceSettings = new Settings.IniSettingsObject(instanceConfigPath);
        var instance = new MinecraftInstance(
            GlobalSettings ?? throw new InvalidOperationException(
                "InstanceTask requires SetParentSettings before execution."),
            instanceSettings, StagingPath);
        var components = instance.GetPackProfile();
        components.BuildingFromScratch();

        foreach (var target in _version.Targets)
        {
            if (target.Type == "game" && target.Name == "minecraft")
            {
                await components.SetComponentVersion("net.minecraft", target.Version, important: true, cancellationToken);
                break;
            }
        }

        foreach (var target in _version.Targets)
        {
            if (target.Type != "modloader")
            {
                continue;
            }

            if (target.Name == "forge")
            {
                await components.SetComponentVersion("net.minecraftforge", target.Version, cancellationToken: cancellationToken);
            }
            else if (target.Name == "fabric")
            {
                await components.SetComponentVersion("net.fabricmc.fabric-loader", target.Version, cancellationToken: cancellationToken);
            }
            else if (target.Name == "neoforge")
            {
                await components.SetComponentVersion("net.neoforged", target.Version, cancellationToken: cancellationToken);
            }
            else if (target.Name == "quilt")
            {
                await components.SetComponentVersion("org.quiltmc.quilt-loader", target.Version, cancellationToken: cancellationToken);
            }
        }

        // install any jar mods
        var jarModsDir = FileSystem.PathCombine(StagingPath, "minecraft", "jarmods");
        if (Directory.Exists(jarModsDir))
        {
            var jarMods = Directory.EnumerateFiles(jarModsDir).ToList();
            _ = components.InstallJarMods(jarMods);
        }

        components.SaveNow();

        instance.SetName(Name());
        instance.SetIconKey(Icon);
        instance.SetManagedPack("ftb", _pack.Id.ToString(), _pack.Name,
            _version.Id.ToString(), _version.Name);

        instance.SaveNow();

        await DownloadPack(cancellationToken);
    }

    private async Task DownloadPack(CancellationToken cancellationToken)
    {
        EmitStatus("Downloading mods...");

        // FTB blocks multiple requests at a time
        var jobPtr = new NetJob("Mod download", maxConcurrent: 1);
        var downloadRequests = new List<NetRequest>();
        foreach (var file in _version.Files)
        {
            if (file.ServerOnly || file.Url.Length == 0)
            {
                continue;
            }

            var path = FileSystem.PathCombine(StagingPath, ".minecraft", file.Path, file.Name);

            var dl = NetRequest.MakeFile(new Uri(file.Url), path);
            if (file.Sha1.Length != 0)
            {
                dl.AddValidator(ChecksumValidator.FromHex(System.Security.Cryptography.HashAlgorithmName.SHA1, file.Sha1));
            }

            jobPtr.AddNetRequest(dl);
            downloadRequests.Add(dl);
        }

        jobPtr.Progress += (_, _) =>
        {
            EmitDetails($"{downloadRequests.Count(t => t.IsFinished)} out of {downloadRequests.Count} complete");
            SetProgress(downloadRequests.Count(t => t.IsFinished), downloadRequests.Count);
        };
        await jobPtr.StartAsync(cancellationToken);
        if (!jobPtr.WasSuccessful)
        {
            EmitFailed(jobPtr.FailReason);
            return;
        }

        if (_blockedMods.Count != 0)
        {
            CopyBlockedMods();
        }

        // (upstream calls downloadFiles(m_instance.get()) here — the P5 update pipeline;
        //  like the other creation tasks, creation succeeds without fetching game files)
        EmitSucceeded();
    }

    /// <summary>copyBlockedMods: copy the matched blocked mods to the instance staging area.</summary>
    private void CopyBlockedMods()
    {
        EmitStatus("Copying Blocked Mods...");
        var i = 0;
        var total = _blockedMods.Count;
        SetProgress(i, total);
        foreach (var mod in _blockedMods)
        {
            if (!mod.Matched)
            {
                continue;
            }

            var destPath = FileSystem.PathCombine(StagingPath, ".minecraft", mod.TargetFolder, mod.Name);

            EmitStatus($"Copying Blocked Mods ({i} out of {total} are done)");

            if (!FileSystem.Copy(mod.LocalPath, destPath))
            {
                // (the C++ only logs the failure)
            }

            i++;
            SetProgress(i, total);
        }
    }
}
