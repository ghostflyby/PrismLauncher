// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// The ASP.NET Core Minimal API host factory: BuildWebApplication(args) resolves the five
// roots (three-layer flags: precise > home > platform default), fail-fast-probes them
// BEFORE any core service is built, wires the core singletons and maps the /api/v1
// endpoints. Program.cs is a one-line wrapper around this factory so integration tests can
// host the exact same app in-process.

using System.Diagnostics;
using System.Text.Json;
using FlintLauncher.Core;
using FlintLauncher.Core.Auth;
using FlintLauncher.Core.Config;
using FlintLauncher.Core.Diagnostics;
using FlintLauncher.Core.Icons;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Java;
using FlintLauncher.Core.Launch;
using FlintLauncher.Core.Migration;
using FlintLauncher.Core.ModPlatform.AtLauncher;
using FlintLauncher.Core.ModPlatform.Ftb;
using FlintLauncher.Core.ModPlatform.Technic;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Screenshots;
using FlintLauncher.Core.Secrets;
using FlintLauncher.Core.Skins;
using FlintLauncher.Core.State;
using FlintLauncher.ExternalApi.Flame;
using FlintLauncher.ExternalApi.Modrinth;
using FlintLauncher.Web.Endpoints;
using FlintLauncher.Web.Logging;
using FlintLauncher.Web.Models;
using FlintLauncher.Web.OpenApi;
using FlintLauncher.Web.Services;
using TruePath;

// extension-method log events (HttpLog.Request/Probe) live in the Core Diagnostics namespace

namespace FlintLauncher.Web;

