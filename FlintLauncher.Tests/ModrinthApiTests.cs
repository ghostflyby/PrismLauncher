// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Tests for the Modrinth layer: API request shapes (facets JSON, version-file-by-hash,
// multiple-hash POST), the .mrpack import end-to-end on a synthetic pack built with
// MMCZip (index + overrides + mods with correct hashes) -> instance created, files placed,
// metadata written; the export round-trip and the hash-based update check.
//
// The Modrinth base URL is a static override hook (like AuthEndpoints), so the whole class
// is serialized through the "ModrinthApi" collection and restores the defaults in Dispose.

using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.Config;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Minecraft.Resources;
using FlintLauncher.Core.ModPlatform;
using FlintLauncher.Core.ModPlatform.Hashing;
using FlintLauncher.Core.ModPlatform.Modrinth;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Tasks;
using FlintLauncher.Core.Versioning;
using FlintLauncher.ExternalApi;
using TruePath;
using Xunit;
using PackwizModel = FlintLauncher.Core.ModPlatform.Packwiz.Packwiz;
using ResourceType = FlintLauncher.Core.ModPlatform.ResourceType;

namespace FlintLauncher.Tests;

[Collection("ModrinthApi")]
public class ModrinthApiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"flint-modrinth-{Guid.NewGuid():N}");
    private readonly TestGlobalSettings _global = new();
    private readonly StubModrinthServer _server = new();
    private readonly string _baseUrl;

    private readonly ModrinthClient _modrinth;
    private readonly HttpClient _http;
    private readonly NetSettings _settings;

    public ModrinthApiTests()
    {
        Directory.CreateDirectory(_root);
        _baseUrl = _server.Url;
        (_modrinth, _http, _settings) = TestModrinthApi.At(_baseUrl);
        // make the download host match the stub so the modrinth-download-meta header path is used
        _modrinth.DownloadHost = "127.0.0.1";
    }

    public void Dispose()
    {
        _server.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException) { }
    }

    private string TempPath(string name) => Path.Combine(_root, name);

    /// <summary>
    /// What InstanceImportTask does before the creation task runs: unpack the .mrpack
    /// (modrinth.index.json + overrides/) into the staging folder.
    /// </summary>
    private static void UnpackInto(string mrpackPath, string staging)
    {
        Assert.NotNull(MmcZip.ExtractDir(new AbsolutePath(mrpackPath), "", new AbsolutePath(staging)));
        // the downloaded mods and overrides must land in place
        Directory.CreateDirectory(Path.Combine(staging, "minecraft", "mods"));
    }

    // ---------------------------------------------------------------- API shapes

    [Fact]
    public async Task DownloadFileAsync_MismatchedChecksum_LeavesNoArtifact()
    {
        var destination = TempPath("checksum-miss.jar");
        _server.Downloads["payload.bin"] = "actual payload"u8.ToArray();

        var failure = await Assert.ThrowsAsync<ModrinthApiException>(() => _modrinth.Downloads.File(
                $"{_server.Url}/payload.bin",
                checksum: ("sha512", Convert.ToHexString(SHA512.HashData("different payload"u8.ToArray())).ToLowerInvariant()))
            .SendTo(_http, HttpCompletionOption.ResponseHeadersRead, destination));
        Assert.IsType<InvalidDataException>(failure.InnerException);

        // neither the destination nor the temp file survives the failed attempt
        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + ".download"));
    }

    [Fact]
    public async Task DownloadFileAsync_FailingUrl_ThrowsAndFallsBackByCaller()
    {
        var destination = TempPath("fallback.jar");
        var payload = "fallback payload"u8.ToArray();
        _server.Downloads["good.bin"] = payload;

        // the first URL 404s (the real API's answer for an unknown path)...
        await Assert.ThrowsAsync<ModrinthApiException>(() =>
            _modrinth.Downloads.File($"{_server.Url}/missing.bin").SendTo(_http, HttpCompletionOption.ResponseHeadersRead, destination));
        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + ".download"));

        // ...and the caller's fallback walk retries with the next URL
        await _modrinth.Downloads.File($"{_server.Url}/good.bin").SendTo(_http, HttpCompletionOption.ResponseHeadersRead, destination);
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task DownloadFileAsync_SendsDownloadMetaHeader()
    {
        var destination = TempPath("meta.jar");
        var payload = "meta payload"u8.ToArray();
        _server.Downloads["meta.bin"] = payload;

        await _modrinth.Downloads.File($"{_server.Url}/meta.bin",
            new ModrinthDownloadMeta("update", "1.20.1", "fabric", "pack-a")).SendTo(_http, HttpCompletionOption.ResponseHeadersRead, destination);

        Assert.Contains("\"reason\":\"update\"", _server.LastDownloadHeaders["modrinth-download-meta"]);
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public void SearchUrl_BuildsFacetsJson()
    {
        var args = new SearchArgs
        {
            Type = ResourceType.Mod,
            Offset = 50,
            Search = "create mod",
            Sorting = new SortingMethod { Index = 2, Name = "downloads" },
            Loaders = ModLoaderType.Fabric | ModLoaderType.Quilt,
            Versions = [new McVersion("1.20.1"), new McVersion("1.20.2 Pre-Release 1")],
            CategoryIds = ["adventure"],
            OpenSource = true,
        };

        var url = _modrinth.GetSearchUrl(args);

        Assert.NotNull(url);
        Assert.StartsWith(_baseUrl + "/search?", url);
        Assert.Contains("offset=50", url);
        Assert.Contains("limit=25", url);
        // the query is URL-encoded
        Assert.Contains("query=create%20mod", url);
        Assert.Contains("index=downloads", url);

        var urlText = Uri.UnescapeDataString(url);
        var facetsText = urlText[(urlText.IndexOf("facets=", StringComparison.Ordinal) + "facets=".Length)..];
        // the facets parameter is a JSON array of JSON arrays
        var parsed = JsonDocument.Parse(facetsText);
        Assert.Equal(JsonValueKind.Array, parsed.RootElement.ValueKind);
        var groups = parsed.RootElement.EnumerateArray()
            .Select(g => g.EnumerateArray().Select(v => v.GetString()).ToList()).ToList();
        Assert.Contains(groups, g => g.SequenceEqual(["categories:fabric", "categories:quilt"]));
        Assert.Contains(groups, g => g.SequenceEqual(["versions:1.20.1", "versions:1.20.2-pre1"]));
        Assert.Contains(groups, g => g.SequenceEqual(["categories:adventure"]));
        Assert.Contains(groups, g => g.SequenceEqual(["open_source:true"]));
        Assert.Contains(groups, g => g.SequenceEqual(["project_type:mod"]));
    }

    [Fact]
    public void SearchUrl_GivesEachCategoryItsOwnFacetArray()
    {
        // #6099: categories are OR-ed within one facet array, AND-ed across arrays — every
        // search category gets an array of its own
        var args = new SearchArgs
        {
            Type = ResourceType.Mod,
            CategoryIds = ["adventure", "technology", "utility"],
        };

        var url = _modrinth.GetSearchUrl(args);
        Assert.NotNull(url);

        var urlText = Uri.UnescapeDataString(url);
        var facetsText = urlText[(urlText.IndexOf("facets=", StringComparison.Ordinal) + "facets=".Length)..];
        var groups = JsonDocument.Parse(facetsText).RootElement.EnumerateArray()
            .Select(g => g.EnumerateArray().Select(v => v.GetString()).ToList()).ToList();

        Assert.Contains(groups, g => g.SequenceEqual(["categories:adventure"]));
        Assert.Contains(groups, g => g.SequenceEqual(["categories:technology"]));
        Assert.Contains(groups, g => g.SequenceEqual(["categories:utility"]));
        // no group merges two categories anymore (a null entry is malformed and fails too)
        Assert.DoesNotContain(groups, g =>
            g.Count > 1 && g.Any(c => c is null || c.StartsWith("categories:", StringComparison.Ordinal)));
    }

    [Fact]
    public void SearchUrl_RejectsUnsupportedLoaders()
    {
        var args = new SearchArgs
        {
            Type = ResourceType.Mod,
            Loaders = (ModLoaderType)0x8000 // no Modrinth support for unknown loaders
        };
        Assert.Null(_modrinth.GetSearchUrl(args));
    }

    [Fact]
    public async Task Search_SendsFacetsQueryParam()
    {
        var args = new SearchArgs { Type = ResourceType.Mod, Offset = 0, Search = "jei" };
        var url = _modrinth.GetSearchUrl(args);
        Assert.NotNull(url);

        var body = await FlintHttp.GetStringAsync(TestNet.Http, TestNet.Settings,
            new HttpRequestMessage(HttpMethod.Get, url));

        Assert.Contains("/search", _server.LastPath);
        Assert.Equal("0", _server.LastQuery["offset"]);
        Assert.Equal("25", _server.LastQuery["limit"]);
        Assert.Equal("jei", _server.LastQuery["query"]);
        // facets survive the round trip as JSON
        Assert.Equal("""[["project_type:mod"]]""", _server.LastQuery["facets"]);
        Assert.Equal("""{"hits":[]}""", body);
    }

    [Fact]
    public async Task CurrentVersion_FetchesByHashAndAlgorithm()
    {
        _server.VersionFiles["cafebabecafebabe"] = """{"version_number":"1.2.3","id":"v1","project_id":"p1","date_published":"2023-01-01T00:00:00Z","game_versions":["1.20.1"],"loaders":["fabric"],"name":"n","version_number":"1.2.3","version_type":"release","files":[]}""";
        var version = await _modrinth.Versions.ByFileHash("cafebabecafebabe", "sha1").SendTo(_http);

        Assert.NotNull(version);
        Assert.EndsWith("/version_file/cafebabecafebabe?algorithm=sha1", _server.LastPath);
        Assert.Equal("1.2.3", version.VersionNumber);
    }

    [Fact]
    public async Task ApiToken_IsSentAsAuthorizationHeader()
    {
        _modrinth.Options.ApiToken = "mrpat_token";
        _server.VersionFiles["cafebabecafebabe"] = """{"id":"v1","project_id":"p1","date_published":"2023-01-01T00:00:00Z","game_versions":["1.20.1"],"loaders":["fabric"],"name":"n","version_number":"1.2.3","version_type":"release","files":[]}""";
        await _modrinth.Versions.ByFileHash("cafebabecafebabe", "sha1").SendTo(_http);

        Assert.Equal("mrpat_token", _server.LastHeaders["Authorization"]);
    }

    [Fact]
    public async Task CurrentVersions_PostsHashList()
    {
        await _modrinth.Versions.ByFileHashes(["hash1", "hash2"], "sha512").SendTo(_http);

        Assert.EndsWith("/version_files", _server.LastPath);

        var body = JsonDocument.Parse(Encoding.UTF8.GetString(_server.LastBody ?? [])).RootElement;
        var hashes = body.GetProperty("hashes").EnumerateArray().Select(h => h.GetString()).ToList();
        Assert.Equal(["hash1", "hash2"], hashes);
        Assert.Equal("sha512", body.GetProperty("algorithm").GetString());
    }

    // ----------------------------------------------------- release channels (#3404)

    [Fact]
    public async Task LatestVersions_PostsReleaseTypes()
    {
        await _modrinth.GetLatestVersionsAsync(_http, ["hash1"], "sha512", null, null,
        [
            new IndexedVersionType(IndexedVersionTypeValue.Release),
            new IndexedVersionType(IndexedVersionTypeValue.Beta),
        ]);

        Assert.EndsWith("/version_files/update", _server.LastPath);

        var body = JsonDocument.Parse(Encoding.UTF8.GetString(_server.LastBody ?? [])).RootElement;
        var versionTypes = body.GetProperty("version_types").EnumerateArray().Select(v => v.GetString()).ToList();
        Assert.Equal(["release", "beta"], versionTypes);
    }

    [Fact]
    public async Task LatestVersions_OmitsVersionTypesByDefault()
    {
        await _modrinth.GetLatestVersionsAsync(_http, ["hash1"], "sha512", null, null);

        Assert.EndsWith("/version_files/update", _server.LastPath);
        var body = JsonDocument.Parse(Encoding.UTF8.GetString(_server.LastBody ?? [])).RootElement;
        Assert.False(body.TryGetProperty("version_types", out _));
    }

    [Fact]
    public void VersionFileHelpers_MapUrlsAndLoaders()
    {
        Assert.Equal("https://modrinth.com/user/flowln", ModrinthExtensions.GetAuthorUrl("flowln"));
        Assert.Equal(["fabric", "quilt"], ModrinthExtensions.GetModLoaderStrings(ModLoaderType.Fabric | ModLoaderType.Quilt));
        Assert.Equal("\"categories:fabric\",\"categories:quilt\"",
            ModrinthExtensions.GetModLoaderFilters(ModLoaderType.Fabric | ModLoaderType.Quilt));
        // the modrinth<->launcher version string round trip
        Assert.Equal("1.20.2-pre1", ResourceApi.MapMcVersionToModrinth(new McVersion("1.20.2 Pre-Release 1")));
        Assert.Equal("1.20.2 Pre-Release 1", ModrinthExtensions.MapMcVersionFromModrinth("1.20.2-pre1"));
    }

    // ------------------------------------------------------- mrpack import (e2e)

    [Fact]
    public async Task MrpackImport_EndToEnd_InstallsFilesOverridesAndMetadata()
    {
        var staging = TempPath("staging");
        Directory.CreateDirectory(staging);
        var pack = MrPackBuilder.Create(_server.Url);
        _server.Downloads["mod.bin"] = pack.ModBytes;
        var mrpackPath = TempPath("pack.mrpack");
        Assert.True(pack.WriteZip(mrpackPath));

        _server.VersionFiles[pack.ModSha512] = MrPackBuilder.VersionJson(_server.Url, pack);
        _server.Projects["projId1"] = MrPackBuilder.ProjectJson();

        UnpackInto(mrpackPath, staging);

        var task = new ModrinthCreationTask(new AbsolutePath(staging), trustedSource: true, _global, modrinth: _modrinth, http: _http, settings: _settings);
        task.SetName("Imported Pack");
        await task.StartAsync();

        Assert.True(task.WasSuccessful, task.FailReason);

        // files placed
        Assert.Equal(pack.ModBytes,
            await File.ReadAllBytesAsync(Path.Combine(staging, "minecraft", "mods", "test.jar")));
        // overrides applied ('overrides' -> game root)
        Assert.Equal(pack.OptionsTxt,
            await File.ReadAllTextAsync(Path.Combine(staging, "minecraft", "options.txt")));
        // the index is kept in mrpack/
        Assert.True(File.Exists(Path.Combine(staging, "mrpack", "modrinth.index.json")));
        // loader component + minecraft component installed via PackProfile
        using (var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(staging, "mmc-pack.json"))))
        {
            var uids = doc.RootElement.GetProperty("components").EnumerateArray()
                .Select(c => c.GetProperty("uid").GetString()).ToList();
            Assert.Equal(["net.minecraft", "net.fabricmc.fabric-loader"], uids);
        }

        // the staged instance's identity lives in instance.json (Flint format)
        Assert.Contains("Imported Pack", await File.ReadAllTextAsync(Path.Combine(staging, "instance.json")));

        // packwiz-style metadata written per mod
        var indexPath = Path.Combine(staging, "minecraft", "mods", ".index", "test-mod.pw.toml");
        Assert.True(File.Exists(indexPath));
        var metadataToml = await File.ReadAllTextAsync(indexPath);
        Assert.Contains("mod-id = \"projId1\"", metadataToml);
        Assert.Contains("version = \"verId1\"", metadataToml);
        Assert.Contains("filename = \"test.jar\"", metadataToml);
        Assert.Contains("hash-format = \"sha512\"", metadataToml);
        Assert.Contains("url = \"" + _server.Url + "/mod.bin\"", metadataToml);

        // the metadata layer attaches through ModFolderModel (status Installed)
        var modList = new ModFolderModel(MaterializedDirectory.Ensure(
        new AbsolutePath(Path.Combine(staging, "minecraft", "mods")))!);
        modList.Update();
        var mod = Assert.Single(modList.AllMods(),
            m => m.Type == FlintLauncher.Core.Minecraft.Resources.ResourceType.ZipFile);
        Assert.Equal(ResourceStatus.Installed, mod.Status);
        var metadata = modList.GetMetadata(mod);
        Assert.NotNull(metadata);
        // (upstream quirk kept: the indexed scan passes the file name, so the slug of the
        //  loaded metadata carries the .pw.toml suffix; the by-slug lookup would not)
        Assert.StartsWith("test-mod", metadata.Slug);
        Assert.Equal("test.jar", metadata.Filename);
        Assert.Equal("projId1", metadata.ProjectId);
        Assert.Equal("sha512", metadata.HashFormat);
    }

    [Fact]
    public async Task MrpackImport_DownloadSendsMetaHeader()
    {
        var staging = TempPath("staging-meta");
        Directory.CreateDirectory(staging);
        var pack = MrPackBuilder.Create(_server.Url);
        _server.Downloads["mod.bin"] = pack.ModBytes;
        var mrpackPath = TempPath("pack-meta.mrpack");
        Assert.True(pack.WriteZip(mrpackPath));
        UnpackInto(mrpackPath, staging);
        _server.Projects["projId1"] = MrPackBuilder.ProjectJson();
        _server.VersionFiles[pack.ModSha512] = MrPackBuilder.VersionJson(_server.Url, pack);

        var task = new ModrinthCreationTask(new AbsolutePath(staging), trustedSource: true, _global, modrinth: _modrinth, http: _http, settings: _settings);
        task.SetName("Meta Pack");
        await task.StartAsync();

        Assert.True(task.WasSuccessful, task.FailReason);
        // the modrinth-download-meta header carries the download context
        var meta = _server.LastDownloadHeaders["modrinth-download-meta"];
        Assert.Contains("\"reason\":\"modpack\"", meta);
        Assert.Contains("\"game_version\":\"1.20.1\"", meta);
        Assert.Contains("\"loader\":\"fabric\"", meta);
    }

    [Fact]
    public async Task MrpackImport_OptionalFilesAreInstalledDisabled()
    {
        var staging = TempPath("staging-optional");
        Directory.CreateDirectory(staging);
        var pack = MrPackBuilder.Create(_server.Url, withOptionalMod: true);
        _server.Downloads["mod.bin"] = pack.ModBytes;
        if (pack.OptionalBytes is { } optionalBytes)
        {
            _server.Downloads["optional.bin"] = optionalBytes;
        }

        var mrpackPath = TempPath("pack-optional.mrpack");
        Assert.True(pack.WriteZip(mrpackPath));
        UnpackInto(mrpackPath, staging);

        _server.Projects["projId1"] = MrPackBuilder.ProjectJson();
        _server.VersionFiles[pack.ModSha512] = MrPackBuilder.VersionJson(_server.Url, pack);

        var task = new ModrinthCreationTask(new AbsolutePath(staging), trustedSource: true, _global, modrinth: _modrinth, http: _http, settings: _settings);
        task.SetName("Optional Pack");
        await task.StartAsync();

        Assert.True(task.WasSuccessful, task.FailReason);
        Assert.True(File.Exists(Path.Combine(staging, "minecraft", "mods", "test.jar")));
        // env.client = "optional" -> installed disabled (headless equivalent of the dialog)
        Assert.True(File.Exists(Path.Combine(staging, "minecraft", "mods", "optional.jar.disabled")));
    }

    [Fact]
    public async Task MrpackImport_BadChecksum_Fails()
    {
        var staging = TempPath("staging-bad");
        Directory.CreateDirectory(staging);
        var pack = MrPackBuilder.Create(_server.Url);
        // serve tampered content: the download no longer matches the declared sha512
        _server.Downloads["mod.bin"] = "tampered content that no longer matches the hash"u8.ToArray();
        var mrpackPath = TempPath("pack-bad.mrpack");
        Assert.True(pack.WriteZip(mrpackPath));
        UnpackInto(mrpackPath, staging);

        _server.VersionFiles[pack.ModSha512] = MrPackBuilder.VersionJson(_server.Url, pack);

        var task = new ModrinthCreationTask(new AbsolutePath(staging), trustedSource: true, _global, modrinth: _modrinth, http: _http, settings: _settings);
        task.SetName("Bad Pack");
        await task.StartAsync();

        Assert.Equal(TaskState.Failed, task.State);
        Assert.False(File.Exists(Path.Combine(staging, "minecraft", "mods", "test.jar")));
    }

    [Fact]
    public async Task MrpackImport_PathEscape_Fails()
    {
        var staging = TempPath("staging-escape");
        Directory.CreateDirectory(staging);
        var pack = MrPackBuilder.Create(_server.Url, modPath: "../evil.jar");
        _server.Downloads["mod.bin"] = pack.ModBytes;
        var mrpackPath = TempPath("pack-escape.mrpack");
        Assert.True(pack.WriteZip(mrpackPath));
        UnpackInto(mrpackPath, staging);

        _server.VersionFiles[pack.ModSha512] = MrPackBuilder.VersionJson(_server.Url, pack);

        var task = new ModrinthCreationTask(new AbsolutePath(staging), trustedSource: true, _global, modrinth: _modrinth, http: _http, settings: _settings);
        task.SetName("Escape Pack");
        await task.StartAsync();

        Assert.Equal(TaskState.Failed, task.State);
        Assert.Contains("security risk", task.FailReason);
        Assert.False(File.Exists(TempPath("evil.jar")));
    }

    [Fact]
    public async Task MrpackImport_UnknownDependency_Fails()
    {
        var staging = TempPath("staging-dep");
        Directory.CreateDirectory(staging);
        var pack = MrPackBuilder.Create(_server.Url,
            dependencies: """{"minecraft":"1.20.1","unknown-loader":"9.9"}""");
        var mrpackPath = TempPath("pack-dep.mrpack");
        Assert.True(pack.WriteZip(mrpackPath));
        UnpackInto(mrpackPath, staging);

        var task = new ModrinthCreationTask(new AbsolutePath(staging), trustedSource: true, _global, modrinth: _modrinth, http: _http, settings: _settings);
        task.SetName("Dep Pack");
        await task.StartAsync();

        Assert.Equal(TaskState.Failed, task.State);
        Assert.Contains("Unknown dependency type", task.FailReason);
    }

    // ------------------------------------------------------- export round-trip

    [Fact]
    public async Task Export_RoundTrip_MatchesIndexStructure()
    {
        // import first
        var staging = TempPath("staging-export");
        Directory.CreateDirectory(staging);
        var pack = MrPackBuilder.Create(_server.Url);
        var mrpackPath = TempPath("pack-export.mrpack");
        Assert.True(pack.WriteZip(mrpackPath));
        UnpackInto(mrpackPath, staging);
        _server.Downloads["mod.bin"] = pack.ModBytes;
        _server.VersionFiles[pack.ModSha512] = MrPackBuilder.VersionJson(_server.Url, pack);
        _server.Projects["projId1"] = MrPackBuilder.ProjectJson();

        var import = new ModrinthCreationTask(new AbsolutePath(staging), trustedSource: true, _global, modrinth: _modrinth, http: _http, settings: _settings);
        import.SetName("Round Trip Pack");
        await import.StartAsync();
        Assert.True(import.WasSuccessful, import.FailReason);

        // ... then export the instance again (the staged instance is Flint format)
        var instance = new MinecraftInstance(
            new InstanceJsonSettingsObject(new AbsolutePath(staging), _global.Environment), new AbsolutePath(staging));
        var output = TempPath("exported.mrpack");
        var export = new ModrinthPackExportTask("Round Trip Pack", "1.0.0", "round trip", optionalFiles: true,
            instance, new AbsolutePath(output), modrinth: _modrinth, http: _http);
        await export.StartAsync();

        Assert.True(export.WasSuccessful, export.FailReason);
        Assert.True(File.Exists(output));

        using var zip = await ZipFile.OpenReadAsync(output);
        var names = zip.Entries.Select(e => e.FullName).ToHashSet();

        // the mod resolved through the API is referenced by URL, not packed
        Assert.Contains("modrinth.index.json", names);
        Assert.Contains("overrides/options.txt", names);
        Assert.DoesNotContain("overrides/mods/test.jar", names);

        var indexEntry = zip.GetEntry("modrinth.index.json");
        Assert.NotNull(indexEntry);
        string indexJson;
        using (var reader = new StreamReader(await indexEntry.OpenAsync()))
        {
            indexJson = await reader.ReadToEndAsync();
        }

        var index = JsonNode.Parse(indexJson)?.AsObject();
        Assert.NotNull(index);
        Assert.Equal(1, index["formatVersion"]?.GetValue<int>());
        Assert.Equal("minecraft", index["game"]?.GetValue<string>());
        Assert.Equal("Round Trip Pack", index["name"]?.GetValue<string>());
        Assert.Equal("1.0.0", index["versionId"]?.GetValue<string>());
        Assert.Equal("1.20.1", index["dependencies"]?["minecraft"]?.GetValue<string>());
        Assert.Equal("0.14.21", index["dependencies"]?["fabric-loader"]?.GetValue<string>());

        var files = index["files"]?.AsArray();
        Assert.NotNull(files);
        var entry = Assert.Single(files)?.AsObject();
        Assert.NotNull(entry);
        Assert.Equal("mods/test.jar", entry["path"]?.GetValue<string>());
        Assert.Equal("required", entry["env"]?["client"]?.GetValue<string>());
        Assert.Equal(pack.ModSha1, entry["hashes"]?["sha1"]?.GetValue<string>());
        Assert.Equal(pack.ModSha512, entry["hashes"]?["sha512"]?.GetValue<string>());
        Assert.Equal(pack.ModBytes.Length, entry["fileSize"]?.GetValue<int>());
        Assert.Equal($"{_server.Url}/mod.bin", entry["downloads"]?.AsArray().Single()?.GetValue<string>());
    }

    [Fact]
    public void HashLookup_NullPreferredHash_FallsBackToNextAlgorithm()
    {
        // a JSON null hash value counts as absent: the sha512 -> sha1 fallback keeps walking
        var dto = JsonSerializer.Deserialize("""
            {
                "project_id": "p", "id": "v1", "date_published": "2024-01-01T00:00:00Z",
                "game_versions": ["1.20.1"], "loaders": ["fabric"],
                "name": "V", "version_number": "1.0.0", "version_type": "release",
                "files": [ { "filename": "a.jar", "primary": true,
                             "hashes": { "sha512": null, "sha1": "abc" },
                             "url": "https://cdn.modrinth.com/a.jar", "size": 1 } ]
            }
            """, ModrinthJsonContext.Default.ModrinthVersion);
        Assert.NotNull(dto);
        var indexed = ModrinthPackIndex.LoadIndexedPackVersion(dto);

        Assert.Equal("sha1", indexed.HashType);
        Assert.Equal("abc", indexed.Hash);
    }

    // ------------------------------------------------------- update check

    [Fact]
    public async Task CheckUpdate_DetectsNewerVersionByHash()
    {
        var modsDir = TempPath("mods-update");
        Directory.CreateDirectory(Path.Combine(modsDir, ".index"));
        var pack = MrPackBuilder.Create(_server.Url);
        var jarPath = Path.Combine(modsDir, "test.jar");
        await File.WriteAllBytesAsync(jarPath, pack.ModBytes);

        // installed metadata: hash of the *old* jar content
        var oldBytes = "old jar content"u8.ToArray();
        var oldSha512 = Convert.ToHexString(SHA512.HashData(oldBytes)).ToLowerInvariant();
        var metadata = new PackwizModel.Mod
        {
            Slug = "test-mod",
            Name = "Test Mod",
            Filename = "test.jar",
            Provider = ResourceProvider.Modrinth,
            ProjectId = "projId1",
            FileId = "oldVersion",
            VersionNumber = "1.0.0",
            HashFormat = "sha512",
            Hash = oldSha512,
            Mode = "url",
            Url = "https://cdn.modrinth.com/data/projId1/versions/oldVersion/test.jar",
            Loaders = ModLoaderType.Fabric,
        };
        PackwizModel.UpdateModIndex(new AbsolutePath(Path.Combine(modsDir, ".index")), metadata);

        // the API answers with a *different* (newer) file hash for the same project
        _server.VersionFiles[oldSha512] =
            MrPackBuilder.VersionJson(_server.Url, pack, versionNumber: "2.0.0", versionId: "newVersion");

        var mod = new Mod(new AbsolutePath(jarPath));
        var check = new ModrinthCheckUpdate([mod], [new McVersion("1.20.1")], [ModLoaderType.Fabric],
            _ => MetadataHandler.Get(new AbsolutePath(Path.Combine(modsDir, ".index")), "test-mod"), modrinth: _modrinth, http: _http);
        await check.StartAsync();

        Assert.True(check.WasSuccessful, check.FailReason);
        var update = Assert.Single(check.GetUpdates());
        Assert.Equal("Test Mod", update.Name);
        Assert.Equal("1.0.0", update.OldVersion);
        Assert.Equal("2.0.0", update.NewVersion);
        Assert.Equal(ResourceProvider.Modrinth, update.Provider);
    }

    [Fact]
    public async Task CheckUpdate_SameHashMeansNoUpdate()
    {
        var modsDir = TempPath("mods-same");
        Directory.CreateDirectory(Path.Combine(modsDir, ".index"));
        var pack = MrPackBuilder.Create(_server.Url);
        var jarPath = Path.Combine(modsDir, "test.jar");
        await File.WriteAllBytesAsync(jarPath, pack.ModBytes);

        var metadata = new PackwizModel.Mod
        {
            Slug = "test-mod",
            Name = "Test Mod",
            Filename = "test.jar",
            Provider = ResourceProvider.Modrinth,
            ProjectId = "projId1",
            FileId = "verId1",
            VersionNumber = "1.0.0",
            HashFormat = "sha512",
            Hash = pack.ModSha512, // same as the API's latest
            Mode = "url",
            Loaders = ModLoaderType.Fabric,
        };
        PackwizModel.UpdateModIndex(new AbsolutePath(Path.Combine(modsDir, ".index")), metadata);

        _server.VersionFiles[pack.ModSha512] = MrPackBuilder.VersionJson(_server.Url, pack);

        var mod = new Mod(new AbsolutePath(jarPath));
        var check = new ModrinthCheckUpdate([mod], [new McVersion("1.20.1")], [ModLoaderType.Fabric],
            _ => MetadataHandler.Get(new AbsolutePath(Path.Combine(modsDir, ".index")), "test-mod"), modrinth: _modrinth, http: _http);
        await check.StartAsync();

        Assert.True(check.WasSuccessful, check.FailReason);
        Assert.Empty(check.GetUpdates());
    }

    [Fact]
    public void HashUtils_Port_ProvidesModrinthHashType()
    {
        // the Modrinth hasher uses the first declared hash type (sha512)
        Assert.Equal("sha512", ProviderCapabilities.HashType(ResourceProvider.Modrinth)[0]);
        var data = "payload"u8.ToArray();
        Assert.Equal(HashUtils.Hash(data, Algorithm.Sha512), HashUtils.Hash(data, "sha512"));
    }
}

