// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
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
// Port of modplatform/legacy_ftb/PackInstallTask.{h,cpp}: download the pack archive from
// the CDN ({modpacks|privatepacks}/{dir}/{version with '.'->'_'}/{file}), unzip it into
// staging/unzip and install it: unzip/minecraft becomes the instance's minecraft folder,
// the loader comes from minecraft/pack.json's first net.minecraftforge library (fallback:
// the jars in unzip/instMods become jar mods; without either the install fails like the
// C++ "No installation method found!").
//
// Deviations (headless port):
//  - the download goes straight into a temporary folder (the port has no meta cache)
//  - downloadFiles() (the update pipeline) is not ported; creation succeeds without
//    fetching game files, like the other creation tasks.

using System.Text.Json;
using System.Text.Json.Serialization;
using FlintLauncher.Core.Config;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Json;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Net;
using TruePath;
using TruePath.SystemIo;

namespace FlintLauncher.Core.ModPlatform.LegacyFtb;

public sealed class LegacyFtbPackInstallTask(LegacyFtbModpack pack, string version, HttpClient http, NetSettings settings) : InstanceTask
{
    private AbsolutePath _archivePath;

    /// <summary>BuildConfig.LEGACY_FTB_CDN_BASE_URL (per-instance; tests construct the task
    /// pointed at their stub).</summary>
    public string CdnBaseUrl { get; init; } = FlintConfig.Default.LegacyFtbCdnBaseUrl;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        await DownloadPack(cancellationToken);
    }

    private async Task DownloadPack(CancellationToken cancellationToken)
    {
        EmitStatus($"Downloading zip for {pack.Name}");
        SetProgress(1, 4);

        var path = $"{pack.Dir}/{version.Replace(".", "_")}/{pack.File}";

        string url;
        if (pack.Type == PackType.Private)
        {
            url = CdnBaseUrl + "privatepacks/" + path;
        }
        else
        {
            url = CdnBaseUrl + "modpacks/" + path;
        }

        // (the C++ routed this through APPLICATION->metacache()'s "FTBPacks" base; nothing
        // in the port wires a cache here, so the download always goes to a temp file)
        _archivePath = TempCachePath(path);
        var request = HttpDownloadTask.ToFile(new Uri(url), _archivePath, http, settings);

        var job = new NetJob("Download FTB Pack");
        job.AddDownload(request);

        await job.StartAsync(cancellationToken);
        if (!job.WasSuccessful)
        {
            EmitFailed(job.FailReason);
            return;
        }

        await Unzip(cancellationToken);
    }

    private static AbsolutePath TempCachePath(string relativeName)
    {
        var dir = Temporary.CreateTempFolder() / "flint-ftb-cache";
        FileOps.EnsureFolderPathExists(dir);
        string a1 = relativeName.Replace('/', '_');
        return dir / a1;
    }

    private async Task Unzip(CancellationToken cancellationToken)
    {
        EmitStatus("Extracting modpack");
        SetProgress(2, 4);

        var extractDir = StagingPath / "unzip";
        if (MmcZip.ExtractDir(_archivePath, "", extractDir) is null)
        {
            EmitFailed("Failed to extract modpack");
            return;
        }

        await Install(cancellationToken);
    }

    private async Task Install(CancellationToken cancellationToken)
    {
        EmitStatus("Installing modpack");
        SetProgress(3, 4);

        var unzipMcDir = StagingPath / "unzip" / "minecraft";
        if (unzipMcDir.ExistsDirectory())
        {
            // ok, found minecraft dir, move contents to instance dir
            if (!FileOps.Move(unzipMcDir, StagingPath / "minecraft"))
            {
                EmitFailed("Failed to move unpacked Minecraft!");
                return;
            }
        }

        // the staged instance is Flint format from birth (typed instance.json adapter)
        var instance = new MinecraftInstance(
            new InstanceJsonSettingsObject(StagingPath, Environment ?? throw new InvalidOperationException(
                "InstanceTask requires SetParentEnvironment before execution.")),
            StagingPath);
        {
            var components = instance.GetPackProfile();
            components.BuildingFromScratch();
            await components.SetComponentVersion("net.minecraft", pack.McVersion, important: true, cancellationToken);

            var fallback = true;

            // handle different versions
            var packJsonPath = StagingPath / "minecraft" / "pack.json";
            var jarmodDir = StagingPath / "unzip" / "instMods";
            if (packJsonPath.ExistsFile())
            {
                try
                {
                    // the typed pack.json read (we only care about the libs); a malformed
                    // document fails into the same keep-the-file/fall-through path
                    var packJson = JsonSerializer.Deserialize(await packJsonPath.ReadAllBytesAsync(cancellationToken),
                        LegacyFtbJsonContext.Default.LegacyFtbPackJson);

                    foreach (var library in packJson?.Libraries ?? [])
                    {
                        var nameValue = library.Name;
                        if (!nameValue.StartsWith("net.minecraftforge", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        var forgeSpecifier = new GradleSpecifier(nameValue);
                        var forgeVersion = string.IsNullOrEmpty(pack.McVersion)
                            ? forgeSpecifier.Version
                            : forgeSpecifier.Version.Replace(pack.McVersion, "").Replace("-", "");
                        await components.SetComponentVersion("net.minecraftforge", forgeVersion,
                            cancellationToken: cancellationToken);
                        FileOps.DeletePath(new AbsolutePath(packJsonPath));
                        fallback = false;
                        break;
                    }
                }
                catch (JsonException e)
                {
                    // (the C++ keeps the pack.json and falls through to the jar mods)
                    EmitStatus("Failed to parse pack.json: " + e.Message);
                }
            }

            if ((jarmodDir.ExistsDirectory()))
            {
                var jarmods = jarmodDir.EnumerateFiles().ToList();
                _ = components.InstallJarMods(jarmods);
                fallback = false;
            }

            // just nuke unzip directory, it s not needed anymore
            FileOps.DeletePath((new AbsolutePath(StagingPath) / "unzip"));

            if (fallback)
            {
                // TODO: Some fallback mechanism... or just keep failing!
                EmitFailed("No installation method found!");
                return;
            }

            components.SaveNow();

            SetProgress(4, 4);

            instance.SetName(Name());
            instance.SetIconKey(Icon == "default" ? "ftb_logo" : Icon);
        }

        // (upstream calls downloadFiles(m_instance.get()) here — the update pipeline;
        //  like the other creation tasks, creation succeeds without fetching game files)
        await Task.CompletedTask;
        EmitSucceeded();
    }
}

/// <summary>The minecraft/pack.json wire (read-only; only the libraries are consumed —
/// the first net.minecraftforge entry determines the loader version).</summary>
internal sealed record LegacyFtbPackJson
{
    public List<LegacyFtbLibJson>? Libraries { get; set; }
}

internal sealed record LegacyFtbLibJson
{
    [JsonConverter(typeof(QtCoercingStringConverter))]
    public string Name { get; set; } = "";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LegacyFtbPackJson))]
internal sealed partial class LegacyFtbJsonContext : JsonSerializerContext;
