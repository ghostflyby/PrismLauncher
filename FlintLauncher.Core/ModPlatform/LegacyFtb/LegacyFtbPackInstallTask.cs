// SPDX-License-Identifier: GPL-3.0-only
// Port of modplatform/legacy_ftb/PackInstallTask.{h,cpp}: download the pack archive from
// the CDN ({modpacks|privatepacks}/{dir}/{version with '.'->'_'}/{file}), unzip it into
// staging/unzip and install it: unzip/minecraft becomes the instance's minecraft folder,
// the loader comes from minecraft/pack.json's first net.minecraftforge library (fallback:
// the jars in unzip/instMods become jar mods; without either the install fails like the
// C++ "No installation method found!").
//
// Deviations (headless port):
//  - the download goes through the meta cache when MetaCache is set ("FTBPacks" base, like
//    the C++), otherwise a plain download into a temporary folder
//  - downloadFiles() (the P5 update pipeline) is not ported; creation succeeds without
//    fetching game files, like the other creation tasks.

using System.Text.Json;
using FlintLauncher.Core.Config;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Net;

namespace FlintLauncher.Core.ModPlatform.LegacyFtb;

public sealed class LegacyFtbPackInstallTask : InstanceTask
{
    public LegacyFtbPackInstallTask(LegacyFtbModpack pack, string version)
    {
        _pack = pack;
        _version = version;
    }

    private readonly LegacyFtbModpack _pack;
    private readonly string _version;
    private string archivePath = "";

    /// <summary>APPLICATION->metacache() hook ("FTBPacks" base); null = plain downloads.</summary>
    public HttpMetaCache? MetaCache { get; set; }

    /// <summary>BuildConfig.LEGACY_FTB_CDN_BASE_URL (per-instance; tests construct the task
    /// pointed at their stub).</summary>
    public string CdnBaseUrl { get; set; } = FlintConfig.Default.LegacyFtbCdnBaseUrl;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        await DownloadPack(cancellationToken);
    }

    private async Task DownloadPack(CancellationToken cancellationToken)
    {
        EmitStatus($"Downloading zip for {_pack.Name}");
        SetProgress(1, 4);

        var path = $"{_pack.Dir}/{_version.Replace(".", "_")}/{_pack.File}";

        string url;
        NetRequest request;
        if (_pack.Type == PackType.Private)
        {
            url = CdnBaseUrl + "privatepacks/" + path;
        }
        else
        {
            url = CdnBaseUrl + "modpacks/" + path;
        }

        if (MetaCache is not null)
        {
            var entry = MetaCache.ResolveEntry("FTBPacks", path);
            entry.SetStale(true);
            archivePath = entry.GetFullPath();
            request = NetRequest.MakeCached(MetaCache, new Uri(url), entry);
        }
        else
        {
            archivePath = TempCachePath(path);
            request = NetRequest.MakeFile(new Uri(url), archivePath);
        }

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
            await components.SetComponentVersion("net.minecraft", _pack.McVersion, important: true, cancellationToken);

            var fallback = true;

            // handle different versions
            var packJsonPath = FileSystem.PathCombine(StagingPath, "minecraft", "pack.json");
            var jarmodDir = FileSystem.PathCombine(StagingPath, "unzip", "instMods");
            if (File.Exists(packJsonPath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(FileSystem.Read(packJsonPath));

                    // we only care about the libs
                    if (doc.RootElement.TryGetProperty("libraries", out var libs)
                        && libs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var value in libs.EnumerateArray())
                        {
                            var nameValue = value.TryGetProperty("name", out var nameNode)
                                            && nameNode.ValueKind == JsonValueKind.String
                                ? nameNode.GetString() ?? ""
                                : "";
                            if (!nameValue.StartsWith("net.minecraftforge", StringComparison.Ordinal))
                            {
                                continue;
                            }

                            var forgeSpecifier = new GradleSpecifier(nameValue);
                            var forgeVersion = string.IsNullOrEmpty(_pack.McVersion)
                                ? forgeSpecifier.Version
                                : forgeSpecifier.Version.Replace(_pack.McVersion, "").Replace("-", "");
                            await components.SetComponentVersion("net.minecraftforge", forgeVersion,
                                cancellationToken: cancellationToken);
                            FileSystem.DeletePath(packJsonPath);
                            fallback = false;
                            break;
                        }
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