[Collection("ModrinthApi")]
public class ModrinthCreationTaskManifestTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"flint-modrinth-manifest-{Guid.NewGuid():N}");
    private readonly TestGlobalSettings _global = new();

    public ModrinthCreationTaskManifestTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException) { }
    }

    [Fact]
    public async Task ParseManifest_RejectsUnknownGameAndFormat()
    {
        var staging = Path.Combine(_root, "staging");
        Directory.CreateDirectory(staging);

        var (modrinth, http, settings) = TestModrinthApi.Unreachable();
        var task = new ModrinthCreationTask(new AbsolutePath(staging), trustedSource: true, _global,
            modrinth, http, settings);
        task.SetName("M");
        await File.WriteAllTextAsync(Path.Combine(staging, "modrinth.index.json"),
            """{"formatVersion":1,"game":"terraria","files":[],"dependencies":{}}""");
        await task.StartAsync();
        Assert.Equal(TaskState.Failed, task.State);
        Assert.Contains("Unknown game", task.FailReason);

        var (modrinth2, http2, settings2) = TestModrinthApi.Unreachable();
        var task2 = new ModrinthCreationTask(new AbsolutePath(staging), trustedSource: true, _global,
            modrinth2, http2, settings2);
        await File.WriteAllTextAsync(Path.Combine(staging, "modrinth.index.json"),
            """{"formatVersion":42,"game":"minecraft","files":[],"dependencies":{}}""");
        await task2.StartAsync();
        Assert.Equal(TaskState.Failed, task2.State);
        Assert.Contains("Unknown format version", task2.FailReason);
    }
}
