// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/MinecraftInstance.{h,cpp} (launch/update execution excluded).
//
// Deviations from the C++ original:
//  - Application-singleton reach-ins become injectable hooks: playtimeSettings() ->
//    <see cref="BaseInstance.LauncherState"/> (launcher-state.json since S5/C9);
//    m_detectedOpenALPath/m_detectedGLFWPath/
//    m_detectedSDLPath -> the Detected*Path properties; APPLICATION->metacache() ->
//    <see cref="MetaCache"/> (library_directory defaults to "" while unset); APPLICATION->
//    icons()->saveIcon -> <see cref="LaunchServices.Ui"/>.SaveIcon; the QGuiApplication
//    screen geometry of the launch script -> <see cref="LaunchServices.Ui"/>.PrimaryScreenSize.
//    The launch pipeline's full dependency set (logger factory, global settings, install
//    root, UI hooks) travels through the instance-stamped <see cref="LaunchServices"/> /
//    the per-launch services CreateLaunchTask builds. The global settings object
//    (APPLICATION->settings()) is the injected BaseInstance global settings.
//  - The Q_OS_* ifdef'ed bits port to OperatingSystem checks. switcherooSetupGPU() (DBus)
//    and LibraryUtils::findMangoHud() are system-probing process-launch helpers and are
//    TODO(P5-c): the discrete-GPU path always applies the open-driver env fallback.
//  - createUpdateTask()/createLaunchTask()/createLaunchScript() are the P5-b launch pipeline
//    (Launch/LaunchTask + Launch/Steps/*). The switcheroo/MangoHud capability probing and
//    the icon save remain hooks (see above).
//  - The CleanEnviroment() base of createEnvironment() is the P5a JavaUtils.CleanEnvironment().
//  - The folder models for resource packs, texture packs, shader packs and data packs are
//    not ported yet (only ModFolderModel/WorldList exist), so resourcePackList()/
//    texturePackList()/shaderPackList()/dataPackList()/resourceLists() are omitted; the
//    GlobalDataPacksEnabled/Path change hooks that reset the data pack model have nothing
//    to reset here yet. getJarMods()/verboseDescription() only need the mod lists and are
//    ported.
//  - getStatusbarDescription(): upstream kicks off an offline PackProfile reload when the
//    Minecraft version is missing and re-reads immediately. The port mirrors the reload's
//    inline (synchronous) part — SaveNow + InvalidateLaunchProfile + Load — and skips only
//    the asynchronous resolve (ReloadAsync is the awaitable form). The Qt locale short date
//    format maps to the CurrentCulture "g" format.
//  - AssetsUtils::getAssetsDir() is ported locally, reduced to the branch the profile
//    variable mapping needs (which of the virtual/resources paths applies — only the two
//    booleans of the assets index JSON are consulted).
//  - The OrSetting class in MinecraftInstance.cpp is dead code upstream (never used in the
//    revision) and is not ported. getProblems() does not exist on instances upstream.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Java;
using FlintLauncher.Core.Json;
using FlintLauncher.Core.Launch;
using FlintLauncher.Core.Launch.Steps;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Minecraft.Resources;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Settings;
using FlintLauncher.Core.State;
using FlintLauncher.Core.Tasks;
using FlintLauncher.Core.Versioning;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlintLauncher.Core.Instances;

public class MinecraftInstance : BaseInstance, IPackProfileEnv
{
    private static readonly Regex TokenRegex = new(@"\$\{(.+?)\}", RegexOptions.CultureInvariant); // InvertedGreediness

    public MinecraftInstance(SettingsObject settings, string rootDir)
        : base(settings, rootDir)
    {
        Components = new PackProfile(this);
    }

    // Application-singleton hooks (see the file header)
    /// <summary>C++ APPLICATION->m_detectedOpenALPath; used by extraArguments().</summary>
    public string DetectedOpenAlPath { get; set; } = "";

    /// <summary>C++ APPLICATION->m_detectedGLFWPath; used by extraArguments().</summary>
    public string DetectedGlfwPath { get; set; } = "";

    /// <summary>C++ APPLICATION->m_detectedSDLPath; used by extraArguments().</summary>
    public string DetectedSdlPath { get; set; } = "";

    /// <summary>C++ APPLICATION->metacache(); used for the library_directory profile variable,
    /// the library/asset-index download caches and the metadata entities. E6 wiring: setting
    /// it ALSO stamps the pack profile's metadata index (the "meta" base chain — the
    /// SetMetadataCache call the former static SharedCache default papered over).</summary>
    public HttpMetaCache? MetaCache
    {
        get => metaCache;
        set
        {
            metaCache = value;
            if (value is not null)
            {
                Components?.SetMetadataCache(value);
            }
        }
    }

    private HttpMetaCache? metaCache;

    /// <summary>Injected data-directory layout (assets/libraries/java roots, instgroups.json).
    /// Null keeps the legacy CWD-anchored behavior for bare fixtures; hosts always set it
    /// (InstanceList stamps it at discovery).</summary>
    public FlintPaths? Paths { get; set; }

    /// <summary>Injected launch-pipeline dependencies (paths, logger factory, global settings,
    /// install root, UI hooks — the former static hooks). Stamped by the composition root
    /// (InstanceList) or set directly by tests; CreateLaunchTask uses it as the base for the
    /// per-launch services and falls back to headless defaults while null.</summary>
    public LaunchServices? LaunchServices { get; set; }

    public override void SaveNow()
    {
        Components!.SaveNow();
    }

