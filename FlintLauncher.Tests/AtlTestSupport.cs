// SPDX-License-Identifier: GPL-3.0-only
// Test doubles for the ATLauncher layer tests: a stub ATL CDN/API server (free-port
// HttpListener pattern, see ModrinthTestSupport) serving packsnew.json, share-codes,
// per-version Configs.json/Configs.zip and mod downloads, plus a synthetic pack fixture
// (Configs.json + Configs.zip + mod payloads with real md5/sha1 checksums).
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.ModPlatform.AtLauncher;
using Xunit;

namespace Flint.Tests;

/// <summary>
/// Routes a local HttpListener as an ATLauncher CDN/API stub:
///   GET /launcher/json/packsnew.json                      -> PacksNewJson
///   GET /share-codes/{code}                               -> ShareCodes[code]
///   GET /packs/{safe}/{ver}/Configs.json                  -> ConfigsJson["{safe}/{ver}"]
///   GET /packs/{safe}/{ver}/Configs.zip                   -> ConfigsZip["{safe}/{ver}"]
///   GET /&lt;anything else&gt;                                  -> Downloads[last path segment]
/// </summary>
internal sealed class StubAtlServer : IDisposable
{
    private readonly HttpListener _listener = new();

    public StubAtlServer()
    {
        // hold a reserved port until the instant this listener takes it over
        using var lease = PortAllocator.Reserve();
        var port = lease.Port;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        lease.Dispose();
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

    public string PacksNewJson { get; set; } = "[]";

    /// <summary>Keyed by "{safeName}/{versionName}".</summary>
    public Dictionary<string, string> ConfigsJson { get; } = new(StringComparer.Ordinal);

    /// <summary>Keyed by "{safeName}/{versionName}".</summary>
    public Dictionary<string, byte[]> ConfigsZip { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> ShareCodes { get; } = new(StringComparer.Ordinal);

    /// <summary>Payloads served per path segment name, e.g. Downloads["required.jar"].</summary>
    public Dictionary<string, byte[]> Downloads { get; } = new(StringComparer.Ordinal);

    private void Handle(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url!.AbsolutePath;

        byte[] body;
        if (path == "/launcher/json/packsnew.json")
        {
            body = Encoding.UTF8.GetBytes(PacksNewJson);
        }
        else if (path.StartsWith("/share-codes/", StringComparison.Ordinal))
        {
            var code = path["/share-codes/".Length..];
            if (!ShareCodes.TryGetValue(code, out var shareCode))
            {
                ctx.Response.StatusCode = 404;
                return;
            }
            body = Encoding.UTF8.GetBytes(shareCode);
        }
        else if (path.StartsWith("/packs/", StringComparison.Ordinal)
                 && path.EndsWith("/Configs.json", StringComparison.Ordinal))
        {
            var key = ConfigsKey(path, "/Configs.json");
            if (!ConfigsJson.TryGetValue(key, out var configsJson))
            {
                ctx.Response.StatusCode = 404;
                return;
            }
            body = Encoding.UTF8.GetBytes(configsJson);
        }
        else if (path.StartsWith("/packs/", StringComparison.Ordinal)
                 && path.EndsWith("/Configs.zip", StringComparison.Ordinal))
        {
            var key = ConfigsKey(path, "/Configs.zip");
            if (!ConfigsZip.TryGetValue(key, out var configsZip))
            {
                ctx.Response.StatusCode = 404;
                return;
            }
            body = configsZip;
        }
        else
        {
            var name = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
            if (!Downloads.TryGetValue(name, out var payload))
            {
                ctx.Response.StatusCode = 404;
                return;
            }
            body = payload;
        }

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentLength64 = body.Length;
        ctx.Response.OutputStream.Write(body);
    }

    /// <summary>"/packs/{safe}/versions/{ver}/Configs.json" -> "{safe}/{ver}".</summary>
    private static string ConfigsKey(string path, string suffix)
    {
        var relative = path.TrimStart('/'); // packs/{safe}/versions/{ver}{suffix}
        var middle = relative["packs/".Length..^suffix.Length]; // {safe}/versions/{ver}
        var separator = middle.IndexOf("/versions/", StringComparison.Ordinal);
        return middle[..separator] + "/" + middle[(separator + "/versions/".Length)..];
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
}

/// <summary>
/// Builds a synthetic ATLauncher pack version: Configs.json (loader/mods/configs) with real
/// checksums, a Configs.zip holding config/test.cfg and the mod payloads it references.
/// </summary>
internal sealed class AtlPackFixture
{
    public string PackName { get; set; } = "Test Pack";
    public string SafeName => AtLauncherPackIndex.SafePackName(PackName);
    public string VersionName { get; set; } = "1.0.0";
    public string Minecraft { get; set; } = "1.20.1";

    /// <summary>The loader JSON injected verbatim into Configs.json ("" = none).</summary>
    public string LoaderJson { get; set; } = "";

    public byte[] RequiredModBytes { get; set; } = "atl required mod payload 0123456789"u8.ToArray();
    public byte[] OptionalSelectedBytes { get; set; } = "atl optional selected payload"u8.ToArray();
    public byte[] OptionalUnselectedBytes { get; set; } = "atl optional unselected payload"u8.ToArray();
    public byte[] JarModBytes { get; set; } = "atl legacy jar mod payload"u8.ToArray();
    public string ConfigTxt { get; set; } = "atlastest=1\n";

    public string RequiredModMd5 { get; private set; } = "";
    public string OptionalSelectedMd5 { get; private set; } = "";
    public string OptionalUnselectedMd5 { get; private set; } = "";
    public string JarModMd5 { get; private set; } = "";
    public string ConfigsSha1 { get; private set; } = "";
    public byte[] ConfigsZipBytes { get; private set; } = [];

    public string ConfigsJson { get; set; } = "";

    public static AtlPackFixture Create(string? loaderJson = null)
    {
        var fixture = new AtlPackFixture();
        fixture.RequiredModMd5 = Md5(fixture.RequiredModBytes);
        fixture.OptionalSelectedMd5 = Md5(fixture.OptionalSelectedBytes);
        fixture.OptionalUnselectedMd5 = Md5(fixture.OptionalUnselectedBytes);
        fixture.JarModMd5 = Md5(fixture.JarModBytes);

        // build Configs.zip (config/test.cfg)
        var srcDir = Path.Combine(Path.GetTempPath(), $"flint-atl-src-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(srcDir, "config"));
            File.WriteAllText(Path.Combine(srcDir, "config", "test.cfg"), fixture.ConfigTxt);
            var zipPath = Path.Combine(srcDir, "Configs.zip");
            Assert.True(MMCZip.CompressDir(zipPath, srcDir));
            fixture.ConfigsZipBytes = File.ReadAllBytes(zipPath);
        }
        finally
        {
            try
            {
                Directory.Delete(srcDir, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
        fixture.ConfigsSha1 = Convert.ToHexString(SHA1.HashData(fixture.ConfigsZipBytes)).ToLowerInvariant();

        var mods = new JsonArray(
            (JsonNode?)new JsonObject
            {
                ["name"] = "Required Mod",
                ["version"] = "1.0",
                ["url"] = "mods/required.jar",
                ["file"] = "required.jar",
                ["md5"] = fixture.RequiredModMd5,
                ["download"] = "server",
                ["type"] = "mods",
                ["client"] = true,
                ["optional"] = false,
                ["description"] = "A required mod",
            },
            (JsonNode?)new JsonObject
            {
                ["name"] = "Optional Selected",
                ["version"] = "1.0",
                ["url"] = "mods/optsel.jar",
                ["file"] = "optsel.jar",
                ["md5"] = fixture.OptionalSelectedMd5,
                ["download"] = "server",
                ["type"] = "mods",
                ["client"] = true,
                ["optional"] = true,
                ["selected"] = false,
                ["description"] = "An optional mod the user picks",
            },
            (JsonNode?)new JsonObject
            {
                ["name"] = "Optional Unselected",
                ["version"] = "1.0",
                ["url"] = "mods/optunsel.jar",
                ["file"] = "optunsel.jar",
                ["md5"] = fixture.OptionalUnselectedMd5,
                ["download"] = "server",
                ["type"] = "mods",
                ["client"] = true,
                ["optional"] = true,
                ["selected"] = false,
            },
            (JsonNode?)new JsonObject
            {
                ["name"] = "Legacy Jar Mod",
                ["version"] = "1.0",
                ["url"] = "jarmods/legacy.jar",
                ["file"] = "legacy.jar",
                ["md5"] = fixture.JarModMd5,
                ["download"] = "server",
                ["type"] = "jar",
                ["client"] = true,
                ["optional"] = false,
            });

        var configsObj = new JsonObject
        {
            ["version"] = fixture.VersionName,
            ["minecraft"] = fixture.Minecraft,
            ["noConfigs"] = false,
            ["mods"] = mods,
            ["configs"] = new JsonObject
            {
                ["filesize"] = fixture.ConfigsZipBytes.Length,
                ["sha1"] = fixture.ConfigsSha1,
            },
        };
        if (loaderJson is not null)
        {
            configsObj["loader"] = JsonNode.Parse(loaderJson);
        }
        fixture.ConfigsJson = configsObj.ToJsonString();
        return fixture;
    }

    public void Register(StubAtlServer server)
    {
        server.ConfigsJson[$"{SafeName}/{VersionName}"] = ConfigsJson;
        server.ConfigsZip[$"{SafeName}/{VersionName}"] = ConfigsZipBytes;
        server.Downloads["required.jar"] = RequiredModBytes;
        server.Downloads["optsel.jar"] = OptionalSelectedBytes;
        server.Downloads["optunsel.jar"] = OptionalUnselectedBytes;
        server.Downloads["legacy.jar"] = JarModBytes;
    }

    public static string Md5(byte[] data) =>
        Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();
}
