// SPDX-License-Identifier: GPL-3.0-only
// The flint.json record domain full set (CONFIG C5/C6, §2.2 finalized): one record per
// domain, all of them registered into FlintJsonContext (AOT red line — a missing root
// registration is a runtime failure, not a compile error).
//
// Layering shape (C6/C7): every member is NULLABLE — null means "not set at this layer,
// inherit from the layer below". The record PROPERTY DEFAULTS are the single source of the
// C++ Application::registerSettings defaults: <see cref="FlintConfigFold"/> folds each
// layer over a fresh record, so an all-null layer resolves to exactly the defaults.
//
// Secret members are deliberately absent (C4): ProxyPass / ModrinthToken / FlameKeyOverride
// live in the secret store (FlintLauncher.Core.Secrets) and code reads them by name through
// ISecretStore — the config format carries no secret fields.
using FlintLauncher.Core.Launch;

namespace FlintLauncher.Core.Config;

/// <summary>The flint.json user-layer root (C5). The JSON shape is one object with a
/// camelCase member per domain; a future system layer (C2, format-reserved) is another
/// payload of the same record at the head of the fold stack — the format does not change.</summary>
public sealed record FlintConfigFile
{
    /// <summary>The fresh-install default: every domain record constructed, so every
    /// property initializer (the C++ default) is materialized.</summary>
    public static FlintConfigFile Default() => new();

    public LauncherConfig Launcher { get; init; } = new();
    public ConsoleConfig Console { get; init; } = new();
    public GameTimeConfig GameTime { get; init; } = new();
    public WindowConfig Window { get; init; } = new();
    public MemoryConfig Memory { get; init; } = new();
    public PerformanceConfig Performance { get; init; } = new();
    public NativeLibConfig NativeLib { get; init; } = new();
    public CommandsConfig Commands { get; init; } = new();
    public MinecraftMiscConfig MinecraftMisc { get; init; } = new();
    public ModDownloadConfig ModDownload { get; init; } = new();
    public JavaConfig Java { get; init; } = new();
    public DirsConfig Dirs { get; init; } = new();
    public LanguageConfig Language { get; init; } = new();
    public MirrorConfig Mirror { get; init; } = new();
    public ProxyConfig Proxy { get; init; } = new();
    public ApiConfig Api { get; init; } = new();
    public ToolsConfig Tools { get; init; } = new();
}

/// <summary>Launcher shell + concurrency/network behavior (the C++ "launcher" settings
/// sections: parallelism, instance-list ordering, launcher close-on-launch switches).</summary>
public sealed record LauncherConfig
{
    /// <summary>GUI-only today (no window chrome is ported); kept so the /settings wire
    /// shape survives unchanged (C6: the group keys stay).</summary>
    public bool? MenuBarInsteadOfToolBar { get; init; } = false;

    public int? NumberOfConcurrentTasks { get; init; } = 10;
    public int? NumberOfConcurrentDownloads { get; init; } = 6;
    public int? NumberOfManualRetries { get; init; } = 1;
    public int? RequestTimeout { get; init; } = 60;
    public bool? CloseAfterLaunch { get; init; } = false;
    public bool? QuitAfterGameStop { get; init; } = false;
    public string? InstSortMode { get; init; } = "Name";
    public string? InstRenamingMode { get; init; } = "AskEverytime";
}

/// <summary>Console visibility + the line buffer (ConsoleMaxLines/ConsoleOverflowStop are
/// the C7 passthrough pair: never instance-layered, they stay global-only).</summary>
public sealed record ConsoleConfig
{
    public bool? ShowConsole { get; init; } = false;
    public bool? AutoCloseConsole { get; init; } = false;
    public bool? ShowConsoleOnError { get; init; } = true;
    public bool? LogPrePostOutput { get; init; } = true;
    public int? ConsoleMaxLines { get; init; } = 100000;
    public bool? ConsoleOverflowStop { get; init; } = true;
}