    public override void LoadSpecificSettings()
    {
        if (IsSpecificSettingsLoaded())
        {
            return;
        }

        // Java Settings (C7: the gate keys register as derived keys — inheritance is the
        // fold, the override chains are gone; the java probe quintet is NOT here, it lives
        // in launcher-state.json's path-keyed map, S5/C9)
        _settings.RegisterSetting("OverrideJavaLocation", false);
        _settings.RegisterSetting("OverrideJavaArgs", false);
        _settings.RegisterSetting("AutomaticJava", false);
        _settings.RegisterSetting("UseLatestMinecraftVersion", false);
        _settings.RegisterSetting("UseLatestMinecraftVersionType", "release");

        _settings.RegisterSetting("OverrideWindow", false);
        _settings.RegisterSetting("LaunchMaximized", false);
        _settings.RegisterSetting("MinecraftWinWidth", 854);
        _settings.RegisterSetting("MinecraftWinHeight", 480);
        _settings.RegisterSetting("OverrideMemory", false);
        _settings.RegisterSetting("MinMemAlloc", 512);
        _settings.RegisterSetting("MaxMemAlloc", 4096);
        _settings.RegisterSetting("LowMemWarning", true);
        _settings.RegisterSetting("OverrideNativeWorkarounds", false);
        _settings.RegisterSetting("UseNativeOpenAL", false);
        _settings.RegisterSetting("CustomOpenALPath", "");
        _settings.RegisterSetting("UseNativeGLFW", false);
        _settings.RegisterSetting("CustomGLFWPath", "");
        _settings.RegisterSetting("UseNativeSDL", false);
        _settings.RegisterSetting("CustomSDLPath", "");
        _settings.RegisterSetting("OverridePerformance", false);
        _settings.RegisterSetting("EnableFeralGamemode", false);
        _settings.RegisterSetting("EnableMangoHud", false);
        _settings.RegisterSetting("UseDiscreteGpu", false);
        _settings.RegisterSetting("UseZink", false);
        _settings.RegisterSetting("OverrideMiscellaneous", false);
        _settings.RegisterSetting("CloseAfterLaunch", false);
        _settings.RegisterSetting("QuitAfterGameStop", false);
        _settings.RegisterSetting("OverrideLegacySettings", false);
        _settings.RegisterSetting("OnlineFixes", false);
        _settings.RegisterSetting("OverrideEnv", false);
        _settings.RegisterSetting("Env", "{}");
        // the layered member keys resolve instance ?? user through the adapter — the
        // registered values are type anchors only (C6 defaults live in the flint.json
        // record property initializers, the single source)
        _settings.RegisterSetting("JavaPath", "");
        _settings.RegisterSetting("JvmArgs", "");
        _settings.RegisterSetting("IgnoreJavaCompatibility", false);

        // the C++ force-wrote InstanceType=OneSix here; with the typed layer the record
        // default IS OneSix (BaseInstance registration) and a non-OneSix type never
        // constructs a MinecraftInstance (InstanceList yields NullInstance instead), so
        // a fresh load performs no write — the dual-track read-only guarantee (D4)

        // Join server on launch, this does not have a global override
        _settings.RegisterSetting("JoinServerOnLaunch", false);
        _settings.RegisterSetting("JoinServerOnLaunchAddress", "");
        _settings.RegisterSetting("JoinWorldOnLaunch", "");

        // Use account for instance, this does not have a global override
        _settings.RegisterSetting("UseAccountForInstance", false);
        _settings.RegisterSetting("InstanceAccountId", "");

        _settings.RegisterSetting("ExportName", "");
        _settings.RegisterSetting("ExportVersion", "1.0.0");
        _settings.RegisterSetting("ExportSummary", "");
        _settings.RegisterSetting("ExportAuthor", "");
        _settings.RegisterSetting("ExportOptionalFiles", true);
        _settings.RegisterSetting("ExportRecommendedRAM");

        _settings.RegisterSetting("GlobalDataPacksEnabled", false);
        _settings.RegisterSetting("GlobalDataPacksPath", "");
        // (upstream connects both settings to reset the data pack folder model; that model is
        //  not ported yet, so there is nothing to reset here)

        // Mod download loaders (the C7 OverrideModDownloadLoaders family; instance-only)
        _settings.RegisterSetting("OverrideModDownloadLoaders", false);
        _settings.RegisterSetting("ModDownloadLoaders", "[]");

        SetSpecificSettingsLoaded(true);

        UpdateRuntimeContext();
    }

    public override void UpdateRuntimeContext()
    {
        // RuntimeContext::updateFromInstanceSettings (the member itself lives on the C++
        // RuntimeContext struct; the C# struct has no SettingsObject dependency). The
        // probed architectures come from the state-side probe cache (S5 path-keyed map;
        // the former JavaArchitecture/JavaRealArchitecture settings keys are gone, C9).
        var detection = JavaDetection();
        // the struct property returns a copy — read-modify-write it back
        var context = RuntimeContext;
        context.JavaArchitecture = detection?.Architecture ?? "";
        context.JavaRealArchitecture = detection?.RealArchitecture ?? "";
        RuntimeContext = context;
        Components!.InvalidateLaunchProfile();
    }

    public PackProfile GetPackProfile() => Components!;

    public override IReadOnlySet<string> Traits()
    {
        var components = GetPackProfile();
        if (components is null)
        {
            return new HashSet<string> { "version-incomplete" };
        }
        var profile = components.GetProfile();
        if (profile is null)
        {
            return new HashSet<string> { "version-incomplete" };
        }
        return profile.GetTraits();
    }

    public override bool CanEdit() => true;

    public override bool CanExport() => true;

    ////// Directories and files //////

    public string JarModsDir() => QDirAbsolutePath(FileSystem.PathCombine(InstanceRoot(), "jarmods/"));

    public string ResourcePacksDir() => FileSystem.PathCombine(GameRoot(), "resourcepacks");

    public string TexturePacksDir() => FileSystem.PathCombine(GameRoot(), "texturepacks");

    public string ShaderPacksDir() => FileSystem.PathCombine(GameRoot(), "shaderpacks");

    public override string ModsRoot() => FileSystem.PathCombine(GameRoot(), "mods");

    public string CoreModsDir() => FileSystem.PathCombine(GameRoot(), "coremods");

    public string NilModsDir() => FileSystem.PathCombine(GameRoot(), "nilmods");

    public string DataPacksDir()
    {
        var relativePath = Variant.AsString(Settings.Get("GlobalDataPacksPath"));

        if (relativePath.Length == 0)
        {
            relativePath = "datapacks";
        }

        return FileSystem.PathCombine(GameRoot(), relativePath);
    }

    public string ModsCacheLocation() => FileSystem.PathCombine(InstanceRoot(), "mods.cache");

    public string LibDir() => FileSystem.PathCombine(GameRoot(), "lib");

    public string WorldDir() => FileSystem.PathCombine(GameRoot(), "saves");

    public string ResourcesDir() => FileSystem.PathCombine(GameRoot(), "resources");

    /// <summary>QDir::current().absoluteFilePath("libraries").</summary>
    public string LibrariesPath() => Paths?.LibrariesRoot ?? QDirAbsolutePath("libraries");

    public string JarmodsPath() => JarModsDir();

    public override string InstanceConfigFolder() => FileSystem.PathCombine(GameRoot(), "config");

    /// <summary>Path to the instance's minecraft directory.</summary>
    public override string GameRoot()
    {
        var mcDir = FileSystem.PathCombine(InstanceRoot(), "minecraft");
        var dotMcDir = FileSystem.PathCombine(InstanceRoot(), ".minecraft");

        if (FsExists(dotMcDir) && !FsExists(mcDir))
        {
            return dotMcDir;
        }
        return mcDir;
    }

    /// <summary>Path to the instance's minecraft bin directory.</summary>
    public string BinRoot() => FileSystem.PathCombine(GameRoot(), "bin");

