// SPDX-License-Identifier: GPL-3.0-only
// The injectable filesystem model. The C++ application chdir'd into its data directory at
// startup and several subsystems addressed files with data-dir-relative literals ("meta",
// "assets/indexes", "instgroups.json", ...); the port inherited that via the process working
// directory. This record makes every one of those anchors explicit: the composition root
// builds one FlintPaths and threads it down (LauncherRuntime in the host, LaunchServices in
// the launch pipeline, constructor parameters elsewhere), so the process working directory
// is no longer load-bearing and multiple runtimes can coexist in one process (integration
// tests).
//
// Five independent roots (G1 / C1 / E1 / S1): config/data/store/cache/state, each injectable
// on its own (precise flags/env), or all at once via a home layout (--home-dir=X ⇒
// X/{config,data,store,cache,state} — the only implicit-derivation site). Platform defaults
// are absorbed HERE and nowhere else.

namespace FlintLauncher.Core;

/// <summary>Absolute filesystem layout of one Flint installation: the five independent roots
/// plus the derived anchors. Immutable; derived paths are computed so a rebuilt layout never
/// goes stale.</summary>
public sealed record FlintPaths
{
    public required string ConfigRoot { get; init; }
    public required string DataRoot { get; init; }
    public required string StoreRoot { get; init; }
    public required string CacheRoot { get; init; }
    public required string StateRoot { get; init; }

    /// <summary>The home directory a home-layout resolution started from (G1: --home-dir=X ⇒
    /// X/{config,data,store,cache,state}), or null for precise/platform resolutions. The
    /// flint-legacy startup probe checks it as a legacy source: pre-reform flat layouts sit
    /// at the home root (the old "total directory"), which the fresh X/data child cannot
    /// see.</summary>
    public string? HomeRoot { get; init; }

    /// <summary>Compat alias for <see cref="DataRoot"/> (the pre-five-root single-root name;
    /// the single-root-derives-everything form it anchored is retired, CONFIG C1 §2.5).</summary>
    public string DataDir => DataRoot;

    // ------------------------------------------------------------- explicit construction

    /// <summary>Explicit named factory: all five roots given independently (the precise-flag
    /// resolution shape, G1). Paths are made absolute.</summary>
    public static FlintPaths FromRoots(string configRoot, string dataRoot, string storeRoot, string cacheRoot, string stateRoot) => new()
    {
        ConfigRoot = Path.GetFullPath(configRoot),
        DataRoot = Path.GetFullPath(dataRoot),
        StoreRoot = Path.GetFullPath(storeRoot),
        CacheRoot = Path.GetFullPath(cacheRoot),
        StateRoot = Path.GetFullPath(stateRoot),
    };

    /// <summary>Home-layout factory (G1's only implicit-derivation site, and the embedded
    /// "one directory for everything" shape): the five roots are
    /// <paramref name="homeDir"/>'s config/data/store/cache/state children. Named for what
    /// the argument IS — a home, not a data dir: handing a bare data root here would bury
    /// everything one level down (data/config, data/data, …).</summary>
    public static FlintPaths FromHomeDir(string homeDir)
    {
        var home = Path.GetFullPath(homeDir);
        return new()
        {
            ConfigRoot = Path.Combine(home, "config"),
            DataRoot = Path.Combine(home, "data"),
            StoreRoot = Path.Combine(home, "store"),
            CacheRoot = Path.Combine(home, "cache"),
            StateRoot = Path.Combine(home, "state"),
            HomeRoot = home,
        };
    }

    // ---------------------------------------------------------------- platform defaults
    // G1/C1: the default derivation lives here and nowhere else. The data root keeps the
    // historical app-directory anchor (D1: existing installs stay where they are); the other
    // roots follow the platform convention (S1/E1), including macOS' logs exception (S4).

