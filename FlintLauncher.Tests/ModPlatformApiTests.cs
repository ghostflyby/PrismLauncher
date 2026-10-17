// SPDX-License-Identifier: GPL-3.0-only
// P6-fix-b API integration tests: the modplatform endpoints (CurseForge search pass-through,
// per-provider indexed version lists) and the per-instance mods surface (list / tracked
// install with the dependency chain / tracked update) against the real in-process web host
// (FlintApiHost) and the provider stub servers from ModrinthTestSupport/FlameTestSupport.
//
// Collections (the provider base URLs and the Flame key provider are process-global static
// hooks, so these tests are serialized with the suites that own them — same pattern as the
// modrinth search test in ApiIntegrationTests; the runner also runs collections strictly
// sequentially, which covers the classes below that touch both hooks):
//  - the flame search class joins "FlameApi" (FlameConfig.BaseUrl / ApiKeyProvider);
//  - the versions + mods classes join "ModrinthApi" (ModrinthApi.BaseUrl).
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flint.Tests;
using FlintLauncher.Core.ModPlatform.Flame;
using FlintLauncher.Core.ModPlatform.Modrinth;
using Xunit;

namespace FlintLauncher.Tests;

// --------------------------------------------------------------------- flame search

[Collection("FlameApi")]
public class FlintApiFlameSearchTests : IDisposable
{
    private readonly FlameApi _flameApi = new();
    private readonly StubFlameServer _server = new();

    public FlintApiFlameSearchTests()
    {
        _flameApi.BaseUrl = _server.Url;
    }

    public void Dispose()
    {
        _server.Dispose();
    }

    private static string SearchHitJson =>
        new JsonObject
        {
            ["id"] = 306612,
            ["name"] = "Fabric API",
            ["slug"] = "fabric-api",
            ["summary"] = "A synthetic hit",
            ["classId"] = 6,
            ["downloadCount"] = 4242,
            ["links"] = new JsonObject { ["websiteUrl"] = "https://www.curseforge.com/projects/fabric-api" },
            ["authors"] = new JsonArray((JsonNode?)new JsonObject
            {
                ["name"] = "modmuss50",
                ["url"] = "https://www.curseforge.com/members/modmuss50",
            }),
            ["logo"] = new JsonObject { ["title"] = "logo", ["thumbnailUrl"] = "https://cdn.test/fabric.png", ["url"] = "" },
        }.ToJsonString();

    [Fact]
    public async Task Flame_Search_MapsHits_And_SendsApiKey()
    {
        // G4: the Flame key is a store secret — the host startup apply derives the provider
        // from the ISecretStore, so the key goes in through PUT /settings (the former direct
        // ApiKeyProvider override no longer survives host startup)
        _server.SearchJson = new JsonObject { ["data"] = new JsonArray(JsonNode.Parse(SearchHitJson)!) }.ToJsonString();

        await using var host = await FlintApiHost.StartAsync(flame: _flameApi);
        var keyPut = await host.PutAsync("/api/v1/settings", new
        {
            group = "api",
            values = new Dictionary<string, object> { ["FlameKeyOverride"] = "user-supplied-key" },
        });
        Assert.Equal(System.Net.HttpStatusCode.NoContent, keyPut.StatusCode);

        var response = await host.GetAsync(
            "/api/v1/modplatform/flame/search?query=fabric&version=1.18.2&loader=fabric&index=Popularity");
        response.EnsureSuccessStatusCode();
        var hits = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(1, hits.GetArrayLength());
        var hit = hits[0];
        Assert.Equal("306612", hit.GetProperty("id").GetString());
        Assert.Equal("fabric-api", hit.GetProperty("slug").GetString());
        Assert.Equal("Fabric API", hit.GetProperty("name").GetString());
        Assert.Equal("https://cdn.test/fabric.png", hit.GetProperty("iconUrl").GetString());
        Assert.Equal("modmuss50", hit.GetProperty("author").GetString());
        Assert.Equal(4242L, hit.GetProperty("downloads").GetInt64());

        // the request reached the stub as a CurseForge mods search, with the key attached
        Assert.Contains("/mods/search", _server.LastPath);
        Assert.Equal("fabric", _server.LastQuery["searchFilter"]);
        Assert.Equal("6", _server.LastQuery["classId"]);
        Assert.Equal("1.18.2", _server.LastQuery["gameVersion"]);
        Assert.Equal("[4]", _server.LastQuery["modLoaderTypes"]);
        Assert.Equal("2", _server.LastQuery["sortField"]);
        Assert.Equal("user-supplied-key", _server.LastHeaders["x-api-key"]);
    }

