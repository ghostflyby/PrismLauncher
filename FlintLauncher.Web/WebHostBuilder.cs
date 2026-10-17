// SPDX-License-Identifier: GPL-3.0-only
// The ASP.NET Core Minimal API host factory: BuildWebApplication(args) resolves the five
// roots (G1 three-layer flags: precise > home > platform default), fail-fast-probes them
// BEFORE any core service is built, wires the core singletons and maps the /api/v1
// endpoints. Program.cs is a one-line wrapper around this factory so integration tests can
// host the exact same app in-process.

using System.Diagnostics;
using System.Text.Json;
using FlintLauncher.Core;
using FlintLauncher.Core.Auth;
using FlintLauncher.Core.Diagnostics;
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
        // the five-root layout (G1); tests override to point InstallRoot (the jars/ lookup)
        // at a fixture root — the former JavaUtils.LauncherRootPath static test hook
        paths ??= ResolveFlintPaths(args);
        // fail-fast: five roots EnsureDir + writable probe; any unusable root refuses
        // startup with its name (no silent degradation, no half-assembled runtime)
        paths.EnsureUsable();
        // NOTE: the process working directory is NOT touched — every core path is explicit
        // (FlintPaths), so multiple hosts can coexist in one process (integration tests).

        // S2: the API token never touches the disk — host injection (--api-token /
        // FLINT_API_TOKEN) wins, else a process-lifetime token is generated and logged at
        // startup; a pre-reform <dataRoot>/api-token leftover is dead weight and is removed
        ApiAuthService.RemoveLegacyTokenFile(paths.DataRoot);
        var apiAuth = ApiAuthService.Resolve(args);

        var builder = WebApplication.CreateSlimBuilder(args);

        // diagnostics channel (Application.cpp:513-528 parity): level from --log-level /
        // FLINT_LOG_LEVEL (default Information), default console provider plus the rolling
        // state-root file (S4); the core services log through this factory by injection
        // (LaunchServices.LoggerFactory / DI)
        builder.Logging.SetMinimumLevel(ResolveLogLevel(args));
        // S4 + assembly step 2 ("logger early, every later step diagnosable"): the rolling
        // file sink opens its writer IN ITS CONSTRUCTOR, so the provider instance exists
        // before the runtime assembly below (steps 3-9) — a mid-assembly crash gets its
        // last words into Flint-0.log through the catch around the ctor, instead of dying
        // before any log file write
        var fileLogger = new RollingFileLoggerProvider(paths.LogsDir);
        builder.Logging.AddProvider(fileLogger);

        builder.Services.ConfigureHttpJsonOptions(options => { options.SerializerOptions.TypeInfoResolverChain.Insert(0, FlintJsonContext.Default); });

        // machine-readable API surface (G3): source-generated under Native AOT, served
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
            // unwritten; then fail-fast as before (no silent degradation, no half runtime)
            fileLogger.CreateLogger(LogCategories.Startup).LogError(e,
                "runtime assembly failed — the process cannot start");
            fileLogger.Dispose();
            throw;
        }

        builder.Services.AddSingleton(FlintConfig.Default);
        builder.Services.AddSingleton(runtime);
        // the D5/D6 data models are injectable like the rest of the runtime's singletons
        builder.Services.AddSingleton(runtime.Icons);
        builder.Services.AddSingleton(runtime.Skins);
        builder.Services.AddSingleton<FlintTaskRegistry>();
        builder.Services.AddSingleton<DeviceCodeSessionStore>();
        builder.Services.AddSingleton<JavaScanService>();
        builder.Services.AddSingleton(apiAuth);
        builder.Services.AddHostedService<AccountRefreshService>();

        var app = builder.Build();
        var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();

        // S2: a generated token exists only in this process — announce it once at startup
        // (Information, single line) so the host's client can pick it up; nothing is written
        if (apiAuth.Enabled && apiAuth.TokenGenerated)
        {
            loggerFactory.CreateLogger(LogCategories.ApiAuth).LogInformation(
                "Flint API token (temporary, process-lifetime): {Token}", apiAuth.Token);
        }

        // the launch pipeline's dependencies (the C++ APPLICATION singleton's launch half):
        // logger factory from the app services, paths from the FlintPaths layout, meta cache,
        // global settings and runtime state from the runtime. The launch endpoints hand it to
        // the controller and the instance list stamps it onto (re)discovered instances.
        runtime.LaunchServices = new LaunchServices
        {
            Paths = paths,
            LoggerFactory = loggerFactory,
            MetaCache = runtime.MetaCache,
            Config = runtime.Config,
            LauncherState = runtime.State,
            InstallRoot = paths.InstallRoot,
            // CACHE E2 via GLOBAL §3: the java root follows the JavaDir setting under store
            // semantics, and the managed-versions javalist scans it (the C++
            // APPLICATION->javalist() the AutoInstallJava "no download" branch reads)
            JavaRootProvider = () => runtime.JavaRoot,
            JavaInstallListProvider = () => new JavaInstallList(javaRoot: runtime.JavaRoot),
            // the C++ LaunchTask always parses log4j XML events out of the game output
            // (LaunchTask::parseXmlLogs); the port keeps it behind an opt-in gate for
            // pure-core consumers and this host turns it on (fix-3)
            XmlLogParsing = true,
        };
        runtime.Instances.LaunchServices = runtime.LaunchServices;

        // the flint-legacy startup remount (assembly step 5) already ran inside the runtime
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

        // the C5 INI import (assembly step 6) ran in the constructor too — surface its
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

        // fix-3: proxy / NumberOfManualRetries / RequestTimeout / UserAgentOverride reach the
        // shared Http stack (also re-applied after every settings write)
        RuntimeNetConfig.Apply(runtime);

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
        api.MapSkinLibrary(); // DATA D6: the local skin library (apply composes SkinUpload)
        api.MapMisc();
        api.MapModPlatform();
        api.MapInstanceContent();
        api.MapComponents();
        api.MapExport();
        api.MapManagedPack();
        api.MapSettingsAndIcons();
        api.MapMigration();
        api.MapStore(); // CACHE E5: the /store completeness derivation surface (G3 /store 新面)

        return app;
    }

    /// <summary>G1 three-layer root resolution. The platform defaults come from FlintPaths
    /// (the single absorption point); a home flag (--home-dir=X / FLINT_HOME) re-anchors all
    /// five roots at X/{config,data,store,cache,state} — the only implicit-derivation site;
    /// a precise flag (--config-dir/--data-dir/--store-dir/--cache-dir/--state-dir, each
    /// with its FLINT_*_DIR env) overrides exactly its own root, zero sibling implication.
    /// Precedence: precise > home > platform default; within a layer CLI beats env, and the
    /// last occurrence of a flag wins. NOTE: --data-dir names the precise data root since
    /// G1/G3 — the old "total directory" usage is --home-dir.</summary>
    public static FlintPaths ResolveFlintPaths(string[] args)
    {
        var config = FlintPaths.PlatformDefaultConfigRoot();
        var data = FlintPaths.PlatformDefaultDataRoot();
        var store = FlintPaths.PlatformDefaultStoreRoot();
        var cache = FlintPaths.PlatformDefaultCacheRoot();
        var state = FlintPaths.PlatformDefaultStateRoot();

        var home = FlagValue(args, "--home-dir", "FLINT_HOME");
        if (home is not null)
        {
            config = Path.Combine(home, "config");
            data = Path.Combine(home, "data");
            store = Path.Combine(home, "store");
            cache = Path.Combine(home, "cache");
            state = Path.Combine(home, "state");
        }

        return FlintPaths.FromRoots(
                FlagValue(args, "--config-dir", "FLINT_CONFIG_DIR") ?? config,
                FlagValue(args, "--data-dir", "FLINT_DATA_DIR") ?? data,
                FlagValue(args, "--store-dir", "FLINT_STORE_DIR") ?? store,
                FlagValue(args, "--cache-dir", "FLINT_CACHE_DIR") ?? cache,
                FlagValue(args, "--state-dir", "FLINT_STATE_DIR") ?? state) with
            {
                // G1: home mode records its origin so the flint-legacy probe can find the
                // pre-reform flat files at the home root (the fresh X/data child cannot)
                HomeRoot = home,
            };
    }

    /// <summary>The last occurrence of &lt;flag&gt; &lt;value&gt; / &lt;flag&gt;=&lt;value&gt;
    /// wins, else &lt;envName&gt; when non-empty; the result is made absolute. Null when
    /// neither layer speaks.</summary>
    private static string? FlagValue(string[] args, string flag, string envName)
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

        return value is null ? null : Path.GetFullPath(value);
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
