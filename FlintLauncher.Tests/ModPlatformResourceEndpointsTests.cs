// SPDX-License-Identifier: GPL-3.0-only
// Fix-1 endpoint tests for the resource-type search pass, the categories surface and the
// description/changelog routes (ModPlatformEndpoints), against the real in-process web host
// (FlintApiHost) and the provider stub servers from ModrinthTestSupport/FlameTestSupport.
//
// Collections: the provider base URLs / Flame key provider are process-global static hooks,
// so these classes join the suites that own them (see ModPlatformApiTests):
//  - the flame class joins "FlameApi" (FlameConfig.BaseUrl / ApiKeyProvider);
//  - the modrinth class joins "ModrinthApi" (ModrinthApi.BaseUrl).
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flint.Tests;
using FlintLauncher.Core;
using FlintLauncher.Core.ModPlatform.Flame;
using FlintLauncher.Core.ModPlatform.Modrinth;
using Xunit;

namespace FlintLauncher.Tests;

// --------------------------------------------------------------------- flame provider surface

[Collection("FlameApi")]
public class FlintApiFlameResourceTests : IDisposable
{
    private readonly StubFlameServer _server = new();

    public FlintApiFlameResourceTests()
    {
        FlameConfig.BaseUrl = _server.Url;
    }

    public void Dispose()
    {
        FlameConfig.BaseUrl = FlintConfig.Default.FlameBaseUrl;
        FlameConfig.ApiKeyProvider = static () => FlintConfig.Default.FlameApiKey;
        _server.Dispose();
    }

    [Fact]
    public async Task Flame_Search_TypeParam_SelectsClassId()
    {
        _server.SearchJson = """{"data":[]}""";

        await using var host = await FlintApiHost.StartAsync();
        var response = await host.GetAsync("/api/v1/modplatform/flame/search?type=shaderpacks&query=shaders");
        response.EnsureSuccessStatusCode();

        Assert.Contains("/mods/search", _server.LastPath);
        Assert.Equal("6552", _server.LastQuery["classId"]);
    }

    [Fact]
    public async Task Flame_Search_DefaultType_IsMods()
    {
        _server.SearchJson = """{"data":[]}""";

        await using var host = await FlintApiHost.StartAsync();
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

        await using var host = await FlintApiHost.StartAsync();
        var response = await host.GetAsync("/api/v1/modplatform/flame/search?type=texturepacks");
        response.EnsureSuccessStatusCode();

        Assert.Equal("12", _server.LastQuery["classId"]);
    }

