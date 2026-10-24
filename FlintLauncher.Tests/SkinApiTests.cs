// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Skin API tests: the multipart PUT shape of SkinUpload (boundary, parts, ordering), the
// DELETE of the active skin and the cape equip/remove endpoints against the local stub server.

using System.Text;
using System.Text.Json.Nodes;
using FlintLauncher.Core.Auth;
using FlintLauncher.Core.Skins;
using FlintLauncher.Core.Tasks;
using TruePath;
using Xunit;

namespace FlintLauncher.Tests;

public class SkinApiTests : IDisposable
{
    private readonly AuthStubServer _server = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"flint-skins-{Guid.NewGuid():N}");

    /// <summary>The endpoints hook for the requests under test, pointed at the stub.</summary>
    private readonly AuthEndpointsOptions _auth;

    public SkinApiTests()
    {
        _auth = new AuthEndpointsOptions { MinecraftServices = _server.BaseUrl };
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

    [Fact]
    public async Task SkinUpload_Sends_The_Upstream_Multipart_Shape()
    {
        Directory.CreateDirectory(_dir);
        var skinPath = Path.Combine(_dir, "skin.png");
        var pngBytes = "PNGDATA-0123456789-abcdef"u8.ToArray();
        await File.WriteAllBytesAsync(skinPath, pngBytes);

        var request = SkinUpload.Make("SKIN_TOKEN", new AbsolutePath(skinPath), "classic", _auth);
        await request.StartAsync();

        Assert.Equal(TaskState.Succeeded, request.State);

        var recorded = _server.Request("POST", "/minecraft/profile/skins");
        Assert.NotNull(recorded);
        Assert.StartsWith("Bearer SKIN_TOKEN", recorded.Headers["Authorization"]);

        var contentType = recorded.Headers["Content-Type"];
        Assert.StartsWith("multipart/form-data; boundary=", contentType);
        var boundary = contentType["multipart/form-data; boundary=".Length..];

        var body = recorded.Body;
        // part 1: the file (Content-Type before Content-Disposition, like the C++ QHttpPart setup)
        var filePart = $"""
                        --{boundary}
                        Content-Type: image/png
                        Content-Disposition: form-data; name="file"; filename="skin.png"

                        """.ReplaceLineEndings("\r\n");
        var fileIdx = body.IndexOf(filePart, StringComparison.Ordinal);
        Assert.True(fileIdx >= 0, "file part header missing:\n" + body);
        var pngIdx = body.IndexOf(Encoding.UTF8.GetString(pngBytes), StringComparison.Ordinal);
        Assert.True(pngIdx > fileIdx);

        // part 2: the variant value follows
        var variantPart = $"""

                           --{boundary}
                           Content-Disposition: form-data; name="variant"

                           classic
                           --{boundary}--
                           """.ReplaceLineEndings("\r\n");
        Assert.Contains(variantPart, body);

        // the closing delimiter ends the body
        Assert.EndsWith("--" + boundary + "--\r\n", body);
    }

    [Fact]
    public async Task SkinDelete_Calls_The_Active_Skin_Endpoint()
    {
        var request = SkinDelete.Make("SKIN_TOKEN", _auth);
        await request.StartAsync();

        Assert.Equal(TaskState.Succeeded, request.State);
        var recorded = _server.Request("DELETE", "/minecraft/profile/skins/active");
        Assert.NotNull(recorded);
        Assert.Equal("Bearer SKIN_TOKEN", recorded.Headers["Authorization"]);
    }

    [Fact]
    public async Task CapeChange_Puts_The_CapeId()
    {
        var request = CapeChange.Make("SKIN_TOKEN", "1234-abcd", _auth);
        await request.StartAsync();

        Assert.Equal(TaskState.Succeeded, request.State);
        var recorded = _server.Request("PUT", "/minecraft/profile/capes/active");
        Assert.NotNull(recorded);
        Assert.Equal("Bearer SKIN_TOKEN", recorded.Headers["Authorization"]);
        Assert.Equal("""{"capeId":"1234-abcd"}""", recorded.Body);
    }

    [Fact]
    public async Task CapeChange_With_Empty_Id_Deletes_The_Cape()
    {
        var request = CapeChange.Make("SKIN_TOKEN", "", _auth);
        await request.StartAsync();

        Assert.Equal(TaskState.Succeeded, request.State);
        var recorded = _server.Request("DELETE", "/minecraft/profile/capes/active");
        Assert.NotNull(recorded);
        Assert.Equal("Bearer SKIN_TOKEN", recorded.Headers["Authorization"]);
        Assert.Null(_server.Request("PUT", "/minecraft/profile/capes/active"));
    }

    [Fact]
    public async Task SkinUpload_Server_Error_Fails_The_Request()
    {
        // the stub answers 404 for unknown paths; point the upload at one
        var dead = new AuthEndpointsOptions { MinecraftServices = _server.BaseUrl + "/nowhere" };

        var request = SkinUpload.Make("SKIN_TOKEN", [1, 2, 3], "slim", dead);
        await request.StartAsync();

        Assert.Equal(TaskState.Failed, request.State);
        Assert.Contains("404", request.FailReason);
    }

    // ---------------------------------------------------------- index.json persistence

    [Fact]
    public void SkinIndex_RoundTrips_The_Upstream_Format()
    {
        Directory.CreateDirectory(_dir);
        var skins = new List<SkinModel>
        {
            new()
            {
                Path = new AbsolutePath(Path.Combine(_dir, "myskin.png")),
                CapeId = "cape-id",
                Model = SkinModelKind.Slim,
                Url = "https://textures.minecraft.net/texture/abc",
            },
        };
        SkinListStore.Save(new AbsolutePath(_dir), skins);

        // {"skins": [{"name": ..., "capeId": ..., "url": ..., "model": ...}]}
        var indexNode = JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, "index.json")));
        var root = indexNode?.AsObject();
        Assert.NotNull(root);
        Assert.Equal(new HashSet<string> { "skins" }, new HashSet<string>(root.Select(p => p.Key)));
        var skinsNode = root["skins"]?.AsArray();
        Assert.NotNull(skinsNode);
        var entry = skinsNode[0]?.AsObject();
        Assert.NotNull(entry);
        Assert.Equal("myskin", entry["name"]?.GetValue<string>());
        Assert.Equal("cape-id", entry["capeId"]?.GetValue<string>());
        Assert.Equal("https://textures.minecraft.net/texture/abc", entry["url"]?.GetValue<string>());
        Assert.Equal("SLIM", entry["model"]?.GetValue<string>());

        var loaded = SkinListStore.Load(new AbsolutePath(_dir));
        var skin = Assert.Single(loaded);
        Assert.Equal("myskin", skin.Name());
        Assert.Equal("cape-id", skin.CapeId);
        Assert.Equal(SkinModelKind.Slim, skin.Model);
        Assert.Equal("https://textures.minecraft.net/texture/abc", skin.Url);
        Assert.Equal(Path.Combine(_dir, "myskin.png"), skin.Path.Value);
    }

    [Fact]
    public void SkinIndex_Missing_File_Loads_Empty()
    {
        Directory.CreateDirectory(_dir);
        Assert.Empty(SkinListStore.Load(new AbsolutePath(_dir)));
    }
}
