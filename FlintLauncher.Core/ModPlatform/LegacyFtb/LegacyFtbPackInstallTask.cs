// SPDX-License-Identifier: GPL-3.0-only
// Port of modplatform/legacy_ftb/PackInstallTask.{h,cpp}: download the pack archive from
// the CDN ({modpacks|privatepacks}/{dir}/{version with '.'->'_'}/{file}), unzip it into
// staging/unzip and install it: unzip/minecraft becomes the instance's minecraft folder,
// the loader comes from minecraft/pack.json's first net.minecraftforge library (fallback:
// the jars in unzip/instMods become jar mods; without either the install fails like the
// C++ "No installation method found!").
//
// Deviations (headless port):
//  - the download goes straight into a temporary folder (the port has no meta cache)
//  - downloadFiles() (the P5 update pipeline) is not ported; creation succeeds without
//    fetching game files, like the other creation tasks.

using System.Text.Json;
using System.Text.Json.Serialization;
using FlintLauncher.Core.Config;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Json;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Net;

namespace FlintLauncher.Core.ModPlatform.LegacyFtb;

public sealed class LegacyFtbPackInstallTask(LegacyFtbModpack pack, string version) : InstanceTask
{
    private string archivePath = "";

    /// <summary>BuildConfig.LEGACY_FTB_CDN_BASE_URL (per-instance; tests construct the task
    /// pointed at their stub).</summary>
    public string CdnBaseUrl { get; set; } = FlintConfig.Default.LegacyFtbCdnBaseUrl;

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
        NetRequest request;
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
        archivePath = TempCachePath(path);
        request = NetRequest.MakeFile(new Uri(url), archivePath);

        var job = new NetJob("Download FTB Pack");
        job.AddNetRequest(request);

        await job.StartAsync(cancellationToken);
        if (!job.WasSuccessful)
        {
            EmitFailed(job.FailReason);
            return;
        }

        await Unzip(cancellationToken);
    }

    private static string TempCachePath(string relativeName)
    {
        var dir = Path.Combine(Path.GetTempPath(), "flint-ftb-cache");
        FileSystem.EnsureFolderPathExists(dir);
        return FileSystem.PathCombine(dir, relativeName.Replace('/', '_'));
    }

    private async Task Unzip(CancellationToken cancellationToken)
    {
        EmitStatus("Extracting modpack");
        SetProgress(2, 4);

        var extractDir = FileSystem.PathCombine(StagingPath, "unzip");
        if (MmcZip.ExtractDir(archivePath, "", extractDir) is null)
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

        var unzipMcDir = FileSystem.PathCombine(StagingPath, "unzip", "minecraft");
        if (Directory.Exists(unzipMcDir))
        {
            // ok, found minecraft dir, move contents to instance dir
            if (!FileSystem.Move(unzipMcDir, FileSystem.PathCombine(StagingPath, "minecraft")))
            {
                EmitFailed("Failed to move unpacked Minecraft!");
                return;
            }
        }

        // D4: the staged instance is Flint format from birth (typed instance.json adapter)
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
            var packJsonPath = FileSystem.PathCombine(StagingPath, "minecraft", "pack.json");
            var jarmodDir = FileSystem.PathCombine(StagingPath, "unzip", "instMods");
            if (File.Exists(packJsonPath))
            {
                try
                {
                    // the typed pack.json read (we only care about the libs); a malformed
                    // document fails into the same keep-the-file/fall-through path
                    var packJson = JsonSerializer.Deserialize(FileSystem.Read(packJsonPath),
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
                        FileSystem.DeletePath(packJsonPath);
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

            if (Directory.Exists(jarmodDir))
            {
                var jarmods = Directory.EnumerateFiles(jarmodDir).ToList();
                _ = components.InstallJarMods(jarmods);
                fallback = false;
            }

            // just nuke unzip directory, it s not needed anymore
            FileSystem.DeletePath(FileSystem.PathCombine(StagingPath, "unzip"));

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

        // (upstream calls downloadFiles(m_instance.get()) here — the P5 update pipeline;
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
