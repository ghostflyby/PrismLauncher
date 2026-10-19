// SPDX-License-Identifier: GPL-3.0-only
// Technic endpoints (fix-2c), mirroring the ATLauncher/FTB surface: the search/trending
// pass-through (TechnicModel::performQuery's URL selection + ListModel::searchRequestFinished
// mapping), the per-slug pack metadata with the url-vs-solder detection
// (TechnicPage::suggestCurrent + metadataLoaded, including the Solder build listing) and the
// pack install — SingleZipPackInstallTask / SolderPackInstallTask run through the same
// staged-commit flow as POST /modplatform/ftb/install (the metadata/zip/Solder downloads are
// small, so the flow is synchronous: 201 with the committed instance, 500 creation_failed
// with the task's FailReason otherwise).
//
// The Technic client is per-host state (runtime.Technic — the headless equivalent of the C++
// BuildConfig.TECHNIC_API_BASE_URL constant). The optional `serverUrl` (query param on the
// GETs, body field on the install) selects a per-request derived client instead of mutating
// the shared one, so an override can never leak into other requests (and needs no restore).

using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.ModPlatform.Technic;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Tasks;
using FlintLauncher.Web.Models;
using FlintLauncher.Web.Services;

namespace FlintLauncher.Web.Endpoints;

