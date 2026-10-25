// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022 Sefa Eyeoglu <contact@scrumplex.net>
//
// This file incorporates work covered by the following copyright and
// permission notice:
//
//     Copyright 2013-2021 MultiMC Contributors
//
//     Licensed under the Apache License, Version 2.0 (the "License");
//     you may not use this file except in compliance with the License.
//     You may obtain a copy of the License at
//
//         http://www.apache.org/licenses/LICENSE-2.0
//
//     Unless required by applicable law or agreed to in writing, software
//     distributed under the License is distributed on an "AS IS" BASIS,
//     WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//     See the License for the specific language governing permissions and
//     limitations under the License.
//
// Port of modplatform/flame/FlameInstanceCreationTask.{h,cpp}: unpack a CurseForge
// manifest.zip into a new instance (staging) folder, install the modloader components,
// resolve the manifest files and download them.
//   - the manifest.json is kept inside flame/ for later version changes
//   - the pack's overrides folder is moved to the instance's minecraft dir
//   - every resolved mod file lands under minecraft/<targetFolder>/ and mods get their
//     .pw.toml Flame metadata ([update.curseforge] file-id/project-id) written through the
//     MetadataHandler (the LocalResourceUpdateTask of the C++)
//
// Deviations (headless port):
//  - The update-existing-instance flow of executeTask() (APPLICATION->instances() lookups,
//    the askIfShouldUpdate/warn dialogs and the old-manifest diffing) is deferred;
//    the task goes straight to createInstance().
//  - The optional-mods dialog: optional files are installed disabled (".disabled" suffix),
//    like the C++ non-dialog path.
//  - The UntrustedModsDialog: without the dialog the install fails listing the untrusted
//    files when TrustedSource is false (the C++ dialog defaults to rejecting).
//  - The BlockedModsDialog: blocked files (no download URL) are reported through the
//    BlockedMods property; the optional BlockedModLocalFileResolver hook stands in for the
//    dialog's match-a-local-file step. Files that stay unmatched after it fail the install
//    listing them (the C++ dialog defaults to rejecting -> emitAborted).
//  - validateOtherResources uses the API-declared resource type instead of the
//    ResourceUtils::identify content sniffing (which is not ported); .zip files inside
//    mods/ therefore receive no mod metadata (upstream only did for identified jars).
//  - getVersionForLoader's "recommended" branch resolves through the injected
//    RecommendedVersionLookup hook (the meta version list layer is not wired in this
//    phase); without a hook the install fails like the C++ does when the version list
//    cannot be loaded.
//  - logWarning() becomes a Warnings entry + a status line (no warningLogged signal).

using System.Text.Json;
using System.Text.RegularExpressions;
using FlintLauncher.Core.Config;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Launch;
using FlintLauncher.Core.Minecraft.Worlds;
using FlintLauncher.Core.ModPlatform.Modrinth;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Tasks;
using TruePath;
using TruePath.SystemIo;

namespace FlintLauncher.Core.ModPlatform.Flame;

using ResourceType = ResourceType;

public sealed partial class FlameCreationTask : InstanceTask
{
    public FlameCreationTask(AbsolutePath stagingPath,
        bool trustedSource,
        InstanceEnvironment environment,
        ModrinthApi modrinth,
        string id = "",
        string versionId = "",
        string originalInstanceId = "",
        FlameApi? flame = null)
    {
        _modrinth = modrinth;
        _flame = flame ?? new FlameApi(modrinth.Http, modrinth.Settings);
        StagingPath = stagingPath;
        TrustedSource = trustedSource;
        SetParentEnvironment(environment);
        _managedId = id;
        _managedVersionId = versionId;
        SetOverride(false, originalInstanceId);
    }

    public bool TrustedSource { get; }

    private readonly FlameManifest _pack = new();
    private readonly ModrinthApi _modrinth;
    private readonly FlameApi _flame;
    private readonly string _managedId;
    private readonly string _managedVersionId;

    private readonly List<(string FileName, string TargetFolder)> _otherResources = [];

    private MinecraftInstance? _newInstance;

    /// <summary>logWarning() output (no warningLogged signal in this port).</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    private readonly List<string> _warnings = [];

