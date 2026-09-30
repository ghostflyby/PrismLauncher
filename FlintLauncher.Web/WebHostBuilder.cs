// SPDX-License-Identifier: GPL-3.0-only
// The ASP.NET Core Minimal API host factory: BuildWebApplication(args) resolves the data
// directory (--data-dir <path> / FLINT_DATA_DIR), chdir's into it BEFORE any core service is
// built (the meta cache and several core paths are CWD-relative), wires the core singletons
// and maps the /api/v1 endpoints. Program.cs is a one-line wrapper around this factory so
// integration tests can host the exact same app in-process.
using System.Diagnostics;
using FlintLauncher.Core;
using FlintLauncher.Core.Diagnostics;
using FlintLauncher.Web.Endpoints;
using FlintLauncher.Web.Logging;
using FlintLauncher.Web.Services;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Logging;
// extension-method log events (HttpLog.Request/Probe) live in the Core Diagnostics namespace
using static FlintLauncher.Core.Diagnostics.HttpLog;

namespace FlintLauncher.Web;

public static class WebHostBuilder
{
    public static WebApplication BuildWebApplication(string[] args,
        Core.ModPlatform.Modrinth.ModrinthApi? modrinth = null,
        Core.ModPlatform.Flame.FlameApi? flame = null,
        Core.ModPlatform.Technic.TechnicApi? technic = null,
        Core.ModPlatform.AtLauncher.AtLauncherApi? atlauncher = null,
        Core.ModPlatform.Ftb.FtbApi? ftb = null,
        Core.Auth.AuthEndpointsOptions? auth = null)
    {
        var paths = FlintPaths.FromDataDir(ResolveDataDir(args));
        Directory.CreateDirectory(paths.DataDir);
        // NOTE: the process working directory is NOT touched — every core path is explicit
        // (FlintPaths), so multiple hosts can coexist in one process (integration tests).

        var apiAuth = Services.ApiAuthService.Resolve(paths.DataDir, args);

        var builder = WebApplication.CreateSlimBuilder(args);

        // diagnostics channel (Application.cpp:513-528 parity): level from --log-level /
        // FLINT_LOG_LEVEL (default Information), default console provider plus the rolling
        // data-dir file; the Core static facade (FlintLog) shares this exact factory
        builder.Logging.SetMinimumLevel(ResolveLogLevel(args));
        builder.Logging.AddProvider(new RollingFileLoggerProvider(paths.LogsDir));

        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, FlintJsonContext.Default);
        });

        var runtime = new LauncherRuntime(paths, modrinth: modrinth, flame: flame, technic: technic,
            atlauncher: atlauncher, ftb: ftb, auth: auth);
        builder.Services.AddSingleton(FlintConfig.Default);
        builder.Services.AddSingleton(runtime);
        builder.Services.AddSingleton<FlintTaskRegistry>();
        builder.Services.AddSingleton<DeviceCodeSessionStore>();
        builder.Services.AddSingleton<JavaScanService>();
        builder.Services.AddSingleton(apiAuth);
        builder.Services.AddHostedService<Services.AccountRefreshService>();

        var app = builder.Build();
        FlintLog.Initialize(app.Services.GetRequiredService<ILoggerFactory>());

        // request log, outermost so the status is final (the error middleware below may
        // rewrite it to 500); health probes log at Debug to keep the default view readable
        app.Use(async (context, next) =>
        {
            var start = Stopwatch.GetTimestamp();
            await next(context);
            var path = context.Request.Path.Value ?? "";
            var log = FlintLog.For(FlintLog.Categories.Http);
            if (path == "/healthz")
            {
                log.Probe(context.Request.Method, path, context.Response.StatusCode,
                    Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            else
            {
                log.Request(context.Request.Method, path, context.Response.StatusCode,
                    Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
        });

        // fix-3: the C++ LaunchTask always parses log4j XML events out of the game output
        // (LaunchTask::parseXmlLogs); the port keeps it behind an opt-in gate for pure-core
        // consumers and the host turns it on.
        Core.Launch.LaunchTask.XmlParsingEnabled = true;
        // fix-3: proxy / NumberOfManualRetries / RequestTimeout / UserAgentOverride reach the
        // shared Http stack (also re-applied after every settings write)
        Services.RuntimeNetConfig.Apply(runtime);

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
                        System.Text.Json.JsonSerializer.Serialize(
                            new Models.ApiErrorEnvelope(new Models.ErrorResponse("internal_error", ex.Message)),
                            FlintJsonContext.Default.ApiErrorEnvelope));
                }
            }
        });

        app.MapGet("/healthz", () => Results.Ok(new Models.HealthResponse("ok")));

        var api = app.MapGroup("/api/v1");
        api.AddApiAuth(apiAuth);
        api.MapGet("/info", (FlintConfig config) => new Models.InfoResponse(
            config.LauncherDisplayName,
            config.PrintableVersionString(),
            config.UserAgent,
            config.MetaUrl,
            config.JavaDownloaderEnabled));

        api.MapInstances();
        api.MapLaunch();
        api.MapTasks();
        api.MapAccounts();
        api.MapMisc();
        api.MapModPlatform();
        api.MapInstanceContent();
        api.MapComponents();
        api.MapExport();
        api.MapManagedPack();
        api.MapSettingsAndIcons();

        return app;
    }

    /// <summary>--data-dir &lt;path&gt; (or --data-dir=&lt;path&gt;) wins, then FLINT_DATA_DIR,
    /// then the platform default (macOS: ~/Library/Application Support/Flint, Windows:
    /// %APPDATA%/Flint, else ~/.local/share/Flint).</summary>
    public static string ResolveDataDir(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--data-dir" && i + 1 < args.Length)
            {
                return Path.GetFullPath(args[i + 1]);
            }
            if (args[i].StartsWith("--data-dir=", StringComparison.Ordinal))
            {
                return Path.GetFullPath(args[i]["--data-dir=".Length..]);
            }
        }
        var env = Environment.GetEnvironmentVariable("FLINT_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return Path.GetFullPath(env);
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(home, "Library", "Application Support", "Flint");
        }
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "Flint");
        }
        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var baseDir = string.IsNullOrWhiteSpace(xdgDataHome)
            ? Path.Combine(home, ".local", "share")
            : xdgDataHome;
        return Path.Combine(baseDir, "Flint");
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
