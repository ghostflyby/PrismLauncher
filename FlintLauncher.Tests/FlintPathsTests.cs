// SPDX-License-Identifier: GPL-3.0-only
// Five-root layout tests (G1/C1/E1/S1): the home-layout factory, the explicit named
// factory, the three-layer flag resolution (precise > home > platform default, each with
// its env), the --data-dir semantics narrowing, the G1 fail-fast probe, and the S4 logs
// anchor with the macOS exception. Env-dependent cases snapshot and restore the process
// environment (the suite runs with parallelMode none).
using FlintLauncher.Core;
using FlintLauncher.Web;
using Xunit;

namespace FlintLauncher.Tests;

public class FlintPathsTests : IDisposable
{
    private readonly Dictionary<string, string?> _savedEnv = [];

    public void Dispose()
    {
        foreach (var (name, value) in _savedEnv)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    /// <summary>Neutralizes the resolver's env inputs for the duration of a test (restored
    /// on dispose), so machine-level flags cannot skew the layer assertions.</summary>
    private void ClearResolverEnv()
    {
        foreach (var name in (string[])["FLINT_HOME", "FLINT_CONFIG_DIR", "FLINT_DATA_DIR",
                     "FLINT_STORE_DIR", "FLINT_CACHE_DIR", "FLINT_STATE_DIR"])
        {
            if (!_savedEnv.ContainsKey(name))
            {
                _savedEnv[name] = Environment.GetEnvironmentVariable(name);
            }
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    // ------------------------------------------------------------------ home layout (G1)

    [Fact]
    public void FromHomeDir_Is_The_Home_Layout_Five_Roots_As_Children()
    {
        var home = Path.Combine(Path.GetTempPath(), $"flint-home-{Guid.NewGuid():N}");

        var paths = FlintPaths.FromHomeDir(home);

        Assert.Equal(Path.Combine(home, "config"), paths.ConfigRoot);
        Assert.Equal(Path.Combine(home, "data"), paths.DataRoot);
        Assert.Equal(Path.Combine(home, "store"), paths.StoreRoot);
        Assert.Equal(Path.Combine(home, "cache"), paths.CacheRoot);
        Assert.Equal(Path.Combine(home, "state"), paths.StateRoot);
        // the DataDir compat alias IS the data root
        Assert.Equal(paths.DataRoot, paths.DataDir);
    }

    [Fact]
    public void Anchors_Follow_Their_Five_Root_Owners_And_Logs_The_State_Root()
    {
        var paths = FlintPaths.FromHomeDir("/tmp/flint-anchor-probe");

        // data-root anchors (the user-owned half, DATA §2)
        Assert.Equal(Path.Combine(paths.DataRoot, "instances"), paths.InstancesDir);
        Assert.Equal(Path.Combine(paths.DataRoot, "accounts.json"), paths.AccountsFile);
        // C8: the group definitions moved to the config side (members are uuid refs, D3);
        // the data-root instgroups.json anchor survives only as the legacy import fallback
        Assert.Equal(Path.Combine(paths.ConfigRoot, "groups.json"), paths.GroupsFile);
        Assert.Equal(Path.Combine(paths.DataRoot, "instgroups.json"), paths.InstgroupsFile);
        // store anchors (CACHE E2: shared installed content)
        Assert.Equal(Path.Combine(paths.StoreRoot, "libraries"), paths.LibrariesRoot);
        Assert.Equal(Path.Combine(paths.StoreRoot, "assets"), paths.AssetsRoot);
        Assert.Equal(Path.Combine(paths.StoreRoot, "assets", "indexes"), paths.AssetIndexesDir);
        Assert.Equal(Path.Combine(paths.StoreRoot, "java"), paths.JavaRoot);
        // cache anchors (CACHE E3: the ledger + the metadata content moved off the data root)
        Assert.Equal(Path.Combine(paths.CacheRoot, "metacache.json"), paths.MetacacheFile);
        Assert.Equal(Path.Combine(paths.CacheRoot, "meta"), paths.MetaDir);
        Assert.Equal(Path.Combine(paths.CacheRoot, "fmllibs"), paths.FmllibsCacheDir);
        // S4: the logger anchor moved to the state root
        Assert.Equal(Path.Combine(paths.StateRoot, "logs"), paths.LogsDir);
        Assert.Equal(Path.Combine(paths.StateRoot, "migration"), paths.MigrationDir);
        Assert.Equal(Path.Combine(paths.StateRoot, "launcher-state.json"), paths.LauncherStateFile);
    }

    [Fact]
    public void FromRoots_Keeps_The_Five_Roots_Independently()
    {
        var paths = FlintPaths.FromRoots("/c", "/d", "/s", "/k", "/st");

        Assert.Equal("/c", paths.ConfigRoot);
        Assert.Equal("/d", paths.DataRoot);
        Assert.Equal("/s", paths.StoreRoot);
        Assert.Equal("/k", paths.CacheRoot);
        Assert.Equal("/st", paths.StateRoot);
    }

    // ------------------------------------------------------------------- S4 logs anchor

    [Fact]
    public void LogsDir_Pins_To_The_Injected_State_Root_Even_On_MacOS()
    {
        var paths = FlintPaths.FromRoots("/c", "/d", "/s", "/k", "/my/state");

        Assert.Equal(Path.Combine("/my/state", "logs"), paths.LogsDir);
    }

    [Fact]
    public void LogsDir_Platform_Default_State_Root_On_MacOS_Exceptions_To_Library_Logs()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return; // the exception only exists in the macOS platform derivation
        }
        var paths = FlintPaths.FromRoots("/c", "/d", "/s", "/k",
            FlintPaths.PlatformDefaultStateRoot());

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(home, "Library", "Logs", "Flint"), paths.LogsDir);
    }

