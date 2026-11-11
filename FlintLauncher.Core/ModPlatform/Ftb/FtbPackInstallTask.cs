// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022 flowln <flowlnlnln@gmail.com>
// SPDX-FileCopyrightText: 2022 Sefa Eyeoglu <contact@scrumplex.net>
// SPDX-FileCopyrightText: 2022 Jamie Mansfield <jmansfield@cadixdev.org>
//
// This file incorporates work covered by the following copyright and
// permission notice:
//
//     Copyright 2020-2021 Jamie Mansfield <jmansfield@cadixdev.org>
//     Copyright 2020-2021 Petr Mrazek <peterix@gmail.com>
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
//  - downloadFiles() (the update pipeline) is not ported; creation succeeds without
//    fetching game files, like the other creation tasks.

using System.Security.Cryptography;
using System.Text.Json;
using FlintLauncher.Core.Config;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.ModPlatform.Flame;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Tasks;
using FlintLauncher.ExternalApi.Flame;
using FlintLauncher.ExternalApi.Ftb;
using FlintLauncher.ExternalApi.Modrinth;
using TruePath;
using TruePath.SystemIo;

namespace FlintLauncher.Core.ModPlatform.Ftb;

public sealed class FtbPackInstallTask(
    FtbModpack pack,
    string versionName,
    ModrinthClient modrinth,
    HttpClient http,
    FlameClient? flame = null,
    FtbClient? ftb = null)
    : InstanceTask
{
    private readonly FlameClient _flame = flame ?? new FlameClient();
    private readonly FtbClient _ftb = ftb ?? new FtbClient();

    // assigned by OnManifestDownloadSucceeded before any read
    private FtbVersion? _manifest;

    /// <summary>The manifest payload; the body only reads it after the download succeeded, so
    /// a read before that is a broken invariant rather than a null value.</summary>
    private FtbVersion Manifest => _manifest
                                   ?? throw new InvalidOperationException("the pack manifest has not been downloaded");

    private readonly List<long> _fileIds = [];
    private readonly List<BlockedMod> _blockedMods = [];

    /// <summary>The blocked (browser-download) files reported when the resolver ran.</summary>
    public IReadOnlyList<BlockedMod> BlockedMods => _blockedMods;

    /// <summary>BlockedModsDialog stand-in: given a blocked mod, return a matching local file
    /// path (null when the user has none).</summary>
    public Func<BlockedMod, string?>? BlockedModLocalFileResolver { get; init; }

    internal override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        EmitStatus("Getting the manifest...");

        // Find pack version
        FtbVersionInfo? versionInfo = null;
        foreach (var candidate in pack.Versions)
        {
            if (candidate.Name == versionName)
            {
                versionInfo = candidate;
                break;
            }
        }

        if (versionInfo is null)
        {
            throw new InvalidOperationException($"Failed to find pack version {versionName}");
        }

        try
        {
            // the deserialized payload replaces the (not yet populated) manifest wholesale;
            // reads below only run after this assignment
            _manifest = await _ftb.Packs.Version(pack.Id, versionInfo.Id).FetchAsync(http, cancellationToken);
        }
        catch (FtbApiException e) when (e.InnerException is JsonException)
        {
            throw new InvalidOperationException("Could not understand pack manifest:\n" + e.Message, e);
        }
        catch (FtbApiException e)
        {
            throw new InvalidOperationException(e.Message, e);
        }

        await ResolveMods(cancellationToken);
    }

    private async Task ResolveMods(CancellationToken cancellationToken)
    {
        EmitStatus("Resolving mods...");
        SetProgress(0, 100);

        _fileIds.Clear();

        var flameManifest = new FlameManifest();
        foreach (var file in Manifest.Files)
        {
            if (file is { ServerOnly: false, Url: null or "" })
            {
                if (file.CurseforgeRaw is not { FileId: > 0 } curseforge)
                {
                    throw new InvalidOperationException($"Invalid manifest: There's no information available to download the file '{file.Name}'!");
                }

                var flameFile = new FlameFile
                {
                    ProjectId = curseforge.ProjectId,
                    FileId = curseforge.FileId,
                };

                flameManifest.Files.Add(flameFile.FileId, flameFile);
                _fileIds.Add(flameFile.FileId);
            }
            else
            {
                _fileIds.Add(-1);
            }
        }

        await FileResolvingTask.RunAsync(flameManifest, modrinth, http, _flame,
            cancellationToken: cancellationToken);

        await OnResolveModsSucceeded(flameManifest, cancellationToken);
    }

    private async Task OnResolveModsSucceeded(FlameManifest results,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < _fileIds.Count; index++)
        {
            var fileId = _fileIds[index];
            if (fileId < 0)
            {
                continue;
            }

            var resultsFile = results.Files[fileId];
            var localFile = Manifest.Files[index];

            // First check for blocked mods
            if (resultsFile.Version.DownloadUrl is null)
            {
                _blockedMods.Add(new BlockedMod
                {
                    Name = resultsFile.Version.FileName,
                    WebsiteUrl = $"{resultsFile.Pack.WebsiteUrl}/download/{resultsFile.FileId}",
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
                throw new InvalidOperationException("The following files are not available for download in third party launchers.\n" +
                                                    "You will need to manually download them and add them to the instance:\n" +
                                                    string.Join("\n", _blockedMods.Where(mod => !mod.Matched)
                                                        .Select(mod => $"{mod.Name} ({mod.WebsiteUrl})")));
            }
        }

        await CreateInstance(cancellationToken);
    }

    private async Task CreateInstance(CancellationToken cancellationToken)
    {
        EmitStatus("Creating the instance...");
        // the staged instance is Flint format from birth (typed instance.json adapter)
        var instance = new MinecraftInstance(
            new InstanceJsonSettingsObject(StagingPath, Environment ?? throw new InvalidOperationException(
                "InstanceTask requires SetParentEnvironment before execution.")),
            StagingPath);
        var components = instance.GetPackProfile();
        components.BuildingFromScratch();

        foreach (var target in Manifest.Targets)
        {
            if (target is { Type: "game", Name: "minecraft" })
            {
                await components.SetComponentVersion("net.minecraft", target.Version, important: true, cancellationToken);
                break;
            }
        }

        foreach (var target in Manifest.Targets)
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
        var jarModsDir = StagingPath / "minecraft" / "jarmods";
        if (jarModsDir.ExistsDirectory())
        {
            var jarMods = jarModsDir.EnumerateFiles().ToList();
            _ = components.InstallJarMods(jarMods);
        }

        components.SaveNow();

        instance.SetName(Name());
        instance.SetIconKey(Icon);
        instance.SetManagedPack("ftb", pack.Id.ToString(), pack.Name,
            Manifest.Id.ToString(), Manifest.Name);

        instance.SaveNow();

        await DownloadPack(cancellationToken);
    }

    private async Task DownloadPack(CancellationToken cancellationToken)
    {
        EmitStatus("Downloading mods...");

        // FTB blocks multiple requests at a time, so the batch runs one unit at a time
        var downloads = new List<HttpDownloadTask>();
        foreach (var file in Manifest.Files)
        {
            if (file.ServerOnly || string.IsNullOrEmpty(file.Url))
            {
                continue;
            }

            var path = StagingPath / ".minecraft" / file.Path / file.Name;

            var dl = HttpDownloadTask.ToFile(new Uri(file.Url), path, http);
            if (file.Sha1.Length != 0)
            {
                dl.RequireChecksum(HashAlgorithmName.SHA1, file.Sha1);
            }

            downloads.Add(dl);
        }

        await TaskWork.RunAllAsync(downloads,
                (dl, token) => dl.DownloadAsync(null, token), maxConcurrent: 1, cancellationToken)
            .ConfigureAwait(false);

        if (_blockedMods.Count != 0)
        {
            CopyBlockedMods();
        }

        // (upstream calls downloadFiles(m_instance.get()) here — the update pipeline;
        //  like the other creation tasks, creation succeeds without fetching game files)
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

            var destPath = new AbsolutePath(StagingPath) / ".minecraft" / mod.TargetFolder / mod.Name;
            EmitStatus($"Copying Blocked Mods ({i} out of {total} are done)");

            if (!FileOps.Copy(new AbsolutePath(mod.LocalPath), destPath))
            {
                // (the C++ only logs the failure)
            }

            i++;
            SetProgress(i, total);
        }
    }
}
