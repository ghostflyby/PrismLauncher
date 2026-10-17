// SPDX-License-Identifier: GPL-3.0-only
// Test doubles for the Flame layer tests: a stub CurseForge API/download server (free-port
// HttpListener pattern, see StubModrinthServer) plus synthetic CurseForge manifest.zip
// payload builders (manifest.json + overrides) and the file/project/fingerprint JSON
// templates shared by the tests.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.ModPlatform.Hashing;

namespace Flint.Tests;

/// <summary>
/// Routes a local HttpListener as a CurseForge API stub:
///   POST /fingerprints            -> FingerprintJson
///   POST /mods/files              -> FilesJson (batch file fetch)
///   POST /mods                    -> ProjectsJson (batch project fetch)
///   GET  /mods/search             -> SearchJson
///   GET  /categories              -> CategoriesJson
///   GET  /mods/{a}/files/{f}/changelog -> ChangelogJson
///   GET  /mods/{id}/description   -> DescriptionJson
///   GET  /mods/{a}/files?...      -> FilesJson (version list for update checks)
///   GET  /mods/{a}/files/{f}      -> FileJson
///   GET  /mods/{id}               -> ProjectJson (single project: {"data":{...}})
///   GET  /&lt;name&gt;                 -> Downloads[name] (e.g. mod.bin; missing -> 404)
/// </summary>
internal sealed class StubFlameServer : IDisposable
{
    private readonly HttpListener _listener = new();

    public StubFlameServer()
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

    public string FingerprintJson { get; set; } = """{"data":{"exactMatches":[]}}""";
    public string FilesJson { get; set; } = """{"data":[]}""";
    public string ProjectsJson { get; set; } = """{"data":[]}""";
    public string ProjectJson { get; set; } = """{"data":{}}""";
    public string SearchJson { get; set; } = """{"data":[]}""";
    public string CategoriesJson { get; set; } = """{"data":[]}""";
    public string ChangelogJson { get; set; } = """{"data":"changelog text"}""";
    public string DescriptionJson { get; set; } = """{"data":"description text"}""";
    public string FileJson { get; set; } = "{}";

    /// <summary>Payloads served per path suffix, e.g. Downloads["mod.bin"] (missing -> 404).</summary>
    public Dictionary<string, byte[]> Downloads { get; } = new(StringComparer.Ordinal);

    /// <summary>"METHOD path" of every request, in order (for endpoint-hit assertions).</summary>
    public List<string> Requests { get; } = [];

