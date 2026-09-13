// SPDX-License-Identifier: GPL-3.0-only
// The global-settings registration half of the C++ Application::registerSettings(): the set
// of Setting objects every instance/launch code path resolves through RegisterOverride/
// RegisterPassthrough gates. The FlintLauncher.Web host calls this once at startup so the
// instance ctor overrides can bind; tests keep their own stub registrations.
//
// The defaults mirror the C++ global defaults (launcher/Application.cpp) as also encoded in
// the test GlobalLaunchSettingsStub; keys not carrying a C++ default are registered with a
// neutral default so reads never return null.
//
// NOTE: the playtime keys ("TotalPlayTime"/"TotalPlayTimeMigrated") are registered here as
// well so the same object can serve as the InstanceList/BaseInstance PlaytimeSettings hook.
using FlintLauncher.Core.Settings;

namespace FlintLauncher.Core;

public static class GlobalSettingsRegistration
{
    /// <summary>
    /// Register every global setting the ported instance/launch layer reads (idempotent:
    /// already-registered keys are left untouched, like RegisterSetting's duplicate check).
    /// </summary>
    public static void RegisterGlobalDefaults(SettingsObject global)
    {
        ArgumentNullException.ThrowIfNull(global);

        (string Id, object? Default)[] defaults =
        [
            // game time
            ("ShowGameTime", true),
            ("ShowGameTimeWithoutDays", false),
            ("RecordGameTime", true),
            // commands
            ("PreLoadCommand", ""),
            ("PreLaunchCommand", ""),
            ("WrapperCommand", ""),
            ("PostExitCommand", ""),
            // console
            ("ShowConsole", false),
            ("AutoCloseConsole", false),
            ("ShowConsoleOnError", false),
            ("LogPrePostOutput", true),
            ("ConsoleMaxLines", 100000),
            ("ConsoleOverflowStop", false),
            // java
            ("JavaPath", "java"),
            ("JvmArgs", ""),
            ("IgnoreJavaCompatibility", false),
            ("JavaSignature", ""),
            ("JavaArchitecture", ""),
            ("JavaRealArchitecture", ""),
            ("JavaVersion", ""),
            ("JavaVendor", ""),
            ("AutomaticJavaSwitch", false),
            ("AutomaticJavaDownload", false),
            // window
            ("LaunchMaximized", false),
            ("MinecraftWinWidth", 854),
            ("MinecraftWinHeight", 480),
            // memory
            ("MinMemAlloc", 512),
            ("MaxMemAlloc", 4096),
            ("PermGen", 64),
            ("LowMemWarning", false),
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
            // mod lists
            ("ModMetadataDisabled", false),
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