    /// <summary>The blocked files reported after the resolver ran (BlockedModsDialog data).</summary>
    public IReadOnlyList<BlockedMod> BlockedMods { get; private set; } = [];

    /// <summary>
    /// Stand-in for the BlockedModsDialog matching step: given a blocked mod, return the
    /// path of a matching local file (null when the user has none). null disables matching.
    /// </summary>
    public Func<BlockedMod, string?>? BlockedModLocalFileResolver { get; init; }

    /// <summary>
    /// Headless stand-in for the optional-mods dialog selection: keys are the
    /// resolved file's relative path ("&lt;targetFolder&gt;/&lt;fileName&gt;", separators
    /// normalized), values the selected flag. Selected optional files install enabled,
    /// everything else keeps the non-dialog behavior (".disabled" suffix). Flagged Core
    /// addition: the C++ gets this from the dialog, so the property does not exist upstream.
    /// </summary>
    public IReadOnlyDictionary<string, bool> OptionalModsSelection { get; init; } =
        new Dictionary<string, bool>();

    /// <summary>The optional-mods selection check for a relative path against the headless
    /// selection map, '/'-normalized (the C++ dialog-result list has no ported producer).</summary>
    private bool IsOptionalSelected(string relPath)
    {
        var normalized = relPath.Replace('\\', '/');
        return OptionalModsSelection.TryGetValue(normalized, out var selected) && selected;
    }

    /// <summary>Stand-in for the "recommended" loader-version lookup over the meta index:
    /// (loader uid, loader type, mc version) -> version descriptor. Per-task hook (DI):
    /// the caller sets it before StartAsync, so parallel creations cannot see each other's
    /// stub.</summary>
    public Func<string, string, string, string?>? RecommendedVersionLookup { get; init; }

    private void LogWarning(string message)
    {
        _warnings.Add(message);
        EmitStatus(message);
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        // (upstream checks for an existing instance to update here — deferred, see header)
        await CreateInstanceAsync(cancellationToken);
    }

