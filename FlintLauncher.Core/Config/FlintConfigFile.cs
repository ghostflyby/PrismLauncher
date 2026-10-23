// SPDX-License-Identifier: GPL-3.0-only
// The flint.json record domain full set: one record per
// domain, all of them registered into FlintJsonContext (AOT red line — a missing root
// registration is a runtime failure, not a compile error).
//
// Layering shape: the DOMAIN members are non-null — the materializing constructor
// re-creates every domain the payload omitted (the .NET 10 source-generated deserializer
// overwrites init-only initializers with null for absent members), and the domain records
// are BLANK (no scalar initializers) so those re-created domains are inert: every scalar
// stays null = "not set at this layer, inherit from the layer below". The single source of
// the C++ Application::registerSettings defaults is <see cref="FlintConfigFile.Default"/>:
// <see cref="FlintConfigFold"/> folds each layer over it, so an all-null layer resolves to
// exactly the defaults.
//
// Secret members are deliberately absent: ProxyPass / ModrinthToken / FlameKeyOverride
// live in the secret store (FlintLauncher.Core.Secrets) and code reads them by name through
// ISecretStore — the config format carries no secret fields.

using System.Text.Json.Serialization;
using FlintLauncher.Core.Launch;
using FlintLauncher.Core.Net;

namespace FlintLauncher.Core.Config;

/// <summary>The flint.json user-layer root. The JSON shape is one object with a
/// camelCase member per domain; a future system layer is another
/// payload of the same record at the head of the fold stack — the format does not change.</summary>
public sealed record FlintConfigFile
{
    /// <summary>The fresh-install default: the single source of the C++
    /// Application::registerSettings defaults (the domain records themselves are BLANK —
    /// all scalars null — so that the materializing constructor yields inert, inherit-all
    /// domains for partial payloads).</summary>
    public static FlintConfigFile Default() => new()
    {
        Launcher = new LauncherConfig
        {
            MenuBarInsteadOfToolBar = false,
            NumberOfConcurrentTasks = 10,
            NumberOfConcurrentDownloads = 6,
            NumberOfManualRetries = 1,
            RequestTimeout = 60,
            CloseAfterLaunch = false,
            QuitAfterGameStop = false,
            InstSortMode = "Name",
            InstRenamingMode = "AskEverytime",
        },
        Console = new ConsoleConfig
        {
            ShowConsole = false,
            AutoCloseConsole = false,
            ShowConsoleOnError = true,
            LogPrePostOutput = true,
            ConsoleMaxLines = 100000,
            ConsoleOverflowStop = true,
        },
        GameTime = new GameTimeConfig
        {
            ShowGameTime = true,
            ShowGlobalGameTime = true,
            ShowGameTimeWithoutDays = false,
            RecordGameTime = true,
        },
        Window = new WindowConfig
        {
            LaunchMaximized = false,
            MinecraftWinWidth = 854,
            MinecraftWinHeight = 480,
        },
        Memory = new MemoryConfig
        {
            MinMemAlloc = 512,
            MaxMemAlloc = MemoryConfig.DefaultMaxMemAlloc,
            LowMemWarning = true,
        },
        Performance = new PerformanceConfig
        {
            EnableFeralGamemode = false,
            EnableMangoHud = false,
            UseDiscreteGpu = false,
            UseZink = false,
        },
        NativeLib = new NativeLibConfig
        {
            UseNativeOpenAl = false,
            CustomOpenAlPath = "",
            UseNativeGlfw = false,
            CustomGlfwPath = "",
            UseNativeSdl = false,
            CustomSdlPath = "",
        },
        Commands = new CommandsConfig
        {
            PreLoadCommand = "",
            PreLaunchCommand = "",
            WrapperCommand = "",
            PostExitCommand = "",
        },
        MinecraftMisc = new MinecraftMiscConfig
        {
            OnlineFixes = false,
            Env = new EnvMap(),
        },
        ModDownload = new ModDownloadConfig
        {
            ModMetadataDisabled = false,
            ModDependenciesDisabled = false,
            SkipModpackUpdatePrompt = false,
            ShowModIncompat = false,
            DownloadGameFilesDuringInstanceCreation = true,
            ModUpdateReleaseTypes = "[]",
        },
        Java = new JavaConfig
        {
            JavaPath = "",
            JvmArgs = "",
            IgnoreJavaCompatibility = false,
            IgnoreJavaWizard = false,
            AutomaticJavaSwitch = true,
            AutomaticJavaDownload = true,
            AutomaticJava = true,
        },
        Dirs = new DirsConfig
        {
            IconsDir = "icons",
            SkinsDir = "skins",
            JavaDir = "java",
            DownloadsDir = DirsConfig.DefaultDownloadsDir,
            DownloadsDirWatchRecursive = false,
            MoveModsFromDownloadsDir = false,
        },
        Language = new LanguageConfig
        {
            Language = "",
            UseSystemLocale = false,
        },
        Mirror = new MirrorConfig
        {
            MetaUrlOverride = "",
            ResourceUrlOverride = "",
            LegacyFmlLibsUrlOverride = "",
        },
        Proxy = new ProxyConfig
        {
            ProxyType = "None",
            ProxyAddr = "127.0.0.1",
            ProxyPort = 8080,
            ProxyUser = "",
        },
        Api = new ApiConfig
        {
            MsaClientIdOverride = "",
            UserAgentOverride = "",
            PastebinCustomApiBase = "",
            PastebinType = (int)PasteUpload.PasteType.Mclogs,
        },
        Tools = new ToolsConfig
        {
            JsonEditor = "",
            JProfilerPath = "",
            JVisualVmPath = "",
            McEditPath = "",
        },
    };