    /// <summary>where to put the natives during/before launch</summary>
    public string GetNativePath() => QDirAbsolutePath(FileSystem.PathCombine(InstanceRoot(), "natives/"));

    /// <summary>where the instance-local libraries should be</summary>
    public string GetLocalLibraryPath() => QDirAbsolutePath(FileSystem.PathCombine(InstanceRoot(), "libraries/"));

    /// <summary>Returns whether the instance, with its version, has support for demo mode.</summary>
    public bool SupportsDemo()
    {
        var instanceVer = new McVersion(GetPackProfile().GetComponentVersion("net.minecraft"));
        // Demo mode was introduced in 1.3.1: https://minecraft.wiki/w/Demo_mode#History
        // FIXME upstream: Due to Version constraints atm, this can't handle well non-release versions
        return instanceVer >= new McVersion("1.3.1");
    }

    //////  Profile management //////

    //////  Launch stuff //////

    // TODO(P5): the C++ assembles FoldersTask, LibrariesTask, LegacyFMLLibrariesTask and
    // AssetUpdateTask here (the update pipeline). Ported in Launch/LaunchController and the
    // Launch/Steps/* tasks below.
    public override List<FlintTask> CreateUpdateTask() =>
    [
        // create folders
        new FoldersTask(this),
        // libraries download
        new LibrariesTask(this),
        // FML libraries download and copy into the instance
        new LegacyFmlLibrariesTask(this),
        // assets update
        new AssetUpdateTask(this),
    ];

    /// <summary>Port of createLaunchTask() (2-arg form): builds the per-launch services from
    /// the instance-stamped LaunchServices (or headless defaults).</summary>
    public override LaunchTask CreateLaunchTask(AuthSession? session, MinecraftTarget? targetToJoin) =>
        CreateLaunchTask(session, targetToJoin, services: null);

    /// <summary>Port of createLaunchTask(): builds the <see cref="Launch.LaunchServices"/> for
    /// this launch (the explicit <paramref name="services"/> wins, then the instance-stamped
    /// ones, then headless defaults), hands it to the LaunchTask (every step reads its
    /// environment from Parent.Services) and invokes the UI's saveIcon (C++
    /// APPLICATION->icons()->saveIcon) — the former SaveIconHook static.</summary>
    public LaunchTask CreateLaunchTask(AuthSession? session, MinecraftTarget? targetToJoin,
        LaunchServices? services)
    {
        var launchServices = services ?? LaunchServices ?? BuildLaunchServices();
        UpdateRuntimeContext();
        var process = LaunchTask.Create(this, launchServices);

        launchServices.Ui.SaveIcon?.Invoke(IconKey(), FileSystem.PathCombine(GameRoot(), "icon.png"));

        // print a header
        process.AppendStep(new TextPrint(process, "Minecraft folder is:\n  " + GameRoot() + "\n",
            MessageLevel.Launcher));

        // create the .minecraft folder and server-resource-packs (workaround for Minecraft bug MCL-3732)
        process.AppendStep(new CreateGameFolders(process));

        if (targetToJoin is null && Variant.AsBool(Settings.Get("JoinServerOnLaunch")))
        {
            var fullAddress = Variant.AsString(Settings.Get("JoinServerOnLaunchAddress"));
            if (fullAddress.Length != 0)
            {
                targetToJoin = MinecraftTarget.Parse(fullAddress, false);
            }
            else
            {
                var world = Variant.AsString(Settings.Get("JoinWorldOnLaunch"));
                if (world.Length != 0)
                {
                    targetToJoin = MinecraftTarget.Parse(world, true);
                }
            }
        }

        if (targetToJoin is not null && targetToJoin.Port == 25565)
        {
            // Resolve server address to join on launch
            var step = new LookupServerAddress(process, targetToJoin);
            step.LookupAddress = targetToJoin.Address;
            process.AppendStep(step);
        }

        // run pre-load command if that's needed, before the metadata is loaded
        if (GetPreLoadCommand().Length != 0)
        {
            var step = new LaunchCommand(process, GetPreLoadCommand(), "Pre-Load");
            step.SetWorkingDirectory(GameRoot());
            process.AppendStep(step);
        }

        // load meta
        {
            var mode = session?.LaunchMode != LaunchMode.Offline ? NetMode.Online : NetMode.Offline;
            process.AppendStep(new TaskStepWrapper(process, new MinecraftLoadAndCheck(this, mode)));
        }

        // check java
        {
            process.AppendStep(new AutoInstallJava(process));
            process.AppendStep(new CheckJava(process));
            // verify that minimum Java requirements are met
            process.AppendStep(new VerifyJavaInstall(process));
        }

        // run pre-launch command if that's needed
        if (GetPreLaunchCommand().Length != 0)
        {
            var step = new LaunchCommand(process, GetPreLaunchCommand(), "Pre-Launch");
            step.SetWorkingDirectory(GameRoot());
            process.AppendStep(step);
        }

        // if we aren't in offline mode
        if (session?.LaunchMode != LaunchMode.Offline)
        {
            process.AppendStep(new ClaimAccount(process, session));
            foreach (var t in CreateUpdateTask())
            {
                process.AppendStep(new TaskStepWrapper(process, t));
            }
        }
        else
        {
            process.AppendStep(new EnsureOfflineLibraries(process, this));
        }

        // if there are any jar mods
        process.AppendStep(new ModMinecraftJar(process));

        // Scan mods folders for mods
        process.AppendStep(new ScanModFolders(process));

        // make sure we have enough RAM, warn the user if we don't
        process.AppendStep(new EnsureAvailableMemory(process, this));

        // print some instance info here...
        process.AppendStep(new PrintInstanceInfo(process, session, targetToJoin));

        // extract native jars if needed
        process.AppendStep(new ExtractNatives(process));

        // reconstruct assets if needed
        process.AppendStep(new ReconstructAssets(process));

        {
            // actually launch the game
            var step = new LauncherPartLaunch(process);
            step.SetWorkingDirectory(GameRoot());
            step.Session = session;
            step.TargetToJoin = targetToJoin;
            process.AppendStep(step);
        }

        // run post-exit command if that's needed
        if (GetPostExitCommand().Length != 0)
        {
            var step = new LaunchCommand(process, GetPostExitCommand(), "Post-Launch");
            step.SetWorkingDirectory(GameRoot());
            process.AppendStep(step);
        }
        if (session is not null)
        {
            process.SetCensorFilter(CreateCensorFilterFromSession(session));
        }
        if (Variant.AsBool(_settings.Get("QuitAfterGameStop")))
        {
            process.AppendStep(new QuitAfterGameStop(process));
        }
        LaunchProcess = process;
        RaiseLaunchTaskChanged();
        return process;
    }