    private async Task CreateInstanceAsync(CancellationToken cancellationToken)
    {
        var parentFolder = StagingPath / "flame";

        try
        {
            var indexPath = StagingPath / "manifest.json";
            if (!_pack.IsLoaded)
            {
                FlameManifest.LoadManifest(_pack, indexPath);
            }

            // Keep index file in case we need it some other time (like when changing versions)
            var newIndexPlace = parentFolder / "manifest.json";
            FileOps.EnsureFilePathExists(newIndexPlace);
            FileOps.Move(indexPath, newIndexPlace);
        }
        catch (JsonException e)
        {
            EmitFailed("Could not understand pack manifest:\n" + e.Message);
            return;
        }

        if (_pack.Overrides.Length != 0)
        {
            var overridePath = StagingPath / _pack.Overrides;
            // upstream #6183: the manifest is untrusted input — an override path that escapes
            // the staging root (absolute path or .. segments) is a security risk, abort
            if (!IsParentOf(StagingPath.Value, overridePath.Value))
            {
                EmitFailed($"The overrides has a path that leads to an arbitrary location ({_pack.Overrides}). " +
                           "This is a security risk and isn't allowed.");
                return;
            }

            if (overridePath.Exists())
            {
                // Create a list of overrides in "overrides.txt" inside flame/
                OverrideUtils.CreateOverrides("overrides", parentFolder, overridePath);

                var mcPath = StagingPath / "minecraft";
                if (!FileOps.Move(overridePath, mcPath))
                {
                    EmitFailed("Could not rename the overrides folder:\n" + _pack.Overrides);
                    return;
                }
            }
            else
            {
                LogWarning($"The specified overrides folder ({_pack.Overrides}) is missing. " +
                           "Maybe the modpack was already used before?");
            }
        }

        if (!PromptForUntrustedMods())
        {
            EmitFailed("The pack contains files from untrusted sources:\n" + string.Join("\n", UntrustedMods));
            return;
        }

        string loaderType = "";
        string loaderUid = "";
        string loaderVersion = "";

        foreach (var loader in _pack.Minecraft.ModLoaders)
        {
            var id = loader.Id;
            if (id.StartsWith("neoforge-", StringComparison.Ordinal))
            {
                id = id["neoforge-".Length..];
                if (id.StartsWith("1.20.1-", StringComparison.Ordinal))
                {
                    id = id["1.20.1-".Length..]; // this is a mess for curseforge
                }

                loaderType = "neoforge";
                loaderUid = "net.neoforged";
            }
            else if (id.StartsWith("forge-", StringComparison.Ordinal))
            {
                id = id["forge-".Length..];
                loaderType = "forge";
                loaderUid = "net.minecraftforge";
            }
            else if (id.StartsWith("fabric-", StringComparison.Ordinal))
            {
                id = id["fabric-".Length..];
                loaderType = "fabric";
                loaderUid = "net.fabricmc.fabric-loader";
            }
            else if (id.StartsWith("quilt-", StringComparison.Ordinal))
            {
                id = id["quilt-".Length..];
                loaderType = "quilt";
                loaderUid = "org.quiltmc.quilt-loader";
            }
            else
            {
                LogWarning("Unknown mod loader in manifest: " + id);
                continue;
            }

            loaderVersion = id;
        }

        // the staged instance is Flint format from birth (typed instance.json adapter)
        _newInstance = new MinecraftInstance(
            new InstanceJsonSettingsObject(StagingPath, Environment ??
                                                        throw new InvalidOperationException(
                                                            "InstanceTask requires SetParentEnvironment before execution.")),
            StagingPath);
        var mcVersion = _pack.Minecraft.Version;

        // Hack to correct some 'special sauce'...
        if (mcVersion.EndsWith('.'))
        {
            mcVersion = TrailingDotsRegex().Replace(mcVersion, "");
            LogWarning("Mysterious trailing dots removed from Minecraft version while importing pack.");
        }

        var components = _newInstance.GetPackProfile();
        components.BuildingFromScratch();
        await components.SetComponentVersion("net.minecraft", mcVersion, important: true, cancellationToken);
        if (loaderType.Length != 0)
        {
            var version = await GetVersionForLoader(loaderUid, loaderType, loaderVersion, mcVersion, cancellationToken);
            if (version.Length == 0)
            {
                // because there is more info in GetVersionForLoader, the failure was emitted there
                return;
            }

            await components.SetComponentVersion(loaderUid, version, cancellationToken: cancellationToken);
        }

        if (Icon != "default")
        {
            _newInstance.SetIconKey(Icon);
        }
        else
        {
            if (_pack.Name.Contains("Direwolf20", StringComparison.Ordinal))
            {
                _newInstance.SetIconKey("steve");
            }
            else if (_pack.Name.Contains("FTB", StringComparison.Ordinal)
                     || _pack.Name.Contains("Feed The Beast", StringComparison.Ordinal))
            {
                _newInstance.SetIconKey("ftb_logo");
            }
            else
            {
                _newInstance.SetIconKey("flame");
            }
        }

        var recommendedRam = _pack.Minecraft.RecommendedRam;

        // only set memory if this is a fresh instance
        if (recommendedRam > 0)
        {
            var sysMiB = HardwareInfo.TotalRamMiB();
            var max = (ulong)(sysMiB * 0.9);

            if ((ulong)recommendedRam > max)
            {
                LogWarning($"The recommended memory of the modpack exceeds 90% of your system RAM—reducing it " +
                           $"from {recommendedRam} MiB to {max} MiB!");
                recommendedRam = (int)max;
            }

            // writing MaxMemAlloc sets the instance-layer member, which IS the override
            // (the derived OverrideMemory gate reads true; no separate gate write exists)
            _newInstance.Settings.Set("MaxMemAlloc", recommendedRam);
        }

        var jarmodsPath = StagingPath / "minecraft" / "jarmods";
        if (jarmodsPath.ExistsDirectory())
        {
            // install all the jar mods
            List<AbsolutePath> jarMods = jarmodsPath.EnumerateFiles().ToList();
            _ = _newInstance.GetPackProfile().InstallJarMods(jarMods);
            // nuke the original files
            FileOps.DeletePath(jarmodsPath);
        }

        SetManagedPack(_newInstance);

        _newInstance.SetName(Name());
        _newInstance.SaveNow();

        // ---------------------------------------------------------- resolve + download
        var modIdResolver = new FileResolvingTask(_pack, _modrinth, _flame);
        EmitStatus("Resolving mod IDs...");
        await modIdResolver.StartAsync(cancellationToken);
        if (!modIdResolver.WasSuccessful)
        {
            if (modIdResolver.State == TaskState.AbortedByUser)
            {
                EmitAborted();
            }
            else
            {
                EmitFailed("Unable to resolve mod IDs:\n" + modIdResolver.FailReason);
            }

            return;
        }

        await IdResolverSucceeded(modIdResolver, cancellationToken);
    }

