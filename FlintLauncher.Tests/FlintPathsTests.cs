// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Five-root layout tests: the home-layout factory, the explicit named
// factory, the three-layer flag resolution (precise > home > platform default, each with
// its env), the --data-dir semantics narrowing, the fail-fast probe, and the logs
// anchor with the macOS exception. Env-dependent cases snapshot and restore the process
// environment (the suite runs with parallelMode none).

using FlintLauncher.Core;
using FlintLauncher.Web;
using TruePath;
using TruePath.SystemIo;
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
        foreach (var name in (string[])
                 [
                     "FLINT_HOME", "FLINT_CONFIG_DIR", "FLINT_DATA_DIR",
                     "FLINT_STORE_DIR", "FLINT_CACHE_DIR", "FLINT_STATE_DIR",
                 ])
        {
            if (!_savedEnv.ContainsKey(name))
            {
                _savedEnv[name] = Environment.GetEnvironmentVariable(name);
            }

            Environment.SetEnvironmentVariable(name, null);
        }
    }

    // ------------------------------------------------------------------ home layout

    [Fact]
    public void FromHomeDir_Is_The_Home_Layout_Five_Roots_As_Children()
    {
        AbsolutePath home = Temporary.SystemTempDirectory() / $"flint-home-{Guid.NewGuid():N}";

        var paths = FlintPaths.FromHomeDir(home);

        Assert.Equal(home / "config", paths.ConfigRoot);
        Assert.Equal(home / "data", paths.DataRoot);
        Assert.Equal(home / "store", paths.StoreRoot);
        Assert.Equal(home / "cache", paths.CacheRoot);
        Assert.Equal(home / "state", paths.StateRoot);
        // the DataDir compat alias IS the data root
        Assert.Equal(paths.DataRoot, paths.DataDir);
    }

    [Fact]
    public void Anchors_Follow_Their_Five_Root_Owners_And_Logs_The_State_Root()
    {
        var paths = FlintPaths.FromHomeDir(new AbsolutePath("/tmp/flint-anchor-probe"));

        // data-root anchors (the user-owned half, DATA §2)
        Assert.Equal(paths.DataRoot / "instances", paths.InstancesDir);
        Assert.Equal(paths.DataRoot / "accounts.json", paths.AccountsFile);
        // the group definitions moved to the config side (members are uuid refs);
        // the data-root instgroups.json anchor survives only as the legacy import fallback
        Assert.Equal(paths.ConfigRoot / "groups.json", paths.GroupsFile);
        Assert.Equal(paths.DataRoot / "instgroups.json", paths.InstgroupsFile);
        // store anchors (shared installed content)
        Assert.Equal(paths.StoreRoot / "libraries", paths.LibrariesRoot);
        Assert.Equal(paths.StoreRoot / "assets", paths.AssetsRoot);
        Assert.Equal(paths.StoreRoot / "assets" / "indexes", paths.AssetIndexesDir);
        Assert.Equal(paths.StoreRoot / "java", paths.JavaRoot);
        // cache anchors (the ledger + the metadata content moved off the data root)
        Assert.Equal(paths.CacheRoot / "metacache.json", paths.MetacacheFile);
        Assert.Equal(paths.CacheRoot / "meta", paths.MetaDir);
        Assert.Equal(paths.CacheRoot / "fmllibs", paths.FmllibsCacheDir);
        // the logger anchor moved to the state root
        Assert.Equal(paths.StateRoot / "logs", paths.LogsDir);
        Assert.Equal(paths.StateRoot / "migration", paths.MigrationDir);
        Assert.Equal(paths.StateRoot / "launcher-state.json", paths.LauncherStateFile);
    }

    [Fact]
    public void FromRoots_Keeps_The_Five_Roots_Independently()
    {
        // the roots are compared as the AbsolutePath instances they came from — a literal
        // POSIX string would not survive windows normalization
        var config = new AbsolutePath("/c");
        var data = new AbsolutePath("/d");
        var store = new AbsolutePath("/s");
        var cache = new AbsolutePath("/k");
        var state = new AbsolutePath("/st");
        var paths = new FlintPaths()
        {
            ConfigRoot = config,
            DataRoot = data,
            StoreRoot = store,
            CacheRoot = cache,
            StateRoot = state,
        };

        Assert.Equal(config, paths.ConfigRoot);
        Assert.Equal(data, paths.DataRoot);
        Assert.Equal(store, paths.StoreRoot);
        Assert.Equal(cache, paths.CacheRoot);
        Assert.Equal(state, paths.StateRoot);
    }

    // ------------------------------------------------------------------- logs anchor

    [Fact]
    public void LogsDir_Pins_To_The_Injected_State_Root_Even_On_MacOS()
    {
        var paths = new FlintPaths()
        {
            ConfigRoot = new AbsolutePath("/c"),
            DataRoot = new AbsolutePath("/d"),
            StoreRoot = new AbsolutePath("/s"),
            CacheRoot = new AbsolutePath("/k"),
            StateRoot = new AbsolutePath("/my/state"),
        };

        Assert.Equal(new AbsolutePath("/my/state") / "logs", paths.LogsDir);
    }

    [Fact]
    public void LogsDir_Platform_Default_State_Root_On_MacOS_Exceptions_To_Library_Logs()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return; // the exception only exists in the macOS platform derivation
        }

        AbsolutePath stateRoot = FlintPaths.PlatformDefaultStateRoot();
        var paths = new FlintPaths()
        {
            ConfigRoot = new AbsolutePath("/c"),
            DataRoot = new AbsolutePath("/d"),
            StoreRoot = new AbsolutePath("/s"),
            CacheRoot = new AbsolutePath("/k"),
            StateRoot = stateRoot,
        };

        AbsolutePath home = new AbsolutePath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        Assert.Equal(home / "Library" / "Logs" / "Flint", paths.LogsDir);
    }

    // --------------------------------------------------------------------- fail-fast

    [Fact]
    public void EnsureUsable_Creates_All_Five_Roots()
    {
        // re-point at fresh (nonexistent) children to prove the EnsureDir half
        AbsolutePath baseDir = Temporary.SystemTempDirectory() / $"flint-ensure-{Guid.NewGuid():N}";
        AbsolutePath configRoot = baseDir / "c";
        AbsolutePath dataRoot = baseDir / "d";
        AbsolutePath storeRoot = baseDir / "s";
        AbsolutePath cacheRoot = baseDir / "k";
        AbsolutePath stateRoot = baseDir / "st";
        var paths = new FlintPaths()
        {
            ConfigRoot = configRoot,
            DataRoot = dataRoot,
            StoreRoot = storeRoot,
            CacheRoot = cacheRoot,
            StateRoot = stateRoot,
        };

        paths.EnsureUsable();

        foreach (var root in new[]
                 {
                     paths.ConfigRoot, paths.DataRoot, paths.StoreRoot, paths.CacheRoot, paths.StateRoot,
                 })
        {
            Assert.True(root.ExistsDirectory(), root.Value);
        }

        baseDir.DeleteDirectoryRecursively();
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
        var unixMode = File.GetUnixFileMode(readOnly);
        File.SetUnixFileMode(readOnly,
            UnixFileMode.UserRead | UnixFileMode.UserExecute); // no write anywhere
        try
        {
            // config is probed first: the exception names it
            var paths = new FlintPaths()
            {
                ConfigRoot = new AbsolutePath(readOnly),
                DataRoot = new AbsolutePath("/d"),
                StoreRoot = new AbsolutePath("/s"),
                CacheRoot = new AbsolutePath("/k"),
                StateRoot = new AbsolutePath("/st"),
            };

            var error = Assert.Throws<InvalidOperationException>(paths.EnsureUsable);
            Assert.Contains("'config'", error.Message);
            Assert.Contains(readOnly, error.Message);
        }
        finally
        {
            File.SetUnixFileMode(readOnly, unixMode);
            Directory.Delete(readOnly, recursive: true);
        }
    }

    // ------------------------------------------------- three-layer resolution (WebHost)

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

        Assert.Equal(FlintPaths.FromHomeDir(new AbsolutePath(home)), paths);
    }

    [Fact]
    public void Resolver_Precise_Flag_Overrides_Only_Its_Own_Root()
    {
        ClearResolverEnv();
        var home = Path.Combine(Path.GetTempPath(), $"flint-res-home-{Guid.NewGuid():N}");
        var data = Path.Combine(Path.GetTempPath(), $"flint-res-data-{Guid.NewGuid():N}");

        var paths = WebHostBuilder.ResolveFlintPaths(["--home-dir", home, "--data-dir", data]);

        Assert.Equal(data, paths.DataRoot.Value);
        // zero sibling implication: the rest stay on the home layout
        Assert.Equal(Path.Combine(home, "config"), paths.ConfigRoot.Value);
        Assert.Equal(Path.Combine(home, "store"), paths.StoreRoot.Value);
        Assert.Equal(Path.Combine(home, "cache"), paths.CacheRoot.Value);
        Assert.Equal(Path.Combine(home, "state"), paths.StateRoot.Value);
    }

    [Fact]
    public void Resolver_Data_Dir_Is_Narrowed_To_The_Precise_Data_Root()
    {
        ClearResolverEnv();
        var data = Path.Combine(Path.GetTempPath(), $"flint-res-narrow-{Guid.NewGuid():N}");

        var paths = WebHostBuilder.ResolveFlintPaths(["--data-dir", data]);

        Assert.Equal(data, paths.DataRoot.Value);
        // deliberate exception: --data-dir no longer implies the other roots (the old total-directory
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

        Assert.Equal(second, paths.StateRoot.Value);
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
        Assert.Equal(envData, envOnly.DataRoot.Value);
        Assert.Equal(Path.Combine(home, "config"), envOnly.ConfigRoot.Value);

        // the CLI precise flag beats the env precise flag
        var withCli = WebHostBuilder.ResolveFlintPaths(["--data-dir", cliData]);
        Assert.Equal(cliData, withCli.DataRoot.Value);
        Assert.Equal(Path.Combine(home, "state"), withCli.StateRoot.Value);
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

        Assert.Equal(roots["FLINT_CONFIG_DIR"], paths.ConfigRoot.Value);
        Assert.Equal(roots["FLINT_DATA_DIR"], paths.DataRoot.Value);
        Assert.Equal(roots["FLINT_STORE_DIR"], paths.StoreRoot.Value);
        Assert.Equal(roots["FLINT_CACHE_DIR"], paths.CacheRoot.Value);
        Assert.Equal(roots["FLINT_STATE_DIR"], paths.StateRoot.Value);
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
        var homeRoot = fixture.HomeRoot?.Value;
        Assert.NotNull(homeRoot);
        var unixMode = File.GetUnixFileMode(fixture.Paths.ConfigRoot.Value);
        File.SetUnixFileMode(fixture.Paths.ConfigRoot.Value,
            UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() => WebHostBuilder
                .BuildWebApplication(["--home-dir", homeRoot]));
            Assert.Contains("'config'", error.Message);
        }
        finally
        {
            File.SetUnixFileMode(fixture.Paths.ConfigRoot.Value, unixMode);
        }
    }

    [Fact]
    public void BuildWebApplication_Starts_On_The_Home_Layout_And_Logs_Under_The_State_Root()
    {
        using var fixture = TestFlintPaths.UnderHome();
        var homeRoot = fixture.HomeRoot?.Value;
        Assert.NotNull(homeRoot);
        using var app = WebHostBuilder.BuildWebApplication(["--home-dir", homeRoot]);
        // the anchor: the rolling provider opened its file under <state>/logs before any
        // service ran, and the old data-root anchor is gone
        Assert.True((fixture.Paths.StateRoot / "logs").ExistsDirectory());
        Assert.True((fixture.Paths.StateRoot / "logs" / "Flint-0.log").ExistsFile());
        Assert.False((fixture.DataDir / "logs").ExistsDirectory());
    }
}
