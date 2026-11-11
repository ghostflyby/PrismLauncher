// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// API-level tests for the Technic endpoints: the search/trending pass-through
// (the ?build= request param, the "vanilla" slug exclusion, the icon mapping), the pack
// metadata with the url-vs-solder detection (zip vs solder flavor) and both install shapes
// (zip download / Solder instance) against the stub Technic Platform+Solder server —
// reusing the TechnicTestSupport fixture builders, since the endpoint runs the very same
// SingleZipPackInstallTask / SolderPackInstallTask the core tests drive directly.
//
// Every host gets its own TechnicClient pointed at the stub (BaseUrl/ApiBuild are
// per-instance state now, so no [Collection] serialization is needed anymore).

using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.ExternalApi.Technic;
using Xunit;

namespace FlintLauncher.Tests;

public class TechnicApiEndpointsTests : IDisposable
{
    private readonly StubTechnicServer _server = new();
    private readonly TechnicClient _api;

    public TechnicApiEndpointsTests()
    {
        _api = new TechnicClient(new TechnicClientOptions { BaseUrl = _server.Url + "/", ApiBuild = "testbuild" });
    }

    public void Dispose()
    {
        _server.Dispose();
    }

    private void SeedSearchList() =>
        _server.SearchListJson = """
                                 {"modpacks":[
                                     {"name":"Bevo's Tech Pack","slug":"bevo","iconUrl":"http://cdn.technicpack.net/bevo.png"},
                                     {"name":"Vanilla","slug":"vanilla","iconUrl":"null"},
                                     {"name":"No Icon","slug":"noicon"}
                                 ]}
                                 """;

    // ------------------------------------------------------------ search / trending

    [Fact]
    public async Task Search_PassThrough_MapsHitsAndSkipsVanilla()
    {
        SeedSearchList();
        await using var host = await FlintApiHost.StartAsync(technic: _api);

        var response = await host.GetAsync("/api/v1/modplatform/technic/search?query=bevo");
        response.EnsureSuccessStatusCode();

        // the request carried the search path with the API build + escaped query
        Assert.Equal("/search", _server.LastPath);
        Assert.Equal("testbuild", _server.LastQuery["build"]);
        Assert.Equal("bevo", _server.LastQuery["q"]);

        var hits = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(2, hits.GetArrayLength()); // "vanilla" is excluded like the C++ model
        Assert.Equal("bevo", hits[0].GetProperty("id").GetString());
        Assert.Equal("Bevo's Tech Pack", hits[0].GetProperty("name").GetString());
        Assert.Equal("http://cdn.technicpack.net/bevo.png", hits[0].GetProperty("iconUrl").GetString());
        Assert.False(hits[0].TryGetProperty("displayName", out _));
        Assert.Equal("noicon", hits[1].GetProperty("id").GetString());
        Assert.False(hits[1].TryGetProperty("iconUrl", out _)); // the "null" placeholder -> omitted
    }

    [Fact]
    public async Task Trending_PassThrough_HitsTheTrendingPath()
    {
        SeedSearchList();
        await using var host = await FlintApiHost.StartAsync(technic: _api);

        var response = await host.GetAsync("/api/v1/modplatform/technic/trending");
        response.EnsureSuccessStatusCode();
        Assert.Equal("/trending", _server.LastPath);
        Assert.Equal("testbuild", _server.LastQuery["build"]);

        var hits = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(2, hits.GetArrayLength());
    }

    [Fact]
    public async Task Search_ServerUrl_Override_IsHonored_WithoutTouchingTheClient()
    {
        SeedSearchList();
        // a dead base on the injected client forces the query's serverUrl override to be
        // the only working route
        var api = new TechnicClient(new TechnicClientOptions { BaseUrl = "http://127.0.0.1:9/", ApiBuild = "testbuild" });
        await using var host = await FlintApiHost.StartAsync(technic: api);

        var response = await host.GetAsync(
            $"/api/v1/modplatform/technic/search?query=bevo&serverUrl={Uri.EscapeDataString(_server.Url + "/")}");
        response.EnsureSuccessStatusCode();
        Assert.Equal("/search", _server.LastPath);
        // the override is a per-request derived client: the injected client's base is untouched
        Assert.Equal("http://127.0.0.1:9/", api.Options.BaseUrl);
    }