    [Fact]
    public async Task Flame_Search_UnknownType_Is400()
    {
        await using var host = await FlintApiHost.StartAsync();
        var response = await host.GetAsync("/api/v1/modplatform/flame/search?type=floppies");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("invalid_type", error.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Flame_Categories_MapsIdNameIcon_FromType()
    {
        _server.CategoriesJson = new JsonObject
        {
            ["data"] = new JsonArray(
                (JsonNode?)new JsonObject
                {
                    ["id"] = 4736,
                    ["name"] = "Magic",
                    ["icon"] = "https://cdn.test/magic.png",
                },
                (JsonNode?)new JsonObject { ["id"] = 406, ["name"] = "Technology" }),
        }.ToJsonString();

        await using var host = await FlintApiHost.StartAsync();
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

        await using var host = await FlintApiHost.StartAsync();
        var response = await host.GetAsync("/api/v1/modplatform/flame/categories?classId=1234");
        response.EnsureSuccessStatusCode();
        Assert.Equal(JsonValueKind.Array,
            JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.ValueKind);

        Assert.Equal("1234", _server.LastQuery["classId"]);
    }

    [Fact]
    public async Task Flame_Description_WrapsBody()
    {
        await using var host = await FlintApiHost.StartAsync();
        var response = await host.GetAsync("/api/v1/modplatform/flame/mods/306612/description");
        response.EnsureSuccessStatusCode();
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal("description text", body.GetProperty("body").GetString());
        Assert.Contains("/mods/306612/description", _server.LastPath);
    }

    [Fact]
    public async Task Flame_Changelog_RequiresFileId()
    {
        await using var host = await FlintApiHost.StartAsync();
        var missing = await host.GetAsync("/api/v1/modplatform/flame/mods/306612/changelog");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, missing.StatusCode);

        var ok = await host.GetAsync("/api/v1/modplatform/flame/mods/306612/changelog?fileId=4446");
        ok.EnsureSuccessStatusCode();
        var body = JsonDocument.Parse(await ok.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("changelog text", body.GetProperty("body").GetString());
        Assert.Contains("/mods/306612/files/4446/changelog", _server.LastPath);
    }
}

// ------------------------------------------------------------------- modrinth provider surface

/// <summary>
/// A minimal free-port Modrinth stub for the fix-1 routes the shared StubModrinthServer does
/// not model: GET /tag/category (categories) and GET /project/{id}/description (the plain
/// text body endpoint; the shared stub's /project route only handles bare project ids).
/// </summary>
internal sealed class StubModrinthResourceServer : IDisposable
{
    private readonly HttpListener _listener = new();

    public StubModrinthResourceServer()
    {
        var port = GetFreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        Url = $"http://127.0.0.1:{port}";
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
        var path = ctx.Request.Url!.PathAndQuery;
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
        catch (ObjectDisposedException)
        {
        }
    }

    private static int GetFreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}

[Collection("ModrinthApi")]
public class FlintApiModrinthResourceTests : IDisposable
{
    private readonly StubModrinthServer _server = new();
    private readonly StubModrinthResourceServer _resourceServer = new();

    public FlintApiModrinthResourceTests()
    {
        ModrinthApi.BaseUrl = _server.Url;
    }

    public void Dispose()
    {
        ModrinthApi.BaseUrl = "https://api.modrinth.com/v2";
        _server.Dispose();
        _resourceServer.Dispose();
    }

    [Fact]
    public async Task Modrinth_Search_TypeParam_SelectsProjectTypeFacet()
    {
        await using var host = await FlintApiHost.StartAsync();
        var response = await host.GetAsync("/api/v1/modplatform/modrinth/search?type=resourcepacks");
        response.EnsureSuccessStatusCode();

        Assert.Contains("/search", _server.LastPath);
        var facets = _server.LastQuery["facets"];
        Assert.Contains("project_type:resourcepack", facets);
    }

    [Fact]
    public async Task Modrinth_Search_Datapacks_Facet()
    {
        await using var host = await FlintApiHost.StartAsync();
        var response = await host.GetAsync("/api/v1/modplatform/modrinth/search?type=datapacks");
        response.EnsureSuccessStatusCode();

        Assert.Contains("project_type:datapack", _server.LastQuery["facets"]);
    }

    [Fact]
    public async Task Modrinth_Search_Worlds_Is400()
    {
        await using var host = await FlintApiHost.StartAsync();
        var response = await host.GetAsync("/api/v1/modplatform/modrinth/search?type=worlds");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("invalid_type", error.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Modrinth_Categories_FiltersByProjectType()
    {
        _resourceServer.CategoriesJson = new JsonArray(
            (JsonNode?)new JsonObject
            {
                ["name"] = "adventure",
                ["project_type"] = "mod",
                ["icon"] = "https://cdn.modrinth.test/adventure.png",
            },
            (JsonNode?)new JsonObject
            {
                ["name"] = "8x-",
                ["project_type"] = "resourcepack",
                ["icon"] = "https://cdn.modrinth.test/8x-.png",
            },
            (JsonNode?)new JsonObject
            {
                ["name"] = "16x",
                ["project_type"] = "resourcepack",
            }).ToJsonString();

        ModrinthApi.BaseUrl = _resourceServer.Url;
        await using var host = await FlintApiHost.StartAsync();
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

        ModrinthApi.BaseUrl = _resourceServer.Url;
        await using var host = await FlintApiHost.StartAsync();
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
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("invalid_request", error.GetProperty("error").GetProperty("code").GetString());
    }
}