public static class TechnicEndpoints
{
    public static void MapTechnic(this RouteGroupBuilder api)
    {
        api.WithTags("ModPlatform");
        // ------------------------------------------------- modplatform/technic/search
        // ListModel::performSearch: a query selects the search URL, an empty one trending
        // (GET /modplatform/technic/trending below). Results map through
        // TechnicApi.LoadSearchResults (the "vanilla" slug exclusion included).
        api.MapGet("/modplatform/technic/search", async Task<IResult> (string? query, string? serverUrl,
                    LauncherRuntime runtime) =>
                await SearchAsync(string.IsNullOrWhiteSpace(query) ? "" : query.Trim(), serverUrl, runtime))
            .Produces<TechnicSearchResultView[]>();

        // ----------------------------------------------- modplatform/technic/trending
        api.MapGet("/modplatform/technic/trending", async Task<IResult> (string? serverUrl,
                    LauncherRuntime runtime) =>
                await SearchAsync("", serverUrl, runtime))
            .Produces<TechnicSearchResultView[]>();

        // --------------------------------------------- modplatform/technic/modpack/{slug}
        // TechnicPage::suggestCurrent's metadata step (+ metadataLoaded's Solder build
        // listing for Solder packs, with the "fall back to the current version" rule).
        api.MapGet("/modplatform/technic/modpack/{slug}", async Task<IResult> (string slug, string? serverUrl,
            LauncherRuntime runtime) =>
        {
            if (string.IsNullOrWhiteSpace(slug))
            {
                return ApiJson.Error("invalid_request", "'slug' is required", 400);
            }

            var api = ResolveApi(runtime, serverUrl, out var hookError);
            if (hookError is not null)
            {
                return hookError;
            }

            var (metaError, metaFailReason, metaBody) = await FetchAsync(api.PackMetadataUrl(slug), api.Net);
            if (metaError is not null)
            {
                // an upstream 404 is an unknown slug (the REST mapping of the C++ page's
                // failed metadata step); everything else stays a gateway failure
                return metaFailReason.Contains("HTTP status 404", StringComparison.Ordinal)
                    ? ApiJson.NotFound("pack_not_found", $"no Technic pack '{slug}'")
                    : metaError;
            }

            var pack = new TechnicModpack { Slug = slug };
            if (!TechnicApi.LoadPackMetadata(metaBody, pack))
            {
                // LoadPackMetadata returns false when the payload has neither a url nor a
                // solder key — an unknown slug in practice (the C++ warns and bails)
                return ApiJson.NotFound("pack_not_found", $"no Technic pack '{slug}'");
            }

            // metadataLoaded(): strip the Solder URL's trailing slashes, then pull the
            // version list from the Solder instance (zip packs only have one version)
            var solderUrl = pack.IsSolder ? pack.Url.TrimEnd('/') : null;
            var versions = new List<string>();
            string? recommended = null;
            string? latest = null;
            if (solderUrl is not null)
            {
                var (solderError, _, solderBody) = await FetchAsync(
                    TechnicApi.SolderPackUrl(solderUrl, slug), api.Net);
                var listing = solderError is null ? TechnicApi.LoadSolderPack(solderBody) : null;
                if (listing is not null)
                {
                    versions.AddRange(listing.Builds);
                    recommended = NullIfEmpty(listing.Recommended);
                    latest = NullIfEmpty(listing.Latest);
                }

                if (versions.Count == 0 && pack.CurrentVersion.Length != 0)
                {
                    // onSolderLoaded's fallback: the platform's "version" field
                    versions.Add(pack.CurrentVersion);
                }
            }
            else if (pack.CurrentVersion.Length != 0)
            {
                versions.Add(pack.CurrentVersion);
            }

            return Results.Ok(new TechnicPackView(
                pack.Slug,
                DisplayName(metaBody) ?? slug,
                pack.IsSolder,
                solderUrl ?? pack.Url,
                pack.MinecraftVersion,
                pack.WebsiteUrl,
                pack.Author,
                pack.Description,
                pack.CurrentVersion,
                versions,
                recommended,
                latest));
        }).Produces<TechnicPackView>();

        // ------------------------------------------------- modplatform/technic/install
        api.MapPost("/modplatform/technic/install", async Task<IResult> (
            TechnicInstallRequest request, LauncherRuntime runtime) =>
        {
            // two request shapes: {url, minecraftVersion?} (zip download) or
            // {solder, slug, build?} (Solder instance); build omitted = the recommended one
            var zipUrl = request.Url?.Trim() ?? "";
            var solderUrl = request.Solder?.Trim() ?? "";
            var slug = request.Slug?.Trim() ?? "";
            if ((zipUrl.Length == 0) == (solderUrl.Length == 0))
            {
                return ApiJson.Error("invalid_body",
                    "either 'url' (zip pack) or 'solder' + 'slug' (Solder pack) is required", 400);
            }

            if (zipUrl.Length != 0 &&
                (!Uri.TryCreate(zipUrl, UriKind.Absolute, out var zipUri) ||
                 zipUri.Scheme is not ("http" or "https")))
            {
                return ApiJson.Error("invalid_url", "'url' must be an absolute http(s) URL", 400);
            }

            if (solderUrl.Length != 0 &&
                (!Uri.TryCreate(solderUrl, UriKind.Absolute, out var solderUri) ||
                 solderUri.Scheme is not ("http" or "https")))
            {
                return ApiJson.Error("invalid_url", "'solder' must be an absolute http(s) URL", 400);
            }

            if (zipUrl.Length == 0 && slug.Length == 0)
            {
                return ApiJson.Error("invalid_body", "'slug' is required with 'solder'", 400);
            }

            // the request's client is resolved up front: a serverUrl override derives its own
            // client, the runtime's shared one is never mutated (nothing to restore)
            var api = ResolveApi(runtime, request.ServerUrl, out var serverError);
            if (serverError is not null)
            {
                return serverError;
            }

            await runtime.InstanceLock.WaitAsync();
            try
            {
                InstanceTask task;
                var requestedName = request.Name?.Trim();
                string taskName;
                if (zipUrl.Length != 0)
                {
                    var packUri = new Uri(zipUrl, UriKind.Absolute);
                    task = new SingleZipPackInstallTask(packUri, request.MinecraftVersion ?? "")
                    {
                        MetaCache = runtime.MetaCache,
                        Group = request.Group ?? "",
                    };
                    taskName = requestedName ?? FileNameWithoutExtension(packUri);
                }
                else
                {
                    // the C++ strips the Solder URL's trailing slashes before building URLs
                    var solder = solderUrl.TrimEnd('/');
                    var build = request.Build?.Trim();
                    if (string.IsNullOrEmpty(build))
                    {
                        // no build given: resolve the Solder listing's default (the version
                        // box preselects the recommended build, like TechnicPage)
                        var (resolveError, resolved) = await ResolveDefaultBuildAsync(api, solder, slug);
                        if (resolveError is not null)
                        {
                            return resolveError;
                        }

                        build = resolved;
                    }

                    task = new SolderPackInstallTask(new Uri(solder), slug, build,
                        request.MinecraftVersion ?? "")
                    {
                        Group = request.Group ?? "",
                    };
                    taskName = requestedName ?? slug;
                }

                // the staged commit derives the folder from the display name: the pack name
                // (or the request's) is the instance name, the icon follows the vanilla
                // endpoint's "default" fallback
                task.SetName(taskName);
                task.Icon = request.IconKey ?? "default";

                // staged-commit path: the task runs inside an InstanceList staging folder
                // that InstanceStaging commits into instances/ on success (WrapInstanceTask)
                var wrapped = runtime.Instances.WrapInstanceTask(task);
                // compute the expected id BEFORE running the task: after the commit the
                // directory already exists and DirNameFromString would return "<name>(1)"
                var expectedId = FileSystem.DirNameFromString(task.Name(), runtime.Instances.PrimaryDir());
                await wrapped.StartAsync();
                if (!wrapped.WasSuccessful)
                {
                    // the staging wrapper re-emits the inner task's FailReason
                    return ApiJson.Error("creation_failed", wrapped.FailReason, 500);
                }

                runtime.Instances.LoadList();
                // the staged commit's expected dirname, resolved through the D2 chain
                var inst = runtime.Instances.ResolveInstance(expectedId);
                return inst is null
                    ? ApiJson.Error("creation_failed", "instance was committed but not discovered", 500)
                    : Results.Created($"/api/v1/instances/{inst.Uuid}",
                        new InstanceCreatedResponse(inst.Id(), inst.Name()));
            }
            finally
            {
                runtime.InstanceLock.Release();
            }
        }).Produces<InstanceCreatedResponse>(201);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The request's Technic client: the runtime's own, or — with a `serverUrl`
    /// override — a derived client (same net stack and API build), leaving the shared
    /// instance untouched. Returns the error result for a malformed override.</summary>
    private static TechnicApi ResolveApi(LauncherRuntime runtime, string? serverUrl, out IResult? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            return runtime.Technic;
        }

        var trimmed = serverUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var server) ||
            server.Scheme is not ("http" or "https"))
        {
            error = ApiJson.Error("invalid_server_url",
                "'serverUrl' must be an absolute http(s) URL", 400);
            return runtime.Technic;
        }

        // the URL builders concatenate directly onto the base, so keep the slash
        return new TechnicApi(runtime.Technic.Net)
        {
            BaseUrl = trimmed.EndsWith('/') ? trimmed : trimmed + "/",
            ApiBuild = runtime.Technic.ApiBuild,
        };
    }

    /// <summary>The search/trending pass-through shared by the two GET endpoints: the
    /// TechnicApi URL builder (empty term = trending), then LoadSearchResults mapped to
    /// [{id, name, displayName?, iconUrl}].</summary>
    private static async Task<IResult> SearchAsync(string term, string? serverUrl, LauncherRuntime runtime)
    {
        var api = ResolveApi(runtime, serverUrl, out var hookError);
        if (hookError is not null)
        {
            return hookError;
        }

        var (url, mode) = api.SearchUrl(term);
        var request = NetRequest.MakeByteArray(new Uri(url), stack: api.Net);
        await request.StartAsync();
        if (request.State != TaskState.Succeeded)
        {
            return ApiJson.Error("technic_unavailable",
                $"technic search failed: {request.FailReason}", 502);
        }

        var packs = TechnicApi.LoadSearchResults(request.BodyAsString, mode);
        // LoadSearchResults maps the single-pack mode's displayName into Name (the
        // displayed name); the list mode only knows "name"
        return Results.Ok(packs.Select(pack => new TechnicSearchResultView(
            pack.Slug,
            pack.Name,
            mode == TechnicSearchMode.Single ? pack.Name : null,
            pack.LogoUrl == "null" ? null : pack.LogoUrl)).ToArray());
    }

    /// <summary>Fetches a Technic/Solder URL; fetch failures map to the uniform 502 error
    /// envelope, success yields the raw body for the caller's parser.</summary>
    private static async Task<(IResult? Error, string FailReason, string Body)> FetchAsync(
        string url, NetStack? net = null)
    {
        var request = NetRequest.MakeByteArray(new Uri(url), stack: net);
        await request.StartAsync();
        return request.State != TaskState.Succeeded
            ? (ApiJson.Error("technic_unavailable",
                $"technic request failed: {request.FailReason}", 502), request.FailReason, "")
            : (null, "", request.BodyAsString);
    }

    /// <summary>The Solder listing's default build (recommended, then latest, then the first
    /// listed) — an error result when the listing is unusable (500 creation_failed, like the
    /// install's own failure surface).</summary>
    private static async Task<(IResult? Error, string Build)> ResolveDefaultBuildAsync(
        TechnicApi api, string solderUrl, string slug)
    {
        var (_, failReason, body) = await FetchAsync(TechnicApi.SolderPackUrl(solderUrl, slug), api.Net);
        if (failReason.Length != 0)
        {
            return (ApiJson.Error("creation_failed",
                $"fetching the Solder pack listing failed: {failReason}", 500), "");
        }

        var listing = TechnicApi.LoadSolderPack(body);
        var build = NullIfEmpty(listing?.Recommended) ?? NullIfEmpty(listing?.Latest) ??
            listing?.Builds.FirstOrDefault();
        return string.IsNullOrEmpty(build)
            ? (ApiJson.Error("creation_failed", $"the Solder pack '{slug}' has no builds", 500), "")
            : (null, build);
    }

    /// <summary>The displayName (or raw name) property of a Platform metadata payload, when
    /// present (the metadata endpoint merges it in like the search-hit name upstream).</summary>
    private static string? DisplayName(string body)
    {
        try
        {
            if (JsonNode.Parse(body) is not JsonObject obj)
            {
                return null;
            }

            foreach (var key in new[] { "displayName", "name" })
            {
                if (obj[key] is JsonValue value && value.TryGetValue<string>(out var str))
                {
                    return str;
                }
            }
        }
        catch (JsonException)
        {
            // already surfaced through the metadata parse; the name falls back to the slug
        }

        return null;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>The zip URL's file name without extension (the instance-name fallback for
    /// direct zip installs).</summary>
    private static string FileNameWithoutExtension(Uri source)
    {
        var name = Uri.UnescapeDataString(Path.GetFileName(source.AbsolutePath));
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name.Length != 0 ? name : "Technic Pack";
    }
}
