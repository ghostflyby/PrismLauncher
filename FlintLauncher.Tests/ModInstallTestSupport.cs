// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Shared helpers for the mod install/update flow tests (ResourceDownloadTask /
// GetModDependenciesTask / ModUpdateTask): the typed user layer (a real FlintConfigStore,
// seeded with the same values the former in-memory stub registered) and a builder that
// stamps a mc version + mod loader onto the instance's pack profile.

using FlintLauncher.Core.Config;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Minecraft;
using TruePath;

namespace FlintLauncher.Tests;

/// <summary>Instance + mod folder scaffolding for the install/update flow tests.</summary>
internal sealed class ModInstallTestEnv : IDisposable
{
    private readonly string _root;
    private readonly TestGlobalSettings _global = new();

    public ModInstallTestEnv(string root)
    {
        _root = root;
        Directory.CreateDirectory(root);
    }

    public MinecraftInstance CreateInstance(string mcVersion, ModLoaderType loader, string loaderVersion)
    {
        // the typed instance adapter over the env root (instance.json)
        var instance = new MinecraftInstance(
            new InstanceJsonSettingsObject(new AbsolutePath(_root), _global.Environment),
            new AbsolutePath(_root));
        _ = instance.Settings; // force the specific-settings load, like the C++ first touch
        var profile = instance.GetPackProfile();
        profile.AppendComponent(new Component(profile, "net.minecraft") { CachedVersion = mcVersion });
        profile.AppendComponent(new Component(profile, LoaderUid(loader)) { CachedVersion = loaderVersion });
        return instance;
    }

    public static string LoaderUid(ModLoaderType loader) => loader switch
    {
        ModLoaderType.Forge => "net.minecraftforge",
        ModLoaderType.NeoForge => "net.neoforged",
        ModLoaderType.Quilt => "org.quiltmc.quilt-loader",
        ModLoaderType.LiteLoader => "com.mumfrey.liteloader",
        _ => "net.fabricmc.fabric-loader",
    };

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException) { }
    }
}
