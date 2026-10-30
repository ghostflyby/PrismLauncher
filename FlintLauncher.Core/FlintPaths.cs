// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// The injectable filesystem model. The C++ application chdir'd into its data directory at
// startup and several subsystems addressed files with data-dir-relative literals ("meta",
// "assets/indexes", "instgroups.json", ...); the port inherited that via the process working
// directory. This record makes every one of those anchors explicit: the composition root
// builds one FlintPaths and threads it down (the host's WebHostBuilder, LaunchServices in
// the launch pipeline, constructor parameters elsewhere), so the process working directory
// is no longer load-bearing and multiple runtimes can coexist in one process (integration
// tests).
//
// Five independent roots: config/data/store/cache/state, each injectable
// on its own (precise flags/env), or all at once via a home layout (--home-dir=X ⇒
// X/{config,data,store,cache,state} — the only implicit-derivation site). Platform defaults
// are absorbed HERE and nowhere else.

using TruePath;
using TruePath.SystemIo;

namespace FlintLauncher.Core;

/// <summary>Absolute filesystem layout of one Flint installation: the five independent roots
/// plus the derived anchors. Immutable; derived paths are computed so a rebuilt layout never
/// goes stale.</summary>
public sealed record FlintPaths
{
    public required AbsolutePath ConfigRoot { get; init; }
    public required AbsolutePath DataRoot { get; init; }
    public required AbsolutePath StoreRoot { get; init; }
    public required AbsolutePath CacheRoot { get; init; }
    public required AbsolutePath StateRoot { get; init; }

    /// <summary>The home directory a home-layout resolution started from (--home-dir=X ⇒
    /// X/{config,data,store,cache,state}), or null for precise/platform resolutions. The
    /// flint-legacy startup probe checks it as a legacy source: pre-reform flat layouts sit
    /// at the home root (the old "total directory"), which the fresh X/data child cannot
    /// see.</summary>
    public AbsolutePath? HomeRoot { get; init; }

    /// <summary>Compat alias for <see cref="DataRoot"/> (the pre-five-root single-root name;
    /// the single-root-derives-everything form it anchored is retired).</summary>
    public AbsolutePath DataDir => DataRoot;


    /// <summary>Home-layout factory (the only implicit-derivation site, and the embedded
    /// "one directory for everything" shape): the five roots are
    /// <paramref name="homeDir"/>'s config/data/store/cache/state children. Named for what
    /// the argument IS — a home, not a data dir: handing a bare data root here would bury
    /// everything one level down (data/config, data/data, …).</summary>
    public static FlintPaths FromHomeDir(AbsolutePath homeDir)
    {
        var home = new AbsolutePath(homeDir);
        return new FlintPaths
        {
            ConfigRoot = home / "config",
            DataRoot = home / "data",
            StoreRoot = home / "store",
            CacheRoot = home / "cache",
            StateRoot = home / "state",
            HomeRoot = home,
        };
    }

    // ---------------------------------------------------------------- platform defaults
    // The default derivation lives here and nowhere else. The data root keeps the
    // historical app-directory anchor (existing installs stay where they are); the other
    // roots follow the platform convention, including macOS' logs exception.

    /// <summary>User overrides (INI/JSON) and the groups file. Linux: $XDG_CONFIG_HOME/Flint
    /// (else ~/.config/Flint); macOS: ~/Library/Application Support/Flint/config; Windows:
    /// %APPDATA%/Flint/config.</summary>
    public static AbsolutePath PlatformDefaultConfigRoot()
    {
        if (OperatingSystem.IsMacOS())
        {
            return AppSupportFlint() / "config";
        }

        if (OperatingSystem.IsWindows())
        {
            return new AbsolutePath(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)) /
                   "Flint" / "config";
        }

