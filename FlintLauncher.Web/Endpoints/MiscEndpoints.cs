// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Java discovery, the static /about identity, the paste upload (GuiUtil's
// "upload log" flow over the core PasteUpload) and the metadata-cache eviction endpoint.
// (the launcher news half left — GUI-bound, not ported material)

using FlintLauncher.Core;
using FlintLauncher.Core.Java;
using FlintLauncher.Core.Net;
using FlintLauncher.Web.Models;
using FlintLauncher.Web.Services;

namespace FlintLauncher.Web.Endpoints;

public static class MiscEndpoints
{
    public static void MapMisc(this RouteGroupBuilder api)
    {
        api.WithTags("Misc");
        // --------------------------------------------------------------- java
        api.MapGet("/java", async (JavaScanService scan) =>
            Results.Ok((await scan.GetAsync()).ToArray())).Produces<JavaView[]>();

        // discovery is a fast filesystem scan plus a bounded concurrent `java` probe —
        // synchronous by design (documented decision)
        api.MapPost("/java/scan", async (JavaScanService scan) =>
            Results.Ok((await scan.ScanAsync()).ToArray())).Produces<JavaView[]>();

        // (the /news endpoint is gone — the news checker was GUI-bound and the
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
            if (request is null)
            {
                return ApiJson.Error("invalid_body",
                    "body must be {\"content\": ...} or {\"taskId\": ...} (exactly one)", 400);
            }

            // exactly one of taskId (a finished task's log) or content (inline text); an
            // explicitly supplied empty content is still "supplied" and therefore rejected
            var taskId = string.IsNullOrWhiteSpace(request.TaskId) ? null : request.TaskId;
            var inline = request.Content;
            string content;
            if (taskId is not null)
            {
                if (inline is not null)
                {
                    return ApiJson.Error("invalid_body",
                        "body must be {\"content\": ...} or {\"taskId\": ...} (exactly one)", 400);
                }

                var entry = registry.Get(taskId);
                if (entry is null)
                {
                    return ApiJson.NotFound("task_not_found", $"no task '{taskId}'");
                }

                content = string.Join("\n", registry.ReadLog(entry, 0).Select(l => l.Line));
                if (content.Length == 0)
                {
                    return ApiJson.Error("empty_paste",
                        $"task '{taskId}' has no log lines to paste", 400);
                }
            }
            else if (inline is { Length: > 0 } text)
            {
                content = text;
            }
            else
            {
                return ApiJson.Error("invalid_body",
                    "body must be {\"content\": ...} or {\"taskId\": ...} (exactly one)", 400);
            }

            // the typed user layer (the SettingsObject view retired)
            var typeIndex = runtime.Config.Current.Api.PastebinType ?? (int)PasteUpload.PasteType.Mclogs;
            var pasteType = typeIndex >= 0 && typeIndex < PasteUpload.PasteTypes.Length
                ? (PasteUpload.PasteType)typeIndex
                : PasteUpload.PasteType.Mclogs;
            var upload = new PasteUpload(content, runtime.Config.Current.Api.PastebinCustomApiBase ?? "",
                pasteType, runtime.Net);
            await upload.StartAsync();
            return upload is { WasSuccessful: true, PasteLink.Length: > 0 }
                ? Results.Created("/api/v1/paste", new PasteCreatedResponse(upload.PasteLink))
                : ApiJson.Error("paste_failed",
                    upload.WasSuccessful
                        ? "the paste server returned no link"
                        : upload.FailReason, 502);
        }).Produces<PasteCreatedResponse>(201);

        // --------------------------------------------------------------- meta cache
        // evict every cached download (index entries + cached files at the base roots);
        // subsequent fetches re-download like a cold cache
        api.MapDelete("/meta-cache", (LauncherRuntime runtime) =>
            runtime.MetaCache.EvictAll()
                ? Results.NoContent()
                : ApiJson.Error("meta_cache_evict_failed",
                    "some cached files could not be deleted", 500)).Produces(204);
    }
}

/// <summary>Cached java discovery (GET serves the cache, POST re-scans). The scan is the
/// shared detection pipeline (JavaDetection): JdkFind primary + the checker-validated
/// legacy/launcher fallback, so the launcher-managed java root — the JavaDir setting under
/// store semantics — is covered like the system-wide sources. Wire shape unchanged:
/// invalid fallback candidates list as ("unknown", "unknown").</summary>
public sealed class JavaScanService(LauncherRuntime runtime)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile IReadOnlyList<JavaView>? _cache;

    public async Task<IReadOnlyList<JavaView>> GetAsync()
    {
        var cached = _cache;
        if (cached is not null)
        {
            return cached;
        }

        // double-check after the scan so concurrent first hits share one detection pass
        await _lock.WaitAsync();
        try
        {
            if (_cache is { } raced)
            {
                return raced;
            }

            return await ScanCoreAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<JavaView>> ScanAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return await ScanCoreAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Runs one detection pass and replaces the cache. Callers hold <see cref="_lock"/>;
    /// this must stay lock-free because both entry points already hold it.</summary>
    private async Task<IReadOnlyList<JavaView>> ScanCoreAsync()
    {
        var candidates = await JavaDetection.DetectAsync(runtime.JavaRoot);
        var scanned = candidates
            .Select(candidate => new JavaView(candidate.Path, candidate.Version.Original, candidate.Architecture))
            .ToArray();
        _cache = scanned;
        return scanned;
    }
}
