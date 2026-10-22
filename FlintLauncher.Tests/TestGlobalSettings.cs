// SPDX-License-Identifier: GPL-3.0-only
// Shared test double for the user layer (flint.json): a REAL FlintConfigStore over a
// throwaway file, seeded with the same values the former in-memory SettingsObject stub
// registered — so the tests' expectations keep holding. StubSet is a typed store Set (it
// persists like production; the C5 importer's write path is the same).
//
// D4: the instance layer reads this store as the user layer of its instance.json stack, so
// the double converts implicitly to the InstanceEnvironment the instance ctors take (type
// infos from the host's FlintJsonContext — tests reference Web for it).

using System.Collections.Concurrent;
using FlintLauncher.Core.Config;
using FlintLauncher.Core.State;
using FlintLauncher.Web;
using TruePath;

namespace FlintLauncher.Tests;

internal sealed class TestGlobalSettings
{
    public FlintConfigStore Store { get; }

    public TestGlobalSettings()
    {
        Store = CreateStore();
        // the same seed set the former in-memory stub registered (the tests' expectations
        // were written against these — deliberately NOT the production defaults)
        StubSet("ShowGameTime", true);
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
        // NOTE: the java probe quintet is NOT seeded — launcher-state.json owns it (C9)
        StubSet("LaunchMaximized", false);
        StubSet("MinecraftWinWidth", 854);
        StubSet("MinecraftWinHeight", 480);
        StubSet("MinMemAlloc", 512);
        StubSet("MaxMemAlloc", 4096);
        StubSet("LowMemWarning", true);
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
    }

    /// <summary>A typed store.Set (persists to the throwaway flint.json, like production).</summary>
    public void StubSet(string key, object? value) => Store.Set(key, value);

    /// <summary>The instance-layer environment over this store (the D4 typed stack).</summary>
    public InstanceEnvironment Environment => new(Store,
        FlintJsonContext.Default.InstanceConfigFile, FlintJsonContext.Default.InstanceStateFile);

    public static implicit operator InstanceEnvironment(TestGlobalSettings settings) => settings.Environment;

    /// <summary>A throwaway user-layer store (fresh flint.json at the record defaults).</summary>
    public static FlintConfigStore CreateStore() =>
        new(Temporary.SystemTempDirectory() / $"flint-user-{Guid.NewGuid():N}" / "flint.json",
            FlintJsonContext.Default.FlintConfigFile);
}

/// <summary>The typed instance environment over NO user store (record defaults — the bare
/// fixture form) for tests that construct instances directly.</summary>
internal static class TestInstanceEnvironment
{
    public static InstanceEnvironment Default(FlintConfigStore? user = null) => new(user,
        FlintJsonContext.Default.InstanceConfigFile, FlintJsonContext.Default.InstanceStateFile);
}

/// <summary>A throwaway launcher-state store (the real one over a temp file) for tests that
/// wire <c>InstanceList.LauncherState</c> — the playtime hook since C9 retired the settings
/// keys. Created dirs are swept at process exit (tests get no per-store Dispose hook — the
/// store is handed straight to the instance list).</summary>
internal static class TestLauncherState
{
    private static readonly ConcurrentBag<string> Dirs = [];
    private static bool _sweepRegistered;

    public static LauncherStateStore Create()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"flint-state-{Guid.NewGuid():N}");
        lock (Dirs)
        {
            if (!_sweepRegistered)
            {
                _sweepRegistered = true;
                AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                {
                    foreach (var d in Dirs)
                    {
                        try
                        {
                            Directory.Delete(d, recursive: true);
                        }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                };
            }
        }

        Dirs.Add(dir);
        return new(new AbsolutePath(Path.Combine(dir, "launcher-state.json")),
            FlintJsonContext.Default.LauncherState);
    }
}
