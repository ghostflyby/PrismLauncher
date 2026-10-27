// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Fix-1 endpoint tests for the resource-type search pass, the categories surface and the
// description/changelog routes (ModPlatformEndpoints), against the real in-process web host
// (FlintApiHost) and the provider stub servers from ModrinthTestSupport/FlameTestSupport.
//
// Collections: the provider base URLs / Flame key provider are process-global static hooks,
// so these classes join the suites that own them (see ModPlatformApiTests):
//  - the flame class joins "FlameApi" (FlameConfig.BaseUrl / ApiKeyProvider);
//  - the modrinth class joins "ModrinthApi" (ModrinthClient Options.BaseUrl).

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.ModPlatform.Flame;
using FlintLauncher.Core.ModPlatform.Modrinth;
using Xunit;

using FlintLauncher.Core.Net;
using FlintLauncher.ExternalApi;

namespace FlintLauncher.Tests;

// --------------------------------------------------------------------- flame provider surface

[Collection("FlameApi")]
public class FlintApiFlameResourceTests : IDisposable
{
    private readonly FlameApi _flameApi = new(TestNet.Http, TestNet.Settings);
    private readonly StubFlameServer _server = new();

    public FlintApiFlameResourceTests()
    {
        _flameApi.BaseUrl = _server.Url;
    }

    public void Dispose()
    {
        _server.Dispose();
    }

    [Fact]
    public async Task Flame_Search_TypeParam_SelectsClassId()
    {
        _server.SearchJson = """{"data":[]}""";

        await using var host = await FlintApiHost.StartAsync(flame: _flameApi);
        var response = await host.GetAsync("/api/v1/modplatform/flame/search?type=shaderpacks&query=shaders");
        response.EnsureSuccessStatusCode();

        Assert.Contains("/mods/search", _server.LastPath);
        Assert.Equal("6552", _server.LastQuery["classId"]);
    }

    [Fact]
    public async Task Flame_Search_DefaultType_IsMods()
    {
        _server.SearchJson = """{"data":[]}""";

        await using var host = await FlintApiHost.StartAsync(flame: _flameApi);
        var response = await host.GetAsync("/api/v1/modplatform/flame/search?query=fabric");
        response.EnsureSuccessStatusCode();

        Assert.Equal("6", _server.LastQuery["classId"]);
    }

    // texture packs share the resource-pack class (the C++ texture-pack model derives from
    // the resource-pack one; CurseForge serves both under class 12)
    [Fact]
    public async Task Flame_Search_TexturePacks_UseResourcePackClass()
    {
        _server.SearchJson = """{"data":[]}""";

        await using var host = await FlintApiHost.StartAsync(flame: _flameApi);
        var response = await host.GetAsync("/api/v1/modplatform/flame/search?type=texturepacks");
        response.EnsureSuccessStatusCode();

        Assert.Equal("12", _server.LastQuery["classId"]);
    }

