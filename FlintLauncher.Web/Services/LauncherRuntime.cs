// SPDX-License-Identifier: GPL-3.0-only
// The host's core-object wiring: the singletons a C++ Application object would own, built
// once by WebHostBuilder against the resolved data dir. All paths come from the injected
// FlintPaths — the process working directory is not load-bearing.

using System.Text.Json.Nodes;
using FlintLauncher.Core;
using FlintLauncher.Core.Auth;
using FlintLauncher.Core.Config;
using FlintLauncher.Core.Icons;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Launch;
using FlintLauncher.Core.Migration;
using FlintLauncher.Core.ModPlatform.AtLauncher;
using FlintLauncher.Core.ModPlatform.Flame;
using FlintLauncher.Core.ModPlatform.Ftb;
using FlintLauncher.Core.ModPlatform.Modrinth;
using FlintLauncher.Core.ModPlatform.Technic;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Screenshots;
using FlintLauncher.Core.Secrets;
using FlintLauncher.Core.Skins;
using FlintLauncher.Core.State;
using FlintLauncher.Web.Models;

namespace FlintLauncher.Web.Services;

public sealed class LauncherRuntime
{
    public FlintPaths Paths { get; }
    public string DataDir => Paths.DataDir;

    /// <summary>The network stack this runtime's activity flows through (defaults to the
    /// canonical process-wide stack; tests may isolate by constructing their own).</summary>
    public NetStack Net { get; }

    /// <summary>The Modrinth client (injected into platform tasks/endpoints; tests construct
    /// a runtime with a client pointed at their stub).</summary>
    public ModrinthApi Modrinth { get; }

    /// <summary>The Flame (CurseForge) client — same injection story as Modrinth.</summary>
    public FlameApi Flame { get; }

    /// <summary>The Technic client — same injection story as Modrinth/Flame.</summary>
    public TechnicApi Technic { get; }

    /// <summary>The ATLauncher client — same injection story as Modrinth/Flame/Technic.</summary>
    public AtLauncherApi AtLauncher { get; }

    /// <summary>The FTB App API client — same injection story as Modrinth/Flame/Technic.</summary>
    public FtbApi Ftb { get; }

    /// <summary>The imgur screenshot-upload client — same injection story.</summary>
    public ImgurApi Imgur { get; }

    /// <summary>The Microsoft/Xbox/Minecraft auth + skins service endpoints (injected into the
    /// auth flow and the skin/cape calls; tests construct a runtime pointed at their stub).</summary>
    public AuthEndpointsOptions Auth { get; }

    /// <summary>The C++ APPLICATION->settings(): the typed flint.json store (CONFIG C5/C6 —
    /// load-or-default, atomic writes; the /settings surface and RuntimeNetConfig read the
    /// resolved fold through it). The instance layer folds it as the user layer of its own
    /// instance.json stack (D4) — the former SettingsObject adapter view retired with the D4
    /// batch.</summary>
    public FlintConfigStore Config { get; }

    /// <summary>The launcher runtime state (launcher-state.json, S5 — assembly step 4:
    /// loaded BEFORE the config so the C5 importer can strip state keys into it later).</summary>
    public LauncherStateStore State { get; }

    /// <summary>The transitional secret store (CONFIG C4, assembly step 3:
    /// &lt;data-root&gt;/secrets.json, 0600).</summary>
    public ISecretStore Secrets { get; }

    /// <summary>The per-source migration decision records (STATE S3:
    /// &lt;state-root&gt;/migration/&lt;source&gt;.json).</summary>
    public MigrationDecisionStore MigrationDecisions { get; }

    public InstanceList Instances { get; }

    /// <summary>The global icon library (DATA D5: the IconList port — key→path index,
    /// scan, install/delete — over the IconsDir setting).</summary>
    public IconPool Icons { get; }

    /// <summary>The local skin library (DATA D6 plan B) over the SkinsDir setting.</summary>
    public SkinsLibrary Skins { get; }

    public AccountList Accounts { get; }
    public HttpMetaCache MetaCache { get; }

    /// <summary>The flint-legacy eager pass outcome (assembly step 5, GLOBAL G2): what the
    /// startup remount did, so the host can log it and tests can assert it.</summary>
    public FlintLegacyMigration.Result LegacyMigration { get; }

    /// <summary>The C5 INI import outcome (assembly step 6): what the one-shot importer did,
    /// including an unreadable chain INI — the ctor runs before any logger exists, so the
    /// host surfaces this after Build (the flint-legacy result pattern).</summary>
    public FlintConfigImporter.ImportResult ConfigImport { get; } =
        FlintConfigImporter.ImportResult.Idle;

