// SPDX-License-Identifier: GPL-3.0-only
// Test doubles for the Technic layer tests: a stub Technic Platform/Solder server
// (free-port HttpListener pattern, see ModrinthTestSupport) serving search/trending
// results, pack metadata, Solder pack/build listings and file downloads, plus synthetic
// Technic zip pack builders (bin/version.json / bin/modpack.jar / vanilla flavors).

using System.IO.Compression;
using System.Net;
using System.Text;
using FlintLauncher.Core.IO;
using Xunit;

namespace FlintLauncher.Tests;

/// <summary>
/// Routes a local HttpListener as a Technic Platform + Solder stub:
///   GET /trending?build=...            -> SearchListJson
///   GET /search?build=...&amp;q=...        -> SearchListJson
///   GET /modpack/{slug}?build=...      -> PackMetaJson[slug] (Platform metadata)
///   GET /modpack/{slug}                -> SolderPackJson[slug] (Solder build listing)
///   GET /modpack/{slug}/{build}        -> SolderBuildJson["{slug}/{build}"]
///   GET /&lt;anything else&gt;               -> Downloads[last path segment]
/// </summary>
internal sealed class StubTechnicServer : IDisposable
{
    private readonly HttpListener _listener;

    public StubTechnicServer()
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

    public string SearchListJson { get; set; } = """{"modpacks":[]}""";

    /// <summary>Platform pack metadata, keyed by slug (answered when a ?build= query is present).</summary>
    public Dictionary<string, string> PackMetaJson { get; } = new(StringComparer.Ordinal);

    /// <summary>Solder build listing (recommended/latest/builds), keyed by slug.</summary>
    public Dictionary<string, string> SolderPackJson { get; } = new(StringComparer.Ordinal);

    /// <summary>Solder build (minecraft + mods), keyed by "{slug}/{build}".</summary>
    public Dictionary<string, string> SolderBuildJson { get; } = new(StringComparer.Ordinal);

    /// <summary>Payloads served per path segment name, e.g. Downloads["moda.zip"].</summary>
    public Dictionary<string, byte[]> Downloads { get; } = new(StringComparer.Ordinal);

    // last request info
    public string LastPath { get; private set; } = "";
    public Dictionary<string, string> LastQuery { get; } = new(StringComparer.Ordinal);

    private void Handle(HttpListenerContext ctx)
    {
        LastPath = ctx.Request.Url!.AbsolutePath;
        LastQuery.Clear();
        foreach (var key in ctx.Request.QueryString.AllKeys)
        {
            if (key is not null)
            {
                LastQuery[key] = ctx.Request.QueryString[key] ?? "";
            }
        }

        var segments = LastPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        byte[] body;
        if (segments is ["trending" or "search"])
        {
            body = Encoding.UTF8.GetBytes(SearchListJson);
        }
        else if (segments.Length is 2 or 3 && segments[0] == "modpack")
        {
            var slug = segments[1];
            if (segments.Length == 3)
            {
                // /modpack/{slug}/{build} — Solder build
                var key = $"{slug}/{segments[2]}";
                if (!SolderBuildJson.TryGetValue(key, out var buildJson))
                {
                    ctx.Response.StatusCode = 404;
                    return;
                }

                body = Encoding.UTF8.GetBytes(buildJson);
            }
            else if (LastQuery.ContainsKey("build"))
            {
                // /modpack/{slug}?build=... — Platform metadata
                if (!PackMetaJson.TryGetValue(slug, out var metaJson))
                {
                    ctx.Response.StatusCode = 404;
                    return;
                }

                body = Encoding.UTF8.GetBytes(metaJson);
            }
            else
            {
                // /modpack/{slug} — Solder build listing
                if (!SolderPackJson.TryGetValue(slug, out var packJson))
                {
                    ctx.Response.StatusCode = 404;
                    return;
                }

                body = Encoding.UTF8.GetBytes(packJson);
            }
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
        catch (ObjectDisposedException) { }
    }
}

/// <summary>Builds synthetic Technic zip modpacks with the three processor "flavors".</summary>
internal sealed class TechnicZipPackBuilder
{
    /// <summary>version.json content for the bin/version.json flavor (or inside modpack.jar).</summary>
    public string VersionJson { get; set; } =
        """{"inheritsFrom":"1.20.1","id":"technic-test","libraries":[{"name":"net.minecraftforge:forge:1.20.1-42.0.0"}]}""";

    /// <summary>fmlversion.properties content ("" = omit the file).</summary>
    public string FmlVersionProperties { get; set; } = "";

    /// <summary>forgeversion.properties content ("" = omit the file; jar flavor without version.json).</summary>
    public string ForgeVersionProperties { get; set; } = "";

    /// <summary>Whether the pack carries bin/modpack.jar (instead of bin/version.json).</summary>
    public bool WithModpackJar { get; set; }

    /// <summary>Whether the pack carries bin/version.json (false + no jar = the "vanilla" flavor).</summary>
    public bool WithVersionJson { get; set; } = true;

    /// <summary>Whether modpack.jar embeds its own version.json.</summary>
    public bool JarEmbedsVersionJson { get; set; }

    public string OptionsTxt { get; set; } = "technic=1\n";

    /// <summary>Writes the pack zip; returns false on compression failure.</summary>
    public bool WriteZip(string path)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"flint-technic-src-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "modpack"));
            File.WriteAllText(Path.Combine(dir, "modpack", "options.txt"), OptionsTxt);

            if (WithModpackJar)
            {
                Directory.CreateDirectory(Path.Combine(dir, "bin"));
                var jarPath = Path.Combine(dir, "bin", "modpack.jar");
                WriteModpackJar(jarPath);
            }
            else if (WithVersionJson)
            {
                Directory.CreateDirectory(Path.Combine(dir, "bin"));
                File.WriteAllText(Path.Combine(dir, "bin", "version.json"), VersionJson);
            }

            return MmcZip.CompressDir(path, dir);
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

    private void WriteModpackJar(string jarPath)
    {
        using var stream = new FileStream(jarPath, FileMode.Create);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        if (JarEmbedsVersionJson)
        {
            var entry = zip.CreateEntry("version.json");
            using var writer = new StreamWriter(entry.Open());
            writer.Write(VersionJson);
        }

        if (FmlVersionProperties.Length != 0)
        {
            var entry = zip.CreateEntry("fmlversion.properties");
            using var writer = new StreamWriter(entry.Open());
            writer.Write(FmlVersionProperties);
        }

        if (ForgeVersionProperties.Length != 0)
        {
            var entry = zip.CreateEntry("forgeversion.properties");
            using var writer = new StreamWriter(entry.Open());
            writer.Write(ForgeVersionProperties);
        }
    }

    /// <summary>A small "Solder mod" zip with content under mods/.</summary>
    public static byte[] BuildSolderModZip(string innerPath, byte[] content)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"flint-solder-mod-{Guid.NewGuid():N}");
        try
        {
            var inner = Path.Combine(dir, innerPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(inner)!);
            File.WriteAllBytes(inner, content);
            var zipPath = Path.Combine(dir, "mod.zip");
            Assert.True(MmcZip.CompressDir(zipPath, dir));
            return File.ReadAllBytes(zipPath);
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
