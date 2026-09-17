// SPDX-License-Identifier: GPL-3.0-only
// The global-settings registration half of the C++ Application::registerSettings(): the set
// of Setting objects every instance/launch code path resolves through RegisterOverride/
// RegisterPassthrough gates. The FlintLauncher.Web host calls this once at startup so the
// instance ctor overrides can bind; tests keep their own stub registrations.
//
// The defaults mirror the C++ global defaults (launcher/Application.cpp registerSettings) —
// including the console (ShowConsoleOnError/ConsoleOverflowStop true), memory (PermGen 128,
// MaxMemAlloc = SysInfo::defaultMaxJvmMem) and java (JavaPath "" with the automatic-java
// switches derived from the empty path) values. Keys not carrying a C++ default are
// registered with a neutral default so reads never return null.
//
// NOTE: the playtime keys ("TotalPlayTime"/"TotalPlayTimeMigrated") are registered here as
// well so the same object can serve as the InstanceList/BaseInstance PlaytimeSettings hook.
using FlintLauncher.Core.Launch;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Settings;

namespace FlintLauncher.Core;

public static class GlobalSettingsRegistration
{
    /// <summary>
    /// Port of SysInfo::defaultMaxJvmMem: below 6 GiB of total RAM the heap default is
    /// total/1.5, else 4 GiB. Deviation: unknown RAM (TotalRamMiB() == 0) keeps the usable
    /// 4 GiB default instead of the C++ 0.
    /// </summary>
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

    /// <summary>QStandardPaths::writableLocation(DownloadLocation) approximation.</summary>
    public static string DefaultDownloadsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>
    /// Register every global setting the ported instance/launch layer reads (idempotent:
    /// already-registered keys are left untouched, like RegisterSetting's duplicate check).
    /// The table follows the C++ Application::registerSettings() sections.
    /// </summary>
    public static void RegisterGlobalDefaults(SettingsObject global)
    {
        ArgumentNullException.ThrowIfNull(global);

        (string Id, object? Default)[] defaults =
        [
            // concurrency / network (NetJob + Net::Request read these per transfer)
            ("NumberOfConcurrentTasks", 10),
            ("NumberOfConcurrentDownloads", 6),
            ("NumberOfManualRetries", 1),
            ("RequestTimeout", 60),
            // proxy (Application::updateProxySettings)
            ("ProxyType", "None"),
            ("ProxyAddr", "127.0.0.1"),
            ("ProxyPort", 8080),
            ("ProxyUser", ""),
            ("ProxyPass", ""),
            // meta / resource mirrors (the C++ resets invalid URLs; the port stores the raw string)
            ("MetaURLOverride", ""),
            ("ResourceURLOverride", ""),
            ("LegacyFMLLibsURLOverride", ""),
            // api credentials / overrides (APIPage)
            ("MSAClientIDOverride", ""),
            ("UserAgentOverride", ""),
            ("FlameKeyOverride", ""),
            ("ModrinthToken", ""),
            // paste providers (the registerSettings "HACK" block; GuiUtil reads them)
            ("PastebinURL", ""),
            ("PastebinType", (int)PasteUpload.PasteType.Mclogs),
            ("PastebinCustomAPIBase", ""),
            // folders (relative to the data dir, like the C++ defaults)
            ("CentralModsDir", "mods"),
            ("IconsDir", "icons"),
            ("DownloadsDir", DefaultDownloadsDir),
            ("DownloadsDirWatchRecursive", false),
            ("MoveModsFromDownloadsDir", false),
            ("SkinsDir", "skins"),
            ("JavaDir", "java"),
            // language (LanguagePage)
            ("Language", ""),
            ("UseSystemLocale", false),
            // instance list behavior (InstancePage)
            ("InstSortMode", "Name"),
            ("InstRenamingMode", "AskEverytime"),
            // game time
            ("ShowGameTime", true),
            ("ShowGlobalGameTime", true),
            ("ShowGameTimeWithoutDays", false),
            ("RecordGameTime", true),
            // minecraft mods (ModFolderModel / creation tasks)
            ("ModMetadataDisabled", false),
            ("ModDependenciesDisabled", false),
            ("SkipModpackUpdatePrompt", false),
            ("ShowModIncompat", false),
            ("DownloadGameFilesDuringInstanceCreation", true),
            // commands
            ("PreLoadCommand", ""),
            ("PreLaunchCommand", ""),
            ("WrapperCommand", ""),
            ("PostExitCommand", ""),
            // console
            ("ShowConsole", false),
            ("AutoCloseConsole", false),
            ("ShowConsoleOnError", true),
            ("LogPrePostOutput", true),
            ("ConsoleMaxLines", 100000),
            ("ConsoleOverflowStop", true),
            // java
            ("JavaPath", ""),
            ("JvmArgs", ""),
            ("IgnoreJavaCompatibility", false),
            ("IgnoreJavaWizard", false),
            ("JavaSignature", ""),
            ("JavaArchitecture", ""),
            ("JavaRealArchitecture", ""),
            ("JavaVersion", ""),
            ("JavaVendor", ""),
            ("LastHostname", ""),
            // the automatic-java switches derive from the (empty) JavaPath default in the C++
            ("AutomaticJavaSwitch", true),
            ("AutomaticJavaDownload", true),
            ("UserAskedAboutAutomaticJavaDownload", false),
            // window
            ("LaunchMaximized", false),
            ("MinecraftWinWidth", 854),
            ("MinecraftWinHeight", 480),
            // memory
            ("MinMemAlloc", 512),
            ("MaxMemAlloc", DefaultMaxMemAlloc),
            ("PermGen", 128),
            ("LowMemWarning", true),
            // native library workarounds
            ("UseNativeOpenAL", false),
            ("CustomOpenALPath", ""),
            ("UseNativeGLFW", false),
            ("CustomGLFWPath", ""),
            ("UseNativeSDL", false),
            ("CustomSDLPath", ""),
            // performance
            ("EnableFeralGamemode", false),
            ("EnableMangoHud", false),
            ("UseDiscreteGpu", false),
            ("UseZink", false),
            // miscellaneous
            ("CloseAfterLaunch", false),
            ("QuitAfterGameStop", false),
            ("OnlineFixes", false),
            ("Env", "{}"),
            // offline-name storage (LaunchController.GlobalSettingsProvider)
            ("LastOfflinePlayerName", ""),
            // playtime accumulation (C++ APPLICATION->playtimeSettings())
            ("TotalPlayTime", 0L),
            ("TotalPlayTimeMigrated", false),
        ];

        foreach (var (id, def) in defaults)
        {
            if (global.GetSetting(id) is null)
            {
                global.RegisterSetting(id, def);
            }
        }
    }
}