    /// <summary>Headless LaunchServices for bare fixtures (no stamped services, no explicit
    /// per-launch override): CWD-anchored paths, a null logger and the injected instance
    /// cache/settings — the same behavior the former unset static hooks produced.</summary>
    private LaunchServices BuildLaunchServices() => new()
    {
        Paths = Paths ?? FlintPaths.FromHomeDir(Directory.GetCurrentDirectory()),
        LoggerFactory = NullLoggerFactory.Instance,
        MetaCache = MetaCache,
        Config = UserConfigStore,
        InstallRoot = Paths?.InstallRoot ?? AppContext.BaseDirectory,
    };

    public string CreateLaunchScript(AuthSession? session, MinecraftTarget? targetToJoin) =>
        CreateLaunchScript(session, targetToJoin, services: null);

    public string CreateLaunchScript(AuthSession? session, MinecraftTarget? targetToJoin,
        LaunchServices? services)
    {
        var launchScript = "";

        if (Components is null)
        {
            return "";
        }
        var profile = Components.GetProfile();
        if (profile is null)
        {
            return "";
        }

        var mainClass = GetMainClass();
        if (mainClass.Length != 0)
        {
            launchScript += "mainClass " + mainClass + "\n";
        }
        var appletClass = profile.GetAppletClass();
        if (appletClass.Length != 0)
        {
            launchScript += "appletClass " + appletClass + "\n";
        }

        if (targetToJoin is not null)
        {
            if (targetToJoin.Address.Length != 0)
            {
                launchScript += "serverAddress " + targetToJoin.Address + "\n";
                launchScript += "serverPort " + targetToJoin.Port.ToString(CultureInfo.InvariantCulture) + "\n";
            }
            else if (targetToJoin.World.Length != 0)
            {
                launchScript += "worldName " + targetToJoin.World + "\n";
            }
        }

        // generic minecraft params
        // When using a launch script, the server parameters are handled by it
        // (Join over the mapped lines, not a += loop — .NET strings are immutable,
        //  unlike the QString += this replaced)
        launchScript += string.Join("", ProcessMinecraftArgs(session, null).Select(p => "param " + p + "\n"));

        // window size, title and state, legacy
        {
            string windowParams;
            if (Variant.AsBool(Settings.Get("LaunchMaximized")))
            {
                // FIXME doesn't support maximisation
                if (!IsLegacy())
                {
                    // C++ computes the primary screen's available size shrunk by the main
                    // window frame margins; the headless core has no screen, so the injected
                    // UI hook (or the configured window size) fills in
                    var (width, height) = (services ?? LaunchServices)?.Ui.PrimaryScreenSize?.Invoke() ??
                        (Variant.AsInt(Settings.Get("MinecraftWinWidth")),
                         Variant.AsInt(Settings.Get("MinecraftWinHeight")));
                    windowParams = FormattableString.Invariant($"{width}x{height}");
                }
                else
                {
                    windowParams = "maximized";
                }
            }
            else
            {
                windowParams = FormattableString.Invariant(
                    $"{Variant.AsInt(Settings.Get("MinecraftWinWidth"))}x{Variant.AsInt(Settings.Get("MinecraftWinHeight"))}");
            }
            launchScript += "windowTitle " + WindowTitle() + "\n";
            launchScript += "windowParams " + windowParams + "\n";
        }

        // launcher info
        {
            launchScript += "launcherBrand " + FlintConfig.Default.LauncherDisplayName + "\n";
            launchScript += "launcherVersion " + PrintableVersionString() + "\n";
        }

        // instance info
        {
            launchScript += "instanceName " + Name() + "\n";
            launchScript += "instanceIconKey " + Name() + "\n";
            launchScript += "instanceIconPath icon.png\n"; // we already save a copy here
        }

        // legacy auth
        if (session is not null)
        {
            launchScript += "userName " + session.PlayerName + "\n";
            launchScript += "sessionId " + session.Session + "\n";
        }

        launchScript += string.Join("", profile.GetTraits().Select(t => "traits " + t + "\n"));

        if (ShouldApplyOnlineFixes())
        {
            launchScript += "onlineFixes true\n";
        }

        launchScript += "launcher " + GetLauncher() + "\n";

        return launchScript;
    }

    /// <summary>Port of BuildConfig::printableVersionString() (reduced to the release form).</summary>
    private static string PrintableVersionString() => FlintConfig.DefaultVersionString;

    public override List<string> ExtraArguments()
    {
        var list = base.ExtraArguments();
        var version = GetPackProfile();
        if (version is null)
        {
            return list;
        }
        var jarMods = GetJarMods();
        if (jarMods.Count != 0)
        {
            list.Add("-Dfml.ignoreInvalidMinecraftCertificates=true");
            list.Add("-Dfml.ignorePatchDiscrepancies=true");
        }
        var profile = version.GetProfile();
        if (profile is not null)
        {
            var addn = profile.GetAddnJvmArguments();
            if (addn.Count != 0)
            {
                var tokenMapping = MakeProfileVarMapping(profile);

                foreach (var item in addn)
                {
                    list.Add(ReplaceTokensIn(item, tokenMapping));
                }
            }
            foreach (var agent in profile.GetAgents())
            {
                var jar = new List<string>();
                var temp1 = new List<string>();
                var temp2 = new List<string>();
                var temp3 = new List<string>();
                agent.Library.GetApplicableFiles(RuntimeContext, jar, temp1, temp2, temp3, GetLocalLibraryPath());
                list.Add("-javaagent:" + jar[0] + (agent.Argument.Length == 0 ? "" : "=" + agent.Argument));
            }
        }

        {
            string openAlPath = "";
            string glfwPath = "";
            string sdlPath = "";

            if (Variant.AsBool(Settings.Get("UseNativeOpenAL")))
            {
                openAlPath = DetectedOpenAlPath;
                var customPath = Variant.AsString(Settings.Get("CustomOpenALPath"));
                if (customPath.Length != 0)
                {
                    openAlPath = customPath;
                }
            }
            if (Variant.AsBool(Settings.Get("UseNativeGLFW")))
            {
                glfwPath = DetectedGlfwPath;
                var customPath = Variant.AsString(Settings.Get("CustomGLFWPath"));
                if (customPath.Length != 0)
                {
                    glfwPath = customPath;
                }
            }
            if (Variant.AsBool(Settings.Get("UseNativeSDL")))
            {
                sdlPath = DetectedSdlPath;
                var customPath = Variant.AsString(Settings.Get("CustomSDLPath"));
                if (customPath.Length != 0)
                {
                    sdlPath = customPath;
                }
            }

            if (openAlPath.Length != 0 && FsExists(openAlPath))
            {
                list.Add("-Dorg.lwjgl.openal.libname=" + Path.GetFullPath(openAlPath));
            }
            if (glfwPath.Length != 0 && FsExists(glfwPath))
            {
                list.Add("-Dorg.lwjgl.glfw.libname=" + Path.GetFullPath(glfwPath));
            }
            if (sdlPath.Length != 0 && FsExists(sdlPath))
            {
                list.Add("-Dorg.lwjgl.sdl.libname=" + Path.GetFullPath(sdlPath));
            }
        }

        return list;
    }