/// <summary>Game-time display/recording (BaseInstance override-gated at the instance layer).</summary>
public sealed record GameTimeConfig
{
    public bool? ShowGameTime { get; init; } = true;
    public bool? ShowGlobalGameTime { get; init; } = true;
    public bool? ShowGameTimeWithoutDays { get; init; } = false;
    public bool? RecordGameTime { get; init; } = true;
}

/// <summary>Launch window shape (the C++ "Window Size" section).</summary>
public sealed record WindowConfig
{
    public bool? LaunchMaximized { get; init; } = false;
    public int? MinecraftWinWidth { get; init; } = 854;
    public int? MinecraftWinHeight { get; init; } = 480;
}

/// <summary>JVM memory allocation.</summary>
public sealed record MemoryConfig
{
    /// <summary>Port of SysInfo::defaultMaxJvmMem: below 6 GiB of total RAM the heap default
    /// is total/1.5, else 4 GiB. Deviation: unknown RAM (TotalRamMiB() == 0) keeps the usable
    /// 4 GiB default instead of the C++ 0. Lives here — the record property default IS the
    /// single source (C6) since GlobalSettingsRegistration retired.</summary>
    public static int DefaultMaxMemAlloc
    {
        get
        {
            var totalRam = HardwareInfo.TotalRamMiB();
            if (totalRam == 0)
            {
                return 4096;
            }
            return totalRam < 6144UL ? (int)(totalRam / 1.5) : 4096;
        }
    }

    public int? MinMemAlloc { get; init; } = 512;
    public int? MaxMemAlloc { get; init; } = DefaultMaxMemAlloc;
    // PermGen died with the Java <8 support floor (2026-10-05): PermSize is a pre-8 concept.
    public bool? LowMemWarning { get; init; } = true;
}

/// <summary>Linux-only performance wrappers (the C++ "Performance" section).</summary>
public sealed record PerformanceConfig
{
    public bool? EnableFeralGamemode { get; init; } = false;
    public bool? EnableMangoHud { get; init; } = false;
    public bool? UseDiscreteGpu { get; init; } = false;
    public bool? UseZink { get; init; } = false;
}

/// <summary>Native library workarounds (system OpenAL/GLFW/SDL vs the bundled copies).</summary>
public sealed record NativeLibConfig
{
    public bool? UseNativeOpenAL { get; init; } = false;
    public string? CustomOpenALPath { get; init; } = "";
    public bool? UseNativeGLFW { get; init; } = false;
    public string? CustomGLFWPath { get; init; } = "";
    public bool? UseNativeSDL { get; init; } = false;
    public string? CustomSDLPath { get; init; } = "";
}

/// <summary>Custom commands around the game process (BaseInstance override-gated).</summary>
public sealed record CommandsConfig
{
    public string? PreLoadCommand { get; init; } = "";
    public string? PreLaunchCommand { get; init; } = "";
    public string? WrapperCommand { get; init; } = "";
    public string? PostExitCommand { get; init; } = "";
}

/// <summary>Minecraft-miscellaneous: the legacy-services OnlineFixes gate and the custom
/// environment map (stored as its JSON object text, like the C++ Json value).</summary>
public sealed record MinecraftMiscConfig
{
    public bool? OnlineFixes { get; init; } = false;
    public string? Env { get; init; } = "{}";
}

/// <summary>Mod-download behavior (the ModDownloadLoaders gate family; #3404 release
/// channels stay their JSON string-list form).</summary>
public sealed record ModDownloadConfig
{
    public bool? ModMetadataDisabled { get; init; } = false;
    public bool? ModDependenciesDisabled { get; init; } = false;
    public bool? SkipModpackUpdatePrompt { get; init; } = false;
    public bool? ShowModIncompat { get; init; } = false;
    public bool? DownloadGameFilesDuringInstanceCreation { get; init; } = true;
    public string? ModUpdateReleaseTypes { get; init; } = "[]";
}

