// SPDX-License-Identifier: GPL-3.0-only
// CACHE E5 store-completeness tests: the derivation semantics (manifest-driven, ledger +
// file existence, local/inactive exclusion, ${arch} expansion, unsafe-ref refusal) at the
// Core level, and the two routes (GET /store/status/libraries/{versionRef},
// GET /store/status/assets/{assetsId}) end-to-end through the real host.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FlintLauncher.Core;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Store;
using FlintLauncher.Web;
using Xunit;

namespace FlintLauncher.Tests;

public class StoreCompletenessTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"flint-store-{Guid.NewGuid():N}");

    public StoreCompletenessTests()
    {
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (DirectoryNotFoundException) { }
    }

    private FlintPaths Paths() => FlintPaths.FromRoots(
        Path.Combine(_home, "config"), Path.Combine(_home, "data"), Path.Combine(_home, "store"),
        Path.Combine(_home, "cache"), Path.Combine(_home, "state"));

    /// <summary>A cache loaded from the given ledger body (entries load non-stale — the same
    /// mechanism the startup MetaCache.Load() provides).</summary>
    private HttpMetaCache CacheWithLedger(string body)
    {
        var cacheFile = Path.Combine(_home, "cache", "metacache.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
        File.WriteAllText(cacheFile, body);
        var cache = new HttpMetaCache(cacheFile);
        cache.AddBase("libraries", Paths().LibrariesRoot);
        cache.Load();
        return cache;
    }

    private static void WriteVersionManifest(string metaDir, string version, string librariesJson)
    {
        Directory.CreateDirectory(Path.Combine(metaDir, "net.minecraft"));
        File.WriteAllText(Path.Combine(metaDir, "net.minecraft", version + ".json"), $$"""
                                                                                       {"formatVersion":1,"uid":"net.minecraft","version":"{{version}}",
                                                                                        "releaseTime":"2024-12-03T10:15:07+00:00","type":"release",
                                                                                        "libraries":{{librariesJson}}}
                                                                                       """);
    }

    private static string WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static RuntimeContext Context() => new();

    [Fact]
    public void Libraries_Report_Missing_And_Bytes_From_Manifest_Store_And_Ledger()
    {
        var paths = Paths();
        const string brigadier = "com/mojang/brigadier/1.0.18/brigadier-1.0.18.jar";
        const string dfu = "com/mojang/datafixerupper/8.0.16/datafixerupper-8.0.16.jar";
        WriteVersionManifest(paths.MetaDir, "1.21.4",
            """[{"name":"com.mojang:brigadier:1.0.18"},{"name":"com.mojang:datafixerupper:8.0.16"}]""");
        // brigadier: file on the store root AND a live ledger entry → counted
        WriteFile(Path.Combine(paths.LibrariesRoot, brigadier), "jar!");
        using var cache = CacheWithLedger($$"""
                                            {"version":"1","entries":[
                                              {"base":"libraries","path":"{{brigadier}}","md5sum":"","etag":"","last_changed_timestamp":0,"eternal":true}]}
                                            """);
        // dfu: neither file nor entry → missing

        var report = StoreCompleteness.LibrariesStatus(paths, cache, "1.21.4", Context());

        Assert.NotNull(report);
        Assert.False(report.Complete);
        Assert.Equal([dfu], report.Missing);
        Assert.Equal(4, report.Bytes); // the brigadier file's content length
    }

    [Fact]
    public void Libraries_Present_File_Without_A_Live_Ledger_Entry_Counts_As_Missing()
    {
        // the download path resolves the entry first (Library.GetDownloads) — a stale/absent
        // entry means the file would be re-fetched, so the derivation must not call it complete
        var paths = Paths();
        const string brigadier = "com/mojang/brigadier/1.0.18/brigadier-1.0.18.jar";
        WriteVersionManifest(paths.MetaDir, "1.21.4", """[{"name":"com.mojang:brigadier:1.0.18"}]""");
        WriteFile(Path.Combine(paths.LibrariesRoot, brigadier), "jar!");
        using var cache = CacheWithLedger("""{"version":"1","entries":[]}""");

        var report = StoreCompleteness.LibrariesStatus(paths, cache, "1.21.4", Context());

        Assert.NotNull(report);
        Assert.Equal([brigadier], report.Missing);
        Assert.Equal(0, report.Bytes);
    }

    [Fact]
    public void Libraries_Local_Hint_Is_Excluded_From_The_Derivation()
    {
        // local libraries are instance-anchored overrides — never store content
        var paths = Paths();
        WriteVersionManifest(paths.MetaDir, "1.21.4",
            """[{"name":"local:thing:1.0","MMC-hint":"local"}]""");
        using var cache = CacheWithLedger("""{"version":"1","entries":[]}""");

        var report = StoreCompleteness.LibrariesStatus(paths, cache, "1.21.4", Context());

        Assert.NotNull(report);
        Assert.True(report.Complete);
        Assert.Empty(report.Missing);
        Assert.Equal(0, report.Bytes);
    }

    [Fact]
    public void Libraries_Unknown_Version_Or_Unsafe_Ref_Returns_Null()
    {
        var paths = Paths();
        using var cache = CacheWithLedger("""{"version":"1","entries":[]}""");

        Assert.Null(StoreCompleteness.LibrariesStatus(paths, cache, "9.99.9", Context()));
        Assert.Null(StoreCompleteness.LibrariesStatus(paths, cache, "../evil", Context()));
        Assert.Null(StoreCompleteness.AssetsStatus(paths, "../evil"));
    }

    [Fact]
    public void Assets_Reports_Missing_By_Name_And_Verifies_Object_Sizes()
    {
        var paths = Paths();
        const string hash1 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string hash2 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        Directory.CreateDirectory(paths.AssetIndexesDir);
        File.WriteAllText(Path.Combine(paths.AssetIndexesDir, "17.json"),
            "{\"objects\":{\"minecraft/icons/icon_16x16.png\":{\"hash\":\"" + hash1 + "\",\"size\":3}," +
            "\"minecraft/sounds/missing.ogg\":{\"hash\":\"" + hash2 + "\",\"size\":999}}}");
        WriteFile(Path.Combine(paths.AssetsRoot, "objects", hash1[..2], hash1), "abc");
        // a present file with the WRONG size is as missing as an absent one (the
        // GetDownloadAction re-download condition)
        WriteFile(Path.Combine(paths.AssetsRoot, "objects", hash2[..2], hash2), "too small");

        var report = StoreCompleteness.AssetsStatus(paths, "17");

        Assert.NotNull(report);
        Assert.False(report.Complete);
        Assert.Equal(["minecraft/sounds/missing.ogg"], report.Missing);
        Assert.Equal(3, report.Bytes);
    }
}