    [Fact]
    public async Task Flame_Search_OmitsKeyHeader_WhenNoKeyIsConfigured()
    {
        _server.SearchJson = """{"data":[]}""";

        await using var host = await FlintApiHost.StartAsync(flame: _flameApi);
        var response = await host.GetAsync("/api/v1/modplatform/flame/search?query=anything");
        response.EnsureSuccessStatusCode();
        Assert.Equal(JsonValueKind.Array,
            JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.ValueKind);
        Assert.False(_server.LastHeaders.ContainsKey("x-api-key"));
    }

    [Fact]
    public async Task Flame_Search_UnsupportedLoader_Is400()
    {
        await using var host = await FlintApiHost.StartAsync(flame: _flameApi);
        var response = await host.GetAsync("/api/v1/modplatform/flame/search?loader=babric");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("invalid_loader", error.GetProperty("error").GetProperty("code").GetString());
    }
}

// ----------------------------------------------------------------- provider versions

[Collection("ModrinthApi")]
public class FlintApiVersionsTests : IDisposable
{
    private readonly FlameApi _flameApi = new();
    private readonly StubModrinthServer _modrinth = new();
    private readonly StubFlameServer _flame = new();

    private readonly ModrinthApi _modrinthApi = new();

    public FlintApiVersionsTests()
    {
        _modrinthApi.BaseUrl = _modrinth.Url;
        _flameApi.BaseUrl = _flame.Url;
    }

    public void Dispose()
    {
        _modrinth.Dispose();
        _flame.Dispose();
    }

    [Fact]
    public async Task Modrinth_Versions_MapsIndexedList()
    {
        _modrinth.ProjectVersions["projId1"] = new JsonArray((JsonNode?)new JsonObject
        {
            ["id"] = "verId1",
            ["project_id"] = "projId1",
            ["date_published"] = "2023-05-01T00:00:00Z",
            ["game_versions"] = new JsonArray("1.20.1"),
            ["loaders"] = new JsonArray("fabric"),
            ["name"] = "Test Mod 1.2.0",
            ["version_number"] = "1.2.0",
            ["version_type"] = "release",
            ["files"] = new JsonArray((JsonNode?)new JsonObject
            {
                ["url"] = $"{_modrinth.Url}/mod.bin",
                ["filename"] = "test.jar",
                ["primary"] = true,
                ["hashes"] = new JsonObject { ["sha512"] = "abc" },
            }),
        }).ToJsonString();

        await using var host = await FlintApiHost.StartAsync(_modrinthApi);
        var response = await host.GetAsync("/api/v1/modplatform/modrinth/versions?projectId=projId1");
        response.EnsureSuccessStatusCode();
        var versions = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(1, versions.GetArrayLength());
        var version = versions[0];
        Assert.Equal("verId1", version.GetProperty("id").GetString());
        Assert.Equal("1.2.0", version.GetProperty("versionNumber").GetString());
        Assert.Equal("2023-05-01T00:00:00Z", version.GetProperty("date").GetString());
        Assert.Equal("test.jar", version.GetProperty("filename").GetString());
        Assert.Equal($"{_modrinth.Url}/mod.bin", version.GetProperty("downloadUrl").GetString());
        Assert.Equal(["fabric"], version.GetProperty("loaders").EnumerateArray().Select(l => l.GetString()!).ToArray());
        Assert.Equal(["1.20.1"], version.GetProperty("mcVersions").EnumerateArray().Select(v => v.GetString()!).ToArray());
    }

