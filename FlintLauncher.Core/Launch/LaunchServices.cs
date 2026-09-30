// SPDX-License-Identifier: GPL-3.0-only
// The launch pipeline's injected dependencies. The C++ steps reached their environment
// through the APPLICATION singleton (icons, settings, meta cache, jar paths, dialogs); the
// port first approximated that with static hooks (settable static Action?/Func?/flags).
// This class replaces those hooks: the composition root builds one LaunchServices per
// launch/runtime and LaunchTask/launch steps read their dependencies from it — real
// constructor injection, no process-global state, multiple parallel pipelines possible
// (the integration tests build one per test).
//
// The members are intentionally mutable on a per-instance basis only: a host or test may
// adjust UiHooks/XmlLogParsing on ITS OWN LaunchServices before handing it to a launch, and
// no other pipeline in the process can observe that.
using System.Net;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Java;
using FlintLauncher.Core.Meta;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Settings;

namespace FlintLauncher.Core.Launch;

public sealed class LaunchServices
{
    public required FlintPaths Paths { get; init; }

    public required Microsoft.Extensions.Logging.ILoggerFactory LoggerFactory { get; init; }

    /// <summary>Shared metadata cache for library/asset-index downloads (the C++
    /// APPLICATION->metacache() the update tasks receive). Null keeps the legacy one-shot
    /// fallback caches; hosts always set it.</summary>
    public HttpMetaCache? MetaCache { get; init; }

    /// <summary>APLLICATION->settings(): global launcher settings (AutoInstallJava reads
    /// java/download keys from it; LaunchController's offline-name flow).</summary>
    public IniSettingsObject? GlobalSettings { get; init; }

    /// <summary>Parse log4j XML events out of the game output (LaunchTask::parseXmlLogs; the
    /// C++ had no switch — the host enables it).</summary>
    public bool XmlLogParsing { get; init; }

    /// <summary>Where the Flint binary itself lives (jar extraction, share/&lt;launcher&gt;);
    /// replaces the JavaUtils.LauncherRootPath static.</summary>
    public string InstallRoot { get; init; } = AppContext.BaseDirectory;

    /// <summary>Test/override DNS endpoint for the SRV lookup (SrvResolver via
    /// LookupServerAddress; the C++ had QDnsLookup on the system resolver). Null queries the
    /// system nameserver.</summary>
    public IPEndPoint? SrvServerEndpoint { get; init; }

    /// <summary>C++ settings "ResourceURLOverride" (AssetUpdateTask asset-object base URL).
    /// Null keeps the build default base.</summary>
    public string? ResourceUrlOverride { get; init; }

    /// <summary>C++ settings "LegacyFMLLibsURLOverride" (LegacyFMLLibrariesTask download
    /// base). Null keeps the build default base.</summary>
    public string? LegacyFmllibsUrlOverride { get; init; }

    /// <summary>C++ APPLICATION->javalist() (AutoInstallJava's "no download" branch); null
    /// degrades to the upstream "no compatible Java version was found" warning.</summary>
    public Func<JavaInstallList?>? JavaInstallListProvider { get; init; }

    /// <summary>C++ APPLICATION->metadataIndex() (AutoInstallJava's java-meta list); null
    /// keeps the instance PackProfile's index.</summary>
    public Func<MinecraftInstance, Meta.Index>? MetadataIndexProvider { get; init; }

    /// <summary>GUI-only interaction points (dialogs, window management). Unset members keep
    /// the headless defaults — the C++ GUI host would set them.</summary>
    public LaunchUiHooks Ui { get; init; } = new();
}

/// <summary>The C++ GUI touchpoints of the launch pipeline, as per-instance hooks instead of
/// static fields. All nullable: unset means the headless default (skip).</summary>
public sealed class LaunchUiHooks
{
    /// <summary>CloseAfterLaunch mode: the game logged "Setting user ..." — close the launcher
    /// windows (LauncherPartLaunch).</summary>
    public Action? CloseAfterLaunch;

    /// <summary>CloseAfterLaunch mode: the game exited — show the main window again
    /// (LauncherPartLaunch).</summary>
    public Action? ShowMainWindow;

    /// <summary>Low memory pressure warning text — return true to proceed with the launch,
    /// false to abort (EnsureAvailableMemory; the C++ shows a confirm dialog).</summary>
    public Func<string, bool>? ConfirmLaunch;

    /// <summary>The game stopped and the C++ requested quitting the application
    /// (QuitAfterGameStop).</summary>
    public Action? QuitRequested;

    /// <summary>Copy the instance icon into the game root (icon path, target path)
    /// (MinecraftInstance; C++ APPLICATION->icons()->saveIcon).</summary>
    public Action<string, string>? SaveIcon;

    /// <summary>Primary screen available size for maximized launch scripts; unset falls back
    /// to the configured window size (MinecraftInstance).</summary>
    public Func<(int Width, int Height)>? PrimaryScreenSize;
}