    [Fact]
    public async Task Flame_Search_UnknownType_Is400()
    {
        await using var host = await FlintApiHost.StartAsync(flame: _flameApi);
        var response = await host.GetAsync("/api/v1/modplatform/flame/search?type=floppies");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("invalid_type", error.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Flame_Categories_MapsIdNameIcon_FromType()
    {
        _server.CategoriesJson = new JsonObject
        {
            ["data"] = new JsonArray(
                new JsonObject
                {
                    ["id"] = 4736,
                    ["name"] = "Magic",
                    ["icon"] = "https://cdn.test/magic.png",
                },
                new JsonObject { ["id"] = 406, ["name"] = "Technology" }),
        }.ToJsonString();

        await using var host = await FlintApiHost.StartAsync(flame: _flameApi);
        var response = await host.GetAsync("/api/v1/modplatform/flame/categories?type=mods");
        response.EnsureSuccessStatusCode();
        var categories = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(2, categories.GetArrayLength());
        Assert.Equal("4736", categories[0].GetProperty("id").GetString());
        Assert.Equal("Magic", categories[0].GetProperty("name").GetString());
        Assert.Equal("https://cdn.test/magic.png", categories[0].GetProperty("icon").GetString());
        Assert.Equal("406", categories[1].GetProperty("id").GetString());
        Assert.Equal("Technology", categories[1].GetProperty("name").GetString());
        Assert.Equal("", categories[1].GetProperty("icon").GetString());

        Assert.Equal("6", _server.LastQuery["classId"]);
        Assert.Equal("432", _server.LastQuery["gameId"]);
    }

    [Fact]
    public async Task Flame_Categories_RawClassId_Wins()
    {
        _server.CategoriesJson = """{"data":[]}""";

        await using var host = await FlintApiHost.StartAsync(flame: _flameApi);
        var response = await host.GetAsync("/api/v1/modplatform/flame/categories?classId=1234");
        response.EnsureSuccessStatusCode();
        Assert.Equal(JsonValueKind.Array,
            JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.ValueKind);

        Assert.Equal("1234", _server.LastQuery["classId"]);
    }

    [Fact]
    public async Task Flame_Description_WrapsBody()
    {
        await using var host = await FlintApiHost.StartAsync(flame: _flameApi);
        var response = await host.GetAsync("/api/v1/modplatform/flame/mods/306612/description");
        response.EnsureSuccessStatusCode();
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal("description text", body.GetProperty("body").GetString());
        Assert.Contains("/mods/306612/description", _server.LastPath);
    }

    [Fact]
    public async Task Flame_Changelog_RequiresFileId()
    {
        await using var host = await FlintApiHost.StartAsync(flame: _flameApi);
        var missing = await host.GetAsync("/api/v1/modplatform/flame/mods/306612/changelog");
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        var ok = await host.GetAsync("/api/v1/modplatform/flame/mods/306612/changelog?fileId=4446");
        ok.EnsureSuccessStatusCode();
        var body = JsonDocument.Parse(await ok.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("changelog text", body.GetProperty("body").GetString());
        Assert.Contains("/mods/306612/files/4446/changelog", _server.LastPath);
    }
}

// ------------------------------------------------------------------- modrinth provider surface

/// <summary>
/// A minimal free-port Modrinth stub for the routes the shared StubModrinthServer does
/// not model: GET /tag/category (categories) and GET /project/{id}/description (the plain
/// text body endpoint; the shared stub's /project route only handles bare project ids).
/// </summary>
internal sealed class StubModrinthResourceServer : IDisposable
{
    private readonly HttpListener _listener;

    public StubModrinthResourceServer()
    {
        // TestPorts owns the port lease AND the rebind-retry (HttpListener cannot adopt the
        // lease socket, so losing the takeover window retries on a fresh port)
        (_listener, Url) = PortAllocator.StartHttpListener();
        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    try
                    {
                        Handle(context);
                    }
                    finally
                    {
                        context.Response.Close();
                    }
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
            }
        });
    }

    public string Url { get; }

    public string CategoriesJson { get; set; } = "[]";

    public string DescriptionText { get; set; } = "";

    public string LastPath { get; private set; } = "";

    private void Handle(HttpListenerContext ctx)
    {
        var url = ctx.Request.Url ?? throw new InvalidOperationException("the HTTP request has no URL");
        var path = url.PathAndQuery;
        LastPath = path;

        byte[] body;
        if (path.EndsWith("/description", StringComparison.Ordinal))
        {
            body = Encoding.UTF8.GetBytes(DescriptionText);
        }
        else if (path.EndsWith("/tag/category", StringComparison.Ordinal))
        {
            body = Encoding.UTF8.GetBytes(CategoriesJson);
        }
        else
        {
            ctx.Response.StatusCode = 404;
            return;
        }

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentLength64 = body.Length;
        ctx.Response.OutputStream.Write(body);
    }

    public void Dispose()
    {
        try
        {
            _listener.Close();
        }
        catch (ObjectDisposedException) { }
    }
}

[Collection("ModrinthApi")]
public class FlintApiModrinthResourceTests : IDisposable
{
    private readonly StubModrinthServer _server = new();
    private readonly StubModrinthResourceServer _resourceServer = new();

    private ModrinthClient _modrinthApi;

