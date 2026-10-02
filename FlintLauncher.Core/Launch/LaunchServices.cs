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

namespace FlintLauncher.Core.Launch;

public sealed class LaunchServices
{
    public required FlintPaths Paths { get; init; }

    public required Microsoft.Extensions.Logging.ILoggerFactory LoggerFactory { get; init; }

    /// <summary>Shared metadata cache for library/asset-index downloads (the C++
    /// APPLICATION->metacache()). The update steps read the INSTANCE-stamped cache
    /// (MinecraftInstance.MetaCache — the same object the composition root registered);
    /// this mirror exists for pure-core consumers of the services. Since E6 there is no
    /// bare-fixture fallback anywhere: an unstamped instance fails the update steps.</summary>
    public HttpMetaCache? MetaCache { get; init; }

    /// <summary>The typed flint.json store (the user layer of the C7 stack — D4 retired the
    /// SettingsObject adapter view). AutoInstallJava reads the java/download switches from
    /// it; null keeps the bare-fixture "feature off" behavior.</summary>
    public Core.Config.FlintConfigStore? Config { get; init; }

    /// <summary>The launcher runtime state (launcher-state.json, S5): the java probe cache
    /// (CheckJava/VerifyJavaInstall) and the offline-name write-back (LaunchController) live
    /// here since CONFIG C9 moved them out of the settings world. EMBEDDING REQUIREMENT:
    /// hosts must wire it — with a null store CheckJava's probe results are dropped (each
    /// launch re-probes) and VerifyJavaInstall sees an unprobed java, while the offline-name
    /// persistence silently degrades to per-launch defaults. All in-repo construction sites
    /// (WebHostBuilder, launch fixtures) wire it.</summary>
    public Core.State.LauncherStateStore? LauncherState { get; init; }

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

    /// <summary>Resolved launcher-managed java root (CACHE E2 via GLOBAL §3: the JavaDir
    /// setting under STORE semantics — relative joins the store root). The host provides it
    /// so AutoInstallJava installs into the configured store location; null keeps the
    /// Paths.JavaRoot anchor (the store/java default).</summary>
    public Func<string>? JavaRootProvider { get; init; }

    /// <summary>The effective java root: the provider's resolution when wired, else the
    /// store-root java anchor (the same store semantics with the default dir name).</summary>
    public string JavaRoot => JavaRootProvider?.Invoke() ?? Paths.JavaRoot;

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