/// <summary>Java location/arguments plus the automatic-java machinery. The C9 probe quintet
/// (JavaSignature/Architecture/RealArchitecture/Version/Vendor) is deliberately absent —
/// launcher-state.json owns the path-keyed probe cache (STATE S5).</summary>
public sealed record JavaConfig
{
    public string? JavaPath { get; init; } = "";
    public string? JvmArgs { get; init; } = "";
    public bool? IgnoreJavaCompatibility { get; init; } = false;
    public bool? IgnoreJavaWizard { get; init; } = false;
    public bool? AutomaticJavaSwitch { get; init; } = true;
    public bool? AutomaticJavaDownload { get; init; } = true;

    /// <summary>Legacy wire key only (never a C++ global): the java group exposes it and
    /// AutoInstallJava consults the instance-level flag; the C++ derive their defaults from
    /// the empty JavaPath. Default true matches the wire table it replaces (C6).</summary>
    public bool? AutomaticJava { get; init; } = true;
}

/// <summary>Directory layout plus the downloads-drop folder. CentralModsDir is GONE (DATA
/// D8: a true dead setting — zero core consumers; it is no longer registered, so an
/// imported INI's leftover key simply stays unread). JavaDir resolves under STORE root
/// semantics since the E2 remount (GLOBAL §3).</summary>
public sealed record DirsConfig
{
    /// <summary>QStandardPaths::writableLocation(DownloadLocation) approximation. The C++
    /// default (Application.cpp:710) is platform-sharp: Qt reads XDG_DOWNLOAD_DIR from
    /// user-dirs.dirs on Linux and SHGetKnownFolderPath(FOLDERID_Downloads) on Windows, so
    /// a localized/redirected downloads folder diverges from this Home+"Downloads" default.
    /// Zero core consumers until the downloads-dir drop/move feature (C++ BlockedModsDialog)
    /// is ported; re-align there if that ever matters — imported DownloadsDir values are
    /// unaffected.</summary>
    public static string DefaultDownloadsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public string? IconsDir { get; init; } = "icons";
    public string? SkinsDir { get; init; } = "skins";
    public string? JavaDir { get; init; } = "java";
    public string? DownloadsDir { get; init; } = DefaultDownloadsDir;
    public bool? DownloadsDirWatchRecursive { get; init; } = false;
    public bool? MoveModsFromDownloadsDir { get; init; } = false;
}

/// <summary>Language selection (the translations UI is not ported; wire-only).</summary>
public sealed record LanguageConfig
{
    public string? Language { get; init; } = "";
    public bool? UseSystemLocale { get; init; } = false;
}

/// <summary>Metadata/resource mirror overrides (the C++ resets invalid URLs; the port
/// stores the raw string).</summary>
public sealed record MirrorConfig
{
    public string? MetaURLOverride { get; init; } = "";
    public string? ResourceURLOverride { get; init; } = "";
    public string? LegacyFMLLibsURLOverride { get; init; } = "";
}

/// <summary>Proxy coordinates. NO Pass member (C4): the proxy password is a secret, read by
/// name from the secret store.</summary>
public sealed record ProxyConfig
{
    public string? ProxyType { get; init; } = "None";
    public string? ProxyAddr { get; init; } = "127.0.0.1";
    public int? ProxyPort { get; init; } = 8080;
    public string? ProxyUser { get; init; } = "";
}

/// <summary>API customization. NO credential members (C4): FlameKeyOverride/ModrinthToken
/// live in the secret store; MSAClientIDOverride is a public client id and stays here.</summary>
public sealed record ApiConfig
{
    public string? MsaClientIdOverride { get; init; } = "";
    public string? UserAgentOverride { get; init; } = "";
    public string? PastebinCustomAPIBase { get; init; } = "";

    /// <summary>The PasteUpload.PasteType index (the C++ stores the enum's int).</summary>
    public int? PastebinType { get; init; } = (int)Net.PasteUpload.PasteType.Mclogs;
}

/// <summary>External tool paths (the C++ ExternalToolsPage tools; the profiler launchers
/// read JProfilerPath/JVisualVMPath).</summary>
public sealed record ToolsConfig
{
    public string? JsonEditor { get; init; } = "";
    public string? JProfilerPath { get; init; } = "";
    public string? JVisualVMPath { get; init; } = "";
    public string? MCEditPath { get; init; } = "";
}