    /// <summary>The launch-pipeline dependencies (paths, logger factory, global settings,
    /// meta cache, install root). Built by the composition root (WebHostBuilder) once the
    /// app's LoggerFactory exists — the constructor runs before the host is built, so this is
    /// assigned right after builder.Build(); launch endpoints hand it to the controller and
    /// the instance list stamps it onto (re)discovered instances.</summary>
    public LaunchServices? LaunchServices { get; set; }

    public LauncherRuntime(FlintPaths paths, NetStack? net = null, ModrinthApi? modrinth = null,
        FlameApi? flame = null, TechnicApi? technic = null, AtLauncherApi? atlauncher = null,
        FtbApi? ftb = null, ImgurApi? imgur = null, AuthEndpointsOptions? auth = null)
    {
        Paths = paths;
        Net = net ?? NetStack.Default;
        Modrinth = modrinth ?? new ModrinthApi(Net);
        Auth = auth ?? new AuthEndpointsOptions();
        Flame = flame ?? new FlameApi(Net);
        Technic = technic ?? new TechnicApi(Net);
        AtLauncher = atlauncher ?? new AtLauncherApi(Net);
        Ftb = ftb ?? new FtbApi(Net);
        Imgur = imgur ?? new ImgurApi(Net);

        // secrets init (assembly step 3): <dataDir>/secrets.json (0600) — the C4 transitional
        // backend; the secret trio (ProxyPass/ModrinthToken/FlameKeyOverride) reads and
        // writes here (G4: PUT /settings reroutes, RuntimeNetConfig/API clients read the store)
        Secrets = new FileSecretStore(paths.SecretsFile, FlintJsonContext.Default.SecretsFile);

        // state load (assembly step 4): launcher-state.json BEFORE the config import — the
        // importer will strip the C9 state keys into it (GLOBAL §2 ordering)
        State = new LauncherStateStore(paths.LauncherStateFile, FlintJsonContext.Default.LauncherState);

        // per-source migration decision records (S3): <stateDir>/migration/<source>.json
        MigrationDecisions = new MigrationDecisionStore(
            paths.MigrationDir, FlintJsonContext.Default.MigrationDecisionRecord);

        // flint-legacy migration probe/execute (assembly step 5, GLOBAL G2): the pre-reform
        // flat layout remounts onto the five roots ONCE (eager, no lazy dual-read) — before
        // the config import so the moved flint.cfg is found on the config side, and before
        // the cache ledger is built so its Load() reads the remounted metacache.json
        LegacyMigration = FlintLegacyMigration.ExecuteIfNeeded(paths, MigrationDecisions);

        // global config (assembly step 6): flint.json is the authoritative user layer (C5);
        // when it is missing, the one-shot importer reads the INI chain
        // (flint.cfg ← prismlauncher.cfg/polymc.cfg/multimc.cfg|mmc.cfg) and sorts it in a
        // single pass — secret trio → the store (C4, loaded above), C9 state keys → the
        // state store (loaded above), the rest → the record domains. Flint never writes INI.
        Config = new FlintConfigStore(paths.ConfigFile, FlintJsonContext.Default.FlintConfigFile);
        ConfigImport = FlintConfigImporter.ImportIfNeeded(paths, Config, Secrets, State);

        // meta cache (assembly step 8, CACHE E6 — the C++ Application.cpp:1036-1051 startup
        // shape: ONE registration of every base, then the ledger Load()). The mounts follow
        // E2/E3: the store bases (libraries/assets/asset_indexes/java) hang off the store
        // root, the true-cache bases (meta/ledger/general/icons/fmllibs/pack downloads) off
        // the cache root. The former lazy in-task registrations (asset_indexes/fmllibs/java)
        // resolved against the process CWD when they fired before any stamp — the defect E6
        // retires. The dead "screenshots" base is gone with it (DATA D8).
        MetaCache = new HttpMetaCache(paths.MetacacheFile);
        MetaCache.AddBase("meta", paths.MetaDir);                  // cache/meta (E3)
        MetaCache.AddBase("libraries", paths.LibrariesRoot);       // store/libraries (E2)
        MetaCache.AddBase("assets", paths.AssetsRoot);             // store/assets (E2)
        MetaCache.AddBase("asset_indexes", paths.AssetIndexesDir); // store/assets/indexes (E2)
        MetaCache.AddBase("java", paths.JavaRoot);                 // store/java (E2)
        MetaCache.AddBase("general", paths.CacheRoot);             // import staging etc.
        MetaCache.AddBase("icons", Path.Combine(paths.CacheRoot, "icons"));
        MetaCache.AddBase("fmllibs", paths.FmllibsCacheDir); // cache/fmllibs (E3)
        MetaCache.AddBase("ATLauncherPacks", Path.Combine(paths.CacheRoot, "ATLauncherPacks"));
        MetaCache.AddBase("FTBPacks", Path.Combine(paths.CacheRoot, "FTBPacks"));
        // E6: the ledger is READ at startup again (the "write-only ledger" defect — entries
        // re-downloaded on every run because nothing ever loaded the persisted index)
        MetaCache.Load();

        // instance list (assembly step 9): <dataDir>/instances; the typed instance
        // environment (user flint.json store + the D4 instance.json/instance-state.json type
        // infos) is stamped so discovery builds the typed settings stack per instance;
        // discovered instances also carry the layout, cache and state store
        Instances = new InstanceList([paths.InstancesDir])
        {
            Environment = new InstanceEnvironment(
                Config, FlintJsonContext.Default.InstanceConfigFile, FlintJsonContext.Default.InstanceStateFile),
            LauncherState = State,
            Paths = paths,
            MetaCache = MetaCache,
        };
        Instances.LoadList();

        // icon pool (assembly step 9, DATA D5): the IconList successor over the IconsDir
        // SETTING (default "icons", data-root-relative — previously three hardcoded
        // Path.Combine(DataDir, "icons") sites ignored the setting)
        Icons = new IconPool(() => ResolveDataRelativeDir(Config.Current.Dirs.IconsDir, "icons"));

        // local skin library (assembly step 9, DATA D6 plan B): the SkinsDir SETTING
        // (default "skins", data-root-relative) finally has a resident model
        Skins = new SkinsLibrary(() => ResolveDataRelativeDir(Config.Current.Dirs.SkinsDir, "skins"));

        // accounts: <dataDir>/accounts.json (autosaved on every change)
        Accounts = new AccountList();
        Accounts.SetListFilePath(paths.AccountsFile, enableAutosave: true);
        Accounts.LoadList();

        // (CACHE E7: the news checker is gone entirely — the /news endpoint, the feed base
        // and FLINT_NEWS_RSS_URL went with it; the C++ GUI was its only consumer)
    }

