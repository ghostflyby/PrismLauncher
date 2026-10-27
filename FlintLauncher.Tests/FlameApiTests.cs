// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Tests for the Flame (CurseForge) layer: the x-api-key header, request shapes (search URL,
// POST /fingerprints murmur2 batch, batch project/file fetches), the version loader
// (gameVersions parsing, hashes, dependencies), the date/loader-based latest-version
// matching, and the file resolution including a blocked file (null downloadUrl) surfacing
// as blocked.
//
// The Flame base URL and API key are static override hooks (like the Modrinth client factory), so the whole
// class is serialized through the "FlameApi" collection and restores the defaults in Dispose.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.ModPlatform;
using FlintLauncher.Core.ModPlatform.Flame;
using FlintLauncher.Core.ModPlatform.Hashing;
using FlintLauncher.Core.Versioning;
using TruePath;
using Xunit;
using FlintLauncher.ExternalApi;
using FlintLauncher.ExternalApi.Flame;
using FlintLauncher.ExternalApi.Modrinth;

namespace FlintLauncher.Tests;

[Collection("FlameApi")]
public class FlameApiTests : IDisposable
{
    private readonly FlameClient _flameApi = new();
    private readonly ModrinthClient _modrinth;
    private readonly HttpClient _http;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"flint-flame-{Guid.NewGuid():N}");
    private readonly StubFlameServer _server = new();
    private readonly string _baseUrl;

    public FlameApiTests()
    {
        (_modrinth, _http) = TestModrinthApi.Unreachable();
        Directory.CreateDirectory(_root);
        _baseUrl = _server.Url;
        _flameApi.Options.BaseUrl = _baseUrl;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _server.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException) { }
    }

    // ---------------------------------------------------------------- API shapes

    [Fact]
    public void SearchQuery_BuildsCurseForgeShape()
    {
        // the launcher vocabulary translates through FlameMod; the operation owns the wire
        var url = _flameApi.Search.Query(
            classId: FlameMod.GetClassId(ResourceType.Mod),
            offset: 50,
            searchFilter: "create mod",
            sortField: 2,
            modLoaderTypes: FlameMod.GetModLoaderFilterIds(ModLoaderType.Fabric | ModLoaderType.Quilt),
            categoryIds: ["adventure"],
            gameVersion: "1.18.2").CreateRequest().RequestUri!.AbsoluteUri;

        Assert.StartsWith(_baseUrl + "/mods/search?gameId=432&", url);
        Assert.Contains("classId=6", url);
        Assert.Contains("index=50", url);
        Assert.Contains("pageSize=25", url);
        Assert.Contains("searchFilter=create%20mod", url);
        Assert.Contains("sortField=2", url);
        Assert.Contains("sortOrder=desc", url);
        // the mapped loader ids (fabric=4, quilt=5), appended raw like the C++
        Assert.Contains("modLoaderTypes=[4,5]", url);
        Assert.Contains("categoryIds=[adventure]", url);
        Assert.Contains("gameVersion=1.18.2", url);
    }

    [Fact]
    public async Task Requests_SendApiKeyHeader()
    {
        _flameApi.Options.ApiKey = "user-supplied-key";

        await _flameApi.Fingerprints.Match([123456]).SendTo(_http);

        Assert.Equal("user-supplied-key", _server.LastHeaders["x-api-key"]);
    }

    [Fact]
    public async Task MatchFingerprints_PostsMurmur2List()
    {
        await _flameApi.Fingerprints.Match([123456789, 42]).SendTo(_http);

        Assert.Equal(_baseUrl + "/fingerprints", _server.LastPath);

        var body = Assert.IsType<JsonObject>(JsonNode.Parse(Encoding.UTF8.GetString(_server.LastBody ?? [])));
        var fingerprints = Assert.IsType<JsonArray>(body["fingerprints"])
            .Select(f => Assert.IsType<JsonValue>(f, exactMatch: false).GetValue<string>()).ToList();
        // the C++ sends the fingerprints as JSON strings
        Assert.Equal(["123456789", "42"], fingerprints);
    }

    [Fact]
    public async Task GetFiles_PostsFileIdList()
    {
        await _flameApi.Files.ByIds(["456", "789"]).SendTo(_http);

        Assert.Equal(_baseUrl + "/mods/files", _server.LastPath);

        var body = Assert.IsType<JsonObject>(JsonNode.Parse(Encoding.UTF8.GetString(_server.LastBody ?? [])));
        var fileIds = Assert.IsType<JsonArray>(body["fileIds"])
            .Select(f => Assert.IsType<JsonValue>(f, exactMatch: false).GetValue<string>()).ToList();
        Assert.Equal(["456", "789"], fileIds);
    }

    [Fact]
    public async Task GetProjects_PostsModIdList()
    {
        await _flameApi.Projects.ByIds(["123", "222"]).SendTo(_http);

        Assert.Equal(_baseUrl + "/mods", _server.LastPath);

        var body = Assert.IsType<JsonObject>(JsonNode.Parse(Encoding.UTF8.GetString(_server.LastBody ?? [])));
        var modIds = Assert.IsType<JsonArray>(body["modIds"])
            .Select(f => Assert.IsType<JsonValue>(f, exactMatch: false).GetValue<string>()).ToList();
        Assert.Equal(["123", "222"], modIds);
    }

    [Fact]
    public async Task GetFile_FetchesSingleEndpoint()
    {
        _server.FileJson = FlamePayloads.FileJson(_server.Url, fileId: 456);
        await _flameApi.Files.ById("123", "456").SendTo(_http);

        Assert.Equal(_baseUrl + "/mods/123/files/456", _server.LastPath);
    }

    [Fact]
    public async Task ChangelogAndDescription_ReadDataField()
    {
        Assert.Equal("changelog text", await _flameApi.Files.Changelog(123, 456).SendTo(_http));
        Assert.Equal(_baseUrl + "/mods/123/files/456/changelog", _server.LastPath);

        Assert.Equal("description text", await _flameApi.Projects.Description(123).SendTo(_http));
        Assert.Equal(_baseUrl + "/mods/123/description", _server.LastPath);
    }

    [Fact]
    public async Task Categories_UrlAndParsing()
    {
        _server.CategoriesJson = """{"data":[{"id":6,"name":"Mods"},{"id":4471,"name":"Modpacks"}]}""";
        var doc = await _flameApi.Categories.ForClassId(FlameMod.GetClassId(ResourceType.Mod))
            .SendTo(_http);

        Assert.Equal(_baseUrl + "/categories?gameId=432&classId=6", _server.LastPath);

        var categories = FlameMod.LoadModCategories(doc);
        Assert.Equal(2, categories.Count);
        Assert.Equal("Mods", categories[0].Name);
        Assert.Equal("6", categories[0].Id);
    }

    [Fact]
    public void ClassIdRoundTrip_MapsResourceTypes()
    {
        Assert.Equal(6, FlameMod.GetClassId(ResourceType.Mod));
        Assert.Equal(12, FlameMod.GetClassId(ResourceType.ResourcePack));
        Assert.Equal(17, FlameMod.GetClassId(ResourceType.World));
        Assert.Equal(6552, FlameMod.GetClassId(ResourceType.ShaderPack));
        Assert.Equal(4471, FlameMod.GetClassId(ResourceType.Modpack));
        Assert.Equal(6945, FlameMod.GetClassId(ResourceType.DataPack));
        Assert.Equal(0, FlameMod.GetClassId(ResourceType.TexturePack));

        Assert.Equal(ResourceType.Mod, FlameMod.GetResourceType(6));
        Assert.Equal(ResourceType.Unknown, FlameMod.GetResourceType(999));
    }

    [Fact]
    public void SortingMethods_ExposeCurseForgeOrder()
    {
        var methods = FlameMod.GetSortingMethods();
        Assert.Equal(8, methods.Count);
        Assert.Equal("Featured", methods[0].Name);
        Assert.Equal("GameVersion", methods[7].Name);
    }

    [Fact]
    public void FileListQuery_PagesAndLoaderFilter()
    {
        string Url(FlameFileListQuery query) => query.CreateRequest().RequestUri!.AbsoluteUri;

        Assert.Equal(_baseUrl + "/mods/123/files?pageSize=10000", Url(_flameApi.Files.List("123")));
        Assert.Equal(_baseUrl + "/mods/123/files?pageSize=10000&gameVersion=1.18.2",
            Url(_flameApi.Files.List("123", gameVersion: "1.18.2")));
        Assert.Equal(_baseUrl + "/mods/123/files?pageSize=10000&gameVersion=1.18.2&modLoaderType=1",
            Url(_flameApi.Files.List("123", gameVersion: "1.18.2",
                modLoaderType: FlameMod.GetMappedModLoader(ModLoaderType.Forge))));

        // the single-loader gate the call sites apply: multiple loaders -> no filter
        Assert.True(ModIndex.HasSingleModLoaderSelected(ModLoaderType.Forge));
        Assert.False(ModIndex.HasSingleModLoaderSelected(ModLoaderType.Forge | ModLoaderType.Fabric));
        Assert.DoesNotContain("modLoaderType", Url(_flameApi.Files.List("123")));
    }

    // ------------------------------------------------------------- version loader

    [Fact]
    public void LoadIndexedPackVersion_ParsesGameVersionsHashesAndDependencies()
    {
        var json = FlamePayloads.FileJson(_server.Url,
            displayName: "Test Mod 1.0",
            fileName: "test:jar.jar",
            fileDate: "2023-06-01T00:00:00Z",
            gameVersions: """["1.18.2", "1.18.1", "forge", "client"]""",
            sha1: "abc123",
            dependencies: """[{"modId": 999, "relationType": 3}]""");
        var dto = JsonSerializer.Deserialize(json, FlameJsonContext.Default.FlameModFile);
        Assert.NotNull(dto);

        var version = FlameMod.LoadIndexedPackVersion(dto);

        Assert.Equal("123", version.AddonId);
        Assert.Equal("456", version.FileId);
        Assert.Equal("2023-06-01T00:00:00Z", version.Date);
        Assert.Equal("Test Mod 1.0", version.Version);
        Assert.Equal("test-jar.jar", version.FileName); // invalid chars are sanitized
        Assert.Equal(["1.18.2", "1.18.1"], version.McVersion);
        Assert.Equal(ModLoaderType.Forge, version.Loaders);
        Assert.Equal(_baseUrl + "/mod.bin", version.DownloadUrl);
        Assert.Equal("abc123", version.Hash);
        Assert.Equal("sha1", version.HashType);
        Assert.Equal(IndexedVersionTypeValue.Release, version.VersionType.Value);
        var dep = Assert.Single(version.Dependencies);
        Assert.Equal("999", dep.AddonId);
        Assert.Equal(DependencyTypeValue.Required, dep.Type.Value);
    }

    [Fact]
    public void LoadIndexedPack_LoadsProjectFields()
    {
        var dto = JsonSerializer.Deserialize(FlamePayloads.ProjectJson(
            id: 123, name: "Test Mod", slug: "test-mod"), FlameJsonContext.Default.FlameProject);

        var pack = new IndexedPack();
        Assert.NotNull(dto);
        FlameMod.LoadIndexedPack(pack, dto);

        Assert.Equal("123", pack.AddonId);
        Assert.Equal(ResourceProvider.Flame, pack.Provider);
        Assert.Equal("Test Mod", pack.Name);
        Assert.Equal("test-mod", pack.Slug);
        Assert.Equal("https://www.curseforge.com/projects/test-mod", pack.WebsiteUrl);
        Assert.Equal("A synthetic mod", pack.Description);
        Assert.Equal(ResourceType.Mod, pack.ResourceType);
        Assert.False(pack.ExtraDataLoaded);
        var author = Assert.Single(pack.Authors);
        Assert.Equal("alice", author.Name);
    }

    [Fact]
    public void LoadIndexedPackVersions_SortsByDateDescending()
    {
        var older = FlamePayloads.FileJson(_server.Url, fileId: 1, fileDate: "2023-01-01T00:00:00Z");
        var newer = FlamePayloads.FileJson(_server.Url, fileId: 2, fileDate: "2024-01-01T00:00:00Z");

        var pack = new IndexedPack { AddonId = "123" };
        var doc = JsonSerializer.Deserialize(
            $$"""{"data": [{{older}},{{newer}}]}""", FlameJsonContext.Default.FlameFileList);
        Assert.NotNull(doc);
        Assert.True(FlameMod.LoadIndexedPackVersionsFromResponse(pack, doc));

        Assert.True(pack.VersionsLoaded);
        Assert.Equal(["2", "1"], pack.Versions.Select(v => v.FileId));
    }

    // --------------------------------------------------------- latest-version logic

    private static IndexedVersion Ver(string fileId, string date, ModLoaderType loaders = ModLoaderType.None,
        IndexedVersionType? versionType = null)
    {
        return new IndexedVersion
        {
            FileId = fileId,
            Date = date,
            Loaders = loaders,
            VersionType = versionType ?? IndexedVersionType.Invalid,
        };
    }

    [Fact]
    public void GetLatestVersion_NoLoaderCheck_PicksNewestDate()
    {
        var versions = new List<IndexedVersion>
        {
            Ver("1", "2023-01-01T00:00:00Z"),
            Ver("2", "2024-01-01T00:00:00Z"),
        };
        var latest = FlameMod.GetLatestVersion(versions, [], ModLoaderType.None, checkLoaders: false);
        Assert.NotNull(latest);
        Assert.Equal("2", latest.FileId);
    }

    [Fact]
    public void GetLatestVersion_PrefersInstanceLoader()
    {
        var versions = new List<IndexedVersion>
        {
            Ver("forge1", "2023-01-01T00:00:00Z", ModLoaderType.Forge),
            Ver("fabric1", "2023-06-01T00:00:00Z", ModLoaderType.Fabric),
        };
        var latest = FlameMod.GetLatestVersion(versions, [ModLoaderType.Forge], ModLoaderType.None, checkLoaders: true);
        Assert.NotNull(latest);
        Assert.Equal("forge1", latest.FileId);
    }

    [Fact]
    public void GetLatestVersion_FallsBackToUnspecifiedLoader()
    {
        // the mod defines no loaders for its latest file: the no-loader entry wins through
        // the appended fallback
        var versions = new List<IndexedVersion>
        {
            Ver("plain", "2024-01-01T00:00:00Z"),
            Ver("forge1", "2023-01-01T00:00:00Z", ModLoaderType.Forge),
        };
        var latest = FlameMod.GetLatestVersion(versions, [ModLoaderType.Forge], ModLoaderType.None, checkLoaders: true);
        Assert.NotNull(latest);
        Assert.Equal("plain", latest.FileId);
    }

    [Fact]
    public void GetLatestVersion_TwoLoadersOneUnspecified_StaleUnspecifiedIsIgnored()
    {
        // awkward case from the C++: exactly two best matches (forge + unspecified) and the
        // unspecified one is older -> the loader-matched one wins
        var versions = new List<IndexedVersion>
        {
            Ver("plain", "2023-01-01T00:00:00Z"),
            Ver("forge1", "2023-06-01T00:00:00Z", ModLoaderType.Forge),
        };
        var latest = FlameMod.GetLatestVersion(versions, [ModLoaderType.Forge], ModLoaderType.None, checkLoaders: true);
        Assert.NotNull(latest);
        Assert.Equal("forge1", latest.FileId);

        // ... but a NEWER unspecified file takes precedence
        versions =
        [
            Ver("plain", "2024-01-01T00:00:00Z"),
            Ver("forge1", "2023-06-01T00:00:00Z", ModLoaderType.Forge),
        ];
        latest = FlameMod.GetLatestVersion(versions, [ModLoaderType.Forge], ModLoaderType.None, checkLoaders: true);
        Assert.NotNull(latest);
        Assert.Equal("plain", latest.FileId);
    }

    [Fact]
    public void GetLatestVersion_ReleaseChannelFilter_SkipsOtherChannels()
    {
        // #3404: with a release channel list, files on other channels never win
        var versions = new List<IndexedVersion>
        {
            Ver("beta1", "2024-01-01T00:00:00Z", versionType: new IndexedVersionType(IndexedVersionTypeValue.Beta)),
            Ver("release1", "2023-01-01T00:00:00Z", versionType: new IndexedVersionType(IndexedVersionTypeValue.Release)),
        };

        // no filter: the newest file (the beta) wins
        var latest = FlameMod.GetLatestVersion(versions, [], ModLoaderType.None, checkLoaders: false);
        Assert.NotNull(latest);
        Assert.Equal("beta1", latest.FileId);

        // release-only: the beta is skipped in both the no-loader and loader-checked passes
        var releases = new List<IndexedVersionType> { new(IndexedVersionTypeValue.Release) };
        latest = FlameMod.GetLatestVersion(versions, [], ModLoaderType.None, checkLoaders: false, releases);
        Assert.NotNull(latest);
        Assert.Equal("release1", latest.FileId);

        latest = FlameMod.GetLatestVersion(versions, [ModLoaderType.Forge], ModLoaderType.None,
            checkLoaders: true, releases);
        Assert.NotNull(latest);
        Assert.Equal("release1", latest.FileId);

        // alpha-only filter with no alpha files: no version at all
        var alphas = new List<IndexedVersionType> { new(IndexedVersionTypeValue.Alpha) };
        Assert.Null(FlameMod.GetLatestVersion(versions, [], ModLoaderType.None, checkLoaders: false, alphas));
    }

    [Fact]
    public void IndexedVersionType_ReleaseChannelHelpers()
    {
        // IndexedVersionType::fromString is case-insensitive since #3404
        Assert.Equal(IndexedVersionTypeValue.Release, IndexedVersionType.FromString("release").Value);
        Assert.Equal(IndexedVersionTypeValue.Beta, IndexedVersionType.FromString("BETA").Value);
        Assert.Equal(IndexedVersionTypeValue.Alpha, IndexedVersionType.FromString("Alpha").Value);
        Assert.False(IndexedVersionType.FromString("unknown-channel").IsValid);

        // toModrinth / fromStringList / toModrinthList
        Assert.Equal("release", new IndexedVersionType(IndexedVersionTypeValue.Release).ToModrinth());
        Assert.Equal("", IndexedVersionType.Invalid.ToModrinth());

        var parsed = IndexedVersionType.FromStringList(["Release", "beta", "nope", "Alpha"]);
        Assert.Equal(
        [
            IndexedVersionTypeValue.Release, IndexedVersionTypeValue.Beta, IndexedVersionTypeValue.Alpha,
        ], parsed.Select(t => t.Value));
        // invalid entries are dropped, so the Modrinth body list never carries empties
        Assert.Equal(["release", "beta", "alpha"], IndexedVersionType.ToModrinthList(
        [
            new IndexedVersionType(IndexedVersionTypeValue.Release),
            IndexedVersionType.Invalid,
            new IndexedVersionType(IndexedVersionTypeValue.Beta),
            new IndexedVersionType(IndexedVersionTypeValue.Alpha),
        ]));
    }

    // ---------------------------------------------------------- file resolution

    [Fact]
    public async Task FileResolvingTask_ResolvesVersionsAndProjects()
    {
        var fileJson = FlamePayloads.FileJson(_server.Url, fileId: 456);
        _server.FilesJson = new JsonObject { ["data"] = new JsonArray(JsonNode.Parse(fileJson)) }.ToJsonString();
        _server.ProjectsJson = new JsonObject
        {
            ["data"] = new JsonArray(JsonNode.Parse(FlamePayloads.ProjectJson(id: 123))),
        }.ToJsonString();

        var manifest = new FlameManifest { Files = { [456] = new FlameFile { ProjectId = 123, FileId = 456 } } };

        var task = new FileResolvingTask(manifest, _modrinth, _http, flame: _flameApi);
        await task.StartAsync();

        Assert.True(task.WasSuccessful, task.FailReason);
        var file = manifest.Files[456];
        Assert.Equal("456", file.Version.FileId);
        Assert.Equal("test.jar", file.Version.FileName);
        Assert.Equal(_baseUrl + "/mod.bin", file.Version.DownloadUrl);
        Assert.Equal("123", file.Pack.AddonId);
        Assert.Equal("test-mod", file.Pack.Slug);
        Assert.Equal("mods", file.TargetFolder);
    }

    [Fact]
    public async Task FileResolvingTask_BlockedFileSurfacesWithoutDownloadUrl()
    {
        // downloadUrl null (CF hides it for non-bundled keys); the sha1 hash is intentionally
        // omitted so the Modrinth fallback is not triggered here
        var blockedJson = FlamePayloads.FileJson(_server.Url, fileId: 456, downloadUrlPath: null);
        _server.FilesJson = new JsonObject { ["data"] = new JsonArray(JsonNode.Parse(blockedJson)) }.ToJsonString();
        _server.ProjectsJson = new JsonObject
        {
            ["data"] = new JsonArray(JsonNode.Parse(FlamePayloads.ProjectJson(id: 123))),
        }.ToJsonString();

        var manifest = new FlameManifest { Files = { [456] = new FlameFile { ProjectId = 123, FileId = 456 } } };

        var task = new FileResolvingTask(manifest, _modrinth, _http, flame: _flameApi) { FallbackMrBlockedMods = false };
        await task.StartAsync();

        // the resolve succeeds; the blocked state is the *missing download URL*
        Assert.True(task.WasSuccessful, task.FailReason);
        var version = manifest.Files[456].Version;
        Assert.Equal("", version.DownloadUrl);
        Assert.Equal("456", version.FileId);
    }

    [Fact]
    public async Task FileResolvingTask_WorldProjectTargetsSaves()
    {
        var fileJson = FlamePayloads.FileJson(_server.Url, fileId: 456, fileName: "world.zip");
        _server.FilesJson = new JsonObject { ["data"] = new JsonArray(JsonNode.Parse(fileJson)) }.ToJsonString();
        _server.ProjectsJson = new JsonObject
        {
            ["data"] = new JsonArray(JsonNode.Parse(FlamePayloads.ProjectJson(id: 123, classId: 17))),
        }.ToJsonString();

        var manifest = new FlameManifest { Files = { [456] = new FlameFile { ProjectId = 123, FileId = 456 } } };

        var task = new FileResolvingTask(manifest, _modrinth, _http, flame: _flameApi);
        await task.StartAsync();

        Assert.True(task.WasSuccessful, task.FailReason);
        Assert.Equal(ResourceType.World, manifest.Files[456].Pack.ResourceType);
        Assert.Equal("saves", manifest.Files[456].TargetFolder);
    }

    [Fact]
    public void LoadManifest_ParsesAndValidates()
    {
        var pack = FlamePackBuilder.Create();
        var path = Path.Combine(_root, "manifest.json");
        File.WriteAllText(path, pack.ManifestJson);

        var manifest = new FlameManifest();
        FlameManifest.LoadManifest(manifest, new AbsolutePath(path));

        Assert.True(manifest.IsLoaded);
        Assert.Equal("minecraftModpack", manifest.ManifestType);
        Assert.Equal(1, manifest.ManifestVersion);
        Assert.Equal("Test Flame Pack", manifest.Name);
        Assert.Equal("1.0.0", manifest.Version);
        Assert.Equal("tester", manifest.Author);
        Assert.Equal("1.18.2", manifest.Minecraft.Version);
        var loader = Assert.Single(manifest.Minecraft.ModLoaders);
        Assert.Equal("forge-40.1.0", loader.Id);
        Assert.True(loader.Primary);
        var file = Assert.Single(manifest.Files.Values);
        Assert.Equal(123, file.ProjectId);
        Assert.Equal(456, file.FileId);
        Assert.True(file.Required);
        Assert.Equal("overrides", manifest.Overrides);
    }

    [Fact]
    public void LoadManifest_RejectsNonModpackAndUnknownVersion()
    {
        var badType = Path.Combine(_root, "bad-type.json");
        File.WriteAllText(badType, """{"manifestType":"somethingElse","manifestVersion":1}""");
        Assert.Throws<JsonException>(() => FlameManifest.LoadManifest(new FlameManifest(), new AbsolutePath(badType)));

        var badVersion = Path.Combine(_root, "bad-version.json");
        File.WriteAllText(badVersion, """{"manifestType":"minecraftModpack","manifestVersion":42}""");
        Assert.Throws<JsonException>(() => FlameManifest.LoadManifest(new FlameManifest(), new AbsolutePath(badVersion)));
    }

    [Fact]
    public void Murmur2_HashesLikeCurseForge()
    {
        // the murmur2 fingerprint is stable for the same content (the CF API contract)
        var data = "test flame mod jar content 0123456789"u8.ToArray();
        var hash = HashUtils.Hash(data, Algorithm.Murmur2);
        Assert.Equal(hash, HashUtils.Hash(data, Algorithm.Murmur2));
        Assert.Equal(hash, HashUtils.HashFile(WriteTempFile("murmur.bin", data), Algorithm.Murmur2));
        Assert.Equal(FlamePayloads.Murmur(data), hash);
    }

    private string WriteTempFile(string name, byte[] data)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, data);
        return path;
    }
}
