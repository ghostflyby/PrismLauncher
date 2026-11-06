// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Test doubles for the Modrinth layer tests: a stub Modrinth API/download server (free-port
// HttpListener pattern, see NetTests) and a synthetic .mrpack builder (index +
// overrides + mods with real hashes) written through MMCZip.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FlintLauncher.Core.IO;
using TruePath;

namespace FlintLauncher.Tests;

/// <summary>
/// Routes a local HttpListener as a Modrinth API stub:
///   POST /version_files          -> {hash: version} for the registered hashes
///   POST /version_files/update   -> same map (update check)
///   GET  /version_file/{hash}    -> single version (echoes VersionFiles[/hash] or the first)
///   GET  /version/{id}           -> VersionsById[id] (pinned-version dependency lookup)
///   GET  /project/{id}/version   -> ProjectVersions[id] (dependency version list)
///   GET  /project/{id}           -> Projects[id]
///   GET  /search                 -> {"hits":[]}
///   GET  /&lt;name&gt;                -> Downloads[name] (e.g. mod.bin)
/// </summary>
internal sealed class StubModrinthServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly Dictionary<string, string> _versionFiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _projects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _projectVersions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _versionsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _downloads = new(StringComparer.Ordinal);

    public StubModrinthServer()
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

    public Dictionary<string, string> VersionFiles => _versionFiles;

    public Dictionary<string, string> Projects => _projects;

    /// <summary>Per-project version lists (GET /project/{id}/version), e.g. for dependency lookups.</summary>
    public Dictionary<string, string> ProjectVersions => _projectVersions;

    /// <summary>Single pinned versions (GET /version/{id}).</summary>
    public Dictionary<string, string> VersionsById => _versionsById;

    /// <summary>Payloads served per path suffix, e.g. Downloads["mod.bin"] (missing -> 404).</summary>
    public Dictionary<string, byte[]> Downloads => _downloads;

    /// <summary>"METHOD path" of every request, in order (for endpoint-hit assertions).</summary>
    public List<string> Requests { get; } = [];

    // last request info (single-request tests only)
    public string LastPath { get; private set; } = "";
    public Dictionary<string, string> LastQuery { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> LastHeaders { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> LastDownloadHeaders { get; } = new(StringComparer.Ordinal);
    public byte[]? LastBody { get; private set; }

    private void Handle(HttpListenerContext ctx)
    {
        var url = ctx.Request.Url ?? throw new InvalidOperationException("the HTTP request has no URL");
        var path = url.PathAndQuery;
        LastPath = path;
        Requests.Add($"{ctx.Request.HttpMethod} {path}");
        LastHeaders.Clear();
        foreach (var key in ctx.Request.Headers.AllKeys)
        {
            if (key is not null)
            {
                LastHeaders[key] = ctx.Request.Headers[key] ?? "";
            }
        }

        LastQuery.Clear();
        foreach (var key in ctx.Request.QueryString.AllKeys)
        {
            if (key is not null)
            {
                LastQuery[key] = ctx.Request.QueryString[key] ?? "";
            }
        }

        if (path.Contains(".bin", StringComparison.Ordinal))
        {
            LastDownloadHeaders.Clear();
            foreach (var key in ctx.Request.Headers.AllKeys)
            {
                if (key is not null)
                {
                    LastDownloadHeaders[key] = ctx.Request.Headers[key] ?? "";
                }
            }
        }

        if (ctx.Request.HasEntityBody)
        {
            using var ms = new MemoryStream();
            ctx.Request.InputStream.CopyTo(ms);
            LastBody = ms.ToArray();
        }
        else
        {
            LastBody = null;
        }

        byte[] body;
        if (ctx.Request.HttpMethod == "POST" && path.EndsWith("/version_files/update", StringComparison.Ordinal))
        {
            body = VersionFilesMap();
        }
        else if (ctx.Request.HttpMethod == "POST" && path.EndsWith("/version_files", StringComparison.Ordinal))
        {
            body = VersionFilesMap();
        }
        else if (path.Contains("/version_file/", StringComparison.Ordinal))
        {
            var hash = path[(path.IndexOf("/version_file/", StringComparison.Ordinal) + "/version_file/".Length)..]
                .Split('?')[0];
            if (!_versionFiles.TryGetValue(hash, out var version))
            {
                // the real API answers 404 for an unknown hash
                ctx.Response.StatusCode = 404;
                return;
            }

            body = Encoding.UTF8.GetBytes(version);
        }
        else if (path.Contains("/version/", StringComparison.Ordinal))
        {
            // GET /version/{id} (a dependency pinned to a specific version); the real API
            // answers 404 for an unknown id
            var id = path[(path.IndexOf("/version/", StringComparison.Ordinal) + "/version/".Length)..].Split('?')[0];
            if (!_versionsById.TryGetValue(id, out var pinned))
            {
                ctx.Response.StatusCode = 404;
                return;
            }

            body = Encoding.UTF8.GetBytes(pinned);
        }
        else if (path.Contains("/project/", StringComparison.Ordinal) && path.Contains("/version", StringComparison.Ordinal))
        {
            // GET /project/{id}/version (the dependency version list)
            var marker = "/project/";
            var id = path[(path.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..]
                .Split('/')[0].Split('?')[0];
            body = Encoding.UTF8.GetBytes(_projectVersions.GetValueOrDefault(id, "[]"));
        }
        else if (path.Contains("/project/", StringComparison.Ordinal))
        {
            var id = path[(path.IndexOf("/project/", StringComparison.Ordinal) + "/project/".Length)..].Split('?')[0];
            if (!_projects.TryGetValue(id, out var project))
            {
                // the real API answers 404 for an unknown project
                ctx.Response.StatusCode = 404;
                return;
            }

            body = Encoding.UTF8.GetBytes(project);
        }
        else if (path.Contains("/search", StringComparison.Ordinal))
        {
            body = Encoding.UTF8.GetBytes("""{"hits":[]}""");
        }
        else
        {
            var slash = path.LastIndexOf('/');
            var name = slash >= 0 ? path[(slash + 1)..] : path;
            if (_downloads.TryGetValue(name, out var payload))
            {
                body = payload;
            }
            else
            {
                ctx.Response.StatusCode = 404;
                return;
            }
        }

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentLength64 = body.Length;
        ctx.Response.OutputStream.Write(body);
    }

    private byte[] VersionFilesMap()
    {
        var obj = new JsonObject();
        foreach (var (hash, version) in _versionFiles)
        {
            obj[hash] = JsonNode.Parse(version);
        }

        return Encoding.UTF8.GetBytes(obj.ToJsonString());
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

/// <summary>
/// Builds a synthetic (formatVersion 1) .mrpack: modrinth.index.json + overrides/options.txt
/// + mods/test.jar, with real sha1/sha512 hashes of the mod content.
/// </summary>
internal sealed class MrPackBuilder
{
    public byte[] ModBytes { get; set; } = "test mod jar content 0123456789"u8.ToArray();
    public string ModSha1 { get; private set; } = "";
    public string ModSha512 { get; private set; } = "";
    public byte[]? OptionalBytes { get; private set; }
    public string OptionsTxt { get; } = "soundCategory:music=0\n";
    public string IndexJson { get; private set; } = "";

    public static MrPackBuilder Create(string baseUrl, bool withOptionalMod = false,
        string modPath = "mods/test.jar", string? dependencies = null)
    {
        var pack = new MrPackBuilder();
        pack.ModSha1 = Convert.ToHexString(SHA1.HashData(pack.ModBytes)).ToLowerInvariant();
        pack.ModSha512 = Convert.ToHexString(SHA512.HashData(pack.ModBytes)).ToLowerInvariant();

        var files = new JsonArray();

        var mainFile = new JsonObject
        {
            ["path"] = modPath,
            ["hashes"] = new JsonObject
            {
                ["sha1"] = pack.ModSha1,
                ["sha512"] = pack.ModSha512,
            },
            ["env"] = new JsonObject
            {
                ["client"] = "required",
                ["server"] = "required",
            },
            ["downloads"] = new JsonArray(JsonValue.Create($"{baseUrl}/mod.bin")),
            ["fileSize"] = pack.ModBytes.Length,
        };
        files.Add((JsonNode?)mainFile);

        if (withOptionalMod)
        {
            pack.OptionalBytes = "optional mod content"u8.ToArray();
            files.Add((JsonNode?)new JsonObject
            {
                ["path"] = "mods/optional.jar",
                ["hashes"] = new JsonObject
                {
                    ["sha1"] = Convert.ToHexString(SHA1.HashData(pack.OptionalBytes)).ToLowerInvariant(),
                    ["sha512"] = Convert.ToHexString(SHA512.HashData(pack.OptionalBytes)).ToLowerInvariant(),
                },
                ["env"] = new JsonObject
                {
                    ["client"] = "optional",
                    ["server"] = "unsupported",
                },
                ["downloads"] = new JsonArray(JsonValue.Create($"{baseUrl}/optional.bin")),
                ["fileSize"] = pack.OptionalBytes.Length,
            });
        }

        var index = new JsonObject
        {
            ["formatVersion"] = 1,
            ["game"] = "minecraft",
            ["name"] = "Test Pack",
            ["versionId"] = "1.0.0",
            ["summary"] = "A synthetic test pack",
            ["files"] = files,
            ["dependencies"] = JsonNode.Parse(dependencies ?? """{"minecraft":"1.20.1","fabric-loader":"0.14.21"}"""),
        };
        pack.IndexJson = index.ToJsonString();
        return pack;
    }

    /// <summary>The version JSON the API stub answers with for the mod's sha512.</summary>
    public static string VersionJson(string baseUrl, MrPackBuilder pack, string versionNumber = "1.0.0",
        string versionId = "verId1")
    {
        return new JsonObject
        {
            ["id"] = versionId,
            ["project_id"] = "projId1",
            ["date_published"] = "2023-01-01T00:00:00Z",
            ["game_versions"] = new JsonArray("1.20.1"),
            ["loaders"] = new JsonArray("fabric"),
            ["name"] = $"Test Mod {versionNumber}",
            ["version_number"] = versionNumber,
            ["version_type"] = "release",
            ["files"] = new JsonArray(
                new JsonObject
                {
                    ["url"] = $"{baseUrl}/mod.bin",
                    ["filename"] = "test.jar",
                    ["primary"] = true,
                    ["size"] = pack.ModBytes.Length,
                    ["hashes"] = new JsonObject
                    {
                        ["sha1"] = pack.ModSha1,
                        ["sha512"] = pack.ModSha512,
                    },
                }),
            ["dependencies"] = new JsonArray(),
        }.ToJsonString();
    }

    /// <summary>The project JSON the API stub answers with for projId1.</summary>
    public static string ProjectJson()
    {
        return new JsonObject
        {
            ["id"] = "projId1",
            ["slug"] = "test-mod",
            ["title"] = "Test Mod",
            ["description"] = "A synthetic mod",
            ["body"] = "A synthetic mod body",
            ["status"] = "approved",
            ["project_type"] = "mod",
            ["client_side"] = "required",
            ["server_side"] = "required",
            ["icon_url"] = "",
        }.ToJsonString();
    }

    /// <summary>Write the .mrpack zip (index at the root, overrides/ and mods/ content).</summary>
    public bool WriteZip(string path)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"flint-mrpack-src-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "overrides"));
            Directory.CreateDirectory(Path.Combine(dir, "mods"));
            File.WriteAllText(Path.Combine(dir, "modrinth.index.json"), IndexJson);
            File.WriteAllText(Path.Combine(dir, "overrides", "options.txt"), OptionsTxt);
            File.WriteAllBytes(Path.Combine(dir, "mods", "test.jar"), ModBytes);
            if (OptionalBytes is { } optionalBytes)
            {
                File.WriteAllBytes(Path.Combine(dir, "mods", "optional.jar"), optionalBytes);
            }

            return MmcZip.CompressDir(new AbsolutePath(path), new AbsolutePath(dir));
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (DirectoryNotFoundException) { }
        }
    }
}
