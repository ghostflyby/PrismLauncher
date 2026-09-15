// SPDX-License-Identifier: GPL-3.0-only
// Java discovery and launcher news. (The Modrinth search pass-through moved into the merged
// /modplatform/{provider}/search route of ModPlatformEndpoints, UI-API mapping Wave B.)
using FlintLauncher.Core.Java;
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

        // --------------------------------------------------------------- news
        api.MapGet("/news", async (LauncherRuntime runtime) =>
        {
            var news = runtime.News;
            if (!news.IsNewsLoaded && !news.IsLoadingNews)
            {
                try
                {
                    await news.ReloadNewsAsync().WaitAsync(TimeSpan.FromSeconds(15));
                }
                catch (TimeoutException)
                {
                    // return whatever is known (empty) — the feed is cached for next call
                }
            }
            return Results.Ok(news.NewsEntries
                .Select(entry => new NewsView(entry.Title, entry.Content, entry.Link))
                .ToArray());
        });
    }
}

/// <summary>Cached java discovery (GET serves the cache, POST re-scans).</summary>
public sealed class JavaScanService
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile IReadOnlyList<JavaView>? _cache;

    public async Task<IReadOnlyList<JavaView>> GetAsync() => _cache ??= await ScanAsync();

    public async Task<IReadOnlyList<JavaView>> ScanAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var paths = JavaUtils.FindJavaPaths()
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
