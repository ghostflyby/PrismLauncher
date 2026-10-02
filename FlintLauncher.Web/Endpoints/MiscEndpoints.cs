// SPDX-License-Identifier: GPL-3.0-only
// Java discovery, the static /about identity (fix-4), the paste upload (fix-4: GuiUtil's
// "upload log" flow over the core PasteUpload) and the metadata-cache eviction endpoint.
// (the launcher news half left with CACHE E7 — GUI-bound, not ported material)
using FlintLauncher.Core;
using FlintLauncher.Core.Java;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Settings;
using FlintLauncher.Web.Models;
using FlintLauncher.Web.Services;

namespace FlintLauncher.Web.Endpoints;

public static class MiscEndpoints
{
    public static void MapMisc(this IEndpointRouteBuilder api)
    {
        // --------------------------------------------------------------- java
        api.MapGet("/java", async (JavaScanService scan) =>
            Results.Ok((await scan.GetAsync()).ToArray()));

        // discovery is a fast filesystem scan plus a bounded concurrent `java` probe —
        // synchronous by design (documented decision)
        api.MapPost("/java/scan", async (JavaScanService scan) =>
            Results.Ok((await scan.ScanAsync()).ToArray()));

        // (CACHE E7: the /news endpoint is gone — the news checker was GUI-bound and the
        // user decision retired it whole: checker, feed base, FLINT_NEWS_RSS_URL)

        // --------------------------------------------------------------- about
        // the static identity of the launcher (BuildConfig); no dynamic state
        api.MapGet("/about", (FlintConfig config) => new AboutResponse(
            config.LauncherDisplayName,
            config.PrintableVersionString(),
            config.LauncherCopyright,
            config.MatrixUrl,
            config.DiscordUrl,
            config.SubredditUrl,
            config.BugTrackerUrl));

        // --------------------------------------------------------------- paste
        // GuiUtil::uploadPaste: either the raw content or a finished task's buffered log goes
        // to the configured paste provider (PastebinType + PastebinCustomAPIBase global
        // settings, provider default = mclo.gs). Paste bodies are small; this runs synchronously.
        api.MapPost("/paste", async (PasteRequest? request, LauncherRuntime runtime,
            FlintTaskRegistry registry) =>
        {
            if (request is null ||
                (!string.IsNullOrWhiteSpace(request.TaskId) && request.Content is not null) ||
                (string.IsNullOrWhiteSpace(request.TaskId) && string.IsNullOrEmpty(request.Content)))
            {
                return ApiJson.Error("invalid_body",
                    "body must be {\"content\": ...} or {\"taskId\": ...} (exactly one)", 400);
            }

            string content;
            if (!string.IsNullOrWhiteSpace(request.TaskId))
            {
                var entry = registry.Get(request.TaskId!);
                if (entry is null)
                {
                    return ApiJson.NotFound("task_not_found", $"no task '{request.TaskId}'");
                }
                content = string.Join("\n", registry.ReadLog(entry, 0).Select(l => l.Line));
                if (content.Length == 0)
                {
                    return ApiJson.Error("empty_paste",
                        $"task '{request.TaskId}' has no log lines to paste", 400);
                }
            }
            else
            {
                content = request.Content!;
            }

            // the typed user layer (C5/D4 — the SettingsObject view retired)
            var typeIndex = runtime.Config.Current.Api.PastebinType ?? (int)PasteUpload.PasteType.Mclogs;
            var pasteType = typeIndex >= 0 && typeIndex < PasteUpload.PasteTypes.Length
                ? (PasteUpload.PasteType)typeIndex
                : PasteUpload.PasteType.Mclogs;
            var upload = new PasteUpload(content, runtime.Config.Current.Api.PastebinCustomAPIBase ?? "",
                pasteType, runtime.Net);
            await upload.StartAsync();
            return upload.WasSuccessful && upload.PasteLink.Length > 0
                ? Results.Created("/api/v1/paste", new PasteCreatedResponse(upload.PasteLink))
                : ApiJson.Error("paste_failed",
                    upload.WasSuccessful
                        ? "the paste server returned no link"
                        : upload.FailReason, 502);
        });

        // --------------------------------------------------------------- meta cache
        // evict every cached download (index entries + cached files at the base roots);
        // subsequent fetches re-download like a cold cache
        api.MapDelete("/meta-cache", (LauncherRuntime runtime) =>
            runtime.MetaCache.EvictAll()
                ? Results.NoContent()
                : ApiJson.Error("meta_cache_evict_failed",
                    "some cached files could not be deleted", 500));
    }
}

/// <summary>Cached java discovery (GET serves the cache, POST re-scans). The scan covers
/// the launcher-managed java root too — the JavaDir setting under E2 store semantics
/// (relative → store root), not the CWD the parameterless scan used to fall back to.</summary>
public sealed class JavaScanService
{
    private readonly LauncherRuntime _runtime;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile IReadOnlyList<JavaView>? _cache;

    public JavaScanService(LauncherRuntime runtime) => _runtime = runtime;

    public async Task<IReadOnlyList<JavaView>> GetAsync() => _cache ??= await ScanAsync();

    public async Task<IReadOnlyList<JavaView>> ScanAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var paths = JavaUtils.FindJavaPaths(_runtime.JavaRoot)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (paths.Count == 0)
            {
                _cache = [];
                return _cache;
            }

            var checkJar = JavaUtils.GetJavaCheckPath();
            var checks = paths.Select(path => CheckOneAsync(path, checkJar)).ToArray();
            _cache = await Task.WhenAll(checks);
            return _cache;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static async Task<JavaView> CheckOneAsync(string path, string checkJar)
    {
        if (checkJar.Length == 0 || !File.Exists(path))
        {
            return new JavaView(path, "unknown", "unknown");
        }

        var checker = new JavaChecker(path, "");
        JavaChecker.Result? result = null;
        checker.CheckFinished += r => result = r;
        try
        {
            // StartAsync returns when the checker reaches a terminal state; the result event
            // has fired by then (JavaChecker always emits exactly one terminal result).
            await checker.StartAsync().WaitAsync(TimeSpan.FromSeconds(20));
        }
        catch (TimeoutException)
        {
            checker.Abort();
            return new JavaView(path, "unknown", "unknown");
        }

        if (result is null || result.Validity == JavaChecker.Validity.Errored ||
            result.Validity == JavaChecker.Validity.ReturnedInvalidData)
        {
            return new JavaView(path, "unknown", "unknown");
        }
        return new JavaView(path, result.JavaVersion.ToString(),
            result.RealPlatform.Length != 0 ? result.RealPlatform : result.MojangPlatform);
    }
}