    [Fact]
    public async Task Flame_Versions_MapsFileList()
    {
        _flame.FilesJson = new JsonObject
        {
            ["data"] = new JsonArray(JsonNode.Parse(FlamePayloads.FileJson(_flame.Url, modId: 123, fileId: 456,
                displayName: "Test Mod 1.0", fileName: "test.jar", fileDate: "2023-01-01T00:00:00Z",
                gameVersions: """["1.18.2", "forge"]"""))!),
        }.ToJsonString();

        await using var host = await FlintApiHost.StartAsync(_modrinthApi, _flameApi);
        var response = await host.GetAsync("/api/v1/modplatform/flame/versions?projectId=123");
        response.EnsureSuccessStatusCode();
        var versions = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(1, versions.GetArrayLength());
        var version = versions[0];
        Assert.Equal("456", version.GetProperty("id").GetString());
        Assert.Equal("Test Mod 1.0", version.GetProperty("versionNumber").GetString());
        Assert.Equal("2023-01-01T00:00:00Z", version.GetProperty("date").GetString());
        Assert.Equal("test.jar", version.GetProperty("filename").GetString());
        Assert.Equal(["forge"], version.GetProperty("loaders").EnumerateArray().Select(l => l.GetString()!).ToArray());
        Assert.Equal(["1.18.2"], version.GetProperty("mcVersions").EnumerateArray().Select(v => v.GetString()!).ToArray());
    }

