// SPDX-License-Identifier: GPL-3.0-only
// API-level tests for the ATLauncher endpoints (fix-2 step 1): the packsnew.json
// pass-through (list + per-pack versions), the staged pack install against the stub ATL CDN
// (reusing the AtlTestSupport fixture builders — the endpoint runs the same
// AtLauncherPackInstallTask the core tests drive directly) and the failure surface
// (unknown pack -> 500 creation_failed with the task's FailReason, validation -> 400).
//
// [Collection("AtLauncherApi")] matches AtLauncherTests: the ATL download-server/API URL
// hooks are process-global statics, so every suite that touches them must not run in
// parallel (the endpoint under test also saves/restores them around the install call).
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.ModPlatform.AtLauncher;
using FlintLauncher.Tests;
using Xunit;

namespace Flint.Tests;

[Collection("AtLauncherApi")]
public class AtLauncherApiEndpointsTests : IDisposable
{
    private readonly StubAtlServer _server = new();

    public AtLauncherApiEndpointsTests()
    {
        // both URL hooks point at the stub (note the trailing slash like the C++ defaults)
        AtLauncherApi.DownloadServerUrl = _server.Url + "/";
        AtLauncherApi.ApiBaseUrl = _server.Url + "/";
    }

    public void Dispose()
    {
        AtLauncherApi.DownloadServerUrl = FlintLauncher.Core.FlintConfig.Default.AtlDownloadServerUrl;
        AtLauncherApi.ApiBaseUrl = FlintLauncher.Core.FlintConfig.Default.AtlApiBaseUrl;
        _server.Dispose();
    }

    /// <summary>The two-entry packsnew.json index fixture (public + private pack).</summary>
    private static string PacksNewJson => new JsonArray(
        (JsonNode?)new JsonObject
        {
            ["id"] = 1,
            ["position"] = 10,
            ["name"] = "Public Pack",
            ["type"] = "public",
            ["system"] = false,
            ["description"] = "A public pack",
            ["versions"] = new JsonArray(
                (JsonNode?)new JsonObject { ["version"] = "1.0.0", ["minecraft"] = "1.20.1" },
                (JsonNode?)new JsonObject { ["version"] = "1.1.0", ["minecraft"] = "1.20.4" }),
        },
        (JsonNode?)new JsonObject
        {
            ["id"] = 2,
            ["position"] = 5,
            ["name"] = "Private Pack!",
            ["type"] = "private",
            ["system"] = true,
            ["description"] = "",
            ["versions"] = new JsonArray(),
        }).ToJsonString();

    // -------------------------------------------------------------------- packs

    [Fact]
    public async Task Packs_PassThrough_Index()
    {
        _server.PacksNewJson = PacksNewJson;
        await using var host = await FlintApiHost.StartAsync();

        var response = await host.GetAsync("/api/v1/modplatform/atlauncher/packs");
        response.EnsureSuccessStatusCode();
        var packs = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(2, packs.GetArrayLength());
        Assert.Equal("Public Pack", packs[0].GetProperty("name").GetString());
        // safeName is the normalized pack *image* name (LoadIndexedPack's mapping)
        Assert.Equal("publicpack.png", packs[0].GetProperty("safeName").GetString());
        Assert.Equal("public", packs[0].GetProperty("type").GetString());
        Assert.Equal("A public pack", packs[0].GetProperty("description").GetString());
        Assert.Equal(2, packs[0].GetProperty("versions").GetArrayLength());
        Assert.Equal("1.0.0", packs[0].GetProperty("versions")[0].GetProperty("version").GetString());
        Assert.Equal("1.20.1", packs[0].GetProperty("versions")[0].GetProperty("minecraft").GetString());

        Assert.Equal("Private Pack!", packs[1].GetProperty("name").GetString());
        Assert.Equal("private", packs[1].GetProperty("type").GetString());
        Assert.Equal(0, packs[1].GetProperty("versions").GetArrayLength());
    }