    // --------------------------------------------------------------------- G1 fail-fast

    [Fact]
    public void EnsureUsable_Creates_All_Five_Roots()
    {
        // re-point at fresh (nonexistent) children to prove the EnsureDir half
        var baseDir = Path.Combine(Path.GetTempPath(), $"flint-ensure-{Guid.NewGuid():N}");
        var paths = FlintPaths.FromRoots(
            Path.Combine(baseDir, "c"), Path.Combine(baseDir, "d"), Path.Combine(baseDir, "s"),
            Path.Combine(baseDir, "k"), Path.Combine(baseDir, "st"));

        paths.EnsureUsable();

        foreach (var root in new[]
                 {
                     paths.ConfigRoot, paths.DataRoot, paths.StoreRoot, paths.CacheRoot, paths.StateRoot,
                 })
        {
            Assert.True(Directory.Exists(root), root);
        }
        Directory.Delete(baseDir, recursive: true);
    }

    [Fact]
    public void EnsureUsable_Refuses_Startup_Naming_The_Unwritable_Root()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // posix chmod-based negative case
        }
        var readOnly = Path.Combine(Path.GetTempPath(), $"flint-ro-{Guid.NewGuid():N}");
        Directory.CreateDirectory(readOnly);
        var unixMode = System.IO.File.GetUnixFileMode(readOnly);
        System.IO.File.SetUnixFileMode(readOnly,
            UnixFileMode.UserRead | UnixFileMode.UserExecute); // no write anywhere
        try
        {
            // config is probed first: the exception names it
            var paths = FlintPaths.FromRoots(readOnly, "/d", "/s", "/k", "/st");

            var error = Assert.Throws<InvalidOperationException>(paths.EnsureUsable);
            Assert.Contains("'config'", error.Message);
            Assert.Contains(readOnly, error.Message);
        }
        finally
        {
            System.IO.File.SetUnixFileMode(readOnly, unixMode);
            Directory.Delete(readOnly, recursive: true);
        }
    }

    // ------------------------------------------------- G1 three-layer resolution (WebHost)

    [Fact]
    public void Resolver_With_No_Flags_Yields_The_Platform_Defaults()
    {
        ClearResolverEnv();

        var paths = WebHostBuilder.ResolveFlintPaths([]);

        Assert.Equal(FlintPaths.PlatformDefaultConfigRoot(), paths.ConfigRoot);
        Assert.Equal(FlintPaths.PlatformDefaultDataRoot(), paths.DataRoot);
        Assert.Equal(FlintPaths.PlatformDefaultStoreRoot(), paths.StoreRoot);
        Assert.Equal(FlintPaths.PlatformDefaultCacheRoot(), paths.CacheRoot);
        Assert.Equal(FlintPaths.PlatformDefaultStateRoot(), paths.StateRoot);
    }

    [Fact]
    public void Resolver_Home_Flag_Re_Anchors_All_Five_Roots()
    {
        ClearResolverEnv();
        var home = Path.Combine(Path.GetTempPath(), $"flint-res-home-{Guid.NewGuid():N}");

        var paths = WebHostBuilder.ResolveFlintPaths(["--home-dir", home]);

        Assert.Equal(FlintPaths.FromHomeDir(home), paths);
    }

    [Fact]
    public void Resolver_Precise_Flag_Overrides_Only_Its_Own_Root()
    {
        ClearResolverEnv();
        var home = Path.Combine(Path.GetTempPath(), $"flint-res-home-{Guid.NewGuid():N}");
        var data = Path.Combine(Path.GetTempPath(), $"flint-res-data-{Guid.NewGuid():N}");

        var paths = WebHostBuilder.ResolveFlintPaths(["--home-dir", home, "--data-dir", data]);

        Assert.Equal(data, paths.DataRoot);
        // zero sibling implication: the rest stay on the home layout
        Assert.Equal(Path.Combine(home, "config"), paths.ConfigRoot);
        Assert.Equal(Path.Combine(home, "store"), paths.StoreRoot);
        Assert.Equal(Path.Combine(home, "cache"), paths.CacheRoot);
        Assert.Equal(Path.Combine(home, "state"), paths.StateRoot);
    }

    [Fact]
    public void Resolver_Data_Dir_Is_Narrowed_To_The_Precise_Data_Root()
    {
        ClearResolverEnv();
        var data = Path.Combine(Path.GetTempPath(), $"flint-res-narrow-{Guid.NewGuid():N}");

        var paths = WebHostBuilder.ResolveFlintPaths(["--data-dir", data]);

        Assert.Equal(data, paths.DataRoot);
        // G1 破格: --data-dir no longer implies the other roots (the old total-directory
        // semantics); uncovered roots go to the platform defaults, NOT <data>/config etc.
        Assert.Equal(FlintPaths.PlatformDefaultConfigRoot(), paths.ConfigRoot);
        Assert.Equal(FlintPaths.PlatformDefaultStateRoot(), paths.StateRoot);
    }

    [Fact]
    public void Resolver_Equals_Forms_And_Last_Occurrence_Win()
    {
        ClearResolverEnv();
        var first = Path.Combine(Path.GetTempPath(), "flint-res-first");
        var second = Path.Combine(Path.GetTempPath(), "flint-res-second");

        var paths = WebHostBuilder.ResolveFlintPaths(
            ["--state-dir=" + first, "--state-dir", second]);

        Assert.Equal(second, paths.StateRoot);
    }

    [Fact]
    public void Resolver_Precise_Beats_Home_Beats_Env_Beats_Default()
    {
        ClearResolverEnv();
        var home = Path.Combine(Path.GetTempPath(), "flint-res-pre-home");
        var cliData = Path.Combine(Path.GetTempPath(), "flint-res-pre-cli");
        var envData = Path.Combine(Path.GetTempPath(), "flint-res-pre-env");

        // env home + env data: the env home re-anchors all five, the env data overrides one
        _savedEnv["FLINT_HOME"] = Environment.GetEnvironmentVariable("FLINT_HOME");
        _savedEnv["FLINT_DATA_DIR"] = Environment.GetEnvironmentVariable("FLINT_DATA_DIR");
        Environment.SetEnvironmentVariable("FLINT_HOME", home);
        Environment.SetEnvironmentVariable("FLINT_DATA_DIR", envData);
        var envOnly = WebHostBuilder.ResolveFlintPaths([]);
        Assert.Equal(envData, envOnly.DataRoot);
        Assert.Equal(Path.Combine(home, "config"), envOnly.ConfigRoot);

        // the CLI precise flag beats the env precise flag
        var withCli = WebHostBuilder.ResolveFlintPaths(["--data-dir", cliData]);
        Assert.Equal(cliData, withCli.DataRoot);
        Assert.Equal(Path.Combine(home, "state"), withCli.StateRoot);
    }

    [Fact]
    public void Resolver_Each_Precise_Flag_Has_Its_Own_Env()
    {
        ClearResolverEnv();
        var roots = new Dictionary<string, string>
        {
            ["FLINT_CONFIG_DIR"] = Path.Combine(Path.GetTempPath(), "flint-res-env-c"),
            ["FLINT_DATA_DIR"] = Path.Combine(Path.GetTempPath(), "flint-res-env-d"),
            ["FLINT_STORE_DIR"] = Path.Combine(Path.GetTempPath(), "flint-res-env-s"),
            ["FLINT_CACHE_DIR"] = Path.Combine(Path.GetTempPath(), "flint-res-env-k"),
            ["FLINT_STATE_DIR"] = Path.Combine(Path.GetTempPath(), "flint-res-env-st"),
        };
        foreach (var (name, value) in roots)
        {
            _savedEnv[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        var paths = WebHostBuilder.ResolveFlintPaths([]);

        Assert.Equal(roots["FLINT_CONFIG_DIR"], paths.ConfigRoot);
        Assert.Equal(roots["FLINT_DATA_DIR"], paths.DataRoot);
        Assert.Equal(roots["FLINT_STORE_DIR"], paths.StoreRoot);
        Assert.Equal(roots["FLINT_CACHE_DIR"], paths.CacheRoot);
        Assert.Equal(roots["FLINT_STATE_DIR"], paths.StateRoot);
    }

    [Fact]
    public void Resolver_FlagValue_Ignores_Blank_Env_And_Trailing_Flag_Without_Value()
    {
        ClearResolverEnv();
        _savedEnv["FLINT_STATE_DIR"] = Environment.GetEnvironmentVariable("FLINT_STATE_DIR");
        Environment.SetEnvironmentVariable("FLINT_STATE_DIR", "   ");

        // a dangling "--state-dir" at the end has no value and must not throw
        var paths = WebHostBuilder.ResolveFlintPaths(["--state-dir"]);

        Assert.Equal(FlintPaths.PlatformDefaultStateRoot(), paths.StateRoot);
    }

    // ----------------------------------------------------------------- host fail-fast

    [Fact]
    public void BuildWebApplication_Refuses_An_Unusable_Root_Naming_It()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // posix chmod-based negative case
        }
        using var fixture = TestFlintPaths.UnderHome();
        var unixMode = System.IO.File.GetUnixFileMode(fixture.Paths.ConfigRoot);
        System.IO.File.SetUnixFileMode(fixture.Paths.ConfigRoot,
            UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() => WebHostBuilder
                .BuildWebApplication(["--home-dir", fixture.HomeRoot!]));
            Assert.Contains("'config'", error.Message);
        }
        finally
        {
            System.IO.File.SetUnixFileMode(fixture.Paths.ConfigRoot, unixMode);
        }
    }

    [Fact]
    public void BuildWebApplication_Starts_On_The_Home_Layout_And_Logs_Under_The_State_Root()
    {
        using var fixture = TestFlintPaths.UnderHome();

        using var app = WebHostBuilder.BuildWebApplication(["--home-dir", fixture.HomeRoot!]);
        // the S4 anchor: the rolling provider opened its file under <state>/logs before any
        // service ran, and the old data-root anchor is gone
        Assert.True(Directory.Exists(Path.Combine(fixture.Paths.StateRoot, "logs")));
        Assert.True(System.IO.File.Exists(
            Path.Combine(fixture.Paths.StateRoot, "logs", "Flint-0.log")));
        Assert.False(Directory.Exists(Path.Combine(fixture.DataDir, "logs")));
    }
}