    public List<string> JavaArguments()
    {
        var args = new List<string> { "-Duser.language=en" };

        // custom args go first. we want to override them if we have our own here.
        args.AddRange(ExtraArguments());

        // OSX dock icon and name
        if (OperatingSystem.IsMacOS())
        {
            args.Add("-Xdock:icon=icon.png");
            args.Add($"-Xdock:name=\"{WindowTitle()}\"");
        }
        var instanceTraits = Traits();
        // HACK: fix issues on macOS with 1.13 snapshots
        // NOTE: Oracle Java option. if there are alternate jvm implementations, this would be the place to customize this for them
        if (OperatingSystem.IsMacOS() && instanceTraits.Contains("FirstThreadOnMacOS"))
        {
            args.Add("-XstartOnFirstThread");
        }

        // HACK: Stupid hack for Intel drivers. See: https://mojang.atlassian.net/browse/MCL-767
        if (OperatingSystem.IsWindows())
        {
            args.Add("-XX:HeapDumpPath=MojangTricksIntelDriversForPerformance_javaw.exe_minecraft.exe.heapdump");
        }

        // LWJGL2 reads `LWJGL_DISABLE_XRANDR` to force disable xrandr usage and fall back to xf86videomode.
        // It *SHOULD* check for the executable to exist before trying to use it for queries but it doesnt,
        // so WE can and force disable xrandr if it is not available.
        if (OperatingSystem.IsLinux() &&
            Components!.GetComponent("org.lwjgl") is not null &&
            FindExecutable("xrandr").Length == 0)
        {
            args.Add("-DLWJGL_DISABLE_XRANDR=true");
        }

        var min = Variant.AsInt(Settings.Get("MinMemAlloc"));
        var max = Variant.AsInt(Settings.Get("MaxMemAlloc"));
        if (min < max)
        {
            args.Add($"-Xms{min}m");
            args.Add($"-Xmx{max}m");
        }
        else
        {
            args.Add($"-Xms{max}m");
            args.Add($"-Xmx{min}m");
        }

        // The PermGen argument died with the Java <8 support floor: PermSize is a
        // pre-8 concept and the launcher no longer accommodates those runtimes.
        var javaVersion = GetJavaVersion();

        if (javaVersion.IsModular() && ShouldApplyOnlineFixes())
        {
            // allow reflective access to java.net - required by the skin fix
            args.Add("--add-opens");
            args.Add("java.base/java.net=ALL-UNNAMED");
        }

        return args;
    }

    public string GetLauncher()
    {
        // use legacy launcher if the traits are set
        if (IsLegacy())
        {
            return "legacy";
        }

        return "standard";
    }

    public bool ShouldApplyOnlineFixes()
    {
        return Traits().Contains("legacyServices") && Variant.AsBool(Settings.Get("OnlineFixes"));
    }

    public override SortedDictionary<string, string> GetVariables()
    {
        var outVars = new SortedDictionary<string, string>(StringComparer.Ordinal);
        outVars["INST_NAME"] = Name();
        outVars["INST_ID"] = Id();
        outVars["INST_DIR"] = ToNativeSeparators(QDirAbsolutePath(InstanceRoot()));
        outVars["INST_MC_DIR"] = ToNativeSeparators(QDirAbsolutePath(GameRoot()));
        outVars["INST_JAVA"] = ToNativeSeparators(QDirAbsolutePath(Variant.AsString(Settings.Get("JavaPath"))));
        outVars["INST_JAVA_ARGS"] = string.Join(" ", JavaArguments());
        outVars["NO_COLOR"] = "1";
        if (OperatingSystem.IsMacOS())
        {
            // get library for Steam overlay support
            var steamDyldInsertLibraries = Environment.GetEnvironmentVariable("STEAM_DYLD_INSERT_LIBRARIES");
            if (!string.IsNullOrEmpty(steamDyldInsertLibraries))
            {
                outVars["DYLD_INSERT_LIBRARIES"] = steamDyldInsertLibraries;
            }
        }
        return outVars;
    }

    public override ProcessEnvironment CreateEnvironment()
    {
        // prepare the process environment (C++ starts from CleanEnviroment(); the sanitized
        // OS environment is the P5a JavaUtils.CleanEnvironment port)
        var env = JavaUtils.CleanEnvironment();

        // export some infos
        foreach (var (key, value) in GetVariables())
        {
            env.Insert(key, value);
        }
        // custom env
        InsertCustomEnv(env, Variant.AsString(Settings.Get("Env")));
        return env;
    }

    public override ProcessEnvironment CreateLaunchEnvironment()
    {
        // prepare the process environment
        var env = CreateEnvironment();

        if (OperatingSystem.IsLinux())
        {
            if (Variant.AsBool(Settings.Get("EnableMangoHud")))
            {
                // TODO(P5): the C++ also checks APPLICATION->capabilities() and probes the
                // MangoHud libraries with LibraryUtils::findMangoHud().
                var preloadList = new List<string>();
                var value = env.Value("LD_PRELOAD");
                if (value.Length != 0)
                {
                    preloadList = value.Split(':').ToList();
                }

                env.Insert("LD_PRELOAD", string.Join(":", preloadList));
                env.Insert("MANGOHUD", "1");
            }

            if (Variant.AsBool(Settings.Get("UseDiscreteGpu")))
            {
                // TODO(P5): the C++ first tries switcheroo-control via DBus; without it the
                // open/proprietary driver fallbacks below apply.
                // Open Source Drivers
                env.Insert("DRI_PRIME", "1");
                // Proprietary Nvidia Drivers
                env.Insert("__NV_PRIME_RENDER_OFFLOAD", "1");
                env.Insert("__VK_LAYER_NV_optimus", "NVIDIA_only");
                env.Insert("__GLX_VENDOR_LIBRARY_NAME", "nvidia");
            }

            if (Variant.AsBool(Settings.Get("UseZink")))
            {
                // taken from https://wiki.archlinux.org/title/OpenGL#OpenGL_over_Vulkan_(Zink)
                env.Insert("__GLX_VENDOR_LIBRARY_NAME", "mesa");
                env.Insert("MESA_LOADER_DRIVER_OVERRIDE", "zink");
                env.Insert("GALLIUM_DRIVER", "zink");
                env.Insert("LIBGL_KOPPER_DRI2", "1");
            }
        }
        return env;
    }

