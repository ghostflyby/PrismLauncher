// SPDX-License-Identifier: GPL-3.0-only
// Shared fixtures for the P5-b launch pipeline tests: a settings stub with every key the
// launch layer reads (mirroring InstanceTests' GlobalSettingsStub) and a MinecraftInstance
// factory that seeds a complete net.minecraft patch so PackProfile::getProfile() builds a
// LaunchProfile offline (no network, no real Mojang metadata).

using FlintLauncher.Core.Auth;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Launch;
using FlintLauncher.Web;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlintLauncher.Tests;

/// <summary>The user layer for the launch tests: a REAL FlintConfigStore over the fixture's
/// config root, seeded with the same values the former in-memory stub registered (the tests'
/// expectations were written against these — deliberately NOT the production defaults).
/// StubSet is a typed store Set; the D4 instance stack reads this store as its user layer.</summary>
public class GlobalLaunchSettingsStub
{
    /// <summary>The fixture's typed user-layer store (flint.json under the fixture config root).</summary>
    public Core.Config.FlintConfigStore Store { get; }

    private readonly string _configDir;

    public GlobalLaunchSettingsStub()
    {
        _configDir = Path.Combine(Path.GetTempPath(), $"flint-user-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_configDir);
        Store = new Core.Config.FlintConfigStore(
            Path.Combine(_configDir, "flint.json"), FlintJsonContext.Default.FlintConfigFile);

        StubSet("ShowGameTime", true);
        StubSet("ShowGameTimeWithoutDays", false);
        StubSet("RecordGameTime", true);
        StubSet("PreLoadCommand", "");
        StubSet("PreLaunchCommand", "");
        StubSet("WrapperCommand", "");
        StubSet("PostExitCommand", "");
        StubSet("ShowConsole", false);
        StubSet("AutoCloseConsole", false);
        StubSet("ShowConsoleOnError", false);
        StubSet("LogPrePostOutput", true);
        StubSet("ConsoleMaxLines", 100000);
        StubSet("ConsoleOverflowStop", false);
        StubSet("JavaPath", "java");
        StubSet("JvmArgs", "");
        StubSet("IgnoreJavaCompatibility", false);
        // NOTE: the java probe quintet / LastOfflinePlayerName are NOT here — they live in
        // launcher-state.json since CONFIG C9 (seed through LaunchFixture.SeedJavaDetection)

        StubSet("LaunchMaximized", false);
        StubSet("MinecraftWinWidth", 854);
        StubSet("MinecraftWinHeight", 480);
        StubSet("MinMemAlloc", 512);
        StubSet("MaxMemAlloc", 4096);
        StubSet("LowMemWarning", false);
        StubSet("UseNativeOpenAL", false);
        StubSet("CustomOpenALPath", "");
        StubSet("UseNativeGLFW", false);
        StubSet("CustomGLFWPath", "");
        StubSet("UseNativeSDL", false);
        StubSet("CustomSDLPath", "");
        StubSet("EnableFeralGamemode", false);
        StubSet("EnableMangoHud", false);
        StubSet("UseDiscreteGpu", false);
        StubSet("UseZink", false);
        StubSet("CloseAfterLaunch", false);
        StubSet("QuitAfterGameStop", false);
        StubSet("OnlineFixes", false);
        StubSet("Env", "{}");
        StubSet("AutomaticJavaSwitch", false);
        StubSet("AutomaticJavaDownload", false);
        StubSet("ModMetadataDisabled", false);
    }

    public void StubSet(string id, object? value) => Store.Set(id, value);

    /// <summary>The typed instance environment over this store (the D4 instance.json stack).</summary>
    public Core.Config.InstanceEnvironment Environment =>
        new(Store, FlintJsonContext.Default.InstanceConfigFile, FlintJsonContext.Default.InstanceStateFile);

    public static implicit operator Core.Config.InstanceEnvironment(
        GlobalLaunchSettingsStub stub) => stub.Environment;
}

/// <summary>
/// A temp-dir instance with a seeded, offline-loadable net.minecraft patch (the "seeded
/// profile fixture" of the P5-b test plan).
/// </summary>
public sealed class LaunchFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"flint-launch-{Guid.NewGuid():N}");

    public GlobalLaunchSettingsStub Global { get; } = new();

    /// <summary>The fixture's five-root stamp (G1 home layout); disposed with the fixture.</summary>
    private readonly TestFlintPaths _paths = TestFlintPaths.UnderHome();

    /// <summary>The fixture's stamped meta cache (E6 fixture injection: the runtime cache the
    /// composition root owns, "meta" base under the fixture's cache root — the CWD-anchored
    /// static fallback is gone). Instances created here carry it, which also stamps their
    /// pack profiles' metadata index.</summary>
    public Core.Net.HttpMetaCache MetaCache { get; }

    /// <summary>The launcher runtime state store (S5) over the fixture's state root — launch
    /// fixtures seed the java probe cache / offline name through <see cref="SeedJavaDetection"/>
    /// or <see cref="State"/> directly.</summary>
    public Core.State.LauncherStateStore State { get; }

    public LaunchFixture()
    {
        Directory.CreateDirectory(Root);
        State = new Core.State.LauncherStateStore(
            Path.Combine(_paths.Paths.StateRoot, "launcher-state.json"),
            FlintJsonContext.Default.LauncherState);
        MetaCache = new Core.Net.HttpMetaCache(
            Path.Combine(_paths.Paths.CacheRoot, "metacache.json"));
        MetaCache.AddBase("meta", _paths.Paths.MetaDir);
        MetaCache.AddBase("libraries", _paths.Paths.LibrariesRoot);
        MetaCache.AddBase("asset_indexes", _paths.Paths.AssetIndexesDir);
    }

    /// <summary>Seed the S5 java probe cache for a configured java path so CheckJava trusts
    /// the binary without running the checker (the former settings-key seeding, C9): the
    /// stored signature matches ComputeJavaSignature(mtime, resolved-path).</summary>
    public void SeedJavaDetection(string javaPathSetting, long javaUnixTime, string version = "17.0.2",
        string architecture = "64", string realArchitecture = "arm64", string vendor = "fixture")
    {
        var key = MinecraftInstance.JavaDetectKey(javaPathSetting);
        State.Update(state => state with
        {
            Java = state.Java with
            {
                Detections = new Dictionary<string, Core.State.JavaDetectEntry>(
                    state.Java.Detections, StringComparer.Ordinal)
                {
                    [key] = new(
                        Core.Launch.Steps.CheckJava.ComputeJavaSignature(javaUnixTime, key),
                        architecture, realArchitecture, version, vendor),
                },
            },
        });
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

    /// <summary>
    /// LaunchServices for this fixture's data dir (headless: null logger unless the test
    /// attaches one, no global settings double — the IniSettingsObject cast in the core
    /// degrades to null, matching the former unset static providers). The optional
    /// installRoot stands in for the former JavaUtils.LauncherRootPath test hook (the
    /// jars/NewLaunch.jar lookup), xmlLogParsing for the former XmlParsingEnabled gate.
    /// </summary>
    public LaunchServices CreateServices(ILoggerFactory? loggerFactory = null, string? installRoot = null,
        bool xmlLogParsing = false)
    {
        return new LaunchServices
        {
            Paths = _paths.Paths with { InstallRoot = installRoot ?? AppContext.BaseDirectory },
            LoggerFactory = loggerFactory ?? NullLoggerFactory.Instance,
            LauncherState = State,
            XmlLogParsing = xmlLogParsing,
        };
    }

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
        // the typed instance layer (D4): the adapter over the fixture root folds the
        // fixture's user-layer store (Global) with the instance's own instance.json
        var instance = new MinecraftInstance(
            new Core.Config.InstanceJsonSettingsObject(
                instanceRoot, new Core.Config.InstanceEnvironment(
                    Global.Store, FlintJsonContext.Default.InstanceConfigFile,
                    FlintJsonContext.Default.InstanceStateFile)),
            instanceRoot)
        {
            // the composition-root stamp (InstanceList does the same in the host): the
            // CreateLaunchTask path builds its per-launch services from these, the launcher
            // runtime state carries the java probe cache (S5), and the fixture meta cache
            // (E6 fixture injection) feeds the metadata/library/asset-index downloads
            LaunchServices = CreateServices(),
            LauncherState = State,
            MetaCache = MetaCache,
        };
        instance.GetPackProfile().Load();
        // the patch files are read by the component update task (PackProfile::reload)
        var result = await instance.GetPackProfile().ReloadAsync(Core.Net.NetMode.Offline);
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
        MetaCache.Dispose();
        _paths.Dispose();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