    /// <summary>Port of FlameCreationTask::getVersionForLoader.</summary>
    private async Task<string> GetVersionForLoader(string uid, string loaderType, string loaderVersion,
        string mcVersion, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        if (loaderVersion == "recommended")
        {
            var lookup = RecommendedVersionLookup;
            if (lookup is null)
            {
                EmitFailed($"Failed to get local metadata index for {uid}");
                return "";
            }

            // first recommended build we find, filtered by the minecraft version for
            // forge/neoforge (the lookup owns the version-list traversal)
            var version = lookup(uid, loaderType, mcVersion);
            if (string.IsNullOrEmpty(version))
            {
                EmitFailed($"Failed to find version for {loaderType} loader");
                return "";
            }

            return version;
        }

        if (loaderVersion.Length == 0)
        {
            EmitFailed("No loader version set for modpack!");
            return "";
        }

        return loaderVersion;
    }

    private void SetManagedPack(MinecraftInstance instance)
    {
        // Don't add managed info to packs without an ID (most likely imported from ZIP)
        if (_managedId.Length != 0)
        {
            instance.SetManagedPack("flame", _managedId, _pack.Name, _managedVersionId, _pack.Version);
        }
        else
        {
            instance.SetManagedPack("flame", "", Name(), "", "");
        }
    }

    /// <summary>Port of FlameCreationTask::promptForUntrustedMods (see the header note).</summary>
    private bool PromptForUntrustedMods()
    {
        if (TrustedSource)
        {
            return true;
        }

        var untrustedMods = new List<LocalPath>();

        var mcDir = StagingPath / "minecraft";
        var modsPath = StagingPath / "minecraft" / "mods";
        if (modsPath.ExistsDirectory())
        {
            foreach (var iter in modsPath.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                untrustedMods.Add(iter.RelativeTo(mcDir));
            }
        }

        if (untrustedMods.Count == 0)
        {
            return true;
        }

        UntrustedMods = untrustedMods;
        return false;
    }

    /// <summary>The untrusted files reported when TrustedSource is false (see header note).</summary>
    public List<LocalPath> UntrustedMods { get; private set; } = [];

    /// <summary>Port of FlameCreationTask::idResolverSucceeded.</summary>
    private async Task IdResolverSucceeded(FileResolvingTask modIdResolver, CancellationToken cancellationToken)
    {
        var results = modIdResolver.GetResults().Files.Values;

        var optionalFiles = new List<string>();
        foreach (var result in results)
        {
            if (!result.Required)
            {
                optionalFiles.Add((new LocalPath(result.TargetFolder) / result.Version.FileName).Value);
            }
        }

        _ = optionalFiles; // (the OptionalModDialog is not ported — optional files install disabled)

        // first check for blocked mods
        var blockedMods = new List<BlockedMod>();
        var anyBlocked = false;
        foreach (var result in results)
        {
            if (result.Pack.ResourceType != ResourceType.Mod)
            {
                _otherResources.Add((result.Version.FileName, result.TargetFolder));
            }

            // skip optional mods that were not selected
            if (result.Version.DownloadUrl.Length == 0)
            {
                var blockedMod = new BlockedMod
                {
                    Name = result.Version.FileName,
                    WebsiteUrl = $"{result.Pack.WebsiteUrl}/download/{result.Version.FileId}",
                    Matched = false,
                    LocalPath = "",
                    TargetFolder = result.TargetFolder,
                };
                var fileName = FileNaming.RemoveInvalidPathChars(result.Version.FileName);
                var relpath = new LocalPath(result.TargetFolder) / fileName;
                blockedMod.Disabled = !result.Required && !IsOptionalSelected(relpath.Value);

                blockedMods.Add(blockedMod);
                anyBlocked = true;
            }
        }

        BlockedMods = blockedMods;

        if (anyBlocked)
        {
            // (upstream opens the BlockedModsDialog here; the headless flow runs the
            //  optional local-file resolver instead and fails listing the leftovers)
            foreach (var mod in blockedMods)
            {
                var localPath = BlockedModLocalFileResolver?.Invoke(mod);
                if (!string.IsNullOrEmpty(localPath) && File.Exists(localPath))
                {
                    mod.Matched = true;
                    mod.LocalPath = localPath;
                }
            }

            if (blockedMods.Any(mod => !mod.Matched))
            {
                EmitFailed("The following files are not available for download in third party launchers.\n" +
                           "You will need to manually download them and add them to the instance:\n" +
                           string.Join("\n", blockedMods.Where(mod => !mod.Matched)
                               .Select(mod => $"{mod.Name} ({mod.WebsiteUrl})")));
                return;
            }

            await CopyBlockedMods(blockedMods, cancellationToken);
        }

        await SetupDownloadJob(modIdResolver, cancellationToken);
    }

