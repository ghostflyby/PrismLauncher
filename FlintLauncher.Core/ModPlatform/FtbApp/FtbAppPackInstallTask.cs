// SPDX-License-Identifier: GPL-3.0-only
// Port of modplatform/import_ftb/PackInstallTask.{h,cpp}: import a local FTB App instance
// — copy the modpack folder into staging/minecraft and rebuild the instance settings:
// InstanceType=OneSix, totalTimePlayed (from the ms totalPlayTime), the jvmArgs override,
// the net.minecraft component and the modloader component derived from the parsed loader.
//
// Deviation: downloadFiles() (the P5 update pipeline) is not ported; creation succeeds
// without fetching game files, like the other creation tasks.
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Tasks;

namespace FlintLauncher.Core.ModPlatform.FtbApp;

public sealed class FtbAppPackInstallTask : InstanceTask
{
    public FtbAppPackInstallTask(FtbAppModpack pack)
    {
        _pack = pack;
    }

    private readonly FtbAppModpack _pack;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        EmitStatus("Copying files...");
        SetProgress(1, 2);

        var target = FileSystem.PathCombine(StagingPath, "minecraft");
        if (!FileSystem.Copy(_pack.Path, target))
        {
            EmitFailed("Failed to copy the FTB App instance folder");
            return;
        }

        await CopySettings(cancellationToken);
    }

    private async Task CopySettings(CancellationToken cancellationToken)
    {
        EmitStatus("Copying settings...");
        SetProgress(2, 2);

        var instanceConfigPath = FileSystem.PathCombine(StagingPath, "instance.cfg");
        var instance = new MinecraftInstance(
            GlobalSettings ?? throw new InvalidOperationException(
                "InstanceTask requires SetParentSettings before execution."),
            new Settings.IniSettingsObject(instanceConfigPath), StagingPath);

        instance.Settings().Set("InstanceType", "OneSix");
        instance.Settings().Set("totalTimePlayed", _pack.TotalPlayTime / 1000);

        if (_pack.JvmArgs.Length != 0)
        {
            instance.Settings().Set("OverrideJavaArgs", true);
            instance.Settings().Set("JvmArgs", _pack.JvmArgs);
        }

        var components = instance.GetPackProfile();
        components.BuildingFromScratch();
        await components.SetComponentVersion("net.minecraft", _pack.McVersion, important: true, cancellationToken);

        var modloader = _pack.LoaderType;
        if (modloader is not null)
        {
            switch (modloader.Value)
            {
                case ModLoaderType.NeoForge:
                    await components.SetComponentVersion("net.neoforged", _pack.LoaderVersion, important: true, cancellationToken);
                    break;
                case ModLoaderType.Forge:
                    await components.SetComponentVersion("net.minecraftforge", _pack.LoaderVersion, important: true, cancellationToken);
                    break;
                case ModLoaderType.Fabric:
                    await components.SetComponentVersion("net.fabricmc.fabric-loader", _pack.LoaderVersion, important: true, cancellationToken);
                    break;
                case ModLoaderType.Quilt:
                    await components.SetComponentVersion("org.quiltmc.quilt-loader", _pack.LoaderVersion, important: true, cancellationToken);
                    break;
                default:
                    break;
            }
        }
        components.SaveNow();

        instance.SetName(Name());
        instance.SetIconKey(Icon == "default" ? "ftb_logo" : Icon);

        // (upstream calls downloadFiles(m_instance.get()) here — the P5 update pipeline;
        //  like the other creation tasks, creation succeeds without fetching game files)
        await Task.CompletedTask;
        EmitSucceeded();
    }
}
