// SPDX-License-Identifier: GPL-3.0-only
// API-level tests for the account skin endpoints (fix-2c): the profile skin read, the
// SkinUpload multipart PUT (delegating to the same core SkinUpload the SkinApiTests assert
// the byte shape of), the skin/cape removals and the CapeChange equip/remove semantics —
// against the shared AuthStubServer, which records every request.
//
// The skins calls go through the runtime's injected AuthEndpointsOptions (runtime.Auth), so
// every host starts with a stub-pointed options instance. The host fixture seeds accounts.json
// BEFORE the app builds (the runtime loads the account list at startup), which gives an
// MSA-shaped account with real profile skin data plus a token the stub can assert on.

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.Auth;
using FlintLauncher.Web;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace FlintLauncher.Tests;

// ---------------------------------------------------------------------------- host fixture

internal sealed class SkinApiHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly string _previousCwd;

    private SkinApiHost(WebApplication app, HttpClient client, string homeDir, string dataDir,
        string previousCwd)
    {
        _app = app;
        Client = client;
        HomeDir = homeDir;
        DataDir = dataDir;
        _previousCwd = previousCwd;
    }

    public HttpClient Client { get; }

    /// <summary>The temp home root (passed as --home-dir; cleaned up on dispose).</summary>
    public string HomeDir { get; }

    /// <summary>The data root (home layout: &lt;home&gt;/data).</summary>
    public string DataDir { get; }

    public Task<HttpResponseMessage> GetAsync(string path) => Client.GetAsync(path);

    public Task<HttpResponseMessage> DeleteAsync(string path) => Client.DeleteAsync(path);

    /// <summary>Builds the real app over a temp home whose data root is pre-seeded with
    /// <paramref name="seed"/> files (data-root-relative path -> content), so startup-time
    /// loads (accounts.json) see them.</summary>
    public static async Task<SkinApiHost> StartAsync(IReadOnlyDictionary<string, string>? seed = null,
        AuthEndpointsOptions? auth = null)
    {
        var homeDir = Path.Combine(Path.GetTempPath(), $"flint-skinapi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(homeDir);
        var dataDir = Path.Combine(homeDir, "data");
        Directory.CreateDirectory(dataDir);
        foreach (var (name, content) in seed ?? new Dictionary<string, string>())
        {
            await File.WriteAllTextAsync(Path.Combine(dataDir, name), content);
        }
        var previousCwd = Environment.CurrentDirectory;
        // S2: the token is host-injected (--api-token) — no token file exists to read
        var token = Guid.NewGuid().ToString("N");
        var app = WebHostBuilder.BuildWebApplication(
        [
            "--home-dir", homeDir,
            "--api-token", token,
            "--urls", "http://127.0.0.1:0",
        ], auth: auth);
        await app.StartAsync();
        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        client.DefaultRequestHeaders.Add("X-Api-Token", token);
        return new SkinApiHost(app, client, homeDir, dataDir, previousCwd);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        try
        {
            await _app.StopAsync();
        }
        finally
        {
            await _app.DisposeAsync();
            var restoreTo = Directory.Exists(_previousCwd) ? _previousCwd : Path.GetTempPath();
            Environment.CurrentDirectory = restoreTo;
            try
            {
                Directory.Delete(HomeDir, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}

// ---------------------------------------------------------------------------------- tests

public class SkinApiEndpointsTests : IDisposable
{
    private readonly AuthStubServer _server = new();
    private readonly AuthEndpointsOptions _auth = new();

    /// <summary>An MSA-shaped account with profile skin + cape data and a stub token; the
    /// profile id is fixed so tests can address it.</summary>
    public const string ProfileId = "b50ad385829d3141a2167e7d7539ba7f";

    public static string AccountsJson => new JsonObject
    {
        ["formatVersion"] = 3,
        ["accounts"] = new JsonArray(
            new JsonObject
            {
                ["type"] = "MSA",
                ["ygg"] = new JsonObject
                {
                    ["token"] = "SKIN_TOKEN_STUB",
                },
                ["profile"] = new JsonObject
                {
                    ["id"] = ProfileId,
                    ["name"] = "SkinTester",
                    ["skin"] = new JsonObject
                    {
                        ["id"] = "skin-id",
                        // the stored texture URL carries the http scheme the loader rewrites
                        ["url"] = "http://textures.minecraft.net/texture/abc",
                        ["variant"] = "CLASSIC",
                    },
                    ["capes"] = new JsonArray(
                        new JsonObject
                        {
                            ["id"] = "cape-id",
                            ["url"] = "https://textures.minecraft.net/cape.png",
                            ["alias"] = "Migrator",
                        }),
                    ["cape"] = "cape-id",
                },
            }),
    }.ToJsonString();

    public SkinApiEndpointsTests()
    {
        _auth.MinecraftServices = _server.BaseUrl;
    }

    public void Dispose()
    {
        _server.Dispose();
    }

    private Task<SkinApiHost> SeededHost() => SkinApiHost.StartAsync(
        new Dictionary<string, string> { ["accounts.json"] = AccountsJson }, auth: _auth);

    private static MultipartFormDataContent Multipart(params (string Name, string Content)[] fields)
    {
        var form = new MultipartFormDataContent();
        foreach (var (name, content) in fields)
        {
            form.Add(new StringContent(content), name);
        }
        return form;
    }

    // ------------------------------------------------------------------- skin read

    [Fact]
    public async Task Get_Skin_Returns_The_Profile_Skin_Data()
    {
        await using var host = await SeededHost();

        var response = await host.GetAsync($"/api/v1/accounts/{ProfileId}/skin");
        response.EnsureSuccessStatusCode();
        var skin = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        // the stored http texture URL is rewritten to https by the account layer
        Assert.Equal("https://textures.minecraft.net/texture/abc", skin.GetProperty("url").GetString());
        Assert.Equal("CLASSIC", skin.GetProperty("variant").GetString());
    }

    [Fact]
    public async Task Get_Skin_Unknown_Account_Is_NotFound()
    {
        await using var host = await SeededHost();

        var response = await host.GetAsync("/api/v1/accounts/ghost/skin");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("account_not_found", JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    // ----------------------------------------------------------------- skin upload

    [Fact]
    public async Task Put_Skin_Runs_The_Core_Upload_With_The_Multipart_Shape()
    {
        await using var host = await SeededHost();

        using var form = Multipart(("model", "slim"));
        var png = "PNGDATA-0123456789-abcdef"u8.ToArray();
        var fileContent = new ByteArrayContent(png);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(fileContent, "file", "skin.png");

        var response = await host.Client.PutAsync($"/api/v1/accounts/{ProfileId}/skin", form);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // the core SkinUpload ran: the stub saw the single multipart POST with the token
        var recorded = _server.Request("POST", "/minecraft/profile/skins");
        Assert.NotNull(recorded);
        Assert.StartsWith("Bearer SKIN_TOKEN_STUB", recorded.Headers["Authorization"]);
        var contentType = recorded.Headers["Content-Type"];
        Assert.StartsWith("multipart/form-data; boundary=", contentType);
        var boundary = contentType["multipart/form-data; boundary=".Length..];
        var body = recorded.Body;
        Assert.Contains($"""
            --{boundary}
            Content-Type: image/png
            Content-Disposition: form-data; name="file"; filename="skin.png"

            """.ReplaceLineEndings("\r\n"), body);
        Assert.Contains($"""

            --{boundary}
            Content-Disposition: form-data; name="variant"

            slim
            --{boundary}--
            """.ReplaceLineEndings("\r\n"), body);
    }

    [Fact]
    public async Task Put_Skin_Defaults_The_Model_To_Classic()
    {
        await using var host = await SeededHost();

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent([1, 2, 3]);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(fileContent, "file", "skin.png");

        var response = await host.Client.PutAsync($"/api/v1/accounts/{ProfileId}/skin", form);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var recorded = _server.Request("POST", "/minecraft/profile/skins");
        Assert.NotNull(recorded);
        var boundary = recorded.Headers["Content-Type"]["multipart/form-data; boundary=".Length..];
        Assert.Contains($"name=\"variant\"\r\n\r\nclassic\r\n--{boundary}--", recorded.Body);
    }

    [Fact]
    public async Task Put_Skin_Validation_Errors()
    {
        await using var host = await SeededHost();

        // missing file part
        using (var noFile = Multipart(("model", "classic")))
        {
            var missing = await host.Client.PutAsync($"/api/v1/accounts/{ProfileId}/skin", noFile);
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
            Assert.Equal("invalid_body", JsonDocument.Parse(await missing.Content.ReadAsStringAsync())
                .RootElement.GetProperty("error").GetProperty("code").GetString());
        }

        // unknown model
        using (var badModel = Multipart(("model", "round")))
        {
            var fileContent = new ByteArrayContent([1, 2, 3]);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            badModel.Add(fileContent, "file", "skin.png");
            var invalid = await host.Client.PutAsync($"/api/v1/accounts/{ProfileId}/skin", badModel);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Equal("invalid_model", JsonDocument.Parse(await invalid.Content.ReadAsStringAsync())
                .RootElement.GetProperty("error").GetProperty("code").GetString());
        }

        // unknown account wins over the body validation
        var notFound = await host.Client.PutAsync("/api/v1/accounts/ghost/skin", Multipart());
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
    }

    [Fact]
    public async Task Put_Skin_Server_Failure_Surfaces_The_FailReason()
    {
        // point the Minecraft services hook at a dead path (the stub 404s there)
        var dead = new AuthEndpointsOptions { MinecraftServices = _server.BaseUrl + "/nowhere" };
        await using var host = await SkinApiHost.StartAsync(
            new Dictionary<string, string> { ["accounts.json"] = AccountsJson }, auth: dead);

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent([1, 2, 3]);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(fileContent, "file", "skin.png");

        var response = await host.Client.PutAsync($"/api/v1/accounts/{ProfileId}/skin", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement
            .GetProperty("error");
        Assert.Equal("skin_upload_failed", error.GetProperty("code").GetString());
        Assert.Contains("404", error.GetProperty("message").GetString());
    }

    // ----------------------------------------------------------------- skin delete

    [Fact]
    public async Task Delete_Skin_Calls_The_Active_Skin_Endpoint()
    {
        await using var host = await SeededHost();

        var response = await host.DeleteAsync($"/api/v1/accounts/{ProfileId}/skin");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var recorded = _server.Request("DELETE", "/minecraft/profile/skins/active");
        Assert.NotNull(recorded);
        Assert.Equal("Bearer SKIN_TOKEN_STUB", recorded.Headers["Authorization"]);
    }

    // ------------------------------------------------------------------ cape equip

    [Fact]
    public async Task Put_Cape_Equips_By_Id()
    {
        await using var host = await SeededHost();

        using var form = Multipart(("capeId", "1234-abcd"));
        var response = await host.Client.PutAsync($"/api/v1/accounts/{ProfileId}/cape", form);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var recorded = _server.Request("PUT", "/minecraft/profile/capes/active");
        Assert.NotNull(recorded);
        Assert.Equal("Bearer SKIN_TOKEN_STUB", recorded.Headers["Authorization"]);
        Assert.Equal("""{"capeId":"1234-abcd"}""", recorded.Body);
    }

    [Fact]
    public async Task Put_Cape_File_Only_Is_Rejected()
    {
        await using var host = await SeededHost();

        // the Minecraft services API (and the ported CapeChange) only equips by id; a
        // file-only body cannot be turned into a request
        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent([1, 2, 3]);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(fileContent, "file", "cape.png");

        var response = await host.Client.PutAsync($"/api/v1/accounts/{ProfileId}/cape", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_body", JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Null(_server.Request("PUT", "/minecraft/profile/capes/active"));
    }

    // ------------------------------------------------------------------ cape remove

    [Fact]
    public async Task Delete_Cape_Turns_Into_The_Delete_Request()
    {
        await using var host = await SeededHost();

        // CapeChange's delete semantics: the empty cape id becomes a bare DELETE
        var response = await host.DeleteAsync($"/api/v1/accounts/{ProfileId}/cape");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var recorded = _server.Request("DELETE", "/minecraft/profile/capes/active");
        Assert.NotNull(recorded);
        Assert.Equal("Bearer SKIN_TOKEN_STUB", recorded.Headers["Authorization"]);
        Assert.Null(_server.Request("PUT", "/minecraft/profile/capes/active"));
    }

    [Fact]
    public async Task Skin_Routes_Unknown_Account_Are_NotFound()
    {
        await using var host = await SeededHost();

        Assert.Equal(HttpStatusCode.NotFound,
            (await host.DeleteAsync("/api/v1/accounts/ghost/skin")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await host.DeleteAsync("/api/v1/accounts/ghost/cape")).StatusCode);
        using var form = Multipart(("capeId", "x"));
        Assert.Equal(HttpStatusCode.NotFound,
            (await host.Client.PutAsync("/api/v1/accounts/ghost/cape", form)).StatusCode);
    }
}