    public List<string> ProcessMinecraftArgs(AuthSession? session, MinecraftTarget? targetToJoin)
    {
        var profile = Components!.GetProfile()!;
        var args = profile.GetMinecraftArguments()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        foreach (var tweaker in profile.GetTweakers())
        {
            args.Add("--tweakClass");
            args.Add(tweaker);
        }

        if (targetToJoin is not null)
        {
            if (targetToJoin.Address.Length != 0)
            {
                if (profile.HasTrait("feature:is_quick_play_multiplayer"))
                {
                    args.Add("--quickPlayMultiplayer");
                    args.Add(targetToJoin.Address + ':' + targetToJoin.Port.ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    args.Add("--server");
                    args.Add(targetToJoin.Address);
                    args.Add("--port");
                    args.Add(targetToJoin.Port.ToString(CultureInfo.InvariantCulture));
                }
            }
            else if (targetToJoin.World.Length != 0 && profile.HasTrait("feature:is_quick_play_singleplayer"))
            {
                args.Add("--quickPlaySingleplayer");
                args.Add(targetToJoin.World);
            }
        }

        var tokenMapping = MakeProfileVarMapping(profile);

        // yggdrasil!
        if (session is not null)
        {
            // token_mapping["auth_username"] = session->username;
            tokenMapping["auth_session"] = session.Session;
            tokenMapping["auth_access_token"] = session.AccessToken;
            tokenMapping["auth_player_name"] = session.PlayerName;
            tokenMapping["auth_uuid"] = session.Uuid;
            tokenMapping["user_properties"] = session.SerializeUserProperties();
            tokenMapping["user_type"] = session.UserType;

            if (session.LaunchMode == LaunchMode.Demo)
            {
                args.Add("--demo");
            }
        }

        for (var i = 0; i < args.Count; i++)
        {
            args[i] = ReplaceTokensIn(args[i], tokenMapping);
        }
        return args;
    }

    public override List<string> VerboseDescription(AuthSession? session, MinecraftTarget? targetToJoin)
    {
        const string indent = "  ";
        const string emptyLine = "";

        var outList = new List<string>();

        outList.Add("Components:");
        var components = GetPackProfile();
        for (var i = 0; i < components.Components.Count; ++i)
        {
            var component = components.Components[i];
            outList.Add(indent + FormattableString.Invariant(
                $"{i + 1}) {component.GetName()} ({component.GetId()}) {component.GetVersion()}"));
        }
        outList.Add(emptyLine);

        outList.Add("Launcher: " + GetLauncher());
        outList.Add("Main class: " + GetMainClass());
        outList.Add(emptyLine);

        var profile = components.GetProfile();

        // mods and core mods
        void PrintModList(string label, ModFolderModel model)
        {
            if (model.Size != 0)
            {
                outList.Add($"{label}:");
                var modList = model.AllMods();
                modList.Sort((a, b) =>
                {
                    // localeAwareCompare maps to the current-culture comparison
                    var aName = CompleteBaseName(a.Fileinfo.Name);
                    var bName = CompleteBaseName(b.Fileinfo.Name);
                    return string.Compare(aName, bName, StringComparison.CurrentCulture);
                });
                foreach (var mod in modList)
                {
                    if (mod.Type == ResourceType.Folder)
                    {
                        outList.Add("  [🖿] " + CompleteBaseName(mod.Fileinfo.Name) + " (folder)");
                        continue;
                    }

                    if (mod.Enabled)
                    {
                        outList.Add("  [✔] " + CompleteBaseName(mod.Fileinfo.Name));
                    }
                    else
                    {
                        outList.Add("  [✘] " + CompleteBaseName(mod.Fileinfo.Name) + " (disabled)");
                    }
                }
                outList.Add(emptyLine);
            }
        }

        PrintModList("Mods", LoaderModList());
        PrintModList("Core Mods", CoreModList());

        // jar mods
        if (profile is not null)
        {
            var jarMods = profile.GetJarMods();
            if (jarMods.Count != 0)
            {
                outList.Add("Jar Mods:");
                foreach (var jarmod in jarMods)
                {
                    var displayname = jarmod.DisplayName(RuntimeContext);
                    var realname = jarmod.Filename(RuntimeContext);
                    outList.Add(displayname != realname
                        ? indent + displayname + " (" + realname + ")"
                        : indent + realname);
                }
                outList.Add(emptyLine);
            }
        }

        // traits
        var allTraits = Traits();
        if (allTraits.Count != 0)
        {
            outList.Add("Traits:");
            foreach (var trait in allTraits)
            {
                outList.Add(indent + trait);
            }
            outList.Add(emptyLine);
        }

        // native libraries
        var settings = Settings;
        var nativeOpenAl = Variant.AsBool(settings.Get("UseNativeOpenAL"));
        var nativeGlfw = Variant.AsBool(settings.Get("UseNativeGLFW"));
        var nativeSdl = Variant.AsBool(settings.Get("UseNativeSDL"));
        if (nativeOpenAl || nativeGlfw || nativeSdl)
        {
            if (nativeOpenAl)
            {
                outList.Add("Using system OpenAL.");
            }
            if (nativeGlfw)
            {
                outList.Add("Using system GLFW.");
            }
            if (nativeSdl)
            {
                outList.Add("Using system SDL.");
            }
            outList.Add(emptyLine);
        }

        // libraries and class path.
        if (profile is not null)
        {
            outList.Add("Libraries:");
            var jars = new List<string>();
            var nativeJars = new List<string>();
            profile.GetLibraryFiles(RuntimeContext, jars, nativeJars, GetLocalLibraryPath(), BinRoot());
            foreach (var file in jars)
            {
                outList.Add(indent + file);
            }
            outList.Add(emptyLine);
            outList.Add("Native libraries:");
            foreach (var file in nativeJars)
            {
                outList.Add(indent + file);
            }
            outList.Add(emptyLine);
        }

        outList.Add("Natives path:");
        outList.Add(indent + GetNativePath());
        outList.Add(emptyLine);

        // minecraft arguments
        var @params = ProcessMinecraftArgs(null, targetToJoin);
        outList.Add("Minecraft arguments:");
        outList.Add(indent + string.Join(' ', @params));
        outList.Add(emptyLine);

        // window size
        string windowParams;
        if (Variant.AsBool(settings.Get("LaunchMaximized")))
        {
            // NOTE: the launch-script builder (P5) computes a screen-size form; the verbose
            // description only ever prints the "max (if available)" placeholder upstream.
            windowParams = "max (if available)";
        }
        else
        {
            windowParams = FormattableString.Invariant(
                $"{Variant.AsInt(settings.Get("MinecraftWinWidth"))} x {Variant.AsInt(settings.Get("MinecraftWinHeight"))}");
        }
        outList.Add("Window size: " + windowParams);
        outList.Add(emptyLine);

        // environment variables
        var env = Variant.AsString(settings.Get("Env"));
        var envMap = JsonUtils.ToMap(env);
        if (envMap.Count != 0)
        {
            outList.Add("Custom environment variables:");
            foreach (var (key, value) in envMap)
            {
                outList.Add(indent + key + "=" + value);
            }
            outList.Add(emptyLine);
        }

        return outList;
    }

    protected SortedDictionary<string, string> CreateCensorFilterFromSession(AuthSession? session)
    {
        var filter = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (session is null)
        {
            return filter;
        }
        void AddToFilter(string key, string value)
        {
            if (key.Trim().Length != 0)
            {
                filter[key] = value;
            }
        }
        if (session.Session != "-")
        {
            AddToFilter(session.Session, "<SESSION ID>");
        }
        if (session.AccessToken != "0")
        {
            AddToFilter(session.AccessToken, "<ACCESS TOKEN>");
        }
        AddToFilter(session.Uuid, "<PROFILE ID>");

        return filter;
    }

    protected SortedDictionary<string, string> MakeProfileVarMapping(LaunchProfile profile)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);

