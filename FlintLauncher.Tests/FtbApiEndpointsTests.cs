// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// API-level tests for the FTB endpoints: the /modpack/all index pass-through,
// the per-pack description/version list, the staged pack install against the stub FTB server
// (reusing the FtbTestSupport fixture builders — the endpoint runs the same
// FtbPackInstallTask the core tests drive directly, backed by the Flame stub for the
// CurseForge-only file) and the failure surface (unknown pack -> 500 creation_failed with
// the fetch reason, unknown version -> the task's FailReason, validation -> 400).
//
// Each suite builds its own FtbApi/FlameApi clients pointed at the stubs and hands them to
// the host (per-instance BaseUrl, nothing process-global to restore).

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.ModPlatform.Flame;
using FlintLauncher.Core.ModPlatform.Ftb;
using Xunit;

namespace FlintLauncher.Tests;

public class FtbApiEndpointsTests : IDisposable
{
    private readonly FlameApi _flameApi = new(TestNet.Http, TestNet.Settings);
    private readonly FtbApi _ftbApi;
    private readonly StubFtbServer _server = new();
    private readonly StubFlameServer _flameServer = new();
    private readonly byte[] _cfBytes = "ftb curseforge-resolved mod payload"u8.ToArray();

    public FtbApiEndpointsTests()
    {
        _ftbApi = new FtbApi(TestNet.Http, TestNet.Settings) { BaseUrl = _server.Url };
        // the FTB install resolves CurseForge-only files through the Flame layer
        _flameApi.BaseUrl = _flameServer.Url;
    }

    public void Dispose()
    {
        _server.Dispose();
        _flameServer.Dispose();
    }

    private void SetupFlameResolution(string? downloadUrlPath = "mod.bin")
    {
        var fileJson = FlamePayloads.FileJson(_flameServer.Url, fileId: 456,
            fileName: "cfmod.jar", displayName: "CF Mod 2.0",
            downloadUrlPath: downloadUrlPath);
        _flameServer.FilesJson = new JsonObject
        {
            ["data"] = new JsonArray(JsonNode.Parse(fileJson)),
        }.ToJsonString();
        _flameServer.ProjectsJson = new JsonObject
        {
            ["data"] = new JsonArray(JsonNode.Parse(FlamePayloads.ProjectJson(id: 123))),
        }.ToJsonString();
        _flameServer.Downloads["mod.bin"] = _cfBytes;
    }

    // -------------------------------------------------------------------- browse

