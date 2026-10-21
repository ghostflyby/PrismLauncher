// SPDX-License-Identifier: GPL-3.0-only
// Port of modplatform/import_ftb/PackInstallTask.{h,cpp}: import a local FTB App instance
// — copy the modpack folder into staging/minecraft and rebuild the instance settings:
// InstanceType=OneSix, totalTimePlayed (from the ms totalPlayTime), the jvmArgs override,
// the net.minecraft component and the modloader component derived from the parsed loader.
//
// Deviation: downloadFiles() (the P5 update pipeline) is not ported; creation succeeds
// without fetching game files, like the other creation tasks.

using FlintLauncher.Core.Config;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Minecraft;
using TruePath;

namespace FlintLauncher.Core.ModPlatform.FtbApp;

public sealed class FtbAppPackInstallTask(FtbAppModpack pack) : InstanceTask
{
    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        EmitStatus("Copying files...");
        SetProgress(1, 2);

        var target = new AbsolutePath(StagingPath) / "minecraft";
        if (!FileOps.Copy(new AbsolutePath(pack.Path), target))
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

        // D4: the staged instance is Flint format from birth (typed instance.json adapter)
        var instance = new MinecraftInstance(
            new InstanceJsonSettingsObject(StagingPath, Environment ?? throw new InvalidOperationException(
                "InstanceTask requires SetParentEnvironment before execution.")),
            StagingPath);

        instance.Settings.Set("totalTimePlayed", pack.TotalPlayTime / 1000);

        if (pack.JvmArgs.Length != 0)
        {
            // C7: writing JvmArgs sets the instance-layer member, which IS the override (the
            // derived OverrideJavaArgs gate reads true; no separate gate write exists)
            instance.Settings.Set("JvmArgs", pack.JvmArgs);
        }

        var components = instance.GetPackProfile();
        components.BuildingFromScratch();
        await components.SetComponentVersion("net.minecraft", pack.McVersion, important: true, cancellationToken);

        var modloader = pack.LoaderType;
        if (modloader is not null)
        {
            switch (modloader.Value)
            {
                case ModLoaderType.NeoForge:
                    await components.SetComponentVersion("net.neoforged", pack.LoaderVersion, important: true, cancellationToken);
                    break;
                case ModLoaderType.Forge:
                    await components.SetComponentVersion("net.minecraftforge", pack.LoaderVersion, important: true, cancellationToken);
                    break;
                case ModLoaderType.Fabric:
                    await components.SetComponentVersion("net.fabricmc.fabric-loader", pack.LoaderVersion, important: true, cancellationToken);
                    break;
                case ModLoaderType.Quilt:
                    await components.SetComponentVersion("org.quiltmc.quilt-loader", pack.LoaderVersion, important: true, cancellationToken);
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