        result["profile_name"] = Name();
        result["version_name"] = profile.GetMinecraftVersion();
        result["version_type"] = profile.GetMinecraftVersionType();

        var absRootDir = QDirAbsolutePath(GameRoot());
        result["game_directory"] = absRootDir;
        var absAssetsDir = Paths?.AssetsRoot ?? QDirAbsolutePath("assets/");
        var assets = profile.GetMinecraftAssets();
        result["game_assets"] = GetAssetsDir(assets.Id, ResourcesDir());

        // 1.7.3+ assets tokens
        result["assets_root"] = absAssetsDir;
        result["assets_index_name"] = assets.Id;

        result["library_directory"] = MetaCache?.GetBasePath("libraries") ?? "";

        return result;
    }

    public override IReadOnlyList<string> GetLogFileSearchPaths()
    {
        return
        [
            FileSystem.PathCombine(GameRoot(), "crash-reports"),
            FileSystem.PathCombine(GameRoot(), "logs"),
            GameRoot(),
        ];
    }

    public override string GetStatusbarDescription()
    {
        var traitsList = new List<string>();
        if (HasVersionBroken)
        {
            traitsList.Add("broken");
        }

        var mcVersion = Components!.GetComponentVersion("net.minecraft");
        if (mcVersion.Length == 0)
        {
            // Load component info if needed. Upstream calls PackProfile::reload(Offline): its
            // inline (synchronous) part is saveNow + invalidateLaunchProfile + load, only the
            // resolve is a task — mirror the synchronous part and re-read immediately.
            Components.SaveNow();
            Components.InvalidateLaunchProfile();
            Components.Load();
            mcVersion = Components.GetComponentVersion("net.minecraft");
        }

        var description = $"Minecraft {mcVersion}";
        if (Variant.AsBool(_settings.Get("ShowGameTime")))
        {
            // the without-days display flag is global-only (no instance override in C++)
            var withoutDays = UserLayer.GameTime.ShowGameTimeWithoutDays ?? false;
            if (LastTimePlayed() > 0 && LastLaunch() > 0)
            {
                var lastLaunchTime = DateTimeOffset.FromUnixTimeMilliseconds(LastLaunch()).LocalDateTime;
                description += $", last played on {lastLaunchTime.ToString("g", CultureInfo.CurrentCulture)} for " +
                               PrettifyDuration(LastTimePlayed(), withoutDays);
            }

            if (TotalTimePlayed() > 0)
            {
                description += ", total played for " + PrettifyDuration(TotalTimePlayed(), withoutDays);
            }
        }
        if (HasCrashed)
        {
            description += ", has crashed.";
        }
        return description;
    }

    // FIXME upstream: remove (the launch pipeline rebuilds these from the profile)
    public List<string> GetClassPath()
    {
        var jars = new List<string>();
        var nativeJars = new List<string>();
        var profile = Components!.GetProfile()!;
        profile.GetLibraryFiles(RuntimeContext, jars, nativeJars, GetLocalLibraryPath(), BinRoot(),
            storageBase: LibraryStorageBase());
        return jars;
    }

    public List<string> GetNativeJars()
    {
        var jars = new List<string>();
        var nativeJars = new List<string>();
        var profile = Components!.GetProfile()!;
        profile.GetLibraryFiles(RuntimeContext, jars, nativeJars, GetLocalLibraryPath(), BinRoot(),
            storageBase: LibraryStorageBase());
        return nativeJars;
    }

    /// <summary>The storage base unstamped (meta-loaded) library paths resolve against —
    /// the C++ anchored the relative "libraries/" prefix on its chdir'd data dir; the port
    /// has no chdir (DI-B), so it anchors on the store root (E2). Bare fixtures without a
    /// stamped layout keep the historical CWD-relative behavior.</summary>
    public string LibraryStorageBase() => Paths?.StoreRoot ?? "";

    public string GetMainClass()
    {
        var profile = Components!.GetProfile()!;
        return profile.GetMainClass();
    }

    public JavaVersion GetJavaVersion()
    {
        // the probe cache is launcher-state.json's path-keyed java map now (S5/C9) —
        // settings never carried the probed version back
        return new JavaVersion(JavaDetection()?.Version ?? "");
    }

    /// <summary>The probe-cache key for a configured java path (S5): the resolved executable,
    /// or the configured string as-is when it cannot be resolved (deterministic — CheckJava
    /// stores and every reader resolves through this same function).</summary>
    public static string JavaDetectKey(string javaPathSetting)
    {
        var resolved = LauncherJars.ResolveExecutable(javaPathSetting);
        return resolved.Length != 0 ? resolved : javaPathSetting;
    }

    /// <summary>The probe-cache entry for this instance's configured java (S5
    /// JavaDetectState.Detections), or null when that binary has not been probed (or no state
    /// store is wired). Goes through Settings so the specific settings (and the JavaPath
    /// override) are loaded like every other reader, and reads the INSTANCE-stamped state
    /// store — instances discovered at startup carry it from InstanceList, while their
    /// LaunchServices may still be null (it is stamped later by the composition root).</summary>
    public JavaDetectEntry? JavaDetection()
    {
        var key = JavaDetectKey(Variant.AsString(Settings.Get("JavaPath")));
        if (key.Length == 0)
        {
            return null;
        }
        return LauncherState?.Current.Java.Detections.GetValueOrDefault(key);
    }

    //////  Mod Lists //////

    public ModFolderModel LoaderModList()
    {
        if (loaderModList is null)
        {
            var isIndexed = !(UserLayer.ModDownload.ModMetadataDisabled ?? false);
            loaderModList = new ModFolderModel(ModsRoot(), isIndexed, createDir: true);
        }
        return loaderModList;
    }

    public ModFolderModel CoreModList()
    {
        if (coreModList is null)
        {
            var isIndexed = !(UserLayer.ModDownload.ModMetadataDisabled ?? false);
            coreModList = new ModFolderModel(CoreModsDir(), isIndexed, createDir: true);
        }
        return coreModList;
    }

    public ModFolderModel NilModList()
    {
        if (nilModList is null)
        {
            var isIndexed = !(UserLayer.ModDownload.ModMetadataDisabled ?? false);
            // NOTE: upstream does not create the folder for the nil mod list (4th arg false)
            nilModList = new ModFolderModel(NilModsDir(), isIndexed, createDir: false);
        }
        return nilModList;
    }

    // (resourcePackList()/texturePackList()/shaderPackList()/dataPackList()/resourceLists()/
    //  worldList() arrive with the remaining P3b folder models; only ModFolderModel and
    //  WorldList are ported so far. WorldList can be constructed from WorldDir() when needed.)

    public List<Mod> GetJarMods()
    {
        var profile = Components!.GetProfile()!;
        var mods = new List<Mod>();
        foreach (var jarmod in profile.GetJarMods())
        {
            var jar = new List<string>();
            var temp1 = new List<string>();
            var temp2 = new List<string>();
            var temp3 = new List<string>();
            jarmod.GetApplicableFiles(RuntimeContext, jar, temp1, temp2, temp3, JarModsDir());
            mods.Add(new Mod(new FileInfo(jar[0])));
        }
        return mods;
    }

    // ------------------------------------------------------------------ IPackProfileEnv

    string IPackProfileEnv.InstanceRoot => InstanceRoot();

    string IPackProfileEnv.InstanceName => Name();

    RuntimeContext IPackProfileEnv.RuntimeContext => RuntimeContext;

    // ------------------------------------------------------------------------ helpers

    private static void InsertCustomEnv(ProcessEnvironment env, string value)
    {
        var envMap = JsonUtils.ToMap(value);
        if (envMap.Count == 0)
        {
            return;
        }

        foreach (var (key, varValue) in envMap)
        {
            env.Insert(key, varValue);
        }
    }

    private static string ReplaceTokensIn(string text, IReadOnlyDictionary<string, string> with)
    {
        // TODO upstream: does this still work??
        var result = new StringBuilder();
        var lastCapturedEnd = 0;
        foreach (Match match in TokenRegex.Matches(text))
        {
            result.Append(text, lastCapturedEnd, match.Index - lastCapturedEnd);
            var key = match.Groups[1].Value;
            if (with.TryGetValue(key, out var replacement))
            {
                result.Append(replacement);
            }
            lastCapturedEnd = match.Index + match.Length;
        }
        result.Append(text[lastCapturedEnd..]);
        return result.ToString();
    }

    /// <summary>
    /// Port of AssetsUtils::getAssetsDir reduced to the branch the profile variable mapping
    /// needs (only the index's "virtual" and "mapToResources" booleans are consulted).
    /// </summary>
    private string GetAssetsDir(string assetsId, string resourcesFolder)
    {
        var assetsDir = Paths?.AssetsRoot ?? QDirAbsolutePath("assets/");
        var indexPath = FileSystem.PathCombine(assetsDir, "indexes", assetsId + ".json");
        var virtualRoot = FileSystem.PathCombine(assetsDir, "virtual", assetsId);

        if (!File.Exists(indexPath))
        {
            // (upstream logs qCritical here)
            return virtualRoot;
        }
        bool isVirtual;
        bool mapToResources;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(indexPath));
            isVirtual = doc.RootElement.ValueKind == JsonValueKind.Object &&
                        doc.RootElement.TryGetProperty("virtual", out var virtualProp) &&
                        virtualProp.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                        virtualProp.GetBoolean();
            mapToResources = doc.RootElement.ValueKind == JsonValueKind.Object &&
                             doc.RootElement.TryGetProperty("mapToResources", out var mapProp) &&
                             mapProp.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                             mapProp.GetBoolean();
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            // (upstream logs qCritical here)
            return virtualRoot;
        }

        if (isVirtual)
        {
            return virtualRoot;
        }
        if (mapToResources)
        {
            return resourcesFolder;
        }
        return virtualRoot;
    }

    /// <summary>QStandardPaths::findExecutable: locate `name` on PATH; "" when not found.</summary>
    private static string FindExecutable(string name)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
        {
            return "";
        }
        var suffix = OperatingSystem.IsWindows() ? ".exe" : "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, name + suffix);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return "";
    }

    /// <summary>QFileInfo::completeBaseName(): the file name up to the last dot.</summary>
    private static string CompleteBaseName(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        return dot > 0 ? fileName[..dot] : fileName;
    }

    /// <summary>QDir::toNativeSeparators.</summary>
    private static string ToNativeSeparators(string path) =>
        Path.DirectorySeparatorChar != '/' ? path.Replace('/', Path.DirectorySeparatorChar) : path;

    /// <summary>QDir(path).absolutePath(): relative paths anchor at the current directory.</summary>
    private static string QDirAbsolutePath(string path)
    {
        if (path.Length == 0)
        {
            return FileSystem.PathCombine(Directory.GetCurrentDirectory(), "");
        }
        if (path.StartsWith('/') || (path.Length >= 2 && path[1] == ':'))
        {
            return FileSystem.PathCombine(path, "");
        }
        return FileSystem.PathCombine(Directory.GetCurrentDirectory(), path);
    }

    /// <summary>QFileInfo::exists(): file or directory.</summary>
    private static bool FsExists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>Port of Time::prettifyDuration (MMCTime.cpp) for the status bar text.</summary>
    private static string PrettifyDuration(long duration, bool noDays)
    {
        var seconds = (int)(duration % 60);
        duration /= 60;
        var minutes = (int)(duration % 60);
        duration /= 60;
        var hours = (int)(noDays ? duration : duration % 24);
        var days = (int)(noDays ? 0 : duration / 24);
        if (hours == 0 && days == 0)
        {
            return $"{minutes}min {seconds}s";
        }
        if (days == 0)
        {
            return $"{hours}h {minutes}min";
        }
        return $"{days}d {hours}h {minutes}min";
    }

    // ------------------------------------------------------------------------ data

    protected readonly PackProfile? Components; // unique_ptr<PackProfile> m_components (set in ctor)
    private ModFolderModel? loaderModList;
    private ModFolderModel? coreModList;
    private ModFolderModel? nilModList;
    // (the resource pack / texture pack / shader pack / data pack models and the world list
    //  arrive with the remaining P3b folder-model ports)
}