    // last request info (single-request tests only)
    public string LastPath { get; private set; } = "";
    public string LastMethod { get; private set; } = "";
    public Dictionary<string, string> LastQuery { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> LastHeaders { get; } = new(StringComparer.Ordinal);
    public byte[]? LastBody { get; private set; }

    private void Handle(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url!.PathAndQuery;
        LastPath = ctx.Request.Url.GetComponents(UriComponents.AbsoluteUri, UriFormat.SafeUnescaped);
        LastMethod = ctx.Request.HttpMethod;
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
        if (ctx.Request.HttpMethod == "POST" && path.EndsWith("/fingerprints", StringComparison.Ordinal))
        {
            body = Encoding.UTF8.GetBytes(FingerprintJson);
        }
        else if (ctx.Request.HttpMethod == "POST" && path.EndsWith("/mods/files", StringComparison.Ordinal))
        {
            body = Encoding.UTF8.GetBytes(FilesJson);
        }
        else if (ctx.Request.HttpMethod == "POST" && path.EndsWith("/mods", StringComparison.Ordinal))
        {
            body = Encoding.UTF8.GetBytes(ProjectsJson);
        }
        else if (path.Contains("/mods/search", StringComparison.Ordinal))
        {
            body = Encoding.UTF8.GetBytes(SearchJson);
        }
        else if (path.Contains("/categories", StringComparison.Ordinal))
        {
            body = Encoding.UTF8.GetBytes(CategoriesJson);
        }
        else if (path.Contains("/changelog", StringComparison.Ordinal))
        {
            body = Encoding.UTF8.GetBytes(ChangelogJson);
        }
        else if (path.Contains("/description", StringComparison.Ordinal))
        {
            body = Encoding.UTF8.GetBytes(DescriptionJson);
        }
        else if (IsSingleFileGet(path))
        {
            body = Encoding.UTF8.GetBytes(FileJson);
        }
        else if (path.Contains("/files", StringComparison.Ordinal))
        {
            // GET /mods/{id}/files?... (the version list)
            body = Encoding.UTF8.GetBytes(FilesJson);
        }
        else if (path.Contains("/mods/", StringComparison.Ordinal))
        {
            // GET /mods/{id}
            body = Encoding.UTF8.GetBytes(ProjectJson);
        }
        else
        {
            var slash = path.LastIndexOf('/');
            var name = slash >= 0 ? path[(slash + 1)..] : path;
            if (Downloads.TryGetValue(name, out var payload))
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

    // GET /mods/{addonId}/files/{fileId} — a numeric last segment after "/files/"
    private static bool IsSingleFileGet(string path)
    {
        var marker = path.IndexOf("/files/", StringComparison.Ordinal);
        if (marker < 0)
        {
            return false;
        }
        var tail = path[(marker + "/files/".Length)..].Split('?')[0];
        return tail.Length != 0 && tail.All(char.IsDigit);
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

/// <summary>Shared JSON templates for CurseForge file/project/fingerprint payloads.</summary>
internal static class FlamePayloads
{
    /// <summary>A CurseForge "file" object (the shape of loadIndexedPackVersion's input).</summary>
    public static string FileJson(string baseUrl,
        long modId = 123,
        long fileId = 456,
        string displayName = "Test Mod 1.0",
        string fileName = "test.jar",
        string fileDate = "2023-01-01T00:00:00Z",
        int releaseType = 1,
        string? downloadUrlPath = "mod.bin",
        string? gameVersions = null,
        string? sha1 = null,
        string? dependencies = null,
        long? fileFingerprint = null,
        bool isAvailable = true)
    {
        var versions = new JsonArray();
        foreach (var v in JsonObject.Parse(gameVersions ?? """["1.18.2", "forge"]""")!.AsArray())
        {
            versions.Add((JsonNode?)v!.DeepClone());
        }

        var hashes = new JsonArray();
        if (sha1 is not null)
        {
            hashes.Add((JsonNode?)new JsonObject { ["value"] = sha1, ["algo"] = 1 });
        }

        var obj = new JsonObject
        {
            ["id"] = fileId,
            ["modId"] = modId,
            ["displayName"] = displayName,
            ["fileName"] = fileName,
            ["fileDate"] = fileDate,
            ["releaseType"] = releaseType,
            ["gameVersions"] = versions,
            ["downloadUrl"] = downloadUrlPath is null ? null : $"{baseUrl}/{downloadUrlPath}",
            ["hashes"] = hashes,
            ["dependencies"] = JsonObject.Parse(dependencies ?? "[]")!.DeepClone(),
            ["fileFingerprint"] = fileFingerprint ?? fileId,
            ["isAvailable"] = isAvailable,
        };
        return obj.ToJsonString();
    }

    /// <summary>The /fingerprints response for a set of (fingerprint -> file json) matches
    /// (each match wrapped in the {"id", "file"} object of the real response).</summary>
    public static string FingerprintJson(IReadOnlyDictionary<string, string> matches)
    {
        var arr = new JsonArray();
        foreach (var (fingerprint, fileJson) in matches)
        {
            arr.Add((JsonNode?)new JsonObject
            {
                ["id"] = long.TryParse(fingerprint, out var fp) ? fp : 0,
                ["file"] = JsonObject.Parse(fileJson),
            });
        }
        return new JsonObject
        {
            ["data"] = new JsonObject { ["exactMatches"] = arr },
        }.ToJsonString();
    }

    /// <summary>A CurseForge "project" object (the shape of loadIndexedPack's input).</summary>
    public static string ProjectJson(long id = 123,
        string name = "Test Mod",
        string slug = "test-mod",
        string websiteUrl = "https://www.curseforge.com/projects/test-mod",
        long classId = 6,
        string authors = """[{ "name": "alice", "url": "https://www.curseforge.com/members/alice" }]""")
    {
        var obj = new JsonObject
        {
            ["id"] = id,
            ["name"] = name,
            ["slug"] = slug,
            ["summary"] = "A synthetic mod",
            ["classId"] = classId,
            ["links"] = new JsonObject { ["websiteUrl"] = websiteUrl },
            ["authors"] = JsonNode.Parse(authors)!.DeepClone(),
            ["logo"] = new JsonObject { ["title"] = "logo", ["thumbnailUrl"] = "", ["url"] = "" },
        };
        return obj.ToJsonString();
    }

    /// <summary>The murmur2 fingerprint of some content, as a decimal string.</summary>
    public static string Murmur(byte[] bytes) => HashUtils.Hash(bytes, Algorithm.Murmur2);
}

/// <summary>
/// Builds a synthetic CurseForge manifest.zip payload: manifest.json + overrides/ content.
/// Written through MMCZip like the MrPackBuilder.
/// </summary>
internal sealed class FlamePackBuilder
{
    public byte[] ModBytes { get; set; } = "test flame mod jar content 0123456789"u8.ToArray();
    public string OptionsTxt { get; } = "flameOption:1\n";
    public string ConfigTxt { get; } = "key=value\n";
    public string ManifestJson { get; private set; } = "";

    public static FlamePackBuilder Create(long projectId = 123, long fileId = 456,
        string mcVersion = "1.18.2", string loader = "forge-40.1.0")
    {
        var pack = new FlamePackBuilder();
        var manifest = new JsonObject
        {
            ["manifestType"] = "minecraftModpack",
            ["manifestVersion"] = 1,
            ["name"] = "Test Flame Pack",
            ["version"] = "1.0.0",
            ["author"] = "tester",
            ["minecraft"] = new JsonObject
            {
                ["version"] = mcVersion,
                ["modLoaders"] = new JsonArray(new JsonObject
                {
                    ["id"] = loader,
                    ["primary"] = true,
                }),
            },
            ["files"] = new JsonArray(new JsonObject
            {
                ["projectID"] = projectId,
                ["fileID"] = fileId,
                ["required"] = true,
            }),
            ["overrides"] = "overrides",
        };
        pack.ManifestJson = manifest.ToJsonString();
        return pack;
    }

    /// <summary>Write the manifest.zip (manifest.json at the root + overrides/ content).</summary>
    public bool WriteZip(string path)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"flint-flame-src-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "overrides", "config"));
            Directory.CreateDirectory(Path.Combine(dir, "overrides", "mods"));
            File.WriteAllText(Path.Combine(dir, "manifest.json"), ManifestJson);
            File.WriteAllText(Path.Combine(dir, "overrides", "options.txt"), OptionsTxt);
            File.WriteAllText(Path.Combine(dir, "overrides", "config", "settings.txt"), ConfigTxt);
            File.WriteAllBytes(Path.Combine(dir, "overrides", "mods", "override.jar"), ModBytes);
            return MmcZip.CompressDir(path, dir);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