    [Fact]
    public async Task Pack_Versions_FromIndex()
    {
        _server.PacksNewJson = PacksNewJson;
        await using var host = await FlintApiHost.StartAsync();

        var response = await host.GetAsync(
            "/api/v1/modplatform/atlauncher/packs/" + Uri.EscapeDataString("Public Pack") + "/versions");
        response.EnsureSuccessStatusCode();
        var versions = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(2, versions.GetArrayLength());
        Assert.Equal("1.0.0", versions[0].GetProperty("version").GetString());
        Assert.Equal("1.20.4", versions[1].GetProperty("minecraft").GetString());

        var missing = await host.GetAsync("/api/v1/modplatform/atlauncher/packs/Nope/versions");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("pack_not_found", JsonDocument.Parse(await missing.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    // ------------------------------------------------------------------ install

    [Fact]
    public async Task Install_EndToEnd_CommitsInstanceWithPackLayout()
    {
        var fixture = AtlPackFixture.Create();
        fixture.Register(_server);
        await using var host = await FlintApiHost.StartAsync();

        var post = await host.PostAsync("/api/v1/modplatform/atlauncher/install", new
        {
            name = fixture.PackName, // "Test Pack"
            version = fixture.VersionName,
            group = "ATL",
            iconKey = "flint",
            optionalMods = new Dictionary<string, bool>
            {
                ["Optional Selected"] = true,
                ["Optional Unselected"] = false,
            },
            serverUrl = _server.Url + "/",
        });
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var created = JsonDocument.Parse(await post.Content.ReadAsStringAsync()).RootElement;
        var id = created.GetProperty("id").GetString()!;
        Assert.False(string.IsNullOrEmpty(id));
        Assert.Equal("Test Pack", created.GetProperty("name").GetString());

        // the committed instance carries the pack layout: configs zip (sha1-verified) +
        // md5-verified mod downloads through the request's serverUrl override
        var staging = Path.Combine(host.DataDir, "instances", id);
        Assert.Equal(fixture.ConfigTxt,
            await File.ReadAllTextAsync(Path.Combine(staging, "minecraft", "config", "test.cfg")));
        Assert.Equal(fixture.RequiredModBytes,
            await File.ReadAllBytesAsync(Path.Combine(staging, "minecraft", "mods", "required.jar")));
        // the optional-mod selection was honored
        Assert.Equal(fixture.OptionalSelectedBytes,
            await File.ReadAllBytesAsync(Path.Combine(staging, "minecraft", "mods", "optsel.jar")));
        Assert.False(File.Exists(Path.Combine(staging, "minecraft", "mods", "optunsel.jar")));

        // instance.cfg metadata (name + icon key) and the discovered instance surface
        var cfg = await File.ReadAllTextAsync(Path.Combine(staging, "instance.cfg"));
        Assert.Contains("name=Test Pack", cfg);
        Assert.Contains("iconKey=flint", cfg);
        var list = JsonDocument.Parse(
            await (await host.GetAsync("/api/v1/instances")).Content.ReadAsStringAsync()).RootElement;
        var entry = list.EnumerateArray().Single(e => e.GetProperty("id").GetString() == id);
        Assert.Equal("ATL", entry.GetProperty("group").GetString());
        Assert.Equal("flint", entry.GetProperty("iconKey").GetString());
    }

    [Fact]
    public async Task Install_ChooseLoaderVersion_AppliesLoaderComponent()
    {
        var fixture = AtlPackFixture.Create(
            """{"type":"fabric","choose":true,"metadata":{"loader":"","latest":false,"recommended":false}}""");
        fixture.Register(_server);
        await using var host = await FlintApiHost.StartAsync();

        var post = await host.PostAsync("/api/v1/modplatform/atlauncher/install", new
        {
            name = fixture.PackName,
            version = fixture.VersionName,
            chooseLoaderVersion = "0.14.21",
            serverUrl = _server.Url + "/",
        });
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var id = JsonDocument.Parse(await post.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString()!;

        using var doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(host.DataDir, "instances", id, "mmc-pack.json")));
        Assert.Contains(doc.RootElement.GetProperty("components").EnumerateArray(),
            c => c.GetProperty("uid").GetString() == "net.fabricmc.fabric-loader"
                 && c.GetProperty("version").GetString() == "0.14.21");
    }

    [Fact]
    public async Task Install_ServerUrlOverride_IsHonored_AndHooksAreRestored()
    {
        var fixture = AtlPackFixture.Create();
        fixture.Register(_server);
        await using var host = await FlintApiHost.StartAsync();

        // a dead static forces the request's serverUrl override to be the only working route
        AtLauncherApi.DownloadServerUrl = "http://127.0.0.1:9/";
        var previous = AtLauncherApi.DownloadServerUrl;

        var post = await host.PostAsync("/api/v1/modplatform/atlauncher/install", new
        {
            name = fixture.PackName,
            version = fixture.VersionName,
            serverUrl = _server.Url + "/",
        });
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        // the static hooks were restored after the call (the bogus value is still in place)
        Assert.Equal(previous, AtLauncherApi.DownloadServerUrl);
        Assert.True(File.Exists(Path.Combine(host.DataDir, "instances", fixture.PackName,
            "minecraft", "mods", "required.jar")));
    }

    [Fact]
    public async Task Install_UnknownPack_ReturnsCreationFailed_WithTaskMessage()
    {
        await using var host = await FlintApiHost.StartAsync();

        var post = await host.PostAsync("/api/v1/modplatform/atlauncher/install", new
        {
            name = "Missing Pack",
            version = "9.9.9",
            serverUrl = _server.Url + "/", // the stub 404s the Configs.json fetch
        });
        Assert.Equal(HttpStatusCode.InternalServerError, post.StatusCode);
        var error = JsonDocument.Parse(await post.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error");
        Assert.Equal("creation_failed", error.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));

        // nothing was committed
        Assert.False(Directory.Exists(Path.Combine(host.DataDir, "instances", "Missing Pack")));
    }

    [Fact]
    public async Task Install_Validation_Errors()
    {
        await using var host = await FlintApiHost.StartAsync();

        var missingVersion = await host.PostAsync("/api/v1/modplatform/atlauncher/install",
            new { name = "Test Pack" });
        Assert.Equal(HttpStatusCode.BadRequest, missingVersion.StatusCode);
        Assert.Equal("invalid_body", JsonDocument.Parse(await missingVersion.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error").GetProperty("code").GetString());

        var badServer = await host.PostAsync("/api/v1/modplatform/atlauncher/install",
            new { name = "Test Pack", version = "1.0.0", serverUrl = "not a url" });
        Assert.Equal(HttpStatusCode.BadRequest, badServer.StatusCode);
        Assert.Equal("invalid_server_url", JsonDocument.Parse(await badServer.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}
