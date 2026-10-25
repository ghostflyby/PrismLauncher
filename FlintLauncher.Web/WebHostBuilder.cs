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
using FlintLauncher.Core.Diagnostics;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Java;
using FlintLauncher.Core.Launch;
using FlintLauncher.Core.ModPlatform.AtLauncher;
using FlintLauncher.Core.ModPlatform.Flame;
using FlintLauncher.Core.ModPlatform.Ftb;
using FlintLauncher.Core.ModPlatform.Modrinth;
using FlintLauncher.Core.ModPlatform.Technic;
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
        ModrinthApi? modrinth = null,
        FlameApi? flame = null,
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
        // startup with its name (no silent degradation, no half-assembled runtime)
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
        // (LaunchServices.LoggerFactory / DI)
        builder.Logging.SetMinimumLevel(ResolveLogLevel(args));
        // the rolling file sink opens its writer IN ITS CONSTRUCTOR, so the provider instance
        // exists before the runtime assembly below — a mid-assembly crash gets its
        // last words into Flint-0.log through the catch around the ctor, instead of dying
        // before any log file write
        // the logs directory is materialized here: when it is unusable (a file occupying
        // the path, unwritable) the host degrades to console-only logging and says so on
        // stderr — the C++ fatal dialog became a diagnosable, non-fatal startup line. The
        // runtime-assembly crash handler below degrades with it (stderr instead of the
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

        LauncherRuntime runtime;
        try
        {
            runtime = new LauncherRuntime(paths, modrinth: modrinth, flame: flame, technic: technic,
                atlauncher: atlauncher, ftb: ftb, auth: auth);
        }
        catch (Exception e)
        {
            // the file sink exists since before the ctor — the failure is diagnosable, not
            // unwritten; in the degraded (console-only) mode the same line goes to stderr.
            // Either way: fail-fast as before (no silent degradation, no half runtime)
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
        // the assembled runtime is not registered itself — a handler depends on the members
        // it uses, each injectable on its own
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(runtime.Settings);
        builder.Services.AddSingleton(runtime.Http);
        builder.Services.AddSingleton(runtime.Config);
        builder.Services.AddSingleton(runtime.Dirs);
        builder.Services.AddSingleton(runtime.State);
        builder.Services.AddSingleton(runtime.Secrets);
        builder.Services.AddSingleton(runtime.MigrationDecisions);
        builder.Services.AddSingleton(runtime.MetaCache);
        builder.Services.AddSingleton(runtime.Instances);
        builder.Services.AddSingleton(runtime.InstanceLock);
        builder.Services.AddSingleton(runtime.Icons);
        builder.Services.AddSingleton(runtime.Skins);
        builder.Services.AddSingleton(runtime.Accounts);
        builder.Services.AddSingleton(runtime.Modrinth);
        builder.Services.AddSingleton(runtime.Flame);
        builder.Services.AddSingleton(runtime.Technic);
        builder.Services.AddSingleton(runtime.AtLauncher);
        builder.Services.AddSingleton(runtime.Ftb);
        builder.Services.AddSingleton(runtime.Imgur);
        builder.Services.AddSingleton(runtime.Auth);
        builder.Services.AddSingleton<FlintTaskRegistry>();
        builder.Services.AddSingleton<DeviceCodeSessionStore>();
        builder.Services.AddSingleton<JavaScanService>();
        builder.Services.AddSingleton<PrismImportCompletion>();
        builder.Services.AddSingleton<NetSettingsApplier>();
        builder.Services.AddSingleton<DirSettingsApplier>();
        builder.Services.AddSingleton(apiAuth);
        builder.Services.AddHostedService<AccountRefreshService>();

        // the launch pipeline's dependencies (the C++ APPLICATION singleton's launch half):
        // global settings and runtime state from the runtime, the logger factory from the
        // app services (it only exists after Build — hence the factory). The launch
        // endpoints inject it; the instance list is stamped with it after Build.
        builder.Services.AddSingleton(sp => new LaunchServices
        {
            Paths = paths,
            LoggerFactory = sp.GetRequiredService<ILoggerFactory>(),
            Http = runtime.Http,
            Settings = runtime.Settings,
            Config = runtime.Config,
            LauncherState = runtime.State,
            Auth = runtime.Auth,
            // the java root follows the JavaDir setting under store
            // semantics, and the managed-versions javalist scans it (the C++
            // APPLICATION->javalist() the AutoInstallJava "no download" branch reads)
            JavaRootProvider = () => runtime.Dirs.JavaRoot,
            JavaInstallListProvider = () => new JavaInstallList(javaRoot: runtime.Dirs.JavaRoot),
            // the C++ LaunchTask always parses log4j XML events out of the game output
            // (LaunchTask::parseXmlLogs); the port keeps it behind an opt-in gate for
            // pure-core consumers and this host turns it on
            XmlLogParsing = true,
        });

        var app = builder.Build();
        var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();

        // the runtime owns no hosted-service lifetime of its own — its disposal (the watch
        // tenancy, the metacache ledger flush) rides application stop
        app.Lifetime.ApplicationStopping.Register(runtime.Dispose);

        // a generated token exists only in this process — announce it once at startup
        // (Information, single line) so the host's client can pick it up; nothing is written
        if (apiAuth is { Enabled: true, TokenGenerated: true })
        {
            loggerFactory.CreateLogger(LogCategories.ApiAuth).LogInformation(
                "Flint API token (temporary, process-lifetime): {Token}", apiAuth.Token);
        }

        // stamp the launch-pipeline dependencies onto the instance list (re-stamps the
        // already-discovered residents); resolving here is also the factory's eager,
        // fail-fast construction
        runtime.Instances.LaunchServices = app.Services.GetRequiredService<LaunchServices>();

        // the flint-legacy startup remount already ran inside the runtime
        // constructor — surface what it did (informational; a failed pass retries next start)
        if (runtime.LegacyMigration is { Ran: true } legacy)
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

        // the INI import ran in the constructor too — surface its
        // outcome, in particular the previously SILENT failure: a chain INI that exists but
        // cannot be parsed (the launcher starts on a default flint.json behind it)
        if (runtime.ConfigImport.ImportedIni is { } importedIni)
        {
            loggerFactory.CreateLogger(LogCategories.Startup).LogInformation(
                "INI import: flint.json seeded from {Ini}", importedIni);
        }
        else if (runtime.ConfigImport.UnreadableIni is { } unreadableIni)
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
        app.Services.GetRequiredService<NetSettingsApplier>().Apply();

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
