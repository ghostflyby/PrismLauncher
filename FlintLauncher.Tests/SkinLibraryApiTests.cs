// SPDX-License-Identifier: GPL-3.0-only
// API tests for the local skin library (DATA D6 plan B): the GET/POST/DELETE /skins
// surface over the SkinsDir setting (default "skins" under the home layout's data root)
// and the apply endpoint's guard rails (unknown account / missing skin / missing texture).
// The happy-path apply is covered by the SkinApiEndpointsTests stub machinery shape — here
// the local CRUD and the composable guards are exercised without a Minecraft-services stub.
using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using FlintLauncher.Web;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace FlintLauncher.Tests;

public class SkinLibraryApiTests : IAsyncDisposable
{
    private readonly string _homeDir = Path.Combine(Path.GetTempPath(), $"flint-skinlib-{Guid.NewGuid():N}");
    private WebApplication? _app;
    private HttpClient? _client;

    private string DataDir => Path.Combine(_homeDir, "data");
    private string SkinsDir => Path.Combine(DataDir, "skins");

    private async Task<HttpClient> StartAsync()
    {
        Directory.CreateDirectory(_homeDir);
        Directory.CreateDirectory(DataDir);
        var token = Guid.NewGuid().ToString("N");
        _app = WebHostBuilder.BuildWebApplication(
        [
            "--home-dir", _homeDir,
            "--api-token", token,
            "--urls", "http://127.0.0.1:0",
        ]);
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
        _client.DefaultRequestHeaders.Add("X-Api-Token", token);
        return _client;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        _client?.Dispose();
        try
        {
            Directory.Delete(_homeDir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static readonly byte[] PngBytes =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, // PNG magic; the library does not
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, // decode the texture (QImage not
        0x00, 0x00, 0x00, 0x40, 0x00, 0x00, 0x00, 0x40, // ported) — bytes just get stored
    ];

    private static MultipartFormDataContent StoreForm(string name, string model)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(PngBytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "skin.png");
        form.Add(new StringContent(name), "name");
        form.Add(new StringContent(model), "model");
        return form;
    }

    [Fact]
    public async Task Store_List_Delete_RoundTrip()
    {
        var client = await StartAsync();

        // empty library
        var empty = await client.GetAsync("/api/v1/skins");
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        Assert.Equal(0, JsonDocument.Parse(await empty.Content.ReadAsStringAsync())
            .RootElement.GetArrayLength());

        // store (name + model from the form)
        var stored = await client.PostAsync("/api/v1/skins", StoreForm("Test Skin", "slim"));
        Assert.Equal(HttpStatusCode.Created, stored.StatusCode);
        var storedView = JsonDocument.Parse(await stored.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Test Skin", storedView.GetProperty("name").GetString());

        // the texture landed in the SkinsDir (the setting's default, data-root-relative)
        Assert.True(File.Exists(Path.Combine(SkinsDir, "Test Skin.png")));
        Assert.True(File.Exists(Path.Combine(SkinsDir, "index.json")));

        // listed with the stored variant + file presence
        var list = JsonDocument.Parse(await client.GetStringAsync("/api/v1/skins")).RootElement;
        var entry = list.EnumerateArray().Single();
        Assert.Equal("Test Skin", entry.GetProperty("name").GetString());
        Assert.Equal("SLIM", entry.GetProperty("model").GetString());
        Assert.True(entry.GetProperty("fileExists").GetBoolean());

        // single-skin read agrees
        var one = JsonDocument.Parse(await client.GetStringAsync("/api/v1/skins/Test%20Skin"))
            .RootElement;
        Assert.Equal("Test Skin", one.GetProperty("name").GetString());

        // duplicate name is a conflict (no overwrite; delete first to replace)
        var duplicate = await client.PostAsync("/api/v1/skins", StoreForm("Test Skin", "classic"));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("SLIM", JsonDocument.Parse(await client.GetStringAsync("/api/v1/skins"))
            .RootElement.EnumerateArray().Single().GetProperty("model").GetString());

        // delete removes entry + texture
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync("/api/v1/skins/Test%20Skin")).StatusCode);
        Assert.False(File.Exists(Path.Combine(SkinsDir, "Test Skin.png")));
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync("/api/v1/skins/Test%20Skin")).StatusCode);

        // deleting a missing skin is a 404
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.DeleteAsync("/api/v1/skins/Test%20Skin")).StatusCode);
    }

    [Fact]
    public async Task Apply_Guards_And_Composition()
    {
        var client = await StartAsync();

        // unknown skin
        var missingSkin = await client.PostAsJsonAsync("/api/v1/skins/nope/apply",
            new { profileId = "abc" });
        Assert.Equal(HttpStatusCode.NotFound, missingSkin.StatusCode);

        // store one, remove the texture behind the library's back
        await client.PostAsync("/api/v1/skins", StoreForm("Ghost", "classic"));
        File.Delete(Path.Combine(SkinsDir, "Ghost.png"));
        var missingTexture = await client.PostAsJsonAsync("/api/v1/skins/Ghost/apply",
            new { profileId = "abc" });
        Assert.Equal(HttpStatusCode.Conflict, missingTexture.StatusCode);
        Assert.Equal("skin_texture_missing", JsonDocument.Parse(
            await missingTexture.Content.ReadAsStringAsync()).RootElement
            .GetProperty("error").GetProperty("code").GetString());

        // a stored texture + unknown account
        await client.PostAsync("/api/v1/skins", StoreForm("Real", "classic"));
        var unknownAccount = await client.PostAsJsonAsync("/api/v1/skins/Real/apply",
            new { profileId = "no-such-profile" });
        Assert.Equal(HttpStatusCode.BadRequest, unknownAccount.StatusCode);
        Assert.Equal("unknown_account", JsonDocument.Parse(
            await unknownAccount.Content.ReadAsStringAsync()).RootElement
            .GetProperty("error").GetProperty("code").GetString());

        // missing body
        var noBody = await client.PostAsync("/api/v1/skins/Real/apply",
            new StringContent("null", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, noBody.StatusCode);
    }
}