        return XdgBase("XDG_CONFIG_HOME") / ".config" / "Flint";
    }

    /// <summary>User data (accounts, instances). The historical anchors are kept: macOS
    /// ~/Library/Application Support/Flint; Windows %APPDATA%/Flint; Linux
    /// $XDG_DATA_HOME/Flint (else ~/.local/share/Flint).</summary>
    public static AbsolutePath PlatformDefaultDataRoot()
    {
        if (OperatingSystem.IsMacOS())
        {
            return AppSupportFlint();
        }

        if (OperatingSystem.IsWindows())
        {
            return new AbsolutePath(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)) / "Flint";
        }

        return XdgBase("XDG_DATA_HOME") / ".local" / "share" / "Flint";
    }

    /// <summary>Shared installed content (assets/libraries/java). Linux:
    /// $XDG_DATA_HOME/Flint/store; macOS: ~/Library/Application Support/Flint/store; Windows:
    /// %LOCALAPPDATA%/Flint/store (machine-local, not roaming).</summary>
    public static AbsolutePath PlatformDefaultStoreRoot()
    {
        if (OperatingSystem.IsMacOS())
        {
            return AppSupportFlint() / "store";
        }

        if (OperatingSystem.IsWindows())
        {
            return new AbsolutePath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)) /
                   "Flint" / "store";
        }

        return XdgBase("XDG_DATA_HOME") / ".local" / "share" / "Flint" / "store";
    }

    /// <summary>True cache (rebuildable in seconds). Linux: $XDG_CACHE_HOME/Flint (else
    /// ~/.cache/Flint); macOS: ~/Library/Caches/Flint; Windows: %LOCALAPPDATA%/Flint/cache.</summary>
    public static AbsolutePath PlatformDefaultCacheRoot()
    {
        if (OperatingSystem.IsMacOS())
        {
            return new AbsolutePath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) /
                   "Library" / "Caches" / "Flint";
        }

        if (OperatingSystem.IsWindows())
        {
            return new AbsolutePath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)) /
                   "Flint" / "cache";
        }

        return XdgBase("XDG_CACHE_HOME") / ".cache" / "Flint";
    }

    /// <summary>Machine-local runtime state. Linux: $XDG_STATE_HOME/Flint (else
    /// ~/.local/state/Flint); macOS: ~/Library/Application Support/Flint/state; Windows:
    /// %LOCALAPPDATA%/Flint/state (not roaming).</summary>
    public static AbsolutePath PlatformDefaultStateRoot()
    {
        if (OperatingSystem.IsMacOS())
        {
            return AppSupportFlint() / "state";
        }

        if (OperatingSystem.IsWindows())
        {
            return new AbsolutePath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)) /
                   "Flint" / "state";
        }

        return XdgBase("XDG_STATE_HOME") / ".local" / "state" / "Flint";
    }

    private static AbsolutePath AppSupportFlint() => new AbsolutePath(
                                                         Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) /
                                                     "Library" / "Application Support" / "Flint";

    // TODO: fix the xdg fallback logic
    private static AbsolutePath XdgBase(string envVar)
    {
        var env = Environment.GetEnvironmentVariable(envVar);
        return string.IsNullOrWhiteSpace(env)
            ? new AbsolutePath()
            : new AbsolutePath(env);
    }

    // ------------------------------------------------------- config-root children (anchors)

    /// <summary>The user-layer config file (flint.json, the authoritative global
    /// settings source; the INI chain below is import-only read side).</summary>
    public AbsolutePath ConfigFile => ConfigRoot / "flint.json";

    /// <summary>The instance group definitions file (the group-name → member
    /// set half of the old instgroups.json is USER CONFIG — syncable, so it lives on the
    /// config side next to flint.json as its own file; the machine-local "hidden"/collapse
    /// half went to launcher-state.json's ui section). The "groups.json" name is
    /// settled: a plain, self-describing sibling of flint.json. Members
    /// are instance UUID references; a legacy data-root instgroups.json (see
    /// <see cref="InstgroupsFile"/>) is read as the import fallback and promoted into this
    /// file on first load.</summary>
    public AbsolutePath GroupsFile => ConfigRoot / "groups.json";

    // ------------------------------------------------------- data-root children (anchors)
    // The user-owned half of the layout: accounts/secrets/instances live under the
    // data root. The shared installed content and the true cache are remounted onto the
    // store/cache roots (see the store/cache children below).

    /// <summary>The LEGACY instgroups.json anchor (data root). The group
    /// definitions live at <see cref="GroupsFile"/> (config side); this path survives only
    /// as the read-compat import fallback — Prism-imported installs carry instgroups.json
    /// here, and the group model promotes it into the canonical file on first load.</summary>
    public AbsolutePath InstgroupsFile => DataRoot / "instgroups.json";

    /// <summary>Stored Mojang/MSA accounts.</summary>
    public AbsolutePath AccountsFile => DataRoot / "accounts.json";

    /// <summary>The transitional secret store file (data/secrets.json, 0600;
    /// the <see cref="Core.Secrets.FileSecretStore"/> anchor).</summary>
    public AbsolutePath SecretsFile => DataRoot / "secrets.json";

    /// <summary>Discovered/created instances.</summary>
    public AbsolutePath InstancesDir => DataRoot / "instances";

    // ------------------------------------------------------- store-root children
    // Shared installed content ("download and install in one tree" — deleting it means the
    // feature is unusable and GBs re-download, unlike a cache which only gets slow). The
    // internal shapes stay contract-owned (Mojang objects / Maven layout); only the mount
    // root moved off the data root.

    /// <summary>Shared library store ("libraries" base; Maven layout).</summary>
    public AbsolutePath LibrariesRoot => StoreRoot / "libraries";

    /// <summary>Asset store root (objects/, indexes/, virtual/ below).</summary>
    public AbsolutePath AssetsRoot => StoreRoot / "assets";

    /// <summary>Downloaded asset index JSONs (the "asset_indexes" one-shot cache base).</summary>
    public AbsolutePath AssetIndexesDir => AssetsRoot / "indexes";

    /// <summary>Auto-installed Java runtimes (the "java" store base; the JavaDir setting
    /// resolves relative to the store root).</summary>
    public AbsolutePath JavaRoot => StoreRoot / "java";

    // ------------------------------------------------------- cache-root children
    // True cache: seconds to rebuild, safe to auto-clean. The ledger and the metadata
    // content moved here from the data root; the ledger describes downloads across BOTH
    // store and cache (it stays a single file at the cache root).

    /// <summary>Local metadata cache root (MetaEntity "meta" base).</summary>
    public AbsolutePath MetaDir => CacheRoot / "meta";

    /// <summary>HttpMetaCache ledger (downloaded metadata/asset/library download state).
    /// The name is kept as-is; it describes both store and cache bases.</summary>
    public AbsolutePath MetacacheFile => CacheRoot / "metacache.json";

    /// <summary>Legacy FML libraries download cache ("fmllibs" base).</summary>
    public AbsolutePath FmllibsCacheDir => CacheRoot / "fmllibs";

    // ------------------------------------------------------- state-root children (anchors)

    /// <summary>Rolling diagnostics logs: the logger anchor is StateRoot/logs — except
    /// on macOS, where the platform-default state root keeps them at ~/Library/Logs/Flint
    /// (platform convention wins; an explicitly injected state root pins logs under itself).
    /// Rolling 5 generations, per-line flush — only the anchor moved.</summary>
    public AbsolutePath LogsDir
    {
        get
        {
            AbsolutePath b = PlatformDefaultStateRoot();
            return OperatingSystem.IsMacOS() &&
                   StateRoot == b
                ? new AbsolutePath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) /
                  "Library" / "Logs" / "Flint"
                : StateRoot / "logs";
        }
    }

    /// <summary>Per-source migration decision records (migration/&lt;source&gt;.json;
    /// consumed by the migration wiring at startup).</summary>
    public AbsolutePath MigrationDir => StateRoot / "migration";

    /// <summary>The runtime-state single file (launcher-state.json).</summary>
    public AbsolutePath LauncherStateFile => StateRoot / "launcher-state.json";

    // ------------------------------------------------------------------ install root

    /// <summary>Where the Flint binary itself lives (jars/, share/&lt;launcher&gt;/). The C++
    /// read APPLICATION->getJarPath against the install dir; tests point this at a fixture
    /// root. Defaults to the process base — an install-root concept, not a root-layout one.</summary>
    public string InstallRoot { get; init; } = AppContext.BaseDirectory;

    // ------------------------------------------------------------------- fail-fast

    /// <summary>Startup discipline: create each of the five roots in turn and verify it
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
                root.CreateDirectory();
                var probe = root / $".flint-write-probe-{Guid.NewGuid():N}";
                probe.WriteAllText("");
                probe.Delete();
            }
            catch (Exception e)
            {
                throw new InvalidOperationException(
                    $"Flint '{name}' root at '{root}' is not usable: {e.Message}", e);
            }
        }
    }
}
