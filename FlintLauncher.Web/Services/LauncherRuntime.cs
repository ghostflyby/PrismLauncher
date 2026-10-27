// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// The host's core-object wiring: the singletons a C++ Application object would own, built
// once by WebHostBuilder against the resolved data dir. All paths come from the injected
// FlintPaths — the process working directory is not load-bearing.

using FlintLauncher.Core;
using FlintLauncher.Core.Auth;
using FlintLauncher.Core.Config;
using FlintLauncher.Core.Icons;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Migration;
using FlintLauncher.Core.ModPlatform.AtLauncher;
using FlintLauncher.Core.ModPlatform.Flame;
using FlintLauncher.Core.ModPlatform.Ftb;
using FlintLauncher.Core.ModPlatform.Technic;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Screenshots;
using FlintLauncher.Core.Secrets;
using FlintLauncher.Core.Skins;
using FlintLauncher.Core.State;
using FlintLauncher.ExternalApi;
using FlintLauncher.ExternalApi.Flame;
using FlintLauncher.ExternalApi.Modrinth;
using FlintLauncher.Web.Models;
using TruePath;

namespace FlintLauncher.Web.Services;

internal sealed class LauncherRuntime : IDisposable
{
    public FlintPaths Paths { get; }
    public AbsolutePath DataDir => Paths.DataDir;

    /// <summary>The persistent client this runtime's activity flows through: the injected
    /// host client, else a private default client owned by this runtime (tests isolate by
    /// constructing their own; there is no process-wide client). The request-level
    /// configuration (proxy, user agent, retries, timeout) rides the client's handlers and
    /// is written by the host — this runtime only threads the client through.</summary>
    public HttpClient Http { get; }

    /// <summary>The Modrinth client (injected into platform tasks/endpoints; tests construct
    /// a runtime with a client pointed at their stub).</summary>
    public ModrinthClient Modrinth { get; }

    /// <summary>The Flame (CurseForge) client — same injection story as Modrinth.</summary>
    public FlameClient Flame { get; }

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

    /// <summary>The C++ APPLICATION->settings(): the typed flint.json store —
    /// load-or-default, atomic writes; the /settings surface and NetConfigApplier read the
    /// resolved fold through it). The instance layer folds it as the user layer of its own
    /// instance.json stack.</summary>
    public FlintConfigStore Config { get; }

    /// <summary>The dir-kind settings (JavaDir/IconsDir/SkinsDir) resolved against the roots.</summary>
    public ConfiguredDirs Dirs { get; }

    /// <summary>The launcher runtime state (launcher-state.json):
    /// loaded BEFORE the config so the importer can strip state keys into it later).</summary>
    public LauncherStateStore State { get; }

    /// <summary>The transitional secret store (&lt;data-root&gt;/secrets.json, 0600).</summary>
    public ISecretStore Secrets { get; }

    /// <summary>The per-source migration decision records
    /// (&lt;state-root&gt;/migration/&lt;source&gt;.json).</summary>
    public MigrationDecisionStore MigrationDecisions { get; }

    public InstanceList Instances { get; }

    /// <summary>The global icon library (the IconList port — key→path index,
    /// scan, install/delete — over the IconsDir setting).</summary>
    public IconPool Icons { get; }

    /// <summary>The local skin library over the SkinsDir setting.</summary>
    public SkinsLibrary Skins { get; }

    public AccountList Accounts { get; }
    public HttpMetaCache MetaCache { get; }

    /// <summary>The flint-legacy eager pass outcome: what the
    /// startup remount did, so the host can log it and tests can assert it.</summary>
    public FlintLegacyMigration.Result LegacyMigration { get; }

    /// <summary>The INI import outcome: what the one-shot importer did,
    /// including an unreadable chain INI — the ctor runs before any logger exists, so the
    /// host surfaces this after Build (the flint-legacy result pattern).</summary>
    public FlintConfigImporter.ImportResult ConfigImport { get; }

