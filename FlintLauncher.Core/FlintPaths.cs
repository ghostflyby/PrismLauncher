// SPDX-License-Identifier: GPL-3.0-only
// The injectable directory model. The C++ application chdir'd into its data directory at
// startup and several subsystems addressed files with data-dir-relative literals ("meta",
// "assets/indexes", "instgroups.json", ...); the port inherited that via the process working
// directory. This record makes every one of those anchors explicit: the composition root
// builds one FlintPaths from the resolved data dir and threads it down (LauncherRuntime in
// the host, LaunchServices in the launch pipeline, constructor parameters elsewhere), so the
// process working directory is no longer load-bearing and multiple runtimes can coexist in
// one process (integration tests).
namespace FlintLauncher.Core;

/// <summary>Absolute filesystem layout of a Flint data directory. Immutable; derived paths
/// are computed so a rebuilt layout never goes stale.</summary>
public sealed record FlintPaths
{
    public required string DataDir { get; init; }

    public static FlintPaths FromDataDir(string dataDir) =>
        new() { DataDir = Path.GetFullPath(dataDir) };

    // ------------------------------------------------------------- data-dir children

    /// <summary>Local metadata cache root (MetaEntity "meta" base).</summary>
    public string MetaDir => Path.Combine(DataDir, "meta");

    /// <summary>HttpMetaCache index (downloaded metadata/asset/library cache ledger).</summary>
    public string MetacacheFile => Path.Combine(DataDir, "metacache.json");

    /// <summary>Instance groups (the C++ instgroups.json).</summary>
    public string InstgroupsFile => Path.Combine(DataDir, "instgroups.json");

    /// <summary>Stored Mojang/MSA accounts.</summary>
    public string AccountsFile => Path.Combine(DataDir, "accounts.json");

    /// <summary>Discovered/created instances.</summary>
    public string InstancesDir => Path.Combine(DataDir, "instances");

    /// <summary>Shared library store ("libraries" base; Minecraft/Library storage).</summary>
    public string LibrariesRoot => Path.Combine(DataDir, "libraries");

    /// <summary>Asset store root (objects/, indexes/, virtual/ below).</summary>
    public string AssetsRoot => Path.Combine(DataDir, "assets");

    /// <summary>Downloaded asset index JSONs (the "asset_indexes" one-shot cache base).</summary>
    public string AssetIndexesDir => Path.Combine(AssetsRoot, "indexes");

    /// <summary>Dormant anchor kept for C++ parity (MinecraftInstance::versionsPath).</summary>
    public string VersionsRoot => Path.Combine(DataDir, "versions");

    /// <summary>Legacy FML libraries download cache ("fmllibs" base).</summary>
    public string FmllibsCacheDir => Path.Combine(DataDir, "cache", "fmllibs");

    /// <summary>General cache area (cache/icons and friends).</summary>
    public string CacheDir => Path.Combine(DataDir, "cache");

    /// <summary>Auto-installed Java runtimes (JavaUtils "java" root).</summary>
    public string JavaRoot => Path.Combine(DataDir, "java");

    /// <summary>Rolling diagnostics logs (RollingFileLoggerProvider).</summary>
    public string LogsDir => Path.Combine(DataDir, "logs");

    // ------------------------------------------------------------------ install root

    /// <summary>Where the Flint binary itself lives (jars/, share/&lt;launcher&gt;/). The C++
    /// read APPLICATION->getJarPath against the install dir; tests point this at a fixture
    /// root. Defaults to the process base — an install-root concept, not a data-dir one.</summary>
    public string InstallRoot { get; init; } = AppContext.BaseDirectory;
}