    [Fact]
    public async Task Packs_Index_PassThrough()
    {
        _server.ModpacksIndexJson = """{"packs":[42, 43]}""";
        await using var host = await FlintApiHost.StartAsync(flame: _flameApi, ftb: _ftbApi);

        var response = await host.GetAsync("/api/v1/modplatform/ftb/packs");
        response.EnsureSuccessStatusCode();
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        var packs = body.GetProperty("packs");
        Assert.Equal(2, packs.GetArrayLength());
        Assert.Equal(42, packs[0].GetProperty("id").GetInt32());
        Assert.Equal(43, packs[1].GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Pack_Details_FromStub()
    {
        var fixture = FtbPackFixture.Create();
        fixture.Register(_server);
        await using var host = await FlintApiHost.StartAsync(flame: _flameApi, ftb: _ftbApi);

        var response = await host.GetAsync($"/api/v1/modplatform/ftb/packs/{FtbPackFixture.PackId}");
        response.EnsureSuccessStatusCode();
        var pack = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(42, pack.GetProperty("id").GetInt32());
        Assert.Equal("Test FTB Pack", pack.GetProperty("name").GetString());
        Assert.Equal("a synthetic pack", pack.GetProperty("synopsis").GetString());
        // safeName is the normalized pack *image* name (LoadModpack's mapping)
        Assert.Equal("testftbpack.png", pack.GetProperty("safeName").GetString());
        Assert.True(pack.GetProperty("featured").GetBoolean());
        Assert.Equal("tester", pack.GetProperty("authors")[0].GetProperty("name").GetString());
        Assert.Equal("https://example.invalid/logo.png",
            pack.GetProperty("art")[0].GetProperty("url").GetString());
        Assert.Equal("magic", pack.GetProperty("tags")[0].GetString());

        var versions = pack.GetProperty("versions");
        Assert.Equal(2, versions.GetArrayLength());
        Assert.Equal(FtbPackFixture.VersionId, versions[0].GetProperty("id").GetInt32());
        Assert.Equal("1.0.0", versions[0].GetProperty("name").GetString());
        Assert.Equal("Release", versions[0].GetProperty("type").GetString());

        // unknown pack -> the uniform fetch-failure envelope
        var missing = await host.GetAsync("/api/v1/modplatform/ftb/packs/999");
        Assert.Equal(HttpStatusCode.BadGateway, missing.StatusCode);
        Assert.Equal("ftb_unavailable", JsonDocument.Parse(await missing.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Pack_Versions_FromStub()
    {
        var fixture = FtbPackFixture.Create();
        fixture.Register(_server);
        await using var host = await FlintApiHost.StartAsync(flame: _flameApi, ftb: _ftbApi);

        var response = await host.GetAsync(
            $"/api/v1/modplatform/ftb/packs/{FtbPackFixture.PackId}/versions");
        response.EnsureSuccessStatusCode();
        var versions = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(2, versions.GetArrayLength());
        Assert.Equal(FtbPackFixture.VersionId, versions[0].GetProperty("id").GetInt32());
        Assert.Equal("1.0.0", versions[0].GetProperty("name").GetString());
        Assert.Equal(FtbPackFixture.VersionId + 1, versions[1].GetProperty("id").GetInt32());
        Assert.Equal("1.1.0", versions[1].GetProperty("name").GetString());
    }

    // ------------------------------------------------------------------ install

    [Fact]
    public async Task Install_EndToEnd_CommitsInstanceWithComponentsAndFiles()
    {
        var fixture = FtbPackFixture.Create();
        fixture.Register(_server);
        SetupFlameResolution();
        await using var host = await FlintApiHost.StartAsync(flame: _flameApi, ftb: _ftbApi);

        var post = await host.PostAsync("/api/v1/modplatform/ftb/install", new
        {
            packId = FtbPackFixture.PackId,
            versionId = FtbPackFixture.VersionId,
            name = "FTB API Import",
            group = "FTB",
            iconKey = "flint",
            serverUrl = _server.Url,
        });
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var created = JsonDocument.Parse(await post.Content.ReadAsStringAsync()).RootElement;
        var id = created.GetProperty("id").GetString();
        Assert.NotNull(id);
        Assert.False(string.IsNullOrEmpty(id));
        Assert.Equal("FTB API Import", created.GetProperty("name").GetString());

        // the committed instance carries the version payload's files: sha1-verified direct
        // downloads below .minecraft/<path>/<name>, the CurseForge-only file resolved
        // through the Flame layer and the server-only file skipped
        var staging = Path.Combine(host.DataDir, "instances", id);
        Assert.Equal(fixture.ModBytes,
            await File.ReadAllBytesAsync(Path.Combine(staging, ".minecraft", "mods", "testmod.jar")));
        Assert.Equal(fixture.ConfigBytes,
            await File.ReadAllBytesAsync(Path.Combine(staging, ".minecraft", "config", "test.cfg")));
        Assert.Equal(_cfBytes,
            await File.ReadAllBytesAsync(Path.Combine(staging, ".minecraft", "mods", "cfmod.jar")));
        Assert.False(File.Exists(Path.Combine(staging, ".minecraft", "mods", "servermod.jar")));

        // components: net.minecraft + the forge modloader target
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(staging, "mmc-pack.json")));
        var components = doc.RootElement.GetProperty("components").EnumerateArray().ToList();
        Assert.Contains(components,
            c => c.GetProperty("uid").GetString() == "net.minecraft"
                 && c.GetProperty("version").GetString() == "1.18.2");
        Assert.Contains(components,
            c => c.GetProperty("uid").GetString() == "net.minecraftforge"
                 && c.GetProperty("version").GetString() == "40.1.0");

        // instance.json metadata (name + icon key + managed-pack bookkeeping, camelCase)
        // and the discovered instance surface
        var cfg = await File.ReadAllTextAsync(Path.Combine(staging, "instance.json"));
        Assert.Contains("FTB API Import", cfg);
        Assert.Contains("flint", cfg);
        Assert.Contains("managed", cfg);
        Assert.Contains($"{FtbPackFixture.PackId}", cfg);
        var list = JsonDocument.Parse(
            await (await host.GetAsync("/api/v1/instances")).Content.ReadAsStringAsync()).RootElement;
        var entry = list.EnumerateArray().Single(e => e.GetProperty("id").GetString() == id);
        Assert.Equal("FTB", entry.GetProperty("group").GetString());
        Assert.Equal("flint", entry.GetProperty("iconKey").GetString());
    }

    [Fact]
    public async Task Install_WithoutVersionId_UsesLatestListedVersion_AndPackDefaults()
    {
        var fixture = FtbPackFixture.Create();
        fixture.Register(_server);
        SetupFlameResolution();
        await using var host = await FlintApiHost.StartAsync(flame: _flameApi, ftb: _ftbApi);

        var post = await host.PostAsync("/api/v1/modplatform/ftb/install", new
        {
            packId = FtbPackFixture.PackId,
            serverUrl = _server.Url,
        });
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var id = JsonDocument.Parse(await post.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString();
        Assert.NotNull(id);

        // the pack's first-listed version (the fixture's 1.0.0/4242) was installed and the
        // instance name/icon fell back to the pack name / "default" (instance.json)
        var cfg = await File.ReadAllTextAsync(
            Path.Combine(host.DataDir, "instances", id, "instance.json"));
        Assert.Contains($"{FtbPackFixture.VersionId}", cfg);
        Assert.Contains("1.0.0", cfg);
        Assert.Contains("Test FTB Pack", cfg);
        // "iconKey" stays unset (the registered default applies) — the JSON carries it as null
        Assert.DoesNotContain("\"iconKey\":\"", cfg);
    }

    [Fact]
    public async Task Install_UnknownPack_ReturnsCreationFailed()
    {
        await using var host = await FlintApiHost.StartAsync(flame: _flameApi, ftb: _ftbApi);

        var post = await host.PostAsync("/api/v1/modplatform/ftb/install", new
        {
            packId = 999,
            serverUrl = _server.Url, // the stub 404s /modpack/999
        });
        Assert.Equal(HttpStatusCode.InternalServerError, post.StatusCode);
        var error = JsonDocument.Parse(await post.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error");
        Assert.Equal("creation_failed", error.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));

        // nothing was committed
        Assert.False(Directory.Exists(Path.Combine(host.DataDir, "instances", "Test FTB Pack")));
    }

    [Fact]
    public async Task Install_UnknownVersion_SurfacesTaskMessage()
    {
        var fixture = FtbPackFixture.Create();
        fixture.Register(_server);
        await using var host = await FlintApiHost.StartAsync(flame: _flameApi, ftb: _ftbApi);

        var post = await host.PostAsync("/api/v1/modplatform/ftb/install", new
        {
            packId = FtbPackFixture.PackId,
            versionId = 987654, // not one of the pack's version ids
            serverUrl = _server.Url,
        });
        Assert.Equal(HttpStatusCode.InternalServerError, post.StatusCode);
        var error = JsonDocument.Parse(await post.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error");
        Assert.Equal("creation_failed", error.GetProperty("code").GetString());
        // the task's own "Failed to find pack version" message surfaced
        Assert.Contains("Failed to find pack version", error.GetProperty("message").GetString());

        Assert.False(Directory.Exists(Path.Combine(host.DataDir, "instances", "Test FTB Pack")));
    }

    [Fact]
    public async Task Install_ServerUrlOverride_IsHonored_AndSharedClientUntouched()
    {
        var fixture = FtbPackFixture.Create();
        fixture.Register(_server);
        SetupFlameResolution();
        // a dead runtime client forces the request's serverUrl override to be the only
        // working route
        var deadClient = new FtbApi(TestNet.Http, TestNet.Settings) { BaseUrl = "http://127.0.0.1:9" };
        await using var host = await FlintApiHost.StartAsync(flame: _flameApi, ftb: deadClient);

        var post = await host.PostAsync("/api/v1/modplatform/ftb/install", new
        {
            packId = FtbPackFixture.PackId,
            versionId = FtbPackFixture.VersionId,
            serverUrl = _server.Url,
        });
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        // the override built a derived client; the shared one keeps its (bogus) base URL
        Assert.Equal("http://127.0.0.1:9", deadClient.BaseUrl);
        Assert.True(File.Exists(Path.Combine(host.DataDir, "instances", "Test FTB Pack",
            ".minecraft", "mods", "testmod.jar")));
    }

    [Fact]
    public async Task Install_Validation_Errors()
    {
        await using var host = await FlintApiHost.StartAsync(flame: _flameApi, ftb: _ftbApi);

        var missingPack = await host.PostAsync("/api/v1/modplatform/ftb/install",
            new { versionId = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, missingPack.StatusCode);
        Assert.Equal("invalid_body", JsonDocument.Parse(await missingPack.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error").GetProperty("code").GetString());

        var badServer = await host.PostAsync("/api/v1/modplatform/ftb/install",
            new { packId = FtbPackFixture.PackId, serverUrl = "not a url" });
        Assert.Equal(HttpStatusCode.BadRequest, badServer.StatusCode);
        Assert.Equal("invalid_server_url", JsonDocument.Parse(await badServer.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}
