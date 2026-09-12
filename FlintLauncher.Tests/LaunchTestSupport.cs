// SPDX-License-Identifier: GPL-3.0-only
// Shared fixtures for the P5-b launch pipeline tests: a settings stub with every key the
// launch layer reads (mirroring InstanceTests' GlobalSettingsStub) and a MinecraftInstance
// factory that seeds a complete net.minecraft patch so PackProfile::getProfile() builds a
// LaunchProfile offline (no network, no real Mojang metadata).
using FlintLauncher.Core.Auth;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Settings;
using Xunit;

namespace FlintLauncher.Tests;

/// <summary>Global settings with every key the instance/launch layer expects.</summary>
public class GlobalLaunchSettingsStub : SettingsObject
{
    private readonly Dictionary<string, object?> _values = new();

    public GlobalLaunchSettingsStub()
    {
        RegisterSetting("ShowGameTime", true);
        RegisterSetting("ShowGameTimeWithoutDays", false);
        RegisterSetting("RecordGameTime", true);
        RegisterSetting("PreLoadCommand", "");
        RegisterSetting("PreLaunchCommand", "");
        RegisterSetting("WrapperCommand", "");
        RegisterSetting("PostExitCommand", "");
        RegisterSetting("ShowConsole", false);
        RegisterSetting("AutoCloseConsole", false);
        RegisterSetting("ShowConsoleOnError", false);
        RegisterSetting("LogPrePostOutput", true);
        RegisterSetting("ConsoleMaxLines", 100000);
        RegisterSetting("ConsoleOverflowStop", false);
        RegisterSetting("JavaPath", "java");
        RegisterSetting("JvmArgs", "");
        RegisterSetting("IgnoreJavaCompatibility", false);
        RegisterSetting("JavaSignature", "");
        RegisterSetting("JavaArchitecture", "");
        RegisterSetting("JavaRealArchitecture", "");
        RegisterSetting("JavaVersion", "");
        RegisterSetting("JavaVendor", "");
        RegisterSetting("LaunchMaximized", false);
        RegisterSetting("MinecraftWinWidth", 854);
        RegisterSetting("MinecraftWinHeight", 480);
        RegisterSetting("MinMemAlloc", 512);
        RegisterSetting("MaxMemAlloc", 4096);
        RegisterSetting("PermGen", 64);
        RegisterSetting("LowMemWarning", false);
        RegisterSetting("UseNativeOpenAL", false);
        RegisterSetting("CustomOpenALPath", "");
        RegisterSetting("UseNativeGLFW", false);
        RegisterSetting("CustomGLFWPath", "");
        RegisterSetting("UseNativeSDL", false);
        RegisterSetting("CustomSDLPath", "");
        RegisterSetting("EnableFeralGamemode", false);
        RegisterSetting("EnableMangoHud", false);
        RegisterSetting("UseDiscreteGpu", false);
        RegisterSetting("UseZink", false);
        RegisterSetting("CloseAfterLaunch", false);
        RegisterSetting("QuitAfterGameStop", false);
        RegisterSetting("OnlineFixes", false);
        RegisterSetting("Env", "{}");
        RegisterSetting("AutomaticJavaSwitch", false);
        RegisterSetting("AutomaticJavaDownload", false);
        RegisterSetting("ModMetadataDisabled", false);
        RegisterSetting("LastOfflinePlayerName", "");
    }

    public void StubSet(string id, object? value) => _values[id] = value;

    protected override void ChangeSetting(Setting setting, object? value) => _values[setting.Id] = value;

    protected override void ResetSetting(Setting setting) => _values.Remove(setting.Id);

    internal override object? RetrieveValue(Setting setting) => _values.GetValueOrDefault(setting.Id);

    public override void SuspendSave() { }

    public override void ResumeSave() { }
}

/// <summary>
/// A temp-dir instance with a seeded, offline-loadable net.minecraft patch (the "seeded
/// profile fixture" of the P5-b test plan).
/// </summary>
public sealed class LaunchFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"flint-launch-{Guid.NewGuid():N}");

    public GlobalLaunchSettingsStub Global { get; } = new();

    public LaunchFixture()
    {
        Directory.CreateDirectory(Root);
    }

    /// <summary>The default profile fixture: main class, minecraft arguments, a trait and assets.</summary>
    public const string DefaultPatchJson = """
        {
            "formatVersion": 1,
            "name": "Minecraft",
            "uid": "net.minecraft",
            "version": "1.20.1",
            "mainClass": "net.minecraft.client.main.Main",
            "type": "release",
            "assets": "5",
            "minecraftArguments": "--username ${auth_player_name} --version ${version_name} --gameDir ${game_directory} --assetsDir ${assets_root} --assetIndex ${assets_index_name} --uuid ${auth_uuid} --accessToken ${auth_access_token} --userType ${user_type} --userProperties ${user_properties}",
            "+traits": ["FirstThreadOnMacOS"]
        }
        """;

    public async Task<MinecraftInstance> CreateInstance(string? patchJson = DefaultPatchJson)
    {
        var instanceRoot = Path.Combine(Root, "inst");
        Directory.CreateDirectory(instanceRoot);
        if (patchJson is not null)
        {
            WriteInstanceFile(instanceRoot, "patches/net.minecraft.json", patchJson);
            WriteInstanceFile(instanceRoot, "mmc-pack.json",
                """
                {
                    "formatVersion": 1,
                    "components": [
                        { "important": true, "uid": "net.minecraft" }
                    ]
                }
                """);
        }
        var instance = new MinecraftInstance(Global, new IniSettingsObject(Path.Combine(instanceRoot, "instance.cfg")),
            instanceRoot);
        instance.GetPackProfile().Load();
        // the patch files are read by the component update task (PackProfile::reload)
        var result = await instance.GetPackProfile().ReloadAsync(FlintLauncher.Core.Net.NetMode.Offline);
        Assert.True(result.Ok, result.ErrorMessage);
        return instance;
    }

    public AuthSession OfflineSession(string playerName = "Steve")
    {
        var account = MinecraftAccount.CreateOffline(playerName);
        var session = new AuthSession();
        account.FillSession(session);
        return session;
    }

    public void WriteInstanceFile(string instanceRoot, string relativePath, string content)
    {
        var path = Path.Combine(instanceRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
