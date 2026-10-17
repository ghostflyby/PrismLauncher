// SPDX-License-Identifier: GPL-3.0-only
// The typed layer-stack fold (CONFIG C6/C7): a layer stack resolves by folding each layer
// over the one below it — the UPPER layer wins wherever its member is non-null, null
// inherits. The bottom of every fold is a fresh record, so the record property defaults
// are the single default source: an all-null stack resolves to exactly them.
//
// Stack shape (C2/ask): system layer is NOT implemented — the format reserves the stack
// head for it, so this phase folds user → instance (instance typed payloads arrive with
// the D4 instance.json batch). Fold() takes the layers bottom-up and simply accepts more.
//
// All merging is with-expressions over explicit members — no reflection (Native AOT).
// NOTE: the source-generated deserializer does not honor property initializers for members
// absent from a payload (the same finding FileSecretStore/LauncherStateStore fold around),
// so a partially-written flint.json surfaces nulls that this fold replaces with defaults.
namespace FlintLauncher.Core.Config;

public static class FlintConfigFold
{
    /// <summary>Fold the layers bottom-up over the record defaults: layers[0] is the lowest
    /// (user), later entries override it (instance, …; a future C2 system layer would be
    /// prepended). Null layers are skipped, so callers can pass optional stages as-is.</summary>
    public static FlintConfigFile Fold(params FlintConfigFile?[] layers)
    {
        var resolved = FlintConfigFile.Default();
        foreach (var layer in layers)
        {
            if (layer is not null)
            {
                resolved = Merge(resolved, layer);
            }
        }
        return resolved;
    }

    /// <summary>Fold <paramref name="upper"/> over <paramref name="lower"/>: non-null upper
    /// members win, null inherits (C7). Domains are always materialized (the roots'
    /// materializing constructors); the layer semantics live at the scalar level — a scalar
    /// absent from either side is null and inherits, so the fold result is always fully
    /// resolved.
    /// NOTE: layers are expected to arrive as PARTIAL PAYLOADS — a payload-absent domain
    /// re-materializes BLANK via the roots' constructors (every scalar null = inherit), so
    /// only payload-PRESENT scalars ever win over the lower layer. Hand-materialized domain
    /// records are blank too (the defaults live in <see cref="FlintConfigFile.Default"/>),
    /// so fixture layers behave identically whether built from JSON or object initializers.</summary>
    public static FlintConfigFile Merge(FlintConfigFile lower, FlintConfigFile upper) => new()
    {
        Launcher = Merge(lower.Launcher, upper.Launcher),
        Console = Merge(lower.Console, upper.Console),
        GameTime = Merge(lower.GameTime, upper.GameTime),
        Window = Merge(lower.Window, upper.Window),
        Memory = Merge(lower.Memory, upper.Memory),
        Performance = Merge(lower.Performance, upper.Performance),
        NativeLib = Merge(lower.NativeLib, upper.NativeLib),
        Commands = Merge(lower.Commands, upper.Commands),
        MinecraftMisc = Merge(lower.MinecraftMisc, upper.MinecraftMisc),
        ModDownload = Merge(lower.ModDownload, upper.ModDownload),
        Java = Merge(lower.Java, upper.Java),
        Dirs = Merge(lower.Dirs, upper.Dirs),
        Language = Merge(lower.Language, upper.Language),
        Mirror = Merge(lower.Mirror, upper.Mirror),
        Proxy = Merge(lower.Proxy, upper.Proxy),
        Api = Merge(lower.Api, upper.Api),
        Tools = Merge(lower.Tools, upper.Tools),
    };

    /// <summary>Normalize a payload read from disk: absent domain objects and members take
    /// the record defaults (the record-merge half of TypedJsonFile's load).</summary>
    public static FlintConfigFile MergeOverDefault(FlintConfigFile payload) =>
        Merge(FlintConfigFile.Default(), payload);