    /// <summary>Port of FlameCreationTask::setupDownloadJob.</summary>
    private async Task SetupDownloadJob(FileResolvingTask modIdResolver, CancellationToken cancellationToken)
    {
        var filesJob = new NetJob("Mod Download Flame");
        var downloadTasks = new List<NetRequest>();
        var results = modIdResolver.GetResults().Files.Values;

        foreach (var result in results)
        {
            var fileName = FileNaming.RemoveInvalidPathChars(result.Version.FileName);
            var relpath = new LocalPath(result.TargetFolder) / fileName;

            if (!result.Required && !IsOptionalSelected(relpath.Value))
            {
                relpath = new LocalPath(relpath.Value + ".disabled");
            }

            var path = StagingPath / "minecraft" / relpath;

            if (result.Version.DownloadUrl.Length != 0)
            {
                var dl = NetRequest.MakeFile(new Uri(result.Version.DownloadUrl), path, _flame.Http, _flame.Settings);
                filesJob.AddNetRequest(dl);
                downloadTasks.Add(dl);
            }
        }

        EmitStatus("Downloading mods...");
        filesJob.Progress += (_, _) =>
        {
            EmitDetails($"{downloadTasks.Count(t => t.IsFinished)} out of {downloadTasks.Count} complete");
            SetProgress(downloadTasks.Count(t => t.IsFinished), downloadTasks.Count);
        };
        await filesJob.StartAsync(cancellationToken);
        if (!filesJob.WasSuccessful)
        {
            EmitFailed(filesJob.FailReason);
            return;
        }

        await ValidateOtherResources(modIdResolver);
    }

