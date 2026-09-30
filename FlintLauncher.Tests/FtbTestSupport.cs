// SPDX-License-Identifier: GPL-3.0-only
// Test doubles for the FTB App API layer tests: a stub modpacks.ch server (free-port
// HttpListener pattern, see AtlTestSupport) serving /modpack/all, /modpack/{id} and
// /modpack/{id}/{version} plus file downloads, and a synthetic pack/version fixture with
// real sha1 checksums.
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Flint.Tests;

/// <summary>
/// Routes a local HttpListener as an FTB App API stub:
///   GET /modpack/all             -> ModpacksIndexJson
///   GET /modpack/{id}            -> Modpacks[id]
///   GET /modpack/{id}/{version}  -> Versions["{id}/{version}"]
///   GET /&lt;anything else&gt;         -> Downloads[last path segment]
/// </summary>
internal sealed class StubFtbServer : IDisposable
{
    private readonly HttpListener _listener = new();

    public StubFtbServer()
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

    public string ModpacksIndexJson { get; set; } = """{"packs":[]}""";

    /// <summary>Keyed by pack id.</summary>
    public Dictionary<string, string> Modpacks { get; } = new(StringComparer.Ordinal);

    /// <summary>Keyed by "{packId}/{versionId}".</summary>
    public Dictionary<string, string> Versions { get; } = new(StringComparer.Ordinal);

    /// <summary>Payloads served per path segment name, e.g. Downloads["testmod.jar"].</summary>
    public Dictionary<string, byte[]> Downloads { get; } = new(StringComparer.Ordinal);

