// SPDX-License-Identifier: GPL-3.0-only
// Tests for the BaseInstance/MinecraftInstance ports. Everything synthetic (temp dirs);
// instance.cfg persistence is exercised through the real QSettings-compatible INI layer.
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Settings;
using Xunit;

namespace Flint.Tests;

public class InstanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"flint-inst-{Guid.NewGuid():N}");
    private readonly GlobalSettingsStub _global = new();
    private readonly string _cfgPath;

    public InstanceTests()
    {
        Directory.CreateDirectory(_root);
        _cfgPath = Path.Combine(_root, "instance.cfg");
    }

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

    private MinecraftInstance CreateInstance()
    {
        var settings = new IniSettingsObject(_cfgPath);
        return new MinecraftInstance(_global, settings, _root);
    }

    [Fact]
    public void Defaults_MatchUpstream()
    {
        var instance = CreateInstance();
        Assert.Equal("Unnamed Instance", instance.Name());
        Assert.Equal("default", instance.IconKey());
        Assert.Equal("", instance.Notes());
        // uuid: QUuid::Id128 form = 32 hex digits
        Assert.Equal(32, instance.Uuid.Length);
        Assert.True(instance.Uuid.All(char.IsAsciiHexDigit));
        Assert.Equal(Path.GetFileName(_root), instance.Id());
    }

    [Fact]
    public void NameIconNotes_PersistToInstanceCfg()
    {
        var instance = CreateInstance();
        instance.SetName("My Test");
        instance.SetIconKey("custom-icon");
        instance.SetNotes("some notes\nwith a newline");
        instance.SaveNow();

        var reloaded = new MinecraftInstance(_global, new IniSettingsObject(_cfgPath), _root);
        Assert.Equal("My Test", reloaded.Name());
        Assert.Equal("custom-icon", reloaded.IconKey());
        Assert.Equal("some notes\nwith a newline", reloaded.Notes());

        var text = File.ReadAllText(_cfgPath);
        Assert.Contains("name=My Test", text);
    }

    [Fact]
    public void InstanceType_ForcedToOneSix()
    {
        var instance = CreateInstance();
        // loadSpecificSettings runs lazily through Settings()
        _ = instance.Settings();
        Assert.Equal("OneSix", instance.InstanceType());
        Assert.Contains("InstanceType=OneSix", File.ReadAllText(_cfgPath));
    }

    [Fact]
    public void CommandOverrides_ResolveThroughGate()
    {
        _global.Set("PreLaunchCommand", "echo global");
        var instance = CreateInstance();

        // gate off -> global value
        Assert.Equal("echo global", instance.GetPreLaunchCommand());

        // gate on -> local value
        instance.Settings().Set("OverrideCommands", true);
        instance.Settings().Set("PreLaunchCommand", "echo local");
        Assert.Equal("echo local", instance.GetPreLaunchCommand());

        instance.SaveNow();
        var text = File.ReadAllText(_cfgPath);
        Assert.Contains("OverrideCommands=true", text);
        Assert.Contains("PreLaunchCommand=echo local", text);
    }

    [Fact]
    public void MemoryOverride_GatedWrite()
    {
        var instance = CreateInstance();
        // gate off -> global default
        Assert.Equal(4096, Variant.AsInt(instance.Settings().Get("MaxMemAlloc")));

        instance.Settings().Set("OverrideMemory", true);
        instance.Settings().Set("MaxMemAlloc", 8192);
        instance.SaveNow();

        Assert.Equal(8192, Variant.AsInt(instance.Settings().Get("MaxMemAlloc")));
        Assert.Contains("MaxMemAlloc=8192", File.ReadAllText(_cfgPath));
    }

    [Fact]
    public void GameRoot_PrefersMinecraftDir_UsesDotMinecraftFallback()
    {
        var instance = CreateInstance();
        // nothing exists yet -> planned "minecraft" layout
        Assert.Equal(FileSystemPathCombine(_root, "minecraft"), instance.GameRoot());

        // legacy layout: only .minecraft exists
        Directory.CreateDirectory(Path.Combine(_root, ".minecraft"));
        Assert.Equal(FileSystemPathCombine(_root, ".minecraft"), instance.GameRoot());
    }

    [Fact]
    public void PathAccessors_MatchUpstreamComposition()
    {
        var instance = CreateInstance();
        var gameRoot = instance.GameRoot();

        Assert.Equal(FileSystemPathCombine(gameRoot, "mods"), instance.ModsRoot());
        Assert.Equal(FileSystemPathCombine(gameRoot, "coremods"), instance.CoreModsDir());
        Assert.Equal(FileSystemPathCombine(gameRoot, "resourcepacks"), instance.ResourcePacksDir());
        Assert.Equal(FileSystemPathCombine(gameRoot, "shaderpacks"), instance.ShaderPacksDir());
        Assert.Equal(FileSystemPathCombine(gameRoot, "bin"), instance.BinRoot());
        // these live under the instance root, not gameRoot
        Assert.Equal(FileSystemPathCombine(_root, "jarmods"), instance.JarModsDir());
        Assert.Equal(FileSystemPathCombine(_root, "natives"), instance.GetNativePath());
        Assert.Equal(FileSystemPathCombine(_root, "libraries"), instance.GetLocalLibraryPath());
    }

    [Fact]
    public void PackProfile_WiresUpComponents()
    {
        File.WriteAllText(
            Path.Combine(_root, "mmc-pack.json"),
            """
            {
                "formatVersion": 1,
                "components": [
                    {
                        "important": true,
                        "uid": "net.minecraft",
                        "version": "1.20.1"
                    },
                    {
                        "uid": "org.quiltmc.foo",
                        "version": "1.0.0"
                    }
                ]
            }
            """);

        var instance = CreateInstance();
        var profile = instance.GetPackProfile();
        profile.Load();

        var uids = profile.Components.Select(c => c.Uid).ToList();
        Assert.Contains("net.minecraft", uids);
        Assert.Contains("org.quiltmc.foo", uids);
    }

    [Fact]
    public void Playtime_AccumulatesOnRunningToggle()
    {
        var instance = CreateInstance();
        Assert.Equal(0, instance.TotalTimePlayed());

        instance.SetMinecraftRunning(true);
        Assert.Equal(0, instance.TotalTimePlayed()); // reset base while running
        Assert.True(instance.LastLaunch() > 0);

        instance.SetMinecraftRunning(false);
        // ~0 seconds elapsed, but the keys must have been written
        Assert.True(instance.TotalTimePlayed() >= 0);
        Assert.True(Variant.AsLong(instance.Settings().Get("lastTimePlayed")) >= 0);
    }

    [Fact]
    public void Playtime_SkippedWhenRecordingDisabled()
    {
        _global.Set("RecordGameTime", false);
        var instance = CreateInstance();
        instance.SetMinecraftRunning(true);
        instance.SetMinecraftRunning(false);
        // RecordGameTime=false: no bookkeeping at all (upstream returns early)
        Assert.Equal(0, instance.LastLaunch());
    }

    [Fact]
    public void ManagedPack_SettersAndAutomaticJavaReset()
    {
        var instance = CreateInstance();
        instance.Settings().Set("AutomaticJava", true);
        instance.Settings().Set("OverrideJavaLocation", true);
        _global.Set("AutomaticJavaSwitch", true);

        instance.SetManagedPack("modrinth", "id1", "Pack", "vid", "1.0.0");

        Assert.True(instance.IsManagedPack());
        Assert.Equal("modrinth", instance.GetManagedPackType());
        Assert.Equal("id1", instance.GetManagedPackID());
        // upstream: managed-pack adoption clears a local AutomaticJava override; with the gate
        // off, JavaPath resolves through to the global setting again.
        Assert.False(Variant.AsBool(instance.Settings().Get("OverrideJavaLocation")));
        Assert.Equal("java", Variant.AsString(instance.Settings().Get("JavaPath")));
    }

    [Fact]
    public void NullInstance_IsBrokenByDefault()
    {
        var settings = new IniSettingsObject(_cfgPath);
        var instance = new NullInstance(_global, settings, _root);
        Assert.True(instance.HasVersionBroken);
        Assert.False(instance.CanLaunch());
    }

    private static string FileSystemPathCombine(string a, string b) =>
        FlintLauncher.Core.IO.FileSystem.PathCombine(a, b).TrimEnd('/');

    /// <summary>Global settings with every key the instance ctors expect (Application.cpp's set).</summary>
    private sealed class GlobalSettingsStub : SettingsObject
    {
        private readonly Dictionary<string, object?> _values = new();

        public GlobalSettingsStub()
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
        }

        private void Register(string id, object? def) => RegisterSetting(id, def);

        public void Set(string id, object? value) => _values[id] = value;

        protected override void ChangeSetting(Setting setting, object? value) => _values[setting.Id] = value;

        protected override void ResetSetting(Setting setting) => _values.Remove(setting.Id);

        internal override object? RetrieveValue(Setting setting) => _values.GetValueOrDefault(setting.Id);

        public override void SuspendSave() { }

        public override void ResumeSave() { }
    }
}
