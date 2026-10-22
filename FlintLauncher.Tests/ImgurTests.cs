// SPDX-License-Identifier: GPL-3.0-only
// Tests for the imgur screenshot layer (launcher/screenshots/ImgurAPI.{h,cpp}, the unified
// shape that replaced ImgurUpload/ImgurAlbumCreation): the exact multipart/form shape and
// headers, the JSON response parsing into the ScreenShot metadata, and every failure path.
// The factories are instance methods (the client rides an injected NetStack); each suite
// builds its own ImgurApi.

using System.Text;
using System.Text.Json;
using FlintLauncher.Core;
using FlintLauncher.Core.Screenshots;
using FlintLauncher.Core.Tasks;
using TruePath;
using Xunit;

namespace FlintLauncher.Tests;

public class ImgurTests : IDisposable
{
    private readonly ImgurApi _imgur = new();
    private readonly PeripheralStubServer _server = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"flint-imgur-{Guid.NewGuid():N}");

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
        var shot = new ScreenShot(new AbsolutePath("/instances/a/screens/2026-09-13_10.11.12.png"));
        Assert.Equal("/instances/a/screens/2026-09-13_10.11.12.png", shot.FilePath.Value);
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
        // BuildConfig.IMGUR_BASE_URL + "image"; the defaults come from FlintConfig
        var shot = new ScreenShot(TempPng([1, 2, 3]));
        var upload = _imgur.MakeUpload(shot);
        Assert.Equal("https://api.imgur.com/3/image", upload.EndpointUrl);
        Assert.Equal(FlintConfig.Default.ImgurBaseUrl + "image", upload.EndpointUrl);
        Assert.Equal(FlintConfig.Default.ImgurBaseUrl + "image", ImgurApi.UploadUrl());
    }

    [Fact]
    public async Task Upload_Request_Shape_Headers_And_Body()
    {
        var imageData = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0xFF };
        var path = TempPng(imageData);
        _server.Responder = _ => (200, """{"success":true,"data":{"id":"abc123","link":"https://i.imgur.com/abc123.png","deletehash":"del789"}}""", "OK");

        var shot = new ScreenShot(path);
        var upload = _imgur.MakeUpload(shot, _server.BaseUrl + "/", "client-id-42");
        await upload.StartAsync();

        Assert.Equal(TaskState.Succeeded, upload.State);
        var request = _server.Request("/image");
        Assert.NotNull(request);
        Assert.Equal("POST", request.Method);
        Assert.Equal("Client-ID client-id-42", request.Headers["Authorization"]);
        Assert.Equal("application/json", request.Headers["Accept"]);
        Assert.StartsWith("multipart/form-data; boundary=", request.Headers["Content-Type"]);

        // QHttpMultiPart(FormDataType) framing: image part first (Content-Type set before the
        // Content-Disposition), then type=file, then title=baseName.
        var boundary = MultipartAssertions.BoundaryFromContentType(request.Headers["Content-Type"]);
        var parts = MultipartAssertions.SplitParts(request.Body, boundary);
        Assert.Equal(3, parts.Count);

        Assert.Equal(
        [
            "Content-Type: image/png",
            "Content-Disposition: form-data; name=\"image\"; filename=\"2026-09-13_10.11.12.png\"",
        ], parts[0].Headers);
        Assert.Equal(imageData, parts[0].Payload);

        Assert.Equal(["Content-Disposition: form-data; name=\"type\""], parts[1].Headers);
        Assert.Equal("file"u8.ToArray(), parts[1].Payload);

        // the title part is the QFileInfo::baseName(), UTF-8 encoded
        Assert.Equal(["Content-Disposition: form-data; name=\"title\""], parts[2].Headers);
        Assert.Equal("2026-09-13_10", Encoding.UTF8.GetString(parts[2].Payload));

        // the body ends with the closing boundary marker
        var tail = Encoding.UTF8.GetBytes("--" + boundary + "--\r\n");
        Assert.True(((ReadOnlySpan<byte>)request.Body)[^tail.Length..].SequenceEqual(tail));
    }

    [Fact]
    public async Task Upload_Parses_Response_Into_ScreenShot()
    {
        var path = TempPng([1]);
        _server.Responder = _ => (200,
            """{"status":200,"success":true,"data":{"id":"abc123","link":"https://i.imgur.com/abc123.png","deletehash":"del789"}}""",
            "OK");

        var shot = new ScreenShot(path);
        var upload = _imgur.MakeUpload(shot, _server.BaseUrl + "/", "cid");
        await upload.StartAsync();

        Assert.Equal(TaskState.Succeeded, upload.State);
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
        var upload = _imgur.MakeUpload(shot, _server.BaseUrl + "/", "cid");
        await upload.StartAsync();

        Assert.Equal(TaskState.Failed, upload.State);
        Assert.Equal("Screenshot was not uploaded successfully", upload.FailReason);
    }

    [Fact]
    public async Task Upload_Fails_On_Invalid_Json()
    {
        var path = TempPng([1]);
        _server.Responder = _ => (200, "<html>gateway error</html>", "OK");

        var shot = new ScreenShot(path);
        var upload = _imgur.MakeUpload(shot, _server.BaseUrl + "/", "cid");
        await upload.StartAsync();

        Assert.Equal(TaskState.Failed, upload.State);
        Assert.Equal("Invalid json reply", upload.FailReason);
    }

    [Fact]
    public async Task Upload_Fails_On_Http_Error_Status()
    {
        var path = TempPng([1]);
        _server.Responder = _ => (429, """{"success":false}""", "Too Many Requests");

        var shot = new ScreenShot(path);
        var upload = _imgur.MakeUpload(shot, _server.BaseUrl + "/", "cid");
        await upload.StartAsync();

        Assert.Equal(TaskState.Failed, upload.State);
        Assert.Contains("429", upload.FailReason);
    }

    [Fact]
    public async Task Upload_Fails_When_File_Missing()
    {
        var shot = new ScreenShot(new AbsolutePath(Path.Combine(_dir, "does-not-exist.png")));
        var upload = _imgur.MakeUpload(shot, _server.BaseUrl + "/", "cid");
        await upload.StartAsync();

        Assert.Equal(TaskState.Failed, upload.State);
        Assert.StartsWith("Could not open file ", upload.FailReason);
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
        var result = new ImgurApi.AlbumResult();
        var album = _imgur.MakeAlbum(result, [ShotWithHash("h1")]);
        Assert.Equal("https://api.imgur.com/3/album", album.EndpointUrl);
        Assert.Equal(FlintConfig.Default.ImgurBaseUrl + "album", ImgurApi.AlbumUrl());
    }

    [Fact]
    public async Task Album_Request_Shape_And_Response()
    {
        _server.Responder = _ => (200, """{"status":200,"success":true,"data":{"id":"aB1cD2e","deletehash":"dhXyZ"}}""", "OK");

        var result = new ImgurApi.AlbumResult();
        var album = _imgur.MakeAlbum(result,
        [
            ShotWithHash("hash-one"), ShotWithHash("hash-two"), ShotWithHash("hash-three"),
        ], _server.BaseUrl + "/", "cid");
        await album.StartAsync();

        Assert.Equal(TaskState.Succeeded, album.State);
        Assert.Equal("dhXyZ", result.DeleteHash);
        Assert.Equal("aB1cD2e", result.Id);

        var request = _server.Request("/album");
        Assert.NotNull(request);
        Assert.Equal("POST", request.Method);
        Assert.Equal("application/x-www-form-urlencoded", request.Headers["Content-Type"]);
        Assert.Equal("Client-ID cid", request.Headers["Authorization"]);
        Assert.Equal("application/json", request.Headers["Accept"]);
        // "deletehashes=" + join("&deletehashes=") + "&title=Minecraft%20Screenshots&privacy=hidden"
        Assert.Equal(
            "deletehashes=hash-one&deletehashes=hash-two&deletehashes=hash-three&title=Minecraft%20Screenshots&privacy=hidden",
            request.BodyAsString);
    }

    [Fact]
    public async Task Album_Fails_When_Success_Is_False()
    {
        _server.Responder = _ => (200, """{"success":false,"status":403}""", "OK");

        var result = new ImgurApi.AlbumResult();
        var album = _imgur.MakeAlbum(result, [ShotWithHash("h")], _server.BaseUrl + "/", "cid");
        await album.StartAsync();

        Assert.Equal(TaskState.Failed, album.State);
        Assert.Equal("Failed to create album", album.FailReason);
        Assert.Equal("", result.DeleteHash);
        Assert.Equal("", result.Id);
    }

    [Fact]
    public async Task Album_Fails_On_Invalid_Json()
    {
        _server.Responder = _ => (200, "nope", "OK");

        var result = new ImgurApi.AlbumResult();
        var album = _imgur.MakeAlbum(result, [ShotWithHash("h")], _server.BaseUrl + "/", "cid");
        await album.StartAsync();

        Assert.Equal(TaskState.Failed, album.State);
        Assert.Equal("Invalid json reply", album.FailReason);
    }

    [Fact]
    public async Task Album_Fails_On_Http_Error_Status()
    {
        _server.Responder = _ => (500, "boom", "Internal Server Error");

        var result = new ImgurApi.AlbumResult();
        var album = _imgur.MakeAlbum(result, [ShotWithHash("h")], _server.BaseUrl + "/", "cid");
        await album.StartAsync();

        Assert.Equal(TaskState.Failed, album.State);
        Assert.Contains("500", album.FailReason);
    }

    // The C++ reads data.id/data.link/data.deletehash via QJsonObject::value().toString(); a
    // missing "data" object keeps the fields empty (no crash).
    [Fact]
    public async Task Upload_With_Missing_Data_Object_Leaves_Fields_Empty()
    {
        var path = TempPng([1]);
        _server.Responder = _ => (200, """{"success":true}""", "OK");

        var shot = new ScreenShot(path);
        var upload = _imgur.MakeUpload(shot, _server.BaseUrl + "/", "cid");
        await upload.StartAsync();

        Assert.Equal(TaskState.Succeeded, upload.State);
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