    /// <summary>copyBlockedMods: copy the matched blocked mods to the instance staging area.</summary>
    private async Task CopyBlockedMods(IReadOnlyList<BlockedMod> blockedMods, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        EmitStatus("Copying Blocked Mods...");
        var i = 0;
        var total = blockedMods.Count;
        SetProgress(i, total);
        foreach (var mod in blockedMods)
        {
            if (!mod.Matched)
            {
                continue;
            }

            // the ".disabled" suffix rides on the file name, like in setupDownloadJob
            var name = mod.Disabled ? mod.Name + ".disabled" : mod.Name;
            var destPath = StagingPath / "minecraft" / mod.TargetFolder / name;

            EmitStatus($"Copying Blocked Mods ({i} out of {total} are done)");

            // the C++ Move/copy fork was the BlockedModsDialog's checkbox; the headless port
            // always copies (never moves) the user-picked local file
            try
            {
                FileOps.EnsureFilePathExists(destPath);
                File.Copy(mod.LocalPath, destPath.Value, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // (the C++ only logs the failure)
            }

            i++;
            SetProgress(i, total);
        }
    }

    /// <summary>Port of FlameCreationTask::validateOtherResources (+ the metadata pass of the
    /// LocalResourceUpdateTask loop and finishInstall).</summary>
    private async Task ValidateOtherResources(FileResolvingTask modIdResolver)
    {
        var zipMods = new List<string>();
        var results = modIdResolver.GetResults().Files.Values.ToList();
        foreach (var (fileName, targetFolder) in _otherResources)
        {
            var localPath = StagingPath / "minecraft" / targetFolder / fileName;

            string ValidatePath(string name, string folder, string realTarget)
            {
                if (folder != realTarget)
                {
                    var destPath = StagingPath / "minecraft" / realTarget / name;
                    if (FileOps.Move(localPath, destPath))
                    {
                        return destPath.Value;
                    }
                }

                return localPath.Value;
            }

            var resource = OtherResourcesType(fileName, results);
            string worldPath;
            switch (resource)
            {
                case ResourceType.Mod:
                    ValidatePath(fileName, targetFolder, "mods");
                    zipMods.Add(fileName);
                    break;
                case ResourceType.ResourcePack:
                    ValidatePath(fileName, targetFolder, "resourcepacks");
                    break;
                case ResourceType.TexturePack:
                    ValidatePath(fileName, targetFolder, "texturepacks");
                    break;
                case ResourceType.DataPack:
                    ValidatePath(fileName, targetFolder, "datapacks");
                    break;
                case ResourceType.ShaderPack:
                    // in theory flame API can't do this but who knows, that *may* change ?
                    ValidatePath(fileName, targetFolder, "shaderpacks");
                    break;
                case ResourceType.World:
                    worldPath = ValidatePath(fileName, targetFolder, "saves");
                    InstallWorld(worldPath);
                    break;
            }
        }

        _ = zipMods; // (see the header note: without identify() the zip-mod special case is inert)

        // ---------------------------------------------------------- write mod metadata
        var folder = StagingPath / "minecraft" / "mods" / ".index";
        foreach (var file in results)
        {
            if (file.TargetFolder != "mods"
                || (file.Version.FileName.EndsWith(".zip", StringComparison.Ordinal)
                    && !zipMods.Contains(file.Version.FileName)))
            {
                continue;
            }

            // LocalResourceUpdateTask::executeTask (create + write the .pw.toml index)
            var ver = file.Version;
            ver.FileName = FileNaming.RemoveInvalidPathChars(ver.FileName);
            var pwMod = MetadataHandler.Create(folder, file.Pack, ver);
            if (pwMod.IsValid)
            {
                MetadataHandler.Update(folder, pwMod);
            }
            else
            {
                EmitStatus($"Failed to generate metadata for {file.Version.FileName}");
            }
        }

        await FinishInstallAsync();
    }

    /// <summary>The API-declared resource type of the recorded other-resource (see the
    /// header note about the identify() substitution).</summary>
    private static ResourceType OtherResourcesType(string fileName, IEnumerable<FlameFile> results)
    {
        foreach (var file in results)
        {
            if (file.Version.FileName == fileName)
            {
                return file.Pack.ResourceType;
            }
        }

        return ResourceType.Unknown;
    }

    private void InstallWorld(string worldPath)
    {
        var world = new World(worldPath);
        if (!world.IsValid)
        {
            // (the C++ only logs; the world is skipped)
            return;
        }

        _ = world.Install(StagingPath / "minecraft" / "saves");
    }

    /// <summary>Port of FlameCreationTask::finishInstall (fresh-install branch).</summary>
    private async Task FinishInstallAsync()
    {
        await Task.CompletedTask; // async signature kept for the deferred update flow

        if (ShouldOverride)
        {
            var deleteFailed = false;

            EmitStatus("Removing old conflicting files...");

            foreach (var path in FilesToRemove)
            {
                if (!path.Exists())
                {
                    continue;
                }

                if (!FileOps.DeletePath(path))
                {
                    deleteFailed = true;
                }
            }

            if (deleteFailed)
            {
                EmitFailed("Failed to remove old conflicting files.");
                return;
            }
        }

        // (upstream calls downloadFiles(m_newInstance.get()) here — the update pipeline;
        //  like the other creation tasks, creation succeeds without fetching game files)
        EmitSucceeded();
    }

    /// <summary>QUrl::fromLocalFile(root).isParentOf(QUrl::fromLocalFile(child)) for real
    /// filesystem paths: both are fully resolved, then containment is lexical (the child must
    /// live under root, not in a sibling whose name shares the prefix).</summary>
    private static bool IsParentOf(string root, string child)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var childFull = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return childFull.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal)
               || childFull.StartsWith(rootFull + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"[.]+$")]
    private static partial Regex TrailingDotsRegex();
}
