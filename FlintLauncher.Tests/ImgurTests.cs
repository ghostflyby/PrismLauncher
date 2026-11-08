// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Tests for the imgur screenshot layer (launcher/screenshots/ImgurAPI.{h,cpp}, the unified
// shape that replaced ImgurUpload/ImgurAlbumCreation): the exact multipart/form shape and
// headers, the JSON response parsing into the ScreenShot metadata, and every failure path.
// Each test builds its own ImgurClient pointed at the stub.

using System.Text;
using System.Text.Json;
using FlintLauncher.Core.Screenshots;
using FlintLauncher.ExternalApi.Imgur;
using TruePath;
using Xunit;

namespace FlintLauncher.Tests;

public class ImgurTests : IDisposable
{
    private readonly PeripheralStubServer _server = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"flint-imgur-{Guid.NewGuid():N}");

    /// <summary>An upload of the screenshot through a client at the given base and client id
    /// (the option defaults when null).</summary>
    private static ImgurUpload MakeUpload(ScreenShot shot, string? baseUrl = null, string? clientId = null) =>
        new(shot, Client(baseUrl, clientId), TestNet.Http);

    /// <summary>An album over the screenshots through a client at the given base and client
    /// id (the option defaults when null).</summary>
    private static ImgurAlbumCreation MakeAlbum(ImgurAlbumResult result, IReadOnlyList<ScreenShot> screenshots,
        string? baseUrl = null, string? clientId = null) =>
        new(result, screenshots, Client(baseUrl, clientId), TestNet.Http);

    private static ImgurClient Client(string? baseUrl, string? clientId)
    {
        var options = new ImgurClientOptions();
        if (baseUrl is not null)
        {
            options.BaseUrl = baseUrl;
        }

        if (clientId is not null)
        {
            options.ClientId = clientId;
        }

        return new ImgurClient(options);
    }

    public void Dispose()
    {
        _server.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException) { }
    }

    private AbsolutePath TempPng(byte[] bytes)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "2026-09-13_10.11.12.png");
        File.WriteAllBytes(path, bytes);
        return new AbsolutePath(path);
    }

    // ---------------------------------------------------------------- ScreenShot model

    [Fact]
    public void ScreenShot_FileInfo_Semantics()
    {
        // compared against the input path itself — a nonexistent screenshot under the
        // sandbox temp dir stands in for the real file, absolute and per-OS on every
        // platform
        var path = new AbsolutePath(_dir) / "instances" / "a" / "screens" / "2026-09-13_10.11.12.png";
        var shot = new ScreenShot(path);
        Assert.Equal(path.Value, shot.FilePath.Value);
        // QFileInfo::fileName()
        Assert.Equal("2026-09-13_10.11.12.png", shot.FileName());
        // QFileInfo::baseName(): everything before the first '.'
        Assert.Equal("2026-09-13_10", shot.BaseName());
        Assert.Equal("", shot.Url);
        Assert.Equal("", shot.ImgurId);
        Assert.Equal("", shot.ImgurDeleteHash);
    }

    // ---------------------------------------------------------------- ImgurUpload

    [Fact]
    public void Upload_Endpoints_And_Defaults()
    {
        var upload = new ImgurClient().Images.Upload([1, 2, 3], "shot.png", "shot");
        Assert.Equal("https://api.imgur.com/3/image", upload.Url);
        Assert.Equal(new ImgurClientOptions().BaseUrl + "image", upload.Url);
    }

    [Fact]
    public async Task Upload_Request_Shape_Headers_And_Body()
    {
        var imageData = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0xFF };
        var path = TempPng(imageData);
        _server.Responder = _ => (200, """{"success":true,"data":{"id":"abc123","link":"https://i.imgur.com/abc123.png","deletehash":"del789"}}""", "OK");

        var shot = new ScreenShot(path);
        var upload = MakeUpload(shot, _server.BaseUrl + "/", "client-id-42");
        await upload.UploadAsync();

        var request = _server.Request("/image");
        Assert.NotNull(request);
        Assert.Equal("POST", request.Method);
        Assert.Equal("Client-ID client-id-42", request.Headers["Authorization"]);
        Assert.Equal("application/json", request.Headers["Accept"]);

        // the upload's fields: the image under its file name, the type word, and the
        // QFileInfo::baseName() as the title
        var boundary = MultipartAssertions.BoundaryFromContentType(request.Headers["Content-Type"]);
        var parts = MultipartAssertions.SplitParts(request.Body, boundary);
        Assert.Equal(3, parts.Count);
        Assert.Equal(imageData, MultipartAssertions.Field(parts, "image")!);
        Assert.Equal("file", Encoding.UTF8.GetString(MultipartAssertions.Field(parts, "type")!));
        Assert.Equal("2026-09-13_10", Encoding.UTF8.GetString(MultipartAssertions.Field(parts, "title")!));
    }

    [Fact]
    public async Task Upload_Parses_Response_Into_ScreenShot()
    {
        var path = TempPng([1]);
        _server.Responder = _ => (200,
            """{"status":200,"success":true,"data":{"id":"abc123","link":"https://i.imgur.com/abc123.png","deletehash":"del789"}}""",
            "OK");

        var shot = new ScreenShot(path);
        var upload = MakeUpload(shot, _server.BaseUrl + "/", "cid");
        await upload.UploadAsync();

        Assert.Equal("abc123", shot.ImgurId);
        Assert.Equal("https://i.imgur.com/abc123.png", shot.Url);
        Assert.Equal("del789", shot.ImgurDeleteHash);
    }

    [Fact]
    public async Task Upload_Fails_When_Success_Is_False()
    {
        var path = TempPng([1]);
        _server.Responder = _ => (200, """{"success":false,"status":403,"data":{"error":"rate limit"}}""", "OK");

        var shot = new ScreenShot(path);
        var upload = MakeUpload(shot, _server.BaseUrl + "/", "cid");
        try
        {
            await upload.UploadAsync();
            Assert.Fail("the upload should have failed");
        }
        catch (ImgurApiException e)
        {
            Assert.Equal("Screenshot was not uploaded successfully", e.Message);
        }
    }

    [Fact]
    public async Task Upload_Fails_On_Invalid_Json()
    {
        var path = TempPng([1]);
        _server.Responder = _ => (200, "<html>gateway error</html>", "OK");

        var shot = new ScreenShot(path);
        var upload = MakeUpload(shot, _server.BaseUrl + "/", "cid");
        try
        {
            await upload.UploadAsync();
            Assert.Fail("the upload should have failed");
        }
        catch (ImgurApiException e)
        {
            Assert.Equal("Invalid json reply", e.Message);
        }
    }

    [Fact]
    public async Task Upload_Fails_On_Http_Error_Status()
    {
        var path = TempPng([1]);
        _server.Responder = _ => (429, """{"success":false}""", "Too Many Requests");

        var shot = new ScreenShot(path);
        var upload = MakeUpload(shot, _server.BaseUrl + "/", "cid");
        try
        {
            await upload.UploadAsync();
            Assert.Fail("the upload should have failed");
        }
        catch (ImgurApiException e)
        {
            Assert.Contains("429", e.Message);
        }
    }

    [Fact]
    public async Task Upload_Fails_When_File_Missing()
    {
        var shot = new ScreenShot(new AbsolutePath(Path.Combine(_dir, "does-not-exist.png")));
        var upload = MakeUpload(shot, _server.BaseUrl + "/", "cid");
        try
        {
            await upload.UploadAsync();
            Assert.Fail("the upload should have failed");
        }
        catch (InvalidOperationException e)
        {
            Assert.StartsWith("Could not open file ", e.Message);
        }
        Assert.Empty(_server.Requests);
    }

    // ---------------------------------------------------------------- ImgurAlbumCreation

    private static ScreenShot ShotWithHash(string deleteHash) => new(new AbsolutePath(Path.Combine(Path.GetTempPath(), "unused.png")))
    {
        ImgurId = "id-" + deleteHash,
        Url = "https://i.imgur.com/" + deleteHash + ".png",
        ImgurDeleteHash = deleteHash,
    };

    [Fact]
    public void Album_Endpoint_Default()
    {
        var album = new ImgurClient().Albums.Create(["h1"]);
        Assert.Equal("https://api.imgur.com/3/album", album.Url);
        Assert.Equal(new ImgurClientOptions().BaseUrl + "album", album.Url);
    }

    [Fact]
    public async Task Album_Request_Shape_And_Response()
    {
        _server.Responder = _ => (200, """{"status":200,"success":true,"data":{"id":"aB1cD2e","deletehash":"dhXyZ"}}""", "OK");

        var result = new ImgurAlbumResult();
        var album = MakeAlbum(result,
        [
            ShotWithHash("hash-one"), ShotWithHash("hash-two"), ShotWithHash("hash-three"),
        ], _server.BaseUrl + "/", "cid");
        await album.CreateAsync();

        Assert.Equal("dhXyZ", result.DeleteHash);
        Assert.Equal("aB1cD2e", result.Id);

        var request = _server.Request("/album");
        Assert.NotNull(request);
        Assert.Equal("POST", request.Method);
        Assert.Equal("Client-ID cid", request.Headers["Authorization"]);
        Assert.Equal("application/json", request.Headers["Accept"]);
    }

    [Fact]
    public async Task Album_Fails_When_Success_Is_False()
    {
        _server.Responder = _ => (200, """{"success":false,"status":403}""", "OK");

        var result = new ImgurAlbumResult();
        var album = MakeAlbum(result, [ShotWithHash("h")], _server.BaseUrl + "/", "cid");
        try
        {
            await album.CreateAsync();
            Assert.Fail("the album should have failed");
        }
        catch (ImgurApiException e)
        {
            Assert.Equal("Failed to create album", e.Message);
        }
        Assert.Equal("", result.DeleteHash);
        Assert.Equal("", result.Id);
    }

    [Fact]
    public async Task Album_Fails_On_Invalid_Json()
    {
        _server.Responder = _ => (200, "nope", "OK");

        var result = new ImgurAlbumResult();
        var album = MakeAlbum(result, [ShotWithHash("h")], _server.BaseUrl + "/", "cid");
        try
        {
            await album.CreateAsync();
            Assert.Fail("the album should have failed");
        }
        catch (ImgurApiException e)
        {
            Assert.Equal("Invalid json reply", e.Message);
        }
    }

    [Fact]
    public async Task Album_Fails_On_Http_Error_Status()
    {
        _server.Responder = _ => (500, "boom", "Internal Server Error");

        var result = new ImgurAlbumResult();
        var album = MakeAlbum(result, [ShotWithHash("h")], _server.BaseUrl + "/", "cid");
        try
        {
            await album.CreateAsync();
            Assert.Fail("the album should have failed");
        }
        catch (ImgurApiException e)
        {
            Assert.Contains("500", e.Message);
        }
    }

    // The C++ reads data.id/data.link/data.deletehash via QJsonObject::value().toString(); a
    // missing "data" object keeps the fields empty (no crash).
    [Fact]
    public async Task Upload_With_Missing_Data_Object_Leaves_Fields_Empty()
    {
        var path = TempPng([1]);
        _server.Responder = _ => (200, """{"success":true}""", "OK");

        var shot = new ScreenShot(path);
        var upload = MakeUpload(shot, _server.BaseUrl + "/", "cid");
        await upload.UploadAsync();

        Assert.Equal("", shot.ImgurId);
        Assert.Equal("", shot.Url);
        Assert.Equal("", shot.ImgurDeleteHash);
    }

    [Fact]
    public void Json_Response_Shape_Matches_The_Cpp_Paths()
    {
        // documents the exact JSON paths the sinks read: success / data.id / data.link /
        // data.deletehash (upload) and success / data.deletehash / data.id (album)
        using var doc = JsonDocument.Parse(
            """{"success":true,"data":{"id":"i","link":"l","deletehash":"d"}}""");
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("i", doc.RootElement.GetProperty("data").GetProperty("id").GetString());
        Assert.Equal("l", doc.RootElement.GetProperty("data").GetProperty("link").GetString());
        Assert.Equal("d", doc.RootElement.GetProperty("data").GetProperty("deletehash").GetString());
    }
}