    // The materializing constructor: the source-generated deserializer overwrites init-only
    // property initializers with null for members absent from the payload (.NET 10), so the
    // constructor body re-materializes them — every in-memory FlintConfigFile has real
    // domain values regardless of the on-disk shape. The layer semantics live at the SCALAR
    // level (null member = inherit, resolved by FlintConfigFold), never at the domain level.
    [JsonConstructor]
    public FlintConfigFile(LauncherConfig? launcher = null, ConsoleConfig? console = null,
        GameTimeConfig? gameTime = null, WindowConfig? window = null, MemoryConfig? memory = null,
        PerformanceConfig? performance = null, NativeLibConfig? nativeLib = null,
        CommandsConfig? commands = null, MinecraftMiscConfig? minecraftMisc = null,
        ModDownloadConfig? modDownload = null, JavaConfig? java = null, DirsConfig? dirs = null,
        LanguageConfig? language = null, MirrorConfig? mirror = null, ProxyConfig? proxy = null,
        ApiConfig? api = null, ToolsConfig? tools = null)
    {
        Launcher = launcher ?? new LauncherConfig();
        Console = console ?? new ConsoleConfig();
        GameTime = gameTime ?? new GameTimeConfig();
        Window = window ?? new WindowConfig();
        Memory = memory ?? new MemoryConfig();
        Performance = performance ?? new PerformanceConfig();
        NativeLib = nativeLib ?? new NativeLibConfig();
        Commands = commands ?? new CommandsConfig();
        MinecraftMisc = minecraftMisc ?? new MinecraftMiscConfig();
        ModDownload = modDownload ?? new ModDownloadConfig();
        Java = java ?? new JavaConfig();
        Dirs = dirs ?? new DirsConfig();
        Language = language ?? new LanguageConfig();
        Mirror = mirror ?? new MirrorConfig();
        Proxy = proxy ?? new ProxyConfig();
        Api = api ?? new ApiConfig();
        Tools = tools ?? new ToolsConfig();
    }

    public LauncherConfig Launcher { get; init; }
    public ConsoleConfig Console { get; init; }
    public GameTimeConfig GameTime { get; init; }
    public WindowConfig Window { get; init; }
    public MemoryConfig Memory { get; init; }
    public PerformanceConfig Performance { get; init; }
    public NativeLibConfig NativeLib { get; init; }
    public CommandsConfig Commands { get; init; }
    public MinecraftMiscConfig MinecraftMisc { get; init; }
    public ModDownloadConfig ModDownload { get; init; }
    public JavaConfig Java { get; init; }
    public DirsConfig Dirs { get; init; }
    public LanguageConfig Language { get; init; }
    public MirrorConfig Mirror { get; init; }
    public ProxyConfig Proxy { get; init; }
    public ApiConfig Api { get; init; }
    public ToolsConfig Tools { get; init; }
}