    [Fact]
    public async Task Search_BadServerUrl_IsRejected()
    {
        SeedSearchList();
        await using var host = await FlintApiHost.StartAsync(technic: _api);

        var response = await host.GetAsync("/api/v1/modplatform/technic/search?query=bevo&serverUrl=nope");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_server_url", JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    // -------------------------------------------------------------- pack metadata

    [Fact]
    public async Task PackMetadata_ZipPack_HasSolderFlagFalse()
    {
        _server.PackMetaJson["zippack"] = new JsonObject
        {
            ["displayName"] = "Zip Pack",
            ["name"] = "zippack",
            ["url"] = "http://www.technicpack.net/download/zippack",
            ["minecraft"] = "1.12.2",
            ["platformUrl"] = "http://technicpack.net",
            ["user"] = "sarge",
            ["description"] = "A zip pack",
            ["version"] = "1.2.3",
        }.ToJsonString();
        await using var host = await FlintApiHost.StartAsync(technic: _api);

        var response = await host.GetAsync("/api/v1/modplatform/technic/modpack/zippack");
        response.EnsureSuccessStatusCode();
        Assert.Equal("/modpack/zippack", _server.LastPath);
        Assert.Equal("testbuild", _server.LastQuery["build"]);

        var pack = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("zippack", pack.GetProperty("slug").GetString());
        Assert.Equal("Zip Pack", pack.GetProperty("name").GetString());
        Assert.False(pack.GetProperty("isSolder").GetBoolean());
        Assert.Equal("http://www.technicpack.net/download/zippack", pack.GetProperty("url").GetString());
        Assert.Equal("1.12.2", pack.GetProperty("minecraftVersion").GetString());
        Assert.Equal("sarge", pack.GetProperty("author").GetString());
        Assert.Equal("A zip pack", pack.GetProperty("description").GetString());
        Assert.Equal("1.2.3", pack.GetProperty("currentVersion").GetString());
        // a zip pack only has the platform's "version" as its single version
        Assert.Equal("1.2.3", pack.GetProperty("versions")[0].GetString());
    }

    [Fact]
    public async Task PackMetadata_SolderPack_ListsBuilds()
    {
        _server.PackMetaJson["solderpack"] = new JsonObject
        {
            ["solder"] = _server.Url + "/",
            ["minecraft"] = "1.7.10",
        }.ToJsonString();
        _server.SolderPackJson["solderpack"] = new JsonObject
        {
            ["recommended"] = "1.1.0",
            ["latest"] = "1.2.0",
            ["builds"] = new JsonArray("1.0.0", "1.1.0", "1.2.0"),
        }.ToJsonString();
        await using var host = await FlintApiHost.StartAsync(technic: _api);

        var response = await host.GetAsync("/api/v1/modplatform/technic/modpack/solderpack");
        response.EnsureSuccessStatusCode();

        var pack = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.True(pack.GetProperty("isSolder").GetBoolean());
        // the solder URL is handed out without its trailing slashes (metadataLoaded)
        Assert.Equal(_server.Url, pack.GetProperty("url").GetString());
        Assert.Equal("1.7.10", pack.GetProperty("minecraftVersion").GetString());
        Assert.Equal(3, pack.GetProperty("versions").GetArrayLength());
        Assert.Equal("1.1.0", pack.GetProperty("recommended").GetString());
        Assert.Equal("1.2.0", pack.GetProperty("latest").GetString());
    }

    [Fact]
    public async Task PackMetadata_UnknownSlug_IsNotFound()
    {
        await using var host = await FlintApiHost.StartAsync(technic: _api);

        var response = await host.GetAsync("/api/v1/modplatform/technic/modpack/ghost");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("pack_not_found", JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    // ------------------------------------------------------------------- installs

    [Fact]
    public async Task Install_ZipUrl_EndToEnd_CommitsInstance()
    {
        // a bin/version.json-flavoured zip served by the stub, like the Platform "url" field
        var pack = new TechnicZipPackBuilder();
        var zipName = "zippack-1.2.3.zip";
        var zipPath = Path.Combine(Path.GetTempPath(), $"flint-technic-api-{Guid.NewGuid():N}.zip");
        Assert.True(pack.WriteZip(zipPath));
        _server.Downloads[zipName] = await File.ReadAllBytesAsync(zipPath);
        try
        {
            await using var host = await FlintApiHost.StartAsync(technic: _api);

            var post = await host.PostAsync("/api/v1/modplatform/technic/install", new
            {
                url = $"{_server.Url}/{zipName}",
                minecraftVersion = "1.20.1",
                name = "Zip Installed",
                group = "Technic",
                iconKey = "flint",
            });
            Assert.Equal(HttpStatusCode.Created, post.StatusCode);
            var created = JsonDocument.Parse(await post.Content.ReadAsStringAsync()).RootElement;
            var id = created.GetProperty("id").GetString();
            Assert.NotNull(id);
            Assert.Equal("Zip Installed", created.GetProperty("name").GetString());

            // the extracted pack landed in the committed instance's minecraft/ dir
            var staging = Path.Combine(host.DataDir, "instances", id);
            Assert.Equal("technic=1\n",
                await File.ReadAllTextAsync(Path.Combine(staging, "minecraft", "modpack", "options.txt")));
            // the version.json libraries became components
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(staging, "mmc-pack.json")));
            var components = doc.RootElement.GetProperty("components").EnumerateArray()
                .Select(c => (Uid: c.GetProperty("uid").GetString(),
                    Version: c.TryGetProperty("version", out var v) ? v.GetString() : null))
                .ToList();
            Assert.Contains(components, c => c is { Uid: "net.minecraft", Version: "1.20.1" });
            Assert.Contains(components, c => c is { Uid: "net.minecraftforge", Version: "42.0.0" });

            var list = JsonDocument.Parse(
                await (await host.GetAsync("/api/v1/instances")).Content.ReadAsStringAsync()).RootElement;
            var entry = list.EnumerateArray().Single(e => e.GetProperty("id").GetString() == id);
            Assert.Equal("Technic", entry.GetProperty("group").GetString());
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    [Fact]
    public async Task Install_Solder_Build_EndToEnd_DownloadsAndExtractsMods()
    {
        var modBytes = TechnicZipPackBuilder.BuildSolderModZip("mods/soldermod.jar",
            "solder mod payload"u8.ToArray());
        _server.SolderBuildJson["testpack/1.0.0"] = new JsonObject
        {
            ["minecraft"] = "1.20.1",
            ["mods"] = new JsonArray(
                new JsonObject
                {
                    ["name"] = "Solder Mod",
                    ["version"] = "1.0",
                    ["md5"] = Convert.ToHexString(MD5.HashData(modBytes))
                        .ToLowerInvariant(),
                    ["url"] = $"{_server.Url}/files/soldermod.zip",
                }),
        }.ToJsonString();
        _server.Downloads["soldermod.zip"] = modBytes;
        await using var host = await FlintApiHost.StartAsync(technic: _api);

        var post = await host.PostAsync("/api/v1/modplatform/technic/install", new
        {
            solder = _server.Url,
            slug = "testpack",
            build = "1.0.0",
        });
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var created = JsonDocument.Parse(await post.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("testpack", created.GetProperty("id").GetString()); // name defaults to the slug

        var staging = Path.Combine(host.DataDir, "instances", "testpack");
        Assert.Equal("solder mod payload",
            await File.ReadAllTextAsync(Path.Combine(staging, "minecraft", "mods", "soldermod.jar")));
        // the processor used the build's minecraft version
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(staging, "mmc-pack.json")));
        Assert.Contains(doc.RootElement.GetProperty("components").EnumerateArray(),
            c => c.GetProperty("uid").GetString() == "net.minecraft"
                 && c.GetProperty("version").GetString() == "1.20.1");
    }

    [Fact]
    public async Task Install_Solder_WithoutBuild_ResolvesTheRecommendedOne()
    {
        // the listing is fetched and the recommended build installed (the version box's
        // preselection)
        _server.SolderPackJson["defaultpack"] = new JsonObject
        {
            ["recommended"] = "1.1.0",
            ["latest"] = "1.2.0",
            ["builds"] = new JsonArray("1.0.0", "1.1.0", "1.2.0"),
        }.ToJsonString();
        _server.SolderBuildJson["defaultpack/1.1.0"] = new JsonObject
        {
            ["minecraft"] = "1.16.5",
            ["mods"] = new JsonArray(),
        }.ToJsonString();
        await using var host = await FlintApiHost.StartAsync(technic: _api);

        var post = await host.PostAsync("/api/v1/modplatform/technic/install", new
        {
            solder = _server.Url,
            slug = "defaultpack",
            name = "Default Build",
        });
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var id = JsonDocument.Parse(await post.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString();
        Assert.NotNull(id);
        Assert.Equal("Default Build", id);

        using var doc = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(host.DataDir, "instances", id, "mmc-pack.json")));
        Assert.Contains(doc.RootElement.GetProperty("components").EnumerateArray(),
            c => c.GetProperty("uid").GetString() == "net.minecraft"
                 && c.GetProperty("version").GetString() == "1.16.5");
    }

    [Fact]
    public async Task Install_Solder_UnknownBuild_ReturnsCreationFailed()
    {
        await using var host = await FlintApiHost.StartAsync(technic: _api);

        var post = await host.PostAsync("/api/v1/modplatform/technic/install", new
        {
            solder = _server.Url,
            slug = "ghostpack",
            build = "9.9.9", // the stub 404s the build JSON
        });
        Assert.Equal(HttpStatusCode.InternalServerError, post.StatusCode);
        var error = JsonDocument.Parse(await post.Content.ReadAsStringAsync()).RootElement
            .GetProperty("error");
        Assert.Equal("creation_failed", error.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));

        // nothing was committed
        Assert.False(Directory.Exists(Path.Combine(host.DataDir, "instances", "ghostpack")));
    }