public static class WebHostBuilder
{
    public static WebApplication BuildWebApplication(string[] args,
        ModrinthClient? modrinth = null,
        FlameClient? flame = null,
        TechnicApi? technic = null,
        AtLauncherApi? atlauncher = null,
        FtbApi? ftb = null,
        AuthEndpointsOptions? auth = null,
        FlintPaths? paths = null)
    {
        // the five-root layout; tests override to point InstallRoot (the jars/ lookup)
        // at a fixture root — the former JavaUtils.LauncherRootPath static test hook
        paths ??= ResolveFlintPaths(args);
        // fail-fast: five roots EnsureDir + writable probe; any unusable root refuses
        // startup with its name (no silent degradation, no half-assembled host)
        paths.EnsureUsable();
        // NOTE: the process working directory is NOT touched — every core path is explicit
        // (FlintPaths), so multiple hosts can coexist in one process (integration tests).

        // the API token never touches the disk — host injection (--api-token /
        // FLINT_API_TOKEN) wins, else a process-lifetime token is generated and logged at
        // startup; a pre-reform <dataRoot>/api-token leftover is dead weight and is removed
        ApiAuthService.RemoveLegacyTokenFile(paths.DataRoot);
        var apiAuth = ApiAuthService.Resolve(args);

        var builder = WebApplication.CreateSlimBuilder(args);

        // diagnostics channel (Application.cpp:513-528 parity): level from --log-level /
        // FLINT_LOG_LEVEL (default Information), default console provider plus the rolling
        // state-root file; the core services log through this factory by injection
        // (the LaunchFactory / DI)
        builder.Logging.SetMinimumLevel(ResolveLogLevel(args));
        // the rolling file sink opens its writer IN ITS CONSTRUCTOR, so the provider instance
        // exists before the core assembly below — a mid-assembly crash gets its
        // last words into Flint-0.log through the catch around the assembly, instead of dying
        // before any log file write
        // the logs directory is materialized here: when it is unusable (a file occupying
        // the path, unwritable) the host degrades to console-only logging and says so on
        // stderr — the C++ fatal dialog became a diagnosable, non-fatal startup line. The
        // the assembly-crash handler below degrades with it (stderr instead of the
        // file sink), so a broken logs path can never hide a startup failure.
        RollingFileLoggerProvider? fileLogger = null;
        if (MaterializedDirectory.Ensure(paths.LogsDir) is { } logsDir)
        {
            fileLogger = new RollingFileLoggerProvider(logsDir);
            builder.Logging.AddProvider(fileLogger);
        }
        else
        {
            Console.Error.WriteLine(
                $"Flint: the logs directory '{paths.LogsDir.Value}' is unusable — console-only logging");
        }

        builder.Services.ConfigureHttpJsonOptions(options => { options.SerializerOptions.TypeInfoResolverChain.Insert(0, FlintJsonContext.Default); });

        // machine-readable API surface: source-generated under Native AOT, served
        // unauthenticated next to /healthz as a discovery-plane artifact
        builder.Services.AddOpenApi("v1", ApiOpenApi.Configure);

        // the host owns the persistent client and its configuration carriers: the two
        // handlers hold the proxy/user agent/retries/timeout (the applier writes them), and
        // the client over their chain is what every caller threads through
        var primaryHandler = new NetPrimaryHandler();
        var retryHandler = new RetryHandler { InnerHandler = primaryHandler };
        var http = new HttpClient(retryHandler, disposeHandler: false)
        {
            // no total-duration timeout — the request executors carry the RequestTimeout
            // inactivity window instead
            Timeout = Timeout.InfiniteTimeSpan,
        };

        // ------------------------------------------------------------------ core assembly
        // The singletons a C++ Application object would own, built once against the resolved
        // roots. The order IS the dependency graph: platform clients over the shared
        // transport → the watch tenancy and its lock → secrets → state (loaded before the
        // config import, which sorts state keys into it) → the flint-legacy remount (eager,
        // once — before the config import so a moved flint.cfg is found, and before the cache
        // ledger so its Load reads the remounted ledger) → config + INI import → dir settings
        // → cache ledger → instances → icons/skins → accounts.
        InstanceMutationLock instanceLock;
        DirectorySpace watchSpace;
        ISecretStore secrets;
        LauncherStateStore state;
        MigrationDecisionStore migrationDecisions;
        FlintLegacyMigration.Result legacyMigration;
        FlintConfigStore config;
        FlintConfigImporter.ImportResult configImport;
        ConfiguredDirs dirs;
        HttpMetaCache metaCache;
        InstanceList instances;
        IconPool icons;
        SkinsLibrary skins;
        AccountList accounts;
        ModrinthClient modrinthClient;
        FlameClient flameClient;
        TechnicApi technicClient;
        AtLauncherApi atLauncherClient;
        FtbApi ftbClient;
        ImgurApi imgurClient;
        AuthEndpointsOptions authOptions;
        try
        {
            // platform clients: the injected instances from the host factory, else defaults
            // over the shared client
            modrinthClient = modrinth ?? new ModrinthClient();
            authOptions = auth ?? new AuthEndpointsOptions { Http = http };
            flameClient = flame ?? new FlameClient();
            technicClient = technic ?? new TechnicApi(http);
            atLauncherClient = atlauncher ?? new AtLauncherApi(http);
            ftbClient = ftb ?? new FtbApi(http);
            imgurClient = new ImgurApi(http);

            // the watched-directory tenancy: reconciles are serialized through the instance
            // lock — watch events arrive on threadpool threads and must not race endpoint
            // mutations of the instance list
            instanceLock = new InstanceMutationLock();
            watchSpace = new DirectorySpace(action =>
            {
                instanceLock.Wait();
                try
                {
                    action();
                }
                finally
                {
                    instanceLock.Release();
                }
            });

            // secrets: <data>/secrets.json (0600) — the secret trio (ProxyPass,
            // ModrinthToken, FlameKeyOverride) reads and writes here
            secrets = new FileSecretStore(paths.SecretsFile, FlintJsonContext.Default.SecretsFile);

            // state load: launcher-state.json BEFORE the config import — the importer strips
            // the state keys into it
            state = new LauncherStateStore(paths.LauncherStateFile, FlintJsonContext.Default.LauncherState);

            // per-source migration decision records: <state>/migration/<source>.json
            migrationDecisions = new MigrationDecisionStore(
                paths.MigrationDir, FlintJsonContext.Default.MigrationDecisionRecord);

            // the pre-reform flat layout remounts onto the five roots exactly once (eager,
            // no lazy dual-read), before the config import and the cache ledger
            legacyMigration = FlintLegacyMigration.ExecuteIfNeeded(paths, migrationDecisions);

            // flint.json is the authoritative user layer; when it is missing, the one-shot
            // importer reads the INI chain (flint.cfg ← prismlauncher.cfg ← polymc.cfg ←
            // multimc.cfg|mmc.cfg) and sorts it in a single pass: secret trio → the store,
            // state keys → the state store, the rest → the record domains
            config = new FlintConfigStore(paths.ConfigFile, FlintJsonContext.Default.FlintConfigFile);
            configImport = FlintConfigImporter.ImportIfNeeded(paths, config, secrets, state);
            dirs = new ConfiguredDirs(paths, config);

            // the standard base table, registered exactly once before any use, then the
            // ledger Load(): the persisted index restores, so existing entries do not
            // re-download on every run
            metaCache = HttpMetaCache.ForRoots(paths, http);
            metaCache.Load();

            // instance discovery over <data>/instances; the typed environment carries the
            // user flint.json layer, and discovered instances also carry the cache, the
            // state store and the launch-pipeline stamp
            var instanceDirs = MaterializedDirectory.Ensure(paths.InstancesDir)!;
            instances = new InstanceList(
                [instanceDirs],
                new InstanceEnvironment(
                    config, FlintJsonContext.Default.InstanceConfigFile,
                    FlintJsonContext.Default.InstanceStateFile),
                paths.GroupsFile,
                [paths.InstgroupsFile, instanceDirs.Path / "instgroups.json"],
                storeRoot: paths.StoreRoot,
                metaCache: metaCache,
                launcherState: state);
            instances.LoadList();
            // external changes to the instance roots reload the list (coalesced, serialized
            // through the instance lock) instead of waiting for a caller
            instances.AttachWatcher(watchSpace);

            // icon library and local skin library over their dir-kind settings
            icons = new IconPool(MaterializedDirectory.Ensure(dirs.IconsDir)!);
            skins = new SkinsLibrary(MaterializedDirectory.Ensure(dirs.SkinsDir)!);
            icons.AttachWatcher(watchSpace);
            skins.AttachWatcher(watchSpace);
            // a rename/move of a watched root re-points the dir-kind models
            watchSpace.TenantMoved += (oldDir, newDir) =>
            {
                if (oldDir == icons.IconsDir)
                {
                    icons.SetIconsDir(newDir);
                }

                if (oldDir == skins.SkinsDir)
                {
                    skins.SetSkinsDir(newDir);
                }
            };

            // accounts: <data>/accounts.json, autosaved on every change
            accounts = new AccountList();
            accounts.SetListFilePath(paths.AccountsFile, enableAutosave: true);
            accounts.LoadList();
        }
        catch (Exception e)
        {
            // the file sink exists since before the assembly — the failure is diagnosable, not
            // unwritten; in the degraded (console-only) mode the same line goes to stderr.
            // Either way: fail-fast as before (no silent degradation, no half-booted host)
            if (fileLogger is { } sink)
            {
                sink.CreateLogger(LogCategories.Startup).LogError(e,
                    "runtime assembly failed — the process cannot start");
                sink.Dispose();
            }
            else
            {
                Console.Error.WriteLine(
                    "Flint: runtime assembly failed — the process cannot start: " + e.Message);
            }

            throw;
        }

        builder.Services.AddSingleton(FlintConfig.Default);
        // each assembled service is registered by its own type — a handler depends on the
        // members it uses, each injectable on its own
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(primaryHandler);
        builder.Services.AddSingleton(retryHandler);
        builder.Services.AddSingleton(http);
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(dirs);
        builder.Services.AddSingleton(state);
        builder.Services.AddSingleton(secrets);
        builder.Services.AddSingleton(migrationDecisions);
        builder.Services.AddSingleton(metaCache);
        builder.Services.AddSingleton(instances);
        builder.Services.AddSingleton(instanceLock);
        builder.Services.AddSingleton(icons);
        builder.Services.AddSingleton(skins);
        builder.Services.AddSingleton(accounts);
        builder.Services.AddSingleton(modrinthClient);
        builder.Services.AddSingleton(flameClient);
        builder.Services.AddSingleton(technicClient);
        builder.Services.AddSingleton(atLauncherClient);
        builder.Services.AddSingleton(ftbClient);
        builder.Services.AddSingleton(imgurClient);
        builder.Services.AddSingleton(authOptions);
        builder.Services.AddSingleton<FlintTaskRegistry>();
        builder.Services.AddSingleton<DeviceCodeSessionStore>();
        builder.Services.AddSingleton<JavaScanService>();
        builder.Services.AddSingleton<PrismImportCompletion>();
        builder.Services.AddSingleton<NetConfigApplier>();
        builder.Services.AddSingleton<DirSettingsApplier>();
        builder.Services.AddSingleton(apiAuth);
        builder.Services.AddHostedService<AccountRefreshService>();

        // the launch pipeline's composition point (the C++ APPLICATION singleton's launch
        // half): the globals assembled above, the logger factory from the app services (it
        // only exists after Build — hence the factory). The launch endpoints inject it.
        builder.Services.AddSingleton(sp => new LaunchFactory(
            paths, sp.GetRequiredService<ILoggerFactory>(), http,
            // the C++ LaunchTask always parses log4j XML events out of the game output
            // (LaunchTask::parseXmlLogs); the port keeps it behind an opt-in gate for
            // pure-core consumers and this host turns it on
            new LaunchOptions(XmlLogParsing: true),
            config: config,
            dirs: dirs,
            auth: authOptions));

        var app = builder.Build();
        var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();

        // the two assembled services with deferred shutdown work: the watch tenancy stops
        // its watchers, the metacache ledger flushes its pending write (the 30s timer) so
        // shutdown after activity does not cost re-hashes and revalidations on the next start.
        // One callback in this order — the watchers must not fire into a flushed ledger.
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            watchSpace.Dispose();
            metaCache.Dispose();
        });

        // a generated token exists only in this process — announce it once at startup
        // (Information, single line) so the host's client can pick it up; nothing is written
        if (apiAuth is { Enabled: true, TokenGenerated: true })
        {
            loggerFactory.CreateLogger(LogCategories.ApiAuth).LogInformation(
                "Flint API token (temporary, process-lifetime): {Token}", apiAuth.Token);
        }

        // resolve the launch factory eagerly — its construction is the last piece of the
        // composition, and failing here beats failing at the first launch request
        _ = app.Services.GetRequiredService<LaunchFactory>();

        // the flint-legacy startup remount ran during assembly — surface what it did
        // (informational; a failed pass retries next start)
        if (legacyMigration is { Ran: true } legacy)
        {
            loggerFactory.CreateLogger(LogCategories.Migration).LogInformation(
                "flint-legacy remount: {Items} item(s), {Bytes} bytes, legacy Prism ledger cleaned: {Cleaned}{Suffix}",
                legacy.ItemsMoved, legacy.Bytes, legacy.LegacyLedgerCleaned,
                legacy.Error is null ? "" : $" (incomplete, will resume: {legacy.Error})");
        }
        else
        {
            // silent skips cost a real-machine diagnosis once already: say why, at Debug
            // (a fresh install skips on every startup — Information would be spam)
            loggerFactory.CreateLogger(LogCategories.Migration).LogDebug(
                "flint-legacy remount: skipped (no undecided legacy layout at the data or home root)");
        }

        // the INI import ran during assembly too — surface its outcome, in particular the
        // previously SILENT failure: a chain INI that exists but cannot be parsed (the
        // launcher starts on a default flint.json behind it)
        if (configImport.ImportedIni is { } importedIni)
        {
            loggerFactory.CreateLogger(LogCategories.Startup).LogInformation(
                "INI import: flint.json seeded from {Ini}", importedIni);
        }
        else if (configImport.UnreadableIni is { } unreadableIni)
        {
            loggerFactory.CreateLogger(LogCategories.Startup).LogWarning(
                "INI import: {Ini} is unreadable — flint.json stays default (the INI is kept; the write side is JSON-only)",
                unreadableIni);
        }

        // request log, outermost so the status is final (the error middleware below may
        // rewrite it to 500); health probes log at Debug to keep the default view readable
        var httpLog = loggerFactory.CreateLogger(LogCategories.Http);
        app.Use(async (context, next) =>
        {
            var start = Stopwatch.GetTimestamp();
            await next(context);
            var path = context.Request.Path.Value ?? "";
            if (path == "/healthz")
            {
                httpLog.Probe(context.Request.Method, path, context.Response.StatusCode,
                    Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            else
            {
                httpLog.Request(context.Request.Method, path, context.Response.StatusCode,
                    Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
        });

        // proxy / NumberOfManualRetries / RequestTimeout / UserAgentOverride reach the
        // shared Http stack (also re-applied after every settings write)
        app.Services.GetRequiredService<NetConfigApplier>().Apply();

        // uniform {"error":{...}} body for unhandled faults
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (Exception ex)
            {
                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = 500;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(
                        JsonSerializer.Serialize(
                            new ApiErrorEnvelope(new ErrorResponse("internal_error", ex.Message)),
                            FlintJsonContext.Default.ApiErrorEnvelope));
                }
            }
        });

        app.MapOpenApi();

        app.MapGet("/healthz", () => Results.Ok(new HealthResponse("ok")))
            .WithTags("Meta")
            .Produces<HealthResponse>();

        var api = app.MapGroup("/api/v1");
        api.AddApiAuth(apiAuth);
        api.MapGet("/info", (FlintConfig config) => new InfoResponse(
                config.LauncherDisplayName,
                config.PrintableVersionString(),
                config.UserAgent,
                config.MetaUrl,
                config.JavaDownloaderEnabled))
            .WithTags("Meta");

        api.MapInstances();
        api.MapLaunch();
        api.MapTasks();
        api.MapAccounts();
        api.MapSkinLibrary(); // the local skin library (apply composes SkinUpload)
        api.MapMisc();
        api.MapModPlatform();
        api.MapInstanceContent();
        api.MapComponents();
        api.MapExport();
        api.MapManagedPack();
        api.MapSettingsAndIcons();
        api.MapMigration();
        api.MapStore(); // the /store completeness derivation surface

        return app;
    }

    /// <summary>Three-layer root resolution. The platform defaults come from FlintPaths
    /// (the single absorption point); a home flag (--home-dir=X / FLINT_HOME) re-anchors all
    /// five roots at X/{config,data,store,cache,state} — the only implicit-derivation site;
    /// a precise flag (--config-dir/--data-dir/--store-dir/--cache-dir/--state-dir, each
    /// with its FLINT_*_DIR env) overrides exactly its own root, zero sibling implication.
    /// Precedence: precise > home > platform default; within a layer CLI beats env, and the
    /// last occurrence of a flag wins. NOTE: --data-dir names the precise data root —
    /// the old "total directory" usage is --home-dir.</summary>
    public static FlintPaths ResolveFlintPaths(string[] args)
    {
        var config = FlintPaths.PlatformDefaultConfigRoot();
        var data = FlintPaths.PlatformDefaultDataRoot();
        var store = FlintPaths.PlatformDefaultStoreRoot();
        var cache = FlintPaths.PlatformDefaultCacheRoot();
        var state = FlintPaths.PlatformDefaultStateRoot();

        var home = FlagValue(args, "--home-dir", "FLINT_HOME");
        if (home is { } homeRoot)
        {
            config = homeRoot / "config";
            data = homeRoot / "data";
            store = homeRoot / "store";
            cache = homeRoot / "cache";
            state = homeRoot / "state";
        }

        AbsolutePath configRoot = FlagValue(args, "--config-dir", "FLINT_CONFIG_DIR") ?? config;
        AbsolutePath dataRoot = FlagValue(args, "--data-dir", "FLINT_DATA_DIR") ?? data;
        AbsolutePath storeRoot = FlagValue(args, "--store-dir", "FLINT_STORE_DIR") ?? store;
        AbsolutePath cacheRoot = FlagValue(args, "--cache-dir", "FLINT_CACHE_DIR") ?? cache;
        AbsolutePath stateRoot = FlagValue(args, "--state-dir", "FLINT_STATE_DIR") ?? state;
        var roots = new FlintPaths()
        {
            ConfigRoot = configRoot,
            DataRoot = dataRoot,
            StoreRoot = storeRoot,
            CacheRoot = cacheRoot,
            StateRoot = stateRoot,
        };

        // home mode records its origin so the flint-legacy probe can find the
        // pre-reform flat files at the home root (the fresh X/data child cannot)
        return roots with
        {
            HomeRoot = home,
        };
    }

    /// <summary>The last occurrence of &lt;flag&gt; &lt;value&gt; / &lt;flag&gt;=&lt;value&gt;
    /// wins, else &lt;envName&gt; when non-empty; the result is made absolute. Null when
    /// neither layer speaks.</summary>
    private static AbsolutePath? FlagValue(string[] args, string flag, string envName)
    {
        string? value = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == flag && i + 1 < args.Length)
            {
                value = args[i + 1];
            }
            else if (args[i].StartsWith(flag + "=", StringComparison.Ordinal))
            {
                value = args[i][(flag.Length + 1)..];
            }
        }

        if (value is null)
        {
            var env = Environment.GetEnvironmentVariable(envName);
            if (!string.IsNullOrWhiteSpace(env))
            {
                value = env;
            }
        }

        return value is null ? null : new AbsolutePath(Path.GetFullPath(value));
    }

    /// <summary>--log-level &lt;level&gt; (or --log-level=&lt;level&gt;) wins, then
    /// FLINT_LOG_LEVEL, then Information. Accepts the LogLevel names plus the common
    /// aliases info/warn; an unrecognized value falls back to Information silently (the
    /// alternative — refusing to start over a logging flag — hurts more than it helps).</summary>
    public static LogLevel ResolveLogLevel(string[] args)
    {
        string? raw = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--log-level" && i + 1 < args.Length)
            {
                raw = args[i + 1];
            }
            else if (args[i].StartsWith("--log-level=", StringComparison.Ordinal))
            {
                raw = args[i]["--log-level=".Length..];
            }
        }

        raw ??= Environment.GetEnvironmentVariable("FLINT_LOG_LEVEL");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return LogLevel.Information;
        }

        raw = raw.Trim() switch
        {
            "info" or "INFO" => nameof(LogLevel.Information),
            "warn" or "WARN" => nameof(LogLevel.Warning),
            _ => raw.Trim(),
        };
        return Enum.TryParse<LogLevel>(raw, ignoreCase: true, out var level) ? level : LogLevel.Information;
    }
}