    private static LauncherConfig Merge(LauncherConfig lower, LauncherConfig upper) => new()
    {
        MenuBarInsteadOfToolBar = upper.MenuBarInsteadOfToolBar ?? lower.MenuBarInsteadOfToolBar,
        NumberOfConcurrentTasks = upper.NumberOfConcurrentTasks ?? lower.NumberOfConcurrentTasks,
        NumberOfConcurrentDownloads = upper.NumberOfConcurrentDownloads ?? lower.NumberOfConcurrentDownloads,
        NumberOfManualRetries = upper.NumberOfManualRetries ?? lower.NumberOfManualRetries,
        RequestTimeout = upper.RequestTimeout ?? lower.RequestTimeout,
        CloseAfterLaunch = upper.CloseAfterLaunch ?? lower.CloseAfterLaunch,
        QuitAfterGameStop = upper.QuitAfterGameStop ?? lower.QuitAfterGameStop,
        InstSortMode = upper.InstSortMode ?? lower.InstSortMode,
        InstRenamingMode = upper.InstRenamingMode ?? lower.InstRenamingMode
    };

    private static ConsoleConfig Merge(ConsoleConfig lower, ConsoleConfig upper) => new()
    {
        ShowConsole = upper.ShowConsole ?? lower.ShowConsole,
        AutoCloseConsole = upper.AutoCloseConsole ?? lower.AutoCloseConsole,
        ShowConsoleOnError = upper.ShowConsoleOnError ?? lower.ShowConsoleOnError,
        LogPrePostOutput = upper.LogPrePostOutput ?? lower.LogPrePostOutput,
        ConsoleMaxLines = upper.ConsoleMaxLines ?? lower.ConsoleMaxLines,
        ConsoleOverflowStop = upper.ConsoleOverflowStop ?? lower.ConsoleOverflowStop
    };

    private static GameTimeConfig Merge(GameTimeConfig lower, GameTimeConfig upper) => new() { ShowGameTime = upper.ShowGameTime ?? lower.ShowGameTime, ShowGlobalGameTime = upper.ShowGlobalGameTime ?? lower.ShowGlobalGameTime, ShowGameTimeWithoutDays = upper.ShowGameTimeWithoutDays ?? lower.ShowGameTimeWithoutDays, RecordGameTime = upper.RecordGameTime ?? lower.RecordGameTime };

    private static WindowConfig Merge(WindowConfig lower, WindowConfig upper) => new() { LaunchMaximized = upper.LaunchMaximized ?? lower.LaunchMaximized, MinecraftWinWidth = upper.MinecraftWinWidth ?? lower.MinecraftWinWidth, MinecraftWinHeight = upper.MinecraftWinHeight ?? lower.MinecraftWinHeight };

    private static MemoryConfig Merge(MemoryConfig lower, MemoryConfig upper) => new() { MinMemAlloc = upper.MinMemAlloc ?? lower.MinMemAlloc, MaxMemAlloc = upper.MaxMemAlloc ?? lower.MaxMemAlloc, LowMemWarning = upper.LowMemWarning ?? lower.LowMemWarning };

    private static PerformanceConfig Merge(PerformanceConfig lower, PerformanceConfig upper) => new() { EnableFeralGamemode = upper.EnableFeralGamemode ?? lower.EnableFeralGamemode, EnableMangoHud = upper.EnableMangoHud ?? lower.EnableMangoHud, UseDiscreteGpu = upper.UseDiscreteGpu ?? lower.UseDiscreteGpu, UseZink = upper.UseZink ?? lower.UseZink };

    private static NativeLibConfig Merge(NativeLibConfig lower, NativeLibConfig upper) => new()
    {
        UseNativeOpenAl = upper.UseNativeOpenAl ?? lower.UseNativeOpenAl,
        CustomOpenAlPath = upper.CustomOpenAlPath ?? lower.CustomOpenAlPath,
        UseNativeGlfw = upper.UseNativeGlfw ?? lower.UseNativeGlfw,
        CustomGlfwPath = upper.CustomGlfwPath ?? lower.CustomGlfwPath,
        UseNativeSdl = upper.UseNativeSdl ?? lower.UseNativeSdl,
        CustomSdlPath = upper.CustomSdlPath ?? lower.CustomSdlPath
    };

    private static CommandsConfig Merge(CommandsConfig lower, CommandsConfig upper) => new() { PreLoadCommand = upper.PreLoadCommand ?? lower.PreLoadCommand, PreLaunchCommand = upper.PreLaunchCommand ?? lower.PreLaunchCommand, WrapperCommand = upper.WrapperCommand ?? lower.WrapperCommand, PostExitCommand = upper.PostExitCommand ?? lower.PostExitCommand };

    private static MinecraftMiscConfig Merge(MinecraftMiscConfig lower, MinecraftMiscConfig upper) => new() { OnlineFixes = upper.OnlineFixes ?? lower.OnlineFixes, Env = upper.Env ?? lower.Env };