    public LauncherRuntime(FlintPaths paths, HttpClient? http = null,
        ModrinthClient? modrinth = null,
        FlameClient? flame = null, TechnicApi? technic = null, AtLauncherApi? atlauncher = null,
        FtbApi? ftb = null, ImgurApi? imgur = null, AuthEndpointsOptions? auth = null)
    {
        Paths = paths;
        Http = http ?? NetPrimaryHandler.CreateClient();
        Modrinth = modrinth ?? new ModrinthClient();
        Auth = auth ?? new AuthEndpointsOptions { Http = Http };
        Flame = flame ?? new FlameClient();
        Technic = technic ?? new TechnicApi(Http);
        AtLauncher = atlauncher ?? new AtLauncherApi(Http);
        Ftb = ftb ?? new FtbApi(Http);
        Imgur = imgur ?? new ImgurApi(Http);

        // the watched-directory tenancy (the restored C++ watcher glue): reconciles are
        // serialized through the instance lock — watch events arrive on threadpool threads
        // and must not race endpoint mutations of the instance list
        InstanceWatchSpace = new DirectorySpace(action =>
        {
            InstanceLock.Wait();
            try
            {
                action();
            }
            finally
            {
                InstanceLock.Release();
            }
        });

        // secrets init: <dataDir>/secrets.json (0600) — the transitional
        // store; the secret trio (ProxyPass/ModrinthToken/FlameKeyOverride) reads and
        // writes here (PUT /settings reroutes, NetConfigApplier/API clients read the store)
        Secrets = new FileSecretStore(paths.SecretsFile, FlintJsonContext.Default.SecretsFile);

        // state load: launcher-state.json BEFORE the config import — the
        // importer will strip the state keys into it
        State = new LauncherStateStore(paths.LauncherStateFile, FlintJsonContext.Default.LauncherState);

        // per-source migration decision records: <stateDir>/migration/<source>.json
        MigrationDecisions = new MigrationDecisionStore(
            paths.MigrationDir, FlintJsonContext.Default.MigrationDecisionRecord);

        // flint-legacy migration probe/execute: the pre-reform
        // flat layout remounts onto the five roots ONCE (eager, no lazy dual-read) — before
        // the config import so the moved flint.cfg is found on the config side, and before
        // the cache ledger is built so its Load() reads the remounted metacache.json
        LegacyMigration = FlintLegacyMigration.ExecuteIfNeeded(paths, MigrationDecisions);

        // global config: flint.json is the authoritative user layer;
        // when it is missing, the one-shot importer reads the INI chain
        // (flint.cfg ← prismlauncher.cfg/polymc.cfg/multimc.cfg|mmc.cfg) and sorts it in a
        // single pass — secret trio → the store (loaded above), state keys → the
        // state store (loaded above), the rest → the record domains. Flint never writes INI.
        Config = new FlintConfigStore(paths.ConfigFile, FlintJsonContext.Default.FlintConfigFile);
        ConfigImport = FlintConfigImporter.ImportIfNeeded(paths, Config, Secrets, State);
        Dirs = new ConfiguredDirs(paths, Config);

        // meta cache: the standard base table (registered exactly once, before any use — a
        // registration is never lazy enough to resolve against the process CWD), then the
        // ledger Load(): the persisted index restores, so existing entries do not
        // re-download on every run
        MetaCache = HttpMetaCache.ForRoots(paths, Http);
        MetaCache.Load();

        // instance list: <dataDir>/instances; the typed instance
        // environment (user flint.json store + the instance.json/instance-state.json type
        // infos) and the group-file config objects are constructor-injected; discovered
        // instances also carry the cache, state store and the launch pipeline stamp.
        // The legacy read-compat probes: the data-root
        // instgroups.json (the Prism import target), then the primary-instance-dir copy
        // upstream used before there was a launcher data dir
        var instanceDirs = MaterializedDirectory.Ensure(paths.InstancesDir)!;
        Instances = new InstanceList(
            [instanceDirs],
            new InstanceEnvironment(
                Config, FlintJsonContext.Default.InstanceConfigFile, FlintJsonContext.Default.InstanceStateFile),
            paths.GroupsFile,
            [paths.InstgroupsFile, instanceDirs.Path / "instgroups.json"],
            metaCache: MetaCache,
            launcherState: State);
        Instances.LoadList();
        // the restored watcher glue: external changes to the instance roots now reload the
        // list (coalesced, serialized through InstanceLock) instead of waiting for a caller
        Instances.AttachWatcher(InstanceWatchSpace);

        // icon pool: the IconList successor over the IconsDir
        // SETTING (default "icons", data-root-relative — previously three hardcoded
        // Path.Combine(DataDir, "icons") sites ignored the setting)
        Icons = new IconPool(MaterializedDirectory.Ensure(Dirs.IconsDir)!);

        // local skin library: the SkinsDir SETTING
        // (default "skins", data-root-relative) finally has a resident model
        Skins = new SkinsLibrary(MaterializedDirectory.Ensure(Dirs.SkinsDir)!);
        // the restored watcher glue now covers the dir-kind models too; a rename/move of
        // a watched root re-points them (the index/list rebuilds over the new location)
        Icons.AttachWatcher(InstanceWatchSpace);
        Skins.AttachWatcher(InstanceWatchSpace);
        InstanceWatchSpace.TenantMoved += (oldDir, newDir) =>
        {
            if (oldDir == Icons.IconsDir)
            {
                Icons.SetIconsDir(newDir);
            }

            if (oldDir == Skins.SkinsDir)
            {
                Skins.SetSkinsDir(newDir);
            }
        };

        // accounts: <dataDir>/accounts.json (autosaved on every change)
        Accounts = new AccountList();
        Accounts.SetListFilePath(paths.AccountsFile, enableAutosave: true);
        Accounts.LoadList();

        // (there is no news checker — no /news endpoint, no feed base, no
        // FLINT_NEWS_RSS_URL; the C++ GUI was its only consumer)
    }

    /// <summary>Serialize the instance-list mutations (create/delete/rename + LoadList):
    /// InstanceList is not thread-safe and the API handlers can interleave.</summary>
    public InstanceMutationLock InstanceLock { get; } = new();

    /// <summary>The watched-directory tenancy: instance-root watchers live here so external
    /// changes reload the instance list (the restored C++ watcher glue). Owned by the
    /// runtime; disposed with it.</summary>
    public DirectorySpace InstanceWatchSpace { get; }

    public void Dispose()
    {
        InstanceWatchSpace.Dispose();
        // the metacache ledger's deferred write (the 30s timer) flushes now, so shutdown
        // after activity does not cost re-hashes and revalidations on the next start
        MetaCache.Dispose();
    }
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
}