    public FlintApiModrinthResourceTests()
    {
        _modrinthApi = TestModrinthApi.At(_server.Url).Client;
    }

    public void Dispose()
    {
        _server.Dispose();
        _resourceServer.Dispose();
    }

    [Fact]
    public async Task Modrinth_Search_TypeParam_SelectsProjectTypeFacet()
    {
        await using var host = await FlintApiHost.StartAsync(_modrinthApi);
        var response = await host.GetAsync("/api/v1/modplatform/modrinth/search?type=resourcepacks");
        response.EnsureSuccessStatusCode();

        Assert.Contains("/search", _server.LastPath);
        var facets = _server.LastQuery["facets"];
        Assert.Contains("project_type:resourcepack", facets);
    }

    [Fact]
    public async Task Modrinth_Search_Datapacks_Facet()
    {
        await using var host = await FlintApiHost.StartAsync(_modrinthApi);
        var response = await host.GetAsync("/api/v1/modplatform/modrinth/search?type=datapacks");
        response.EnsureSuccessStatusCode();

        Assert.Contains("project_type:datapack", _server.LastQuery["facets"]);
    }

    [Fact]
    public async Task Modrinth_Search_Worlds_Is400()
    {
        await using var host = await FlintApiHost.StartAsync();
        var response = await host.GetAsync("/api/v1/modplatform/modrinth/search?type=worlds");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("invalid_type", error.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Modrinth_Categories_FiltersByProjectType()
    {
        _resourceServer.CategoriesJson = new JsonArray(
            new JsonObject
            {
                ["name"] = "adventure",
                ["project_type"] = "mod",
                ["icon"] = "https://cdn.modrinth.test/adventure.png",
                ["header"] = "Mod Categories",
            },
            new JsonObject
            {
                ["name"] = "8x-",
                ["project_type"] = "resourcepack",
                ["icon"] = "https://cdn.modrinth.test/8x-.png",
                ["header"] = "Resolution Categories",
            },
            new JsonObject
            {
                ["name"] = "16x",
                ["project_type"] = "resourcepack",
                ["icon"] = "",
                ["header"] = "Resolution Categories",
            }).ToJsonString();

        _modrinthApi = TestModrinthApi.At(_resourceServer.Url).Client;
        await using var host = await FlintApiHost.StartAsync(_modrinthApi);
        var response = await host.GetAsync("/api/v1/modplatform/modrinth/categories?type=resourcepacks");
        response.EnsureSuccessStatusCode();
        var categories = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(2, categories.GetArrayLength());
        // the Modrinth category id is its name (like the core LoadCategories mapping)
        Assert.Equal("8x-", categories[0].GetProperty("id").GetString());
        Assert.Equal("8x-", categories[0].GetProperty("name").GetString());
        Assert.Equal("https://cdn.modrinth.test/8x-.png", categories[0].GetProperty("icon").GetString());
        Assert.Equal("16x", categories[1].GetProperty("id").GetString());
        Assert.Equal("", categories[1].GetProperty("icon").GetString());

        Assert.EndsWith("/tag/category", _resourceServer.LastPath);
    }

    [Fact]
    public async Task Modrinth_Description_WrapsPlainTextBody()
    {
        _resourceServer.DescriptionText = "This is a plain-text project body.";

        _modrinthApi = TestModrinthApi.At(_resourceServer.Url).Client;
        await using var host = await FlintApiHost.StartAsync(_modrinthApi);
        var response = await host.GetAsync("/api/v1/modplatform/modrinth/mods/projId1/description");
        response.EnsureSuccessStatusCode();
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal("This is a plain-text project body.", body.GetProperty("body").GetString());
        Assert.EndsWith("/project/projId1/description", _resourceServer.LastPath);
    }

    [Fact]
    public async Task Modrinth_Changelog_IsNotSupported()
    {
        await using var host = await FlintApiHost.StartAsync();
        var response = await host.GetAsync("/api/v1/modplatform/modrinth/mods/projId1/changelog?fileId=1");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("invalid_request", error.GetProperty("error").GetProperty("code").GetString());
    }
}
