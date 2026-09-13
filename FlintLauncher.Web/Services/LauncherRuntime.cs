// SPDX-License-Identifier: GPL-3.0-only
// The host's core-object wiring: the singletons a C++ Application object would own, built
// once by WebHostBuilder against the resolved data dir (which is also the process working
// directory — several core paths are CWD-relative).
using System.Text.Json.Nodes;
using FlintLauncher.Core;
using FlintLauncher.Core.Auth;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.News;
using FlintLauncher.Core.Settings;
using FlintLauncher.Web.Models;

namespace FlintLauncher.Web.Services;

public sealed class LauncherRuntime
{
    public string DataDir { get; }
    public IniSettingsObject GlobalSettings { get; }
    public InstanceList Instances { get; }
    public AccountList Accounts { get; }
    public HttpMetaCache MetaCache { get; }
    public NewsChecker News { get; }

    public LauncherRuntime(string dataDir)
    {
        DataDir = dataDir;

        // global config: flint.cfg first, importing an existing Prism/PolyMC config like the
        // C++ multi-path INISettingsObject ctor (fallbacks are copied to the preferred path)
        GlobalSettings = new IniSettingsObject(
        [
            Path.Combine(dataDir, FlintConfig.Default.LauncherConfigFile),
            Path.Combine(dataDir, "prismlauncher.cfg"),
            Path.Combine(dataDir, "polymc.cfg"),
        ]);
        GlobalSettingsRegistration.RegisterGlobalDefaults(GlobalSettings);

        // meta cache (root of the CWD-relative library/asset paths)
        MetaCache = new HttpMetaCache(Path.Combine(dataDir, "metacache.json"));
        MetaCache.AddBase("root", dataDir);
        MetaCache.AddBase("libraries", Path.Combine(dataDir, "libraries"));
        MetaCache.AddBase("assets", Path.Combine(dataDir, "assets"));
        MetaCache.AddBase("general", Path.Combine(dataDir, "cache"));
        MetaCache.AddBase("icons", Path.Combine(dataDir, "cache", "icons"));
        MetaCache.AddBase("screenshots", Path.Combine(dataDir, "screenshots"));

        // instance list: <dataDir>/instances
        var instancesDir = Path.Combine(dataDir, "instances");
        Instances = new InstanceList(GlobalSettings, [instancesDir])
        {
            PlaytimeSettings = GlobalSettings,
        };
        Instances.LoadList();

        // accounts: <dataDir>/accounts.json (autosaved on every change)
        Accounts = new AccountList();
        Accounts.SetListFilePath(Path.Combine(dataDir, "accounts.json"), autosave: true);
        Accounts.LoadList();

        // news (feed URL overridable for hermetic API tests)
        var feedUrl = Environment.GetEnvironmentVariable("FLINT_NEWS_RSS_URL");
        News = new NewsChecker(
            string.IsNullOrWhiteSpace(feedUrl) ? FlintConfig.Default.NewsRssUrl : feedUrl,
            MetaCache);
    }

    /// <summary>Serialize the instance-list mutations (create/delete/rename + LoadList):
    /// InstanceList is not thread-safe and the API handlers can interleave.</summary>
    public SemaphoreSlim InstanceLock { get; } = new(1, 1);
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
