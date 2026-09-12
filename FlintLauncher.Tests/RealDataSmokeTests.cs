// SPDX-License-Identifier: GPL-3.0-only
// Manual verification against a real PrismLauncher data directory (P3 exit criterion).
// Read-only: skips silently when the directory is absent, never writes to the data dir.
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Settings;
using Xunit;

namespace Flint.Tests;

public class RealDataSmokeTests
{
    private const string DataDir = "/Users/ghostflyby/Library/Application Support/PrismLauncher";

    private static bool DataDirExists() => Directory.Exists(Path.Combine(DataDir, "instances"));

    [Fact]
    public void Enumerate_Real_Instances_And_Read_Settings()
    {
        if (!DataDirExists())
        {
            return; // no real data dir on this machine (CI) — nothing to verify
        }

        // read-only view of the real global config (never saved back)
        var global = new IniSettingsObject(Path.Combine(DataDir, "prismlauncher.cfg"));
        // the C++ application registers the global Setting objects in Application::registerSettings();
        // replicate that minimal registration set so instance ctors can resolve overrides.
        RegisterGlobalDefaults(global);

        var instancesDir = Path.Combine(DataDir, "instances");
        var found = 0;
        var withProfile = 0;
        foreach (var dir in Directory.EnumerateDirectories(instancesDir))
        {
            var cfg = Path.Combine(dir, "instance.cfg");
            if (!File.Exists(cfg))
            {
                continue; // instgroups.json and co.
            }
            found++;
            var settings = new IniSettingsObject(cfg);
            var instance = new MinecraftInstance(global, settings, dir);
            Assert.Equal("OneSix", instance.InstanceType());
            Assert.False(string.IsNullOrEmpty(instance.Uuid));
            Assert.NotNull(instance.GameRoot());

            var mmcPack = Path.Combine(dir, "mmc-pack.json");
            if (File.Exists(mmcPack))
            {
                var profile = instance.GetPackProfile();
                profile.Load();
                Assert.True(profile.Components.Count > 0, $"{dir}: no components loaded");
                var mcVersion = profile.GetComponentVersion("net.minecraft");
                Assert.False(string.IsNullOrEmpty(mcVersion));
                withProfile++;
            }
        }
        Assert.True(found >= 3, $"expected several instances, found {found}");
        Assert.True(withProfile >= 3, $"expected several with components, got {withProfile}");
    }

    /// <summary>The global settings Application::registerSettings() would have registered.</summary>
    private static void RegisterGlobalDefaults(SettingsObject global)
    {
        string[] plain =
        [
            "ShowGameTime", "RecordGameTime", "PreLoadCommand", "PreLaunchCommand", "WrapperCommand",
            "PostExitCommand", "ShowConsole", "AutoCloseConsole", "ShowConsoleOnError", "LogPrePostOutput",
            "ConsoleMaxLines", "ConsoleOverflowStop", "JavaPath", "JvmArgs", "IgnoreJavaCompatibility",
            "JavaSignature", "JavaArchitecture", "JavaRealArchitecture", "JavaVersion", "JavaVendor",
            "LaunchMaximized", "MinecraftWinWidth", "MinecraftWinHeight", "MinMemAlloc", "MaxMemAlloc",
            "PermGen", "LowMemWarning", "UseNativeOpenAL", "CustomOpenALPath", "UseNativeGLFW",
            "CustomGLFWPath", "UseNativeSDL", "CustomSDLPath", "EnableFeralGamemode", "EnableMangoHud",
            "UseDiscreteGpu", "UseZink", "CloseAfterLaunch", "QuitAfterGameStop", "OnlineFixes", "Env",
            "AutomaticJavaSwitch",
        ];
        foreach (var key in plain)
        {
            if (global.GetSetting(key) is null)
            {
                global.RegisterSetting(key);
            }
        }
    }
}