    /// <summary>Serialize the instance-list mutations (create/delete/rename + LoadList):
    /// InstanceList is not thread-safe and the API handlers can interleave.</summary>
    public SemaphoreSlim InstanceLock { get; } = new(1, 1);

    /// <summary>The launcher-managed java root (CACHE E2 via GLOBAL §3): the JavaDir
    /// setting under STORE semantics — absolute wins as-is, relative joins the STORE root
    /// (the C++ "relative to the data dir" convention re-anchored by the store remount),
    /// empty/null falls back to the store/java anchor. Consumed by the java scan
    /// (JavaUtils.FindJavaPaths), the JavaInstallList managed-versions probe and the
    /// launch pipeline's AutoInstallJava (through LaunchServices.JavaRoot).</summary>
    public string JavaRoot =>
        string.IsNullOrWhiteSpace(Config.Current.Dirs.JavaDir)
            ? Paths.JavaRoot
            : Path.IsPathRooted(Config.Current.Dirs.JavaDir)
                ? Config.Current.Dirs.JavaDir
                : Path.Combine(Paths.StoreRoot, Config.Current.Dirs.JavaDir);

    /// <summary>A dir-kind setting (IconsDir/SkinsDir — the C++ "relative to the data dir"
    /// convention, CONFIG matrix A): absolute wins as-is, relative joins the data root,
    /// empty/null falls back to the C++ default name.</summary>
    private string ResolveDataRelativeDir(string? configured, string fallbackName) =>
        string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(DataDir, fallbackName)
            : Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(DataDir, configured);
}

/// <summary>Small JSON helpers shared by the endpoints (AOT-safe: explicit nodes).</summary>
public static class ApiJson
{
    /// <summary>Uniform {"error":{"code","message"}} body.</summary>
    public static IResult Error(string code, string message, int statusCode) =>
        Results.Json(new ApiErrorEnvelope(new ErrorResponse(code, message)),
            FlintJsonContext.Default.ApiErrorEnvelope, statusCode: statusCode);

    public static IResult NotFound(string code, string message) => Error(code, message, 404);

    public static IResult Conflict(string code, string message) => Error(code, message, 409);

    /// <summary>Serialize a manually-built JSON node (settings map, search results).</summary>
    public static IResult Node(JsonObject node) =>
        Results.Content(node.ToJsonString(), "application/json");
}