    private void Handle(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url!.AbsolutePath;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        byte[] body;
        if (segments.Length == 2 && segments[0] == "modpack" && segments[1] == "all")
        {
            body = Encoding.UTF8.GetBytes(ModpacksIndexJson);
        }
        else if (segments.Length == 2 && segments[0] == "modpack")
        {
            if (!Modpacks.TryGetValue(segments[1], out var modpackJson))
            {
                ctx.Response.StatusCode = 404;
                return;
            }
            body = Encoding.UTF8.GetBytes(modpackJson);
        }
        else if (segments.Length == 3 && segments[0] == "modpack")
        {
            var key = $"{segments[1]}/{segments[2]}";
            if (!Versions.TryGetValue(key, out var versionJson))
            {
                ctx.Response.StatusCode = 404;
                return;
            }
            body = Encoding.UTF8.GetBytes(versionJson);
        }
        else
        {
            var name = segments.LastOrDefault() ?? "";
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
/// Builds a synthetic FTB App API pack (/modpack/{id} description + /modpack/{id}/{version}
/// payload) with real sha1 checksums and a direct-download mod, a config file, a
/// CurseForge-only file (resolved through the Flame layer) and a server-only file.
/// </summary>
internal sealed class FtbPackFixture
{
    public const int PackId = 42;
    public const int VersionId = 4242;

    public string PackName { get; set; } = "Test FTB Pack";
    public string VersionName { get; set; } = "1.0.0";
    public string Minecraft { get; set; } = "1.18.2";
    public string ForgeVersion { get; set; } = "40.1.0";

    public byte[] ModBytes { get; set; } = "ftb direct mod payload 0123456789"u8.ToArray();
    public byte[] ConfigBytes { get; set; } = "ftbtest=1\n"u8.ToArray();
    public byte[] ServerOnlyBytes { get; set; } = "ftb server-only payload"u8.ToArray();

    public string ModSha1 { get; private set; } = "";
    public string ConfigSha1 { get; private set; } = "";
    public string ServerOnlySha1 { get; private set; } = "";

    public string PackJson { get; private set; } = "";
    public string VersionJson { get; private set; } = "";

    public static FtbPackFixture Create()
    {
        var fixture = new FtbPackFixture();
        fixture.ModSha1 = Sha1(fixture.ModBytes);
        fixture.ConfigSha1 = Sha1(fixture.ConfigBytes);
        fixture.ServerOnlySha1 = Sha1(fixture.ServerOnlyBytes);

        fixture.PackJson = new JsonObject
        {
            ["id"] = PackId,
            ["name"] = fixture.PackName,
            ["synopsis"] = "a synthetic pack",
            ["description"] = "a synthetic pack description",
            ["type"] = "public",
            ["featured"] = true,
            ["installs"] = 100,
            ["plays"] = 200,
            ["updated"] = 1600000000,
            ["refreshed"] = 1600000001,
            ["art"] = new JsonArray(
                (JsonNode?)new JsonObject
                {
                    ["id"] = 1,
                    ["url"] = "https://example.invalid/logo.png",
                    ["type"] = "square",
                    ["width"] = 100,
                    ["height"] = 100,
                    ["compressed"] = false,
                    ["sha1"] = "0123456789012345678901234567890123456789",
                    ["size"] = 1234,
                    ["updated"] = 1600000000,
                }),
            ["authors"] = new JsonArray(
                (JsonNode?)new JsonObject
                {
                    ["id"] = 7,
                    ["name"] = "tester",
                    ["type"] = "owner",
                    ["website"] = "https://example.invalid/~tester",
                    ["updated"] = 1600000000,
                }),
            ["versions"] = new JsonArray(
                (JsonNode?)new JsonObject
                {
                    ["id"] = VersionId,
                    ["name"] = fixture.VersionName,
                    ["type"] = "Release",
                    ["updated"] = 1600000000,
                    ["specs"] = new JsonObject
                    {
                        ["id"] = 1,
                        ["minimum"] = 2048,
                        ["recommended"] = 4096,
                    },
                },
                (JsonNode?)new JsonObject
                {
                    ["id"] = VersionId + 1,
                    ["name"] = "1.1.0",
                    ["type"] = "Beta",
                    ["updated"] = 1600000100,
                    ["specs"] = new JsonObject
                    {
                        ["id"] = 1,
                        ["minimum"] = 2048,
                        ["recommended"] = 4096,
                    },
                }),
            ["tags"] = new JsonArray(
                (JsonNode?)new JsonObject { ["id"] = 3, ["name"] = "magic" }),
        }.ToJsonString();
        return fixture;
    }

    /// <summary>Build the version payload (direct-download URLs pointing at the stub) and
    /// register everything.</summary>
    public void Register(StubFtbServer server)
    {
        VersionJson = new JsonObject
        {
            ["id"] = VersionId,
            ["parent"] = PackId,
            ["name"] = VersionName,
            ["type"] = "Release",
            ["installs"] = 10,
            ["plays"] = 20,
            ["updated"] = 1600000000,
            ["refreshed"] = 1600000001,
            ["specs"] = new JsonObject
            {
                ["id"] = 1,
                ["minimum"] = 2048,
                ["recommended"] = 4096,
            },
            ["targets"] = new JsonArray(
                (JsonNode?)new JsonObject
                {
                    ["id"] = 1,
                    ["type"] = "game",
                    ["name"] = "minecraft",
                    ["version"] = Minecraft,
                    ["updated"] = 1600000000,
                },
                (JsonNode?)new JsonObject
                {
                    ["id"] = 2,
                    ["type"] = "modloader",
                    ["name"] = "forge",
                    ["version"] = ForgeVersion,
                    ["updated"] = 1600000000,
                }),
            ["files"] = new JsonArray(
                (JsonNode?)new JsonObject
                {
                    ["id"] = 1001,
                    ["type"] = "mod",
                    ["path"] = "mods",
                    ["name"] = "testmod.jar",
                    ["version"] = "1.0",
                    ["url"] = $"{server.Url}/testmod.jar",
                    ["sha1"] = ModSha1,
                    ["size"] = ModBytes.Length,
                    ["clientonly"] = true,
                    ["serveronly"] = false,
                    ["optional"] = false,
                    ["updated"] = 1600000000,
                },
                (JsonNode?)new JsonObject
                {
                    ["id"] = 1002,
                    ["type"] = "config",
                    ["path"] = "config",
                    ["name"] = "test.cfg",
                    ["version"] = "1.0",
                    ["url"] = $"{server.Url}/test.cfg",
                    ["sha1"] = ConfigSha1,
                    ["size"] = ConfigBytes.Length,
                    ["clientonly"] = true,
                    ["serveronly"] = false,
                    ["optional"] = false,
                    ["updated"] = 1600000000,
                },
                // no url + a CurseForge pair: resolved through the Flame layer
                (JsonNode?)new JsonObject
                {
                    ["id"] = 1003,
                    ["type"] = "mod",
                    ["path"] = "mods",
                    ["name"] = "cfmod.jar",
                    ["version"] = "2.0",
                    ["sha1"] = "",
                    ["size"] = 0,
                    ["clientonly"] = true,
                    ["serveronly"] = false,
                    ["optional"] = false,
                    ["updated"] = 1600000000,
                    ["curseforge"] = new JsonObject
                    {
                        ["project"] = 123,
                        ["file"] = 456,
                    },
                },
                // server-only files are skipped entirely
                (JsonNode?)new JsonObject
                {
                    ["id"] = 1004,
                    ["type"] = "mod",
                    ["path"] = "mods",
                    ["name"] = "servermod.jar",
                    ["version"] = "3.0",
                    ["sha1"] = ServerOnlySha1,
                    ["size"] = ServerOnlyBytes.Length,
                    ["clientonly"] = false,
                    ["serveronly"] = true,
                    ["optional"] = false,
                    ["updated"] = 1600000000,
                }),
        }.ToJsonString();

        server.ModpacksIndexJson = new JsonObject { ["packs"] = new JsonArray(PackId) }.ToJsonString();
        server.Modpacks[PackId.ToString()] = PackJson;
        server.Versions[$"{PackId}/{VersionId}"] = VersionJson;
        server.Downloads["testmod.jar"] = ModBytes;
        server.Downloads["test.cfg"] = ConfigBytes;
    }

    public static string Sha1(byte[] data) =>
        Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();
}