    private static ModDownloadConfig Merge(ModDownloadConfig lower, ModDownloadConfig upper) => new()
    {
        ModMetadataDisabled = upper.ModMetadataDisabled ?? lower.ModMetadataDisabled,
        ModDependenciesDisabled = upper.ModDependenciesDisabled ?? lower.ModDependenciesDisabled,
        SkipModpackUpdatePrompt = upper.SkipModpackUpdatePrompt ?? lower.SkipModpackUpdatePrompt,
        ShowModIncompat = upper.ShowModIncompat ?? lower.ShowModIncompat,
        DownloadGameFilesDuringInstanceCreation = upper.DownloadGameFilesDuringInstanceCreation ?? lower.DownloadGameFilesDuringInstanceCreation,
        ModUpdateReleaseTypes = upper.ModUpdateReleaseTypes ?? lower.ModUpdateReleaseTypes
    };

    private static JavaConfig Merge(JavaConfig lower, JavaConfig upper) => new()
    {
        JavaPath = upper.JavaPath ?? lower.JavaPath,
        JvmArgs = upper.JvmArgs ?? lower.JvmArgs,
        IgnoreJavaCompatibility = upper.IgnoreJavaCompatibility ?? lower.IgnoreJavaCompatibility,
        IgnoreJavaWizard = upper.IgnoreJavaWizard ?? lower.IgnoreJavaWizard,
        AutomaticJavaSwitch = upper.AutomaticJavaSwitch ?? lower.AutomaticJavaSwitch,
        AutomaticJavaDownload = upper.AutomaticJavaDownload ?? lower.AutomaticJavaDownload,
        AutomaticJava = upper.AutomaticJava ?? lower.AutomaticJava
    };

    private static DirsConfig Merge(DirsConfig lower, DirsConfig upper) => new()
    {
        IconsDir = upper.IconsDir ?? lower.IconsDir,
        SkinsDir = upper.SkinsDir ?? lower.SkinsDir,
        JavaDir = upper.JavaDir ?? lower.JavaDir,
        DownloadsDir = upper.DownloadsDir ?? lower.DownloadsDir,
        DownloadsDirWatchRecursive = upper.DownloadsDirWatchRecursive ?? lower.DownloadsDirWatchRecursive,
        MoveModsFromDownloadsDir = upper.MoveModsFromDownloadsDir ?? lower.MoveModsFromDownloadsDir
    };

    private static LanguageConfig Merge(LanguageConfig lower, LanguageConfig upper) => new() { Language = upper.Language ?? lower.Language, UseSystemLocale = upper.UseSystemLocale ?? lower.UseSystemLocale };

    private static MirrorConfig Merge(MirrorConfig lower, MirrorConfig upper) => new() { MetaUrlOverride = upper.MetaUrlOverride ?? lower.MetaUrlOverride, ResourceUrlOverride = upper.ResourceUrlOverride ?? lower.ResourceUrlOverride, LegacyFmlLibsUrlOverride = upper.LegacyFmlLibsUrlOverride ?? lower.LegacyFmlLibsUrlOverride };

    private static ProxyConfig Merge(ProxyConfig lower, ProxyConfig upper) => new() { ProxyType = upper.ProxyType ?? lower.ProxyType, ProxyAddr = upper.ProxyAddr ?? lower.ProxyAddr, ProxyPort = upper.ProxyPort ?? lower.ProxyPort, ProxyUser = upper.ProxyUser ?? lower.ProxyUser };

    private static ApiConfig Merge(ApiConfig lower, ApiConfig upper) => new() { MsaClientIdOverride = upper.MsaClientIdOverride ?? lower.MsaClientIdOverride, UserAgentOverride = upper.UserAgentOverride ?? lower.UserAgentOverride, PastebinCustomApiBase = upper.PastebinCustomApiBase ?? lower.PastebinCustomApiBase, PastebinType = upper.PastebinType ?? lower.PastebinType };

    private static ToolsConfig Merge(ToolsConfig lower, ToolsConfig upper) => new() { JsonEditor = upper.JsonEditor ?? lower.JsonEditor, JProfilerPath = upper.JProfilerPath ?? lower.JProfilerPath, JVisualVmPath = upper.JVisualVmPath ?? lower.JVisualVmPath, McEditPath = upper.McEditPath ?? lower.McEditPath };
}