    /// <summary>User overrides (INI/JSON) and the groups file. Linux: $XDG_CONFIG_HOME/Flint
    /// (else ~/.config/Flint); macOS: ~/Library/Application Support/Flint/config; Windows:
    /// %APPDATA%/Flint/config.</summary>
    public static string PlatformDefaultConfigRoot()
    {
        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(AppSupportFlint(), "config");
        }

        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Flint", "config");
        }

        return Path.Combine(XdgBase("XDG_CONFIG_HOME", ".config"), "Flint");
    }

    /// <summary>User data (accounts, instances). D1 keeps the historical anchors: macOS
    /// ~/Library/Application Support/Flint; Windows %APPDATA%/Flint; Linux
    /// $XDG_DATA_HOME/Flint (else ~/.local/share/Flint).</summary>
    public static string PlatformDefaultDataRoot()
    {
        if (OperatingSystem.IsMacOS())
        {
            return AppSupportFlint();
        }

        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Flint");
        }

        return Path.Combine(XdgBase("XDG_DATA_HOME", ".local", "share"), "Flint");
    }

    /// <summary>Shared installed content (assets/libraries/java, E1/E2). Linux:
    /// $XDG_DATA_HOME/Flint/store; macOS: ~/Library/Application Support/Flint/store; Windows:
    /// %LOCALAPPDATA%/Flint/store (machine-local, not roaming).</summary>
    public static string PlatformDefaultStoreRoot()
    {
        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(AppSupportFlint(), "store");
        }

        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Flint", "store");
        }

        return Path.Combine(XdgBase("XDG_DATA_HOME", ".local", "share"), "Flint", "store");
    }

    /// <summary>True cache (rebuildable in seconds, E3). Linux: $XDG_CACHE_HOME/Flint (else
    /// ~/.cache/Flint); macOS: ~/Library/Caches/Flint; Windows: %LOCALAPPDATA%/Flint/cache.</summary>
    public static string PlatformDefaultCacheRoot()
    {
        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Caches", "Flint");
        }

        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Flint", "cache");
        }

        return Path.Combine(XdgBase("XDG_CACHE_HOME", ".cache"), "Flint");
    }

    /// <summary>Machine-local runtime state (S1). Linux: $XDG_STATE_HOME/Flint (else
    /// ~/.local/state/Flint); macOS: ~/Library/Application Support/Flint/state; Windows:
    /// %LOCALAPPDATA%/Flint/state (not roaming).</summary>
    public static string PlatformDefaultStateRoot()
    {
        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(AppSupportFlint(), "state");
        }

        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Flint", "state");
        }

        return Path.Combine(XdgBase("XDG_STATE_HOME", ".local", "state"), "Flint");
    }

    private static string AppSupportFlint() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "Application Support", "Flint");

    private static string XdgBase(string envVar, params string[] fallbackSegments)
    {
        var env = Environment.GetEnvironmentVariable(envVar);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(env)
            ? Path.Combine([home, .. fallbackSegments])
            : env;
    }

    // ------------------------------------------------------- config-root children (anchors)

    /// <summary>The user-layer config file (CONFIG C5: flint.json, the authoritative global
    /// settings source; the INI chain below is import-only read side).</summary>
    public string ConfigFile => Path.Combine(ConfigRoot, "flint.json");

    /// <summary>The instance group definitions file (CONFIG C8: the group-name → member
    /// set half of the old instgroups.json is USER CONFIG — syncable, so it lives on the
    /// config side next to flint.json as its own file; the machine-local "hidden"/collapse
    /// half went to launcher-state.json's ui section, STATE S5). The "groups.json" name is
    /// the C8 leftover resolved: a plain, self-describing sibling of flint.json. Members
    /// are instance UUID references (DATA D3); a legacy data-root instgroups.json (see
    /// <see cref="InstgroupsFile"/>) is read as the import fallback and promoted into this
    /// file on first load.</summary>
    public string GroupsFile => Path.Combine(ConfigRoot, "groups.json");

    // ------------------------------------------------------- data-root children (anchors)
    // The user-owned half of the layout (DATA §2): accounts/secrets/instances live under the
    // data root. The shared installed content and the true cache are remounted onto the
    // store/cache roots (CACHE E2/E3 — see the store/cache children below).

    /// <summary>The LEGACY instgroups.json anchor (data root). Since CONFIG C8 the group
    /// definitions live at <see cref="GroupsFile"/> (config side); this path survives only
    /// as the read-compat import fallback — Prism-imported installs carry instgroups.json
    /// here, and the group model promotes it into the canonical file on first load.</summary>
    public string InstgroupsFile => Path.Combine(DataRoot, "instgroups.json");

    /// <summary>Stored Mojang/MSA accounts.</summary>
    public string AccountsFile => Path.Combine(DataRoot, "accounts.json");

    /// <summary>The transitional secret store file (CONFIG C4: data/secrets.json, 0600;
    /// the <see cref="Core.Secrets.FileSecretStore"/> anchor).</summary>
    public string SecretsFile => Path.Combine(DataRoot, "secrets.json");

    /// <summary>Discovered/created instances.</summary>
    public string InstancesDir => Path.Combine(DataRoot, "instances");

    // ------------------------------------------------------- store-root children (E2)
    // Shared installed content ("download and install in one tree" — deleting it means the
    // feature is unusable and GBs re-download, unlike a cache which only gets slow). The
    // internal shapes stay contract-owned (Mojang objects / Maven layout); only the mount
    // root moved off the data root.

    /// <summary>Shared library store ("libraries" base; Maven layout).</summary>
    public string LibrariesRoot => Path.Combine(StoreRoot, "libraries");

    /// <summary>Asset store root (objects/, indexes/, virtual/ below).</summary>
    public string AssetsRoot => Path.Combine(StoreRoot, "assets");

    /// <summary>Downloaded asset index JSONs (the "asset_indexes" one-shot cache base).</summary>
    public string AssetIndexesDir => Path.Combine(AssetsRoot, "indexes");

    /// <summary>Auto-installed Java runtimes (the "java" store base; the JavaDir setting
    /// resolves relative to the STORE root since E2 — GLOBAL §3).</summary>
    public string JavaRoot => Path.Combine(StoreRoot, "java");

    // ------------------------------------------------------- cache-root children (E3)
    // True cache: seconds to rebuild, safe to auto-clean. The ledger and the metadata
    // content moved here from the data root; the ledger describes downloads across BOTH
    // store and cache (it stays a single file at the cache root, CACHE E3).

    /// <summary>Local metadata cache root (MetaEntity "meta" base).</summary>
    public string MetaDir => Path.Combine(CacheRoot, "meta");

    /// <summary>HttpMetaCache ledger (downloaded metadata/asset/library download state).
    /// The name is kept per CACHE E3 (用户决议 2); it describes both store and cache bases.</summary>
    public string MetacacheFile => Path.Combine(CacheRoot, "metacache.json");

    /// <summary>Legacy FML libraries download cache ("fmllibs" base).</summary>
    public string FmllibsCacheDir => Path.Combine(CacheRoot, "fmllibs");

    // ------------------------------------------------------- state-root children (anchors)

    /// <summary>Rolling diagnostics logs (S4): the logger anchor is StateRoot/logs — except
    /// on macOS, where the platform-default state root keeps them at ~/Library/Logs/Flint
    /// (platform convention wins; an explicitly injected state root pins logs under itself).
    /// Rolling 5 generations, per-line flush — only the anchor moved.</summary>
    public string LogsDir =>
        OperatingSystem.IsMacOS() &&
        SamePath(StateRoot, PlatformDefaultStateRoot())
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Logs", "Flint")
            : Path.Combine(StateRoot, "logs");

    /// <summary>Per-source migration decision records (S3: migration/&lt;source&gt;.json;
    /// consumed by the migration wiring, assembly step 5).</summary>
    public string MigrationDir => Path.Combine(StateRoot, "migration");

    /// <summary>The runtime-state single file (S5; consumed by the state assembly step).</summary>
    public string LauncherStateFile => Path.Combine(StateRoot, "launcher-state.json");

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // ------------------------------------------------------------------ install root

    /// <summary>Where the Flint binary itself lives (jars/, share/&lt;launcher&gt;/). The C++
    /// read APPLICATION->getJarPath against the install dir; tests point this at a fixture
    /// root. Defaults to the process base — an install-root concept, not a root-layout one.</summary>
    public string InstallRoot { get; init; } = AppContext.BaseDirectory;

    // ------------------------------------------------------------------- G1 fail-fast

    /// <summary>Startup discipline (G1): create each of the five roots in turn and verify it
    /// accepts a write. Any unusable root refuses startup with its name in the exception —
    /// no silent degradation, no half-assembled runtime. Explicit roots get no cross-root
    /// assumptions; implicit creation happens under a home because providing the home IS the
    /// provisioning declaration.</summary>
    public void EnsureUsable()
    {
        foreach (var (name, root) in new[]
                 {
                     ("config", ConfigRoot),
                     ("data", DataRoot),
                     ("store", StoreRoot),
                     ("cache", CacheRoot),
                     ("state", StateRoot),
                 })
        {
            try
            {
                Directory.CreateDirectory(root);
                var probe = Path.Combine(root, $".flint-write-probe-{Guid.NewGuid():N}");
                File.WriteAllText(probe, "");
                File.Delete(probe);
            }
            catch (Exception e)
            {
                throw new InvalidOperationException(
                    $"Flint '{name}' root at '{root}' is not usable: {e.Message}", e);
            }
        }
    }
}