public class StoreStatusApiTests
{
    /// <summary>The two E5 routes end-to-end: the manifest/ledger/store fixture is planted
    /// BEFORE the host starts (the runtime ledger loads in the constructor), then both
    /// routes derive their reports over HTTP, and unknown refs 404.</summary>
    [Fact]
    public async Task Store_Status_Routes_Derive_From_Manifests_And_Store()
    {
        var home = Path.Combine(Path.GetTempPath(), $"flint-store-api-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(home);
            var paths = FlintPaths.FromRoots(Path.Combine(home, "config"), Path.Combine(home, "data"),
                Path.Combine(home, "store"), Path.Combine(home, "cache"), Path.Combine(home, "state"));
            const string brigadier = "com/mojang/brigadier/1.0.18/brigadier-1.0.18.jar";
            const string dfu = "com/mojang/datafixerupper/8.0.16/datafixerupper-8.0.16.jar";
            Directory.CreateDirectory(Path.Combine(paths.MetaDir, "net.minecraft"));
            await File.WriteAllTextAsync(Path.Combine(paths.MetaDir, "net.minecraft", "1.21.4.json"), """
                                                                                                      {"formatVersion":1,"uid":"net.minecraft","version":"1.21.4",
                                                                                                       "releaseTime":"2024-12-03T10:15:07+00:00","type":"release",
                                                                                                       "libraries":[{"name":"com.mojang:brigadier:1.0.18"},{"name":"com.mojang:datafixerupper:8.0.16"}]}
                                                                                                      """);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(paths.LibrariesRoot, brigadier))!);
            await File.WriteAllTextAsync(Path.Combine(paths.LibrariesRoot, brigadier), "jar!");
            Directory.CreateDirectory(Path.GetDirectoryName(paths.MetacacheFile)!);
            await File.WriteAllTextAsync(paths.MetacacheFile, $$"""
                                                                {"version":"1","entries":[
                                                                  {"base":"libraries","path":"{{brigadier}}","md5sum":"","etag":"","last_changed_timestamp":0,"eternal":true}]}
                                                                """);
            const string hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            Directory.CreateDirectory(paths.AssetIndexesDir);
            await File.WriteAllTextAsync(Path.Combine(paths.AssetIndexesDir, "17.json"),
                "{\"objects\":{\"minecraft/icons/icon_16x16.png\":{\"hash\":\"" + hash + "\",\"size\":3}}}");
            Directory.CreateDirectory(Path.Combine(paths.AssetsRoot, "objects", hash[..2]));
            await File.WriteAllTextAsync(Path.Combine(paths.AssetsRoot, "objects", hash[..2], hash), "abc");

            var token = Guid.NewGuid().ToString("N");
            using var app = WebHostBuilder.BuildWebApplication(
            [
                "--home-dir", home,
                "--api-token", token,
                "--urls", "http://127.0.0.1:0",
            ]);
            await app.StartAsync();
            using var client = new HttpClient();
            client.BaseAddress = new Uri(app.Urls.First());
            client.DefaultRequestHeaders.Add("X-Api-Token", token);

            var libraries = await client.GetFromJsonAsync<JsonElement>("/api/v1/store/status/libraries/1.21.4");
            Assert.False(libraries.GetProperty("complete").GetBoolean());
            Assert.Equal([dfu], libraries.GetProperty("missing").EnumerateArray()
                .Select(m => m.GetString()).ToList());
            Assert.Equal(4, libraries.GetProperty("bytes").GetInt64());

            var assets = await client.GetFromJsonAsync<JsonElement>("/api/v1/store/status/assets/17");
            Assert.True(assets.GetProperty("complete").GetBoolean());
            Assert.Empty(assets.GetProperty("missing").EnumerateArray());
            Assert.Equal(3, assets.GetProperty("bytes").GetInt64());

            // unknown refs 404 (the derivation is a pure view — it never downloads)
            Assert.Equal(HttpStatusCode.NotFound,
                (await client.GetAsync("/api/v1/store/status/libraries/9.99.9")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound,
                (await client.GetAsync("/api/v1/store/status/assets/99")).StatusCode);
        }
        finally
        {
            try
            {
                Directory.Delete(home, recursive: true);
            }
            catch (DirectoryNotFoundException) { }
        }
    }
}
