// SPDX-License-Identifier: GPL-3.0-only
// The single settings-key registry (C6: "分组→域映射一份"): every global settings key maps
// to its record domain member exactly once — GET/PUT /settings (the wire groups reference
// this table, the old Web Groups value-table retired), the instance-side adapter view
// (FlintConfigSettings — the C7 override/passthrough pairs pull Setting objects from it),
// and the INI importer (one pass sorts keys through the same Apply functions).
//
// Defaults come from the record property initializers via the resolved fold — this table
// never states a value twice. The three C4 secret keys are listed with Secret=true so the
// wire groups keep accepting them; their storage is the ISecretStore, never the config.

using FlintLauncher.Core.Settings;

namespace FlintLauncher.Core.Config;

/// <summary>One settings key: identity, boxed record default (the wire's type anchor),
/// whether the value is a store secret, the resolved-value getter and the typed setter
/// (applied immutably over a resolved config; coerces INI/JSON/boxed inputs like the C++
/// QVariant reads did).</summary>
public sealed record ConfigKeyEntry(
    string Key,
    object? Default,
    bool Secret,
    Func<FlintConfigFile, object?> Get,
    Func<FlintConfigFile, object?, FlintConfigFile> Apply);

public static class FlintConfigKeys
{
    /// <summary>The C4 secret keys: same names as the old settings keys (the wire is
    /// unchanged) but stored in the ISecretStore, never in flint.json.</summary>
    public static readonly IReadOnlyList<string> SecretKeys =
    [
        "ProxyPass",
        "ModrinthToken",
        "FlameKeyOverride",
    ];

    /// <summary>The C++ Setting synonym lists (launcher/Application.cpp registerSetting brace
    /// lists): the legacy INI spellings → the primary registry key. The C++ settings chain
    /// answered reads through the whole synonym list (INISettingsObject::retrieveValue walks
    /// configKeys() and takes the first hit), so an old prismlauncher.cfg carrying e.g.
    /// ProxyPassword/CFKeyOverride must land under the primary — not be silently dropped.
    /// flint.json itself stays primary-name-only (the aliases are an INI-read compat layer);
    /// the CentralModsDir|ModsDir pair is deliberately absent — the primary itself is
    /// unregistered per DATA D8, and an alias of a dead key stays dead.</summary>
    private static readonly Dictionary<string, string> AliasToPrimary = new(StringComparer.Ordinal)
    {
        // Application.cpp:740-742 (window size)
        ["MCWindowMaximize"] = "LaunchMaximized",
        ["MCWindowWidth"] = "MinecraftWinWidth",
        ["MCWindowHeight"] = "MinecraftWinHeight",
        // Application.cpp:746-749 (proxy)
        ["ProxyHostName"] = "ProxyAddr",
        ["ProxyUsername"] = "ProxyUser",
        ["ProxyPassword"] = "ProxyPass", // the C4 secret alias — redirects with the primary
        // Application.cpp:752-753 (memory)
        ["MinMemoryAlloc"] = "MinMemAlloc",
        ["MaxMemoryAlloc"] = "MaxMemAlloc",
        // Application.cpp:811-813 (custom commands)
        ["PreLoadCmd"] = "PreLoadCommand",
        ["PreLaunchCmd"] = "PreLaunchCommand",
        ["PostExitCmd"] = "PostExitCommand",
        // Application.cpp:894 (resource mirror)
        ["ResourceURL"] = "ResourceURLOverride",
        // Application.cpp:912 (flame api key — the C4 secret alias)
        ["CFKeyOverride"] = "FlameKeyOverride",
    };

    private static readonly Dictionary<string, ConfigKeyEntry> AllMap = Build();

    /// <summary>Every registered key, ordered by name (deterministic adapter registration).</summary>
    public static IReadOnlyCollection<ConfigKeyEntry> All => AllMap.Values;

    /// <summary>The primary registry name for an INI spelling: an alias folds to its primary,
    /// a primary (or unknown key) passes through unchanged.</summary>
    public static string Canonical(string key) => AliasToPrimary.GetValueOrDefault(key, key);

    public static ConfigKeyEntry? Resolve(string key) => AllMap.GetValueOrDefault(Canonical(key));

    public static object? Default(string key) => AllMap.GetValueOrDefault(Canonical(key))?.Default;

    public static bool IsSecret(string key) => SecretKeys.Contains(Canonical(key));

