// SPDX-License-Identifier: GPL-3.0-only
// The ASP.NET Core Minimal API host factory: BuildWebApplication(args) resolves the data
// directory (--data-dir <path> / FLINT_DATA_DIR), chdir's into it BEFORE any core service is
// built (the meta cache and several core paths are CWD-relative), wires the core singletons
// and maps the /api/v1 endpoints. Program.cs is a one-line wrapper around this factory so
// integration tests can host the exact same app in-process.
using FlintLauncher.Core;
using FlintLauncher.Web.Endpoints;
using FlintLauncher.Web.Services;
using Microsoft.AspNetCore.Http.Json;

namespace FlintLauncher.Web;

public static class WebHostBuilder
{
    public static WebApplication BuildWebApplication(string[] args)
    {
        var dataDir = ResolveDataDir(args);
        Directory.CreateDirectory(dataDir);
        // Several core paths are CWD-relative (instgroups.json, the meta cache, relative
        // library/asset anchors) — the C++ application chdir'd into its data dir at startup.
        Environment.CurrentDirectory = dataDir;

        var apiAuth = Services.ApiAuthService.Resolve(dataDir, args);

        var builder = WebApplication.CreateSlimBuilder(args);

        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, FlintJsonContext.Default);
        });

        builder.Services.AddSingleton(FlintConfig.Default);
        builder.Services.AddSingleton(new LauncherRuntime(dataDir));
        builder.Services.AddSingleton<FlintTaskRegistry>();
        builder.Services.AddSingleton<DeviceCodeSessionStore>();
        builder.Services.AddSingleton<JavaScanService>();
        builder.Services.AddSingleton(apiAuth);
        builder.Services.AddHostedService<Services.AccountRefreshService>();

        var app = builder.Build();

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
}
