// SPDX-License-Identifier: GPL-3.0-only
// The host's core-object wiring: the singletons a C++ Application object would own, built
// once by WebHostBuilder against the resolved data dir. All paths come from the injected
// FlintPaths — the process working directory is not load-bearing.
using System.Text.Json.Nodes;
using FlintLauncher.Core;
using FlintLauncher.Core.Auth;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.ModPlatform.AtLauncher;
using FlintLauncher.Core.ModPlatform.Flame;
using FlintLauncher.Core.ModPlatform.Modrinth;
using FlintLauncher.Core.ModPlatform.Technic;
using FlintLauncher.Core.ModPlatform.Ftb;
using FlintLauncher.Core.News;
using FlintLauncher.Core.Screenshots;
using FlintLauncher.Core.Settings;
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
    public IniSettingsObject GlobalSettings { get; }
    public InstanceList Instances { get; }
    public AccountList Accounts { get; }
    public HttpMetaCache MetaCache { get; }
    public NewsChecker News { get; }

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
        var dataDir = paths.DataDir;

        // global config: flint.cfg first, importing an existing Prism/PolyMC config like the
        // C++ multi-path INISettingsObject ctor (fallbacks are copied to the preferred path)
        GlobalSettings = new IniSettingsObject(
        [
            Path.Combine(dataDir, FlintConfig.Default.LauncherConfigFile),
            Path.Combine(dataDir, "prismlauncher.cfg"),
            Path.Combine(dataDir, "polymc.cfg"),
        ]);
        GlobalSettingsRegistration.RegisterGlobalDefaults(GlobalSettings);

        // meta cache (the shared download ledger for metadata, libraries, asset indexes)
        MetaCache = new HttpMetaCache(paths.MetacacheFile);
        MetaCache.AddBase("root", dataDir);
        MetaCache.AddBase("meta", paths.MetaDir);
        MetaCache.AddBase("libraries", paths.LibrariesRoot);
        MetaCache.AddBase("assets", paths.AssetsRoot);
        MetaCache.AddBase("general", paths.CacheDir);
        MetaCache.AddBase("icons", Path.Combine(paths.CacheDir, "icons"));
        MetaCache.AddBase("screenshots", Path.Combine(dataDir, "screenshots"));

        // instance list: <dataDir>/instances; discovered instances are stamped with the
        // injected layout and cache (asset/library anchors, metadata entities)
        Instances = new InstanceList(GlobalSettings, [paths.InstancesDir])
        {
            PlaytimeSettings = GlobalSettings,
            Paths = paths,
            MetaCache = MetaCache,
        };
        Instances.LoadList();

        // accounts: <dataDir>/accounts.json (autosaved on every change)
        Accounts = new AccountList();
        Accounts.SetListFilePath(paths.AccountsFile, autosave: true);
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