/// <summary>Launcher shell + concurrency/network behavior (the C++ "launcher" settings
/// sections: parallelism, instance-list ordering, launcher close-on-launch switches).</summary>
public sealed record LauncherConfig
{
    /// <summary>GUI-only today (no window chrome is ported); kept so the /settings wire
    /// shape survives unchanged (the group keys stay).</summary>
    public bool? MenuBarInsteadOfToolBar { get; init; }

    public int? NumberOfConcurrentTasks { get; init; }
    public int? NumberOfConcurrentDownloads { get; init; }
    public int? NumberOfManualRetries { get; init; }
    public int? RequestTimeout { get; init; }
    public bool? CloseAfterLaunch { get; init; }
    public bool? QuitAfterGameStop { get; init; }
    public string? InstSortMode { get; init; }
    public string? InstRenamingMode { get; init; }
}

/// <summary>Console visibility + the line buffer (ConsoleMaxLines/ConsoleOverflowStop are
/// the passthrough pair: never instance-layered, they stay global-only).</summary>
public sealed record ConsoleConfig
{
    public bool? ShowConsole { get; init; }
    public bool? AutoCloseConsole { get; init; }
    public bool? ShowConsoleOnError { get; init; }
    public bool? LogPrePostOutput { get; init; }
    public int? ConsoleMaxLines { get; init; }
    public bool? ConsoleOverflowStop { get; init; }
}

/// <summary>Game-time display/recording (BaseInstance override-gated at the instance layer).</summary>
public sealed record GameTimeConfig
{
    public bool? ShowGameTime { get; init; }
    public bool? ShowGlobalGameTime { get; init; }
    public bool? ShowGameTimeWithoutDays { get; init; }
    public bool? RecordGameTime { get; init; }
}

/// <summary>Launch window shape (the C++ "Window Size" section).</summary>
public sealed record WindowConfig
{
    public bool? LaunchMaximized { get; init; }
    public int? MinecraftWinWidth { get; init; }
    public int? MinecraftWinHeight { get; init; }
}

/// <summary>JVM memory allocation.</summary>
public sealed record MemoryConfig
{
    /// <summary>Port of SysInfo::defaultMaxJvmMem: below 6 GiB of total RAM the heap default
    /// is total/1.5, else 4 GiB. Deviation: unknown RAM (TotalRamMiB() == 0) keeps the usable
    /// 4 GiB default instead of the C++ 0. Lives here — the record property default IS the
    /// single source since GlobalSettingsRegistration retired.</summary>
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

    public int? MinMemAlloc { get; init; }

    public int? MaxMemAlloc { get; init; }

    // PermGen died with the Java <8 support floor (2026-10-05): PermSize is a pre-8 concept.
    public bool? LowMemWarning { get; init; }
}

/// <summary>Linux-only performance wrappers (the C++ "Performance" section).</summary>
public sealed record PerformanceConfig
{
    public bool? EnableFeralGamemode { get; init; }
    public bool? EnableMangoHud { get; init; }
    public bool? UseDiscreteGpu { get; init; }
    public bool? UseZink { get; init; }
}

/// <summary>Native library workarounds (system OpenAL/GLFW/SDL vs the bundled copies).</summary>
public sealed record NativeLibConfig
{
    public bool? UseNativeOpenAl { get; init; }
    public string? CustomOpenAlPath { get; init; }
    public bool? UseNativeGlfw { get; init; }
    public string? CustomGlfwPath { get; init; }
    public bool? UseNativeSdl { get; init; }
    public string? CustomSdlPath { get; init; }
}

/// <summary>Custom commands around the game process (BaseInstance override-gated).</summary>
public sealed record CommandsConfig
{
    public string? PreLoadCommand { get; init; }
    public string? PreLaunchCommand { get; init; }
    public string? WrapperCommand { get; init; }
    public string? PostExitCommand { get; init; }
}