    [Fact]
    public async Task Versions_UnknownProvider_And_MissingProjectId_Are400()
    {
        await using var host = await FlintApiHost.StartAsync(_modrinthApi);

        var unknown = await host.GetAsync("/api/v1/modplatform/ftb/versions?projectId=x");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal("unknown_provider", JsonDocument.Parse(await unknown.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error").GetProperty("code").GetString());

        var missing = await host.GetAsync("/api/v1/modplatform/modrinth/versions");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("invalid_request", JsonDocument.Parse(await missing.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}

// ------------------------------------------------------------------- instance mods

[Collection("ModrinthApi")]
public class FlintApiModsTests : IDisposable
{
    private readonly StubModrinthServer _modrinth = new();

    private readonly ModrinthApi _modrinthApi = new();

    public FlintApiModsTests()
    {
        _modrinthApi.BaseUrl = _modrinth.Url;
    }

    public void Dispose()
    {
        _modrinth.Dispose();
    }

    private static string Sha512Hex(byte[] bytes) =>
        Convert.ToHexString(SHA512.HashData(bytes)).ToLowerInvariant();

    /// <summary>A Modrinth version document for the stub, with real hashes of the payload.</summary>
    private static string VersionJson(string baseUrl, string projectId, string versionId, string versionNumber,
        string fileName, string downloadName, byte[] bytes, string? dependenciesJson = null) =>
        new JsonObject
        {
            ["id"] = versionId,
            ["project_id"] = projectId,
            ["date_published"] = "2023-05-01T00:00:00Z",
            ["game_versions"] = new JsonArray("1.20.1"),
            ["loaders"] = new JsonArray("fabric"),
            ["name"] = $"Test Mod {versionNumber}",
            ["version_number"] = versionNumber,
            ["version_type"] = "release",
            ["files"] = new JsonArray((JsonNode?)new JsonObject
            {
                ["url"] = $"{baseUrl}/{downloadName}",
                ["filename"] = fileName,
                ["primary"] = true,
                ["size"] = bytes.Length,
                ["hashes"] = new JsonObject
                {
                    ["sha1"] = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant(),
                    ["sha512"] = Sha512Hex(bytes),
                },
            }),
            ["dependencies"] = dependenciesJson is null
                ? new JsonArray()
                : JsonNode.Parse(dependenciesJson)!.DeepClone(),
        }.ToJsonString();

    private static string ProjectJson(string id, string slug, string title) =>
        new JsonObject
        {
            ["id"] = id,
            ["slug"] = slug,
            ["title"] = title,
            ["description"] = "A synthetic mod",
            ["project_type"] = "mod",
            ["client_side"] = "required",
            ["server_side"] = "required",
            ["icon_url"] = "",
        }.ToJsonString();

    /// <summary>Create a vanilla instance through the API and seed its pack profile with the
    /// resolved mc version + fabric loader (cachedVersion entries — what a resolved profile
    /// would carry; the vanilla create only writes the plain component list).</summary>
    private async Task<string> CreateSeededInstanceAsync(FlintApiHost host, string name)
    {
        var create = await host.PostAsync("/api/v1/instances/vanilla", new { name, mcVersion = "1.20.1" });
        Assert.Equal(System.Net.HttpStatusCode.Created, create.StatusCode);
        var id = JsonDocument.Parse(await create.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString()!;

        var instanceDir = Path.Combine(host.DataDir, "instances", id);
        var pack =
            """
            {"formatVersion":1,"components":[
              {"uid":"net.minecraft","version":"1.20.1","important":true,"cachedVersion":"1.20.1"},
              {"uid":"net.fabricmc.fabric-loader","version":"0.14.21","cachedVersion":"0.14.21"}]}
            """;
        await File.WriteAllTextAsync(Path.Combine(instanceDir, "mmc-pack.json"), pack);
        return id;
    }

    private string ModsDir(FlintApiHost host, string instanceId) =>
        Path.Combine(host.DataDir, "instances", instanceId, "minecraft", "mods");

    [Fact]
    public async Task Mods_List_UnknownInstance_Is404()
    {
        await using var host = await FlintApiHost.StartAsync(_modrinthApi);
        var response = await host.GetAsync("/api/v1/instances/ghost/mods");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("instance_not_found", JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Mods_List_WithoutMetadata_FallsBackToFileName()
    {
        await using var host = await FlintApiHost.StartAsync(_modrinthApi);
        var id = await CreateSeededInstanceAsync(host, "Bare Mods");

        Directory.CreateDirectory(ModsDir(host, id));
        await File.WriteAllBytesAsync(Path.Combine(ModsDir(host, id), "plain.jar"), "plain jar"u8.ToArray());

        var response = await host.GetAsync($"/api/v1/instances/{Uri.EscapeDataString(id)}/mods");
        response.EnsureSuccessStatusCode();
        var mods = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        var entry = Assert.Single(mods.EnumerateArray(), m => m.GetProperty("fileName").GetString() == "plain.jar");
        Assert.Equal("plain", entry.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("version").ValueKind);
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("provider").ValueKind);
        Assert.True(entry.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Mods_Install_EndToEnd_DownloadsSelectedAndDependencies()
    {
        var jarBytes = "selected mod jar content"u8.ToArray();
        var depBytes = "dependency mod jar content"u8.ToArray();
        _modrinth.Downloads["mod.bin"] = jarBytes;
        _modrinth.Downloads["dep.bin"] = depBytes;

        _modrinth.Projects["projId1"] = ProjectJson("projId1", "test-mod", "Test Mod");
        _modrinth.Projects["depProj"] = ProjectJson("depProj", "dep-mod", "Dep Mod");
        // the latest-version lookup: the selected version requires depProj
        _modrinth.ProjectVersions["projId1"] = new JsonArray(JsonNode.Parse(VersionJson(_modrinth.Url,
            "projId1", "verId1", "1.0.0", "test.jar", "mod.bin", jarBytes,
            dependenciesJson: """[{"project_id":"depProj","dependency_type":"required"}]"""))!).ToJsonString();
        _modrinth.ProjectVersions["depProj"] = new JsonArray(JsonNode.Parse(VersionJson(_modrinth.Url,
            "depProj", "depVerId", "0.1.0", "dep.jar", "dep.bin", depBytes))!).ToJsonString();

        await using var host = await FlintApiHost.StartAsync(_modrinthApi);
        var id = await CreateSeededInstanceAsync(host, "Install Target");

        var post = await host.PostAsync($"/api/v1/instances/{Uri.EscapeDataString(id)}/mods/install",
            new[] { new { provider = "modrinth", projectId = "projId1" } });
        Assert.Equal(System.Net.HttpStatusCode.Accepted, post.StatusCode);
        var taskId = JsonDocument.Parse(await post.Content.ReadAsStringAsync())
            .RootElement.GetProperty("taskId").GetString()!;

        var task = await host.WaitForTaskStateAsync(taskId, "succeeded", TimeSpan.FromSeconds(30));
        Assert.Equal("mods-install", task.GetProperty("kind").GetString());
        Assert.Equal(id, task.GetProperty("instanceId").GetString());

        // both jars are in the mods folder ...
        Assert.Equal(jarBytes, await File.ReadAllBytesAsync(Path.Combine(ModsDir(host, id), "test.jar")));
        Assert.Equal(depBytes, await File.ReadAllBytesAsync(Path.Combine(ModsDir(host, id), "dep.jar")));

        // ... and the packwiz metadata was written for the selection and the auto-installed dependency
        var indexDir = Path.Combine(ModsDir(host, id), ".index");
        var metadataToml = await File.ReadAllTextAsync(Path.Combine(indexDir, "test-mod.pw.toml"));
        Assert.Contains("mod-id = \"projId1\"", metadataToml);
        Assert.Contains("version = \"verId1\"", metadataToml);
        Assert.Contains("filename = \"test.jar\"", metadataToml);
        var depToml = await File.ReadAllTextAsync(Path.Combine(indexDir, "dep-mod.pw.toml"));
        Assert.Contains("mod-id = \"depProj\"", depToml);
        Assert.Contains("version = \"depVerId\"", depToml);

        // the list endpoint now exposes both with their metadata attached
        var list = JsonDocument.Parse(await (await host.GetAsync(
                $"/api/v1/instances/{Uri.EscapeDataString(id)}/mods")).Content.ReadAsStringAsync())
            .RootElement;
        var selected = list.EnumerateArray().Single(m => m.GetProperty("fileName").GetString() == "test.jar");
        Assert.Equal("Test Mod", selected.GetProperty("name").GetString());
        Assert.Equal("1.0.0", selected.GetProperty("version").GetString());
        Assert.Equal("modrinth", selected.GetProperty("provider").GetString());
        Assert.True(selected.GetProperty("enabled").GetBoolean());
        var dependency = list.EnumerateArray().Single(m => m.GetProperty("fileName").GetString() == "dep.jar");
        Assert.Equal("Dep Mod", dependency.GetProperty("name").GetString());
        Assert.Equal("0.1.0", dependency.GetProperty("version").GetString());
    }

    [Fact]
    public async Task Mods_Install_UnresolvableProject_FailsTask()
    {
        await using var host = await FlintApiHost.StartAsync(_modrinthApi);
        var id = await CreateSeededInstanceAsync(host, "Fail Target");

        var post = await host.PostAsync($"/api/v1/instances/{Uri.EscapeDataString(id)}/mods/install",
            new[] { new { provider = "modrinth", projectId = "ghost" } });
        Assert.Equal(System.Net.HttpStatusCode.Accepted, post.StatusCode);
        var taskId = JsonDocument.Parse(await post.Content.ReadAsStringAsync())
            .RootElement.GetProperty("taskId").GetString()!;

        var task = await host.WaitForTaskStateAsync(taskId, "failed", TimeSpan.FromSeconds(30));
        Assert.Contains("ghost", task.GetProperty("failReason").GetString());
        Assert.False(Directory.Exists(ModsDir(host, id)) && Directory.EnumerateFiles(ModsDir(host, id), "*.jar").Any());
    }

    [Fact]
    public async Task Mods_Install_UnknownProvider_Is400()
    {
        await using var host = await FlintApiHost.StartAsync(_modrinthApi);
        var id = await CreateSeededInstanceAsync(host, "Bad Provider");

        var post = await host.PostAsync($"/api/v1/instances/{Uri.EscapeDataString(id)}/mods/install",
            new[] { new { provider = "ftb", projectId = "x" } });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, post.StatusCode);
        Assert.Equal("unknown_provider", JsonDocument.Parse(await post.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Mods_Update_EndToEnd_ReplacesFile_BumpsMetadata_PublishesResult()
    {
        var oldBytes = "old jar content"u8.ToArray();
        var newBytes = "brand new jar content"u8.ToArray();
        _modrinth.Downloads["old.bin"] = oldBytes;
        _modrinth.Downloads["new.bin"] = newBytes;
        _modrinth.Projects["projId1"] = ProjectJson("projId1", "test-mod", "Test Mod");

        await using var host = await FlintApiHost.StartAsync(_modrinthApi);
        var id = await CreateSeededInstanceAsync(host, "Update Target");

        // 1. install the pinned old version
        _modrinth.VersionsById["verOld"] = VersionJson(_modrinth.Url, "projId1", "verOld", "1.0.0",
            "test.jar", "old.bin", oldBytes);
        var install = await host.PostAsync($"/api/v1/instances/{Uri.EscapeDataString(id)}/mods/install",
            new[] { new { provider = "modrinth", projectId = "projId1", versionId = "verOld" } });
        Assert.Equal(System.Net.HttpStatusCode.Accepted, install.StatusCode);
        var installTaskId = JsonDocument.Parse(await install.Content.ReadAsStringAsync())
            .RootElement.GetProperty("taskId").GetString()!;
        await host.WaitForTaskStateAsync(installTaskId, "succeeded", TimeSpan.FromSeconds(30));
        Assert.Equal(oldBytes, await File.ReadAllBytesAsync(Path.Combine(ModsDir(host, id), "test.jar")));

        // 2. the hash-based update check now resolves a newer version for the installed hash
        _modrinth.VersionFiles[Sha512Hex(oldBytes)] = VersionJson(_modrinth.Url, "projId1", "verNew",
            "2.0.0", "test.jar", "new.bin", newBytes);

        var update = await host.PostAsync($"/api/v1/instances/{Uri.EscapeDataString(id)}/mods/update", new { });
        Assert.Equal(System.Net.HttpStatusCode.Accepted, update.StatusCode);
        var updateTaskId = JsonDocument.Parse(await update.Content.ReadAsStringAsync())
            .RootElement.GetProperty("taskId").GetString()!;

        var task = await host.WaitForTaskStateAsync(updateTaskId, "succeeded", TimeSpan.FromSeconds(30));

        // 3. the file was replaced and the metadata bumped ...
        Assert.Equal(newBytes, await File.ReadAllBytesAsync(Path.Combine(ModsDir(host, id), "test.jar")));
        var metadataToml = await File.ReadAllTextAsync(Path.Combine(ModsDir(host, id), ".index", "test-mod.pw.toml"));
        Assert.Contains("version = \"verNew\"", metadataToml);
        Assert.Contains("x-prismlauncher-version-number = \"2.0.0\"", metadataToml);

        // 4. ... and the task view exposes the update through the optional result payload
        var result = task.GetProperty("result");
        var updates = result.GetProperty("updates");
        Assert.Equal(1, updates.GetArrayLength());
        Assert.Equal("test.jar", updates[0].GetProperty("file").GetString());
        Assert.Equal("1.0.0", updates[0].GetProperty("from").GetString());
        Assert.Equal("2.0.0", updates[0].GetProperty("to").GetString());
        Assert.Equal("modrinth", updates[0].GetProperty("provider").GetString());

        // the mods-install task now publishes its own result payload (installed entries)
        var installTask = JsonDocument.Parse(
            await (await host.GetAsync($"/api/v1/tasks/{installTaskId}")).Content.ReadAsStringAsync()).RootElement;
        var installResult = installTask.GetProperty("result");
        Assert.Equal(1, installResult.GetProperty("installed").GetArrayLength());
        Assert.Equal("test.jar", installResult.GetProperty("installed")[0].GetProperty("file").GetString());
        Assert.False(installResult.TryGetProperty("updates", out var updatesProp) && updatesProp.ValueKind == JsonValueKind.Array);
    }

    [Fact]
    public async Task Mods_Update_NoMetadata_SucceedsWithEmptyResult()
    {
        await using var host = await FlintApiHost.StartAsync(_modrinthApi);
        var id = await CreateSeededInstanceAsync(host, "Update Empty");

        Directory.CreateDirectory(ModsDir(host, id));
        await File.WriteAllBytesAsync(Path.Combine(ModsDir(host, id), "plain.jar"), "plain jar"u8.ToArray());

        var update = await host.PostAsync($"/api/v1/instances/{Uri.EscapeDataString(id)}/mods/update", new { });
        Assert.Equal(System.Net.HttpStatusCode.Accepted, update.StatusCode);
        var taskId = JsonDocument.Parse(await update.Content.ReadAsStringAsync())
            .RootElement.GetProperty("taskId").GetString()!;

        var task = await host.WaitForTaskStateAsync(taskId, "succeeded", TimeSpan.FromSeconds(30));
        Assert.Equal(0, task.GetProperty("result").GetProperty("updates").GetArrayLength());
    }
}
