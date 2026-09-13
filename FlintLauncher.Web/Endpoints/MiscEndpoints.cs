// SPDX-License-Identifier: GPL-3.0-only
// Java discovery, launcher news and the Modrinth search pass-through.
using System.Text.Json;
using FlintLauncher.Core.Java;
using FlintLauncher.Core.ModPlatform;
using FlintLauncher.Core.ModPlatform.Modrinth;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Versioning;
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

        // ------------------------------------------------------ modplatform/modrinth
        api.MapGet("/modplatform/modrinth/search", async Task<IResult> (
            LauncherRuntime runtime,
            string? query,
            string? version,
            string? loader,
            string? index) =>
        {
            var args = new SearchArgs
            {
                Type = ResourceType.Mod,
                Offset = 0,
                Search = string.IsNullOrWhiteSpace(query) ? null : query,
                Versions = string.IsNullOrWhiteSpace(version) ? null : [new McVersion(version)],
                Loaders = string.IsNullOrWhiteSpace(loader)
                    ? null
                    : ModIndex.GetModLoaderFromString(loader.ToLowerInvariant()),
                Sorting = new SortingMethod
                {
                    Index = 1,
                    Name = string.IsNullOrWhiteSpace(index) ? "relevance" : index,
                    ReadableName = "Sort by " + (string.IsNullOrWhiteSpace(index) ? "Relevance" : index),
                },
            };

            var url = ModrinthApi.Instance.GetSearchURL(args);
            if (url is null)
            {
                return ApiJson.Error("invalid_loader", $"loader '{loader}' is not supported by Modrinth", 400);
            }

            // pass-through of the core Modrinth search: run the net request and map the hits
            var request = NetRequest.MakeByteArray(new Uri(url));
            await request.StartAsync();
            if (request.State != Core.Tasks.TaskState.Succeeded)
            {
                return ApiJson.Error("modrinth_unavailable",
                    $"modrinth search failed: {request.FailReason}", 502);
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(request.BodyAsString);
            }
            catch (JsonException)
            {
                return ApiJson.Error("modrinth_invalid_response", "modrinth returned invalid JSON", 502);
            }

            using (doc)
            {
                var results = new List<ModrinthSearchResult>();
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("hits", out var hits) &&
                    hits.ValueKind == JsonValueKind.Array)
                {
                    foreach (var hit in hits.EnumerateArray())
                    {
                        results.Add(new ModrinthSearchResult(
                            Str(hit, "project_id"),
                            Str(hit, "slug"),
                            Str(hit, "title"),
                            Str(hit, "icon_url"),
                            Str(hit, "author"),
                            hit.TryGetProperty("downloads", out var downloads) &&
                            downloads.ValueKind == JsonValueKind.Number
                                ? downloads.GetInt64()
                                : 0));
                    }
                }
                return Results.Ok(results.ToArray());
            }
        });
    }

    private static string Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
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