/// <summary>Minecraft-miscellaneous: the legacy-services OnlineFixes gate and the custom
/// environment map. Read compat accepts the legacy embedded-JSON string form (the C++
/// stored it as its JSON object text); writes are the native object.</summary>
public sealed record MinecraftMiscConfig
{
    public bool? OnlineFixes { get; init; }

    [JsonConverter(typeof(EnvMapJsonConverter))]
    public EnvMap? Env { get; init; }
}

/// <summary>Mod-download behavior (the ModDownloadLoaders gate family; #3404 release
/// channels stay their JSON string-list form).</summary>
public sealed record ModDownloadConfig
{
    public bool? ModMetadataDisabled { get; init; }
    public bool? ModDependenciesDisabled { get; init; }
    public bool? SkipModpackUpdatePrompt { get; init; }
    public bool? ShowModIncompat { get; init; }
    public bool? DownloadGameFilesDuringInstanceCreation { get; init; }
    public string? ModUpdateReleaseTypes { get; init; }
}

/// <summary>Java location/arguments plus the automatic-java machinery. The probe quintet
/// (JavaSignature/Architecture/RealArchitecture/Version/Vendor) is deliberately absent —
/// launcher-state.json owns the path-keyed probe cache.</summary>
public sealed record JavaConfig
{
    public string? JavaPath { get; init; }
    public string? JvmArgs { get; init; }
    public bool? IgnoreJavaCompatibility { get; init; }
    public bool? IgnoreJavaWizard { get; init; }
    public bool? AutomaticJavaSwitch { get; init; }
    public bool? AutomaticJavaDownload { get; init; }

    /// <summary>Legacy wire key only (never a C++ global): the java group exposes it and
    /// AutoInstallJava consults the instance-level flag; the C++ derive their defaults from
    /// the empty JavaPath. Default true matches the wire table it replaces.</summary>
    public bool? AutomaticJava { get; init; }
}

/// <summary>Directory layout plus the downloads-drop folder. CentralModsDir is GONE (a
/// true dead setting — zero core consumers; it is no longer registered, so an
/// imported INI's leftover key simply stays unread). JavaDir resolves under store root
/// semantics since the store remount.</summary>
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

    public string? IconsDir { get; init; }
    public string? SkinsDir { get; init; }
    public string? JavaDir { get; init; }
    public string? DownloadsDir { get; init; }
    public bool? DownloadsDirWatchRecursive { get; init; }
    public bool? MoveModsFromDownloadsDir { get; init; }
}

/// <summary>Language selection (the translations UI is not ported; wire-only).</summary>
public sealed record LanguageConfig
{
    public string? Language { get; init; }
    public bool? UseSystemLocale { get; init; }
}

/// <summary>Metadata/resource mirror overrides (the C++ resets invalid URLs; the port
/// stores the raw string).</summary>
public sealed record MirrorConfig
{
    public string? MetaUrlOverride { get; init; }
    public string? ResourceUrlOverride { get; init; }
    public string? LegacyFmlLibsUrlOverride { get; init; }
}

/// <summary>Proxy coordinates. NO Pass member: the proxy password is a secret, read by
/// name from the secret store.</summary>
public sealed record ProxyConfig
{
    public string? ProxyType { get; init; }
    public string? ProxyAddr { get; init; }
    public int? ProxyPort { get; init; }
    public string? ProxyUser { get; init; }
}

/// <summary>API customization. NO credential members: FlameKeyOverride/ModrinthToken
/// live in the secret store; MSAClientIDOverride is a public client id and stays here.</summary>
public sealed record ApiConfig
{
    public string? MsaClientIdOverride { get; init; }
    public string? UserAgentOverride { get; init; }
    public string? PastebinCustomApiBase { get; init; }

    /// <summary>The PasteUpload.PasteType index (the C++ stores the enum's int).</summary>
    public int? PastebinType { get; init; }
}

/// <summary>External tool paths (the C++ ExternalToolsPage tools; the profiler launchers
/// read JProfilerPath/JVisualVMPath).</summary>
public sealed record ToolsConfig
{
    public string? JsonEditor { get; init; }
    public string? JProfilerPath { get; init; }
    public string? JVisualVmPath { get; init; }
    public string? McEditPath { get; init; }
}