    [Fact]
    public async Task Install_ServerUrl_Override_IsHonored_WithoutTouchingTheClient()
    {
        // search-driven installs need the Platform base; a zip install via serverUrl proves
        // the per-request client derivation without touching the injected one
        var pack = new TechnicZipPackBuilder();
        var zipName = "override-pack.zip";
        var zipPath = Path.Combine(Path.GetTempPath(), $"flint-technic-api-{Guid.NewGuid():N}.zip");
        Assert.True(pack.WriteZip(zipPath));
        _server.Downloads[zipName] = await File.ReadAllBytesAsync(zipPath);
        try
        {
            var api = new TechnicClient(new TechnicClientOptions { BaseUrl = "http://127.0.0.1:9/", ApiBuild = "testbuild" });
            await using var host = await FlintApiHost.StartAsync(technic: api);

            var post = await host.PostAsync("/api/v1/modplatform/technic/install", new
            {
                url = $"{_server.Url}/{zipName}",
                minecraftVersion = "1.19.2",
                name = "Override Installed",
                serverUrl = _server.Url + "/",
            });
            Assert.Equal(HttpStatusCode.Created, post.StatusCode);
            Assert.True(File.Exists(Path.Combine(host.DataDir, "instances", "Override Installed",
                "minecraft", "modpack", "options.txt")));
            // the override never leaked into the injected client (the bogus base is still in place)
            Assert.Equal("http://127.0.0.1:9/", api.Options.BaseUrl);
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    [Fact]
    public async Task Install_Validation_Errors()
    {
        await using var host = await FlintApiHost.StartAsync(technic: _api);

        // neither shape
        var empty = await host.PostAsync("/api/v1/modplatform/technic/install", new { });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal("invalid_body", JsonDocument.Parse(await empty.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error").GetProperty("code").GetString());

        // both shapes at once
        var both = await host.PostAsync("/api/v1/modplatform/technic/install", new
        {
            url = "http://example.com/pack.zip",
            solder = "http://example.com/solder",
            slug = "pack",
        });
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
        Assert.Equal("invalid_body", JsonDocument.Parse(await both.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error").GetProperty("code").GetString());

        // solder without slug
        var noSlug = await host.PostAsync("/api/v1/modplatform/technic/install",
            new { solder = "http://example.com/solder" });
        Assert.Equal(HttpStatusCode.BadRequest, noSlug.StatusCode);

        // bad zip url
        var badUrl = await host.PostAsync("/api/v1/modplatform/technic/install",
            new { url = "not-a-url" });
        Assert.Equal(HttpStatusCode.BadRequest, badUrl.StatusCode);
        Assert.Equal("invalid_url", JsonDocument.Parse(await badUrl.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error").GetProperty("code").GetString());

        // bad serverUrl
        var badServer = await host.PostAsync("/api/v1/modplatform/technic/install", new
        {
            url = "http://example.com/pack.zip",
            serverUrl = "not a url",
        });
        Assert.Equal(HttpStatusCode.BadRequest, badServer.StatusCode);
        Assert.Equal("invalid_server_url", JsonDocument.Parse(await badServer.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}
