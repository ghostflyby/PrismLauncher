// SPDX-License-Identifier: GPL-3.0-only
// Shared helpers for the mod install/update flow tests (ResourceDownloadTask /
// GetModDependenciesTask / ModUpdateTask): an instance-settings stub with every key the
// instance ctors expect (mirrors the private GlobalSettingsStub of InstanceTests) and a
// builder that stamps a mc version + mod loader onto the instance's pack profile.
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Settings;

namespace Flint.Tests;

/// <summary>Instance + mod folder scaffolding for the install/update flow tests.</summary>
internal sealed class ModInstallTestEnv : IDisposable
{
    private readonly string _root;
    private readonly string _cfgPath;
    private readonly ModInstallSettingsStub _global = new();

    public ModInstallTestEnv(string root)
    {
        _root = root;
        Directory.CreateDirectory(root);
        _cfgPath = Path.Combine(root, "instance.cfg");
    }

    public MinecraftInstance CreateInstance(string mcVersion, ModLoaderType loader, string loaderVersion)
    {
        var instance = new MinecraftInstance(_global, new IniSettingsObject(_cfgPath), _root);
        _ = instance.Settings(); // force the specific-settings load, like the C++ first touch
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
        catch (DirectoryNotFoundException)
        {
        }
    }
}

/// <summary>Global settings with every key the instance ctors expect (Application.cpp's set).</summary>
internal sealed class ModInstallSettingsStub : SettingsObject
{
    private readonly Dictionary<string, object?> _values = new();

    public ModInstallSettingsStub()
    {
        Register("ShowGameTime", true);
        Register("RecordGameTime", true);
        Register("PreLoadCommand", "");
        Register("PreLaunchCommand", "");
        Register("WrapperCommand", "");
        Register("PostExitCommand", "");
        Register("ShowConsole", false);
        Register("AutoCloseConsole", false);
        Register("ShowConsoleOnError", false);
        Register("LogPrePostOutput", true);
        Register("ConsoleMaxLines", 100000);
        Register("ConsoleOverflowStop", false);
        Register("JavaPath", "java");
        Register("JvmArgs", "");
        Register("IgnoreJavaCompatibility", false);
        Register("JavaSignature", "");
        Register("JavaArchitecture", "");
        Register("JavaRealArchitecture", "");
        Register("JavaVersion", "");
        Register("JavaVendor", "");
        Register("LaunchMaximized", false);
        Register("MinecraftWinWidth", 854);
        Register("MinecraftWinHeight", 480);
        Register("MinMemAlloc", 512);
        Register("MaxMemAlloc", 4096);
        Register("PermGen", 192);
        Register("LowMemWarning", true);
        Register("UseNativeOpenAL", false);
        Register("CustomOpenALPath", "");
        Register("UseNativeGLFW", false);
        Register("CustomGLFWPath", "");
        Register("UseNativeSDL", false);
        Register("CustomSDLPath", "");
        Register("EnableFeralGamemode", false);
        Register("EnableMangoHud", false);
        Register("UseDiscreteGpu", false);
        Register("UseZink", false);
        Register("CloseAfterLaunch", false);
        Register("QuitAfterGameStop", false);
        Register("OnlineFixes", false);
        Register("Env", "{}");
        Register("AutomaticJavaSwitch", false);
        Register("ModMetadataDisabled", false);
    }

    private void Register(string id, object? def) => RegisterSetting(id, def);

    protected override void ChangeSetting(Setting setting, object? value) => _values[setting.Id] = value;

    protected override void ResetSetting(Setting setting) => _values.Remove(setting.Id);

    internal override object? RetrieveValue(Setting setting) => _values.GetValueOrDefault(setting.Id);

    public override void SuspendSave()
    {
    }

    public override void ResumeSave()
    {
    }
}