    private static ConfigKeyEntry Entry(string key, Func<FlintConfigFile, object?> get,
        Func<FlintConfigFile, object?, FlintConfigFile> apply) =>
        new(key, Default0(get), Secret: false, get, apply);

    private static ConfigKeyEntry SecretEntry(string key) =>
        new(key, "", Secret: true, _ => "", static (f, _) => f);

    /// <summary>The boxed default of a key = its value in the all-defaults record.</summary>
    private static object? Default0(Func<FlintConfigFile, object?> get) => get(FlintConfigFile.Default());

    private static Dictionary<string, ConfigKeyEntry> Build()
    {
        List<ConfigKeyEntry> entries =
        [
            // ---- launcher (concurrency/network + instance list + launcher close switches)
            Entry("MenuBarInsteadOfToolBar",
                f => f.Launcher.MenuBarInsteadOfToolBar,
                (f, v) => f with { Launcher = f.Launcher with { MenuBarInsteadOfToolBar = Variant.AsBool(v) } }),
            Entry("NumberOfConcurrentTasks",
                f => f.Launcher.NumberOfConcurrentTasks,
                (f, v) => f with { Launcher = f.Launcher with { NumberOfConcurrentTasks = Variant.AsInt(v) } }),
            Entry("NumberOfConcurrentDownloads",
                f => f.Launcher.NumberOfConcurrentDownloads,
                (f, v) => f with { Launcher = f.Launcher with { NumberOfConcurrentDownloads = Variant.AsInt(v) } }),
            Entry("NumberOfManualRetries",
                f => f.Launcher.NumberOfManualRetries,
                (f, v) => f with { Launcher = f.Launcher with { NumberOfManualRetries = Variant.AsInt(v) } }),
            Entry("RequestTimeout",
                f => f.Launcher.RequestTimeout,
                (f, v) => f with { Launcher = f.Launcher with { RequestTimeout = Variant.AsInt(v) } }),
            Entry("CloseAfterLaunch",
                f => f.Launcher.CloseAfterLaunch,
                (f, v) => f with { Launcher = f.Launcher with { CloseAfterLaunch = Variant.AsBool(v) } }),
            Entry("QuitAfterGameStop",
                f => f.Launcher.QuitAfterGameStop,
                (f, v) => f with { Launcher = f.Launcher with { QuitAfterGameStop = Variant.AsBool(v) } }),
            Entry("InstSortMode",
                f => f.Launcher.InstSortMode,
                (f, v) => f with { Launcher = f.Launcher with { InstSortMode = Variant.AsString(v) } }),
            Entry("InstRenamingMode",
                f => f.Launcher.InstRenamingMode,
                (f, v) => f with { Launcher = f.Launcher with { InstRenamingMode = Variant.AsString(v) } }),

            // ---- console
            Entry("ShowConsole",
                f => f.Console.ShowConsole,
                (f, v) => f with { Console = f.Console with { ShowConsole = Variant.AsBool(v) } }),
            Entry("AutoCloseConsole",
                f => f.Console.AutoCloseConsole,
                (f, v) => f with { Console = f.Console with { AutoCloseConsole = Variant.AsBool(v) } }),
            Entry("ShowConsoleOnError",
                f => f.Console.ShowConsoleOnError,
                (f, v) => f with { Console = f.Console with { ShowConsoleOnError = Variant.AsBool(v) } }),
            Entry("LogPrePostOutput",
                f => f.Console.LogPrePostOutput,
                (f, v) => f with { Console = f.Console with { LogPrePostOutput = Variant.AsBool(v) } }),
            Entry("ConsoleMaxLines",
                f => f.Console.ConsoleMaxLines,
                (f, v) => f with { Console = f.Console with { ConsoleMaxLines = Variant.AsInt(v) } }),
            Entry("ConsoleOverflowStop",
                f => f.Console.ConsoleOverflowStop,
                (f, v) => f with { Console = f.Console with { ConsoleOverflowStop = Variant.AsBool(v) } }),

            // ---- game time
            Entry("ShowGameTime",
                f => f.GameTime.ShowGameTime,
                (f, v) => f with { GameTime = f.GameTime with { ShowGameTime = Variant.AsBool(v) } }),
            Entry("ShowGlobalGameTime",
                f => f.GameTime.ShowGlobalGameTime,
                (f, v) => f with { GameTime = f.GameTime with { ShowGlobalGameTime = Variant.AsBool(v) } }),
            Entry("ShowGameTimeWithoutDays",
                f => f.GameTime.ShowGameTimeWithoutDays,
                (f, v) => f with { GameTime = f.GameTime with { ShowGameTimeWithoutDays = Variant.AsBool(v) } }),
            Entry("RecordGameTime",
                f => f.GameTime.RecordGameTime,
                (f, v) => f with { GameTime = f.GameTime with { RecordGameTime = Variant.AsBool(v) } }),

            // ---- window
            Entry("LaunchMaximized",
                f => f.Window.LaunchMaximized,
                (f, v) => f with { Window = f.Window with { LaunchMaximized = Variant.AsBool(v) } }),
            Entry("MinecraftWinWidth",
                f => f.Window.MinecraftWinWidth,
                (f, v) => f with { Window = f.Window with { MinecraftWinWidth = Variant.AsInt(v) } }),
            Entry("MinecraftWinHeight",
                f => f.Window.MinecraftWinHeight,
                (f, v) => f with { Window = f.Window with { MinecraftWinHeight = Variant.AsInt(v) } }),

            // ---- memory
            Entry("MinMemAlloc",
                f => f.Memory.MinMemAlloc,
                (f, v) => f with { Memory = f.Memory with { MinMemAlloc = Variant.AsInt(v) } }),
            Entry("MaxMemAlloc",
                f => f.Memory.MaxMemAlloc,
                (f, v) => f with { Memory = f.Memory with { MaxMemAlloc = Variant.AsInt(v) } }),
            Entry("LowMemWarning",
                f => f.Memory.LowMemWarning,
                (f, v) => f with { Memory = f.Memory with { LowMemWarning = Variant.AsBool(v) } }),

            // ---- performance
            Entry("EnableFeralGamemode",
                f => f.Performance.EnableFeralGamemode,
                (f, v) => f with { Performance = f.Performance with { EnableFeralGamemode = Variant.AsBool(v) } }),
            Entry("EnableMangoHud",
                f => f.Performance.EnableMangoHud,
                (f, v) => f with { Performance = f.Performance with { EnableMangoHud = Variant.AsBool(v) } }),
            Entry("UseDiscreteGpu",
                f => f.Performance.UseDiscreteGpu,
                (f, v) => f with { Performance = f.Performance with { UseDiscreteGpu = Variant.AsBool(v) } }),
            Entry("UseZink",
                f => f.Performance.UseZink,
                (f, v) => f with { Performance = f.Performance with { UseZink = Variant.AsBool(v) } }),

            // ---- native library workarounds
            Entry("UseNativeOpenAL",
                f => f.NativeLib.UseNativeOpenAl,
                (f, v) => f with { NativeLib = f.NativeLib with { UseNativeOpenAl = Variant.AsBool(v) } }),
            Entry("CustomOpenALPath",
                f => f.NativeLib.CustomOpenAlPath,
                (f, v) => f with { NativeLib = f.NativeLib with { CustomOpenAlPath = Variant.AsString(v) } }),
            Entry("UseNativeGLFW",
                f => f.NativeLib.UseNativeGlfw,
                (f, v) => f with { NativeLib = f.NativeLib with { UseNativeGlfw = Variant.AsBool(v) } }),
            Entry("CustomGLFWPath",
                f => f.NativeLib.CustomGlfwPath,
                (f, v) => f with { NativeLib = f.NativeLib with { CustomGlfwPath = Variant.AsString(v) } }),
            Entry("UseNativeSDL",
                f => f.NativeLib.UseNativeSdl,
                (f, v) => f with { NativeLib = f.NativeLib with { UseNativeSdl = Variant.AsBool(v) } }),
            Entry("CustomSDLPath",
                f => f.NativeLib.CustomSdlPath,
                (f, v) => f with { NativeLib = f.NativeLib with { CustomSdlPath = Variant.AsString(v) } }),

            // ---- commands
            Entry("PreLoadCommand",
                f => f.Commands.PreLoadCommand,
                (f, v) => f with { Commands = f.Commands with { PreLoadCommand = Variant.AsString(v) } }),
            Entry("PreLaunchCommand",
                f => f.Commands.PreLaunchCommand,
                (f, v) => f with { Commands = f.Commands with { PreLaunchCommand = Variant.AsString(v) } }),
            Entry("WrapperCommand",
                f => f.Commands.WrapperCommand,
                (f, v) => f with { Commands = f.Commands with { WrapperCommand = Variant.AsString(v) } }),
            Entry("PostExitCommand",
                f => f.Commands.PostExitCommand,
                (f, v) => f with { Commands = f.Commands with { PostExitCommand = Variant.AsString(v) } }),

            // ---- minecraft misc
            Entry("OnlineFixes",
                f => f.MinecraftMisc.OnlineFixes,
                (f, v) => f with { MinecraftMisc = f.MinecraftMisc with { OnlineFixes = Variant.AsBool(v) } }),
            Entry("Env",
                f => f.MinecraftMisc.Env,
                (f, v) => f with { MinecraftMisc = f.MinecraftMisc with { Env = EnvMapJsonConverter.FromObject(v) } }),

            // ---- mod download
            Entry("ModMetadataDisabled",
                f => f.ModDownload.ModMetadataDisabled,
                (f, v) => f with { ModDownload = f.ModDownload with { ModMetadataDisabled = Variant.AsBool(v) } }),
            Entry("ModDependenciesDisabled",
                f => f.ModDownload.ModDependenciesDisabled,
                (f, v) => f with { ModDownload = f.ModDownload with { ModDependenciesDisabled = Variant.AsBool(v) } }),
            Entry("SkipModpackUpdatePrompt",
                f => f.ModDownload.SkipModpackUpdatePrompt,
                (f, v) => f with { ModDownload = f.ModDownload with { SkipModpackUpdatePrompt = Variant.AsBool(v) } }),
            Entry("ShowModIncompat",
                f => f.ModDownload.ShowModIncompat,
                (f, v) => f with { ModDownload = f.ModDownload with { ShowModIncompat = Variant.AsBool(v) } }),
            Entry("DownloadGameFilesDuringInstanceCreation",
                f => f.ModDownload.DownloadGameFilesDuringInstanceCreation,
                (f, v) => f with
                {
                    ModDownload = f.ModDownload with
                    {
                        DownloadGameFilesDuringInstanceCreation = Variant.AsBool(v),
                    },
                }),
            Entry("ModUpdateReleaseTypes",
                f => f.ModDownload.ModUpdateReleaseTypes,
                (f, v) => f with { ModDownload = f.ModDownload with { ModUpdateReleaseTypes = Variant.AsString(v) } }),

            // ---- java
            Entry("JavaPath",
                f => f.Java.JavaPath,
                (f, v) => f with { Java = f.Java with { JavaPath = Variant.AsString(v) } }),
            Entry("JvmArgs",
                f => f.Java.JvmArgs,
                (f, v) => f with { Java = f.Java with { JvmArgs = Variant.AsString(v) } }),
            Entry("IgnoreJavaCompatibility",
                f => f.Java.IgnoreJavaCompatibility,
                (f, v) => f with { Java = f.Java with { IgnoreJavaCompatibility = Variant.AsBool(v) } }),
            Entry("IgnoreJavaWizard",
                f => f.Java.IgnoreJavaWizard,
                (f, v) => f with { Java = f.Java with { IgnoreJavaWizard = Variant.AsBool(v) } }),
            Entry("AutomaticJavaSwitch",
                f => f.Java.AutomaticJavaSwitch,
                (f, v) => f with { Java = f.Java with { AutomaticJavaSwitch = Variant.AsBool(v) } }),
            Entry("AutomaticJavaDownload",
                f => f.Java.AutomaticJavaDownload,
                (f, v) => f with { Java = f.Java with { AutomaticJavaDownload = Variant.AsBool(v) } }),
            Entry("AutomaticJava",
                f => f.Java.AutomaticJava,
                (f, v) => f with { Java = f.Java with { AutomaticJava = Variant.AsBool(v) } }),

            // ---- dirs (DATA D8: CentralModsDir is dead — zero core consumers, no longer
            // registered; a leftover key in an imported INI stays unread in the file)
            Entry("IconsDir",
                f => f.Dirs.IconsDir,
                (f, v) => f with { Dirs = f.Dirs with { IconsDir = Variant.AsString(v) } }),
            Entry("SkinsDir",
                f => f.Dirs.SkinsDir,
                (f, v) => f with { Dirs = f.Dirs with { SkinsDir = Variant.AsString(v) } }),
            Entry("JavaDir",
                f => f.Dirs.JavaDir,
                (f, v) => f with { Dirs = f.Dirs with { JavaDir = Variant.AsString(v) } }),
            Entry("DownloadsDir",
                f => f.Dirs.DownloadsDir,
                (f, v) => f with { Dirs = f.Dirs with { DownloadsDir = Variant.AsString(v) } }),
            Entry("DownloadsDirWatchRecursive",
                f => f.Dirs.DownloadsDirWatchRecursive,
                (f, v) => f with { Dirs = f.Dirs with { DownloadsDirWatchRecursive = Variant.AsBool(v) } }),
            Entry("MoveModsFromDownloadsDir",
                f => f.Dirs.MoveModsFromDownloadsDir,
                (f, v) => f with { Dirs = f.Dirs with { MoveModsFromDownloadsDir = Variant.AsBool(v) } }),

            // ---- language
            Entry("Language",
                f => f.Language.Language,
                (f, v) => f with { Language = f.Language with { Language = Variant.AsString(v) } }),
            Entry("UseSystemLocale",
                f => f.Language.UseSystemLocale,
                (f, v) => f with { Language = f.Language with { UseSystemLocale = Variant.AsBool(v) } }),

            // ---- mirrors
            Entry("MetaURLOverride",
                f => f.Mirror.MetaUrlOverride,
                (f, v) => f with { Mirror = f.Mirror with { MetaUrlOverride = Variant.AsString(v) } }),
            Entry("ResourceURLOverride",
                f => f.Mirror.ResourceUrlOverride,
                (f, v) => f with { Mirror = f.Mirror with { ResourceUrlOverride = Variant.AsString(v) } }),
            Entry("LegacyFMLLibsURLOverride",
                f => f.Mirror.LegacyFmlLibsUrlOverride,
                (f, v) => f with { Mirror = f.Mirror with { LegacyFmlLibsUrlOverride = Variant.AsString(v) } }),

            // ---- proxy (no Pass member — the password is a secret below)
            Entry("ProxyType",
                f => f.Proxy.ProxyType,
                (f, v) => f with { Proxy = f.Proxy with { ProxyType = Variant.AsString(v) } }),
            Entry("ProxyAddr",
                f => f.Proxy.ProxyAddr,
                (f, v) => f with { Proxy = f.Proxy with { ProxyAddr = Variant.AsString(v) } }),
            Entry("ProxyPort",
                f => f.Proxy.ProxyPort,
                (f, v) => f with { Proxy = f.Proxy with { ProxyPort = Variant.AsInt(v) } }),
            Entry("ProxyUser",
                f => f.Proxy.ProxyUser,
                (f, v) => f with { Proxy = f.Proxy with { ProxyUser = Variant.AsString(v) } }),
            SecretEntry("ProxyPass"),

            // ---- api (no credential members — FlameKeyOverride/ModrinthToken are secrets)
            Entry("MSAClientIDOverride",
                f => f.Api.MsaClientIdOverride,
                (f, v) => f with { Api = f.Api with { MsaClientIdOverride = Variant.AsString(v) } }),
            Entry("UserAgentOverride",
                f => f.Api.UserAgentOverride,
                (f, v) => f with { Api = f.Api with { UserAgentOverride = Variant.AsString(v) } }),
            Entry("PastebinType",
                f => f.Api.PastebinType,
                (f, v) => f with { Api = f.Api with { PastebinType = Variant.AsInt(v) } }),
            Entry("PastebinCustomAPIBase",
                f => f.Api.PastebinCustomApiBase,
                (f, v) => f with { Api = f.Api with { PastebinCustomApiBase = Variant.AsString(v) } }),
            SecretEntry("FlameKeyOverride"),
            SecretEntry("ModrinthToken"),

            // ---- tools
            Entry("JsonEditor",
                f => f.Tools.JsonEditor,
                (f, v) => f with { Tools = f.Tools with { JsonEditor = Variant.AsString(v) } }),
            Entry("JProfilerPath",
                f => f.Tools.JProfilerPath,
                (f, v) => f with { Tools = f.Tools with { JProfilerPath = Variant.AsString(v) } }),
            Entry("JVisualVMPath",
                f => f.Tools.JVisualVmPath,
                (f, v) => f with { Tools = f.Tools with { JVisualVmPath = Variant.AsString(v) } }),
            Entry("MCEditPath",
                f => f.Tools.McEditPath,
                (f, v) => f with { Tools = f.Tools with { McEditPath = Variant.AsString(v) } }),
        ];

        var map = new Dictionary<string, ConfigKeyEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            map.Add(entry.Key, entry);
        }

        return map;
    }
}
