// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Tests for the CurseForge export round-trip: import a manifest.zip first, then export the
// instance back to a CF zip and assert manifest.json (files from the local .pw.toml Flame
// metadata and from the fingerprint API), modlist.html and the overrides/ zip layout
// (resolved files excluded).

using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using FlintLauncher.Core.Config;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.ModPlatform.Flame;
using TruePath;
using Xunit;

namespace FlintLauncher.Tests;

[Collection("FlameApi")]
public class FlameExportTests : IDisposable
{
    private readonly FlameApi _flameApi = new(TestNet.Http, TestNet.Settings);
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"flint-flame-export-{Guid.NewGuid():N}");
    private readonly TestGlobalSettings _global = new();
    private readonly StubFlameServer _server = new();
    private readonly byte[] _downloadBytes = "resolved flame mod binary payload"u8.ToArray();

    public FlameExportTests()
    {
        Directory.CreateDirectory(_root);
        _flameApi.BaseUrl = _server.Url;
    }

    public void Dispose()
    {
        _server.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException) { }
    }

    private string TempPath(string name) => Path.Combine(_root, name);

    /// <summary>Import the synthetic pack (as the FlameImportTests do) and return the staging
    /// path of the created instance folder.</summary>
    private async Task<string> ImportInstance(string name)
    {
        var downloadSha1 = Convert.ToHexString(SHA1.HashData(_downloadBytes))
            .ToLowerInvariant();
        var fileJson = FlamePayloads.FileJson(_server.Url, fileId: 456, sha1: downloadSha1);
        _server.FilesJson = new JsonObject { ["data"] = new JsonArray(JsonNode.Parse(fileJson)) }.ToJsonString();
        _server.ProjectsJson = new JsonObject
        {
            ["data"] = new JsonArray(JsonNode.Parse(FlamePayloads.ProjectJson(id: 123))),
        }.ToJsonString();
        _server.Downloads["mod.bin"] = _downloadBytes;

        var pack = FlamePackBuilder.Create(projectId: 123, fileId: 456);
        var zipPath = TempPath($"pack-{name}.zip");
        Assert.True(pack.WriteZip(zipPath));
        var staging = TempPath($"staging-{name}");
        Directory.CreateDirectory(staging);
        Assert.NotNull(MmcZip.ExtractDir(new AbsolutePath(zipPath), "", new AbsolutePath(staging)));

        var task = new FlameCreationTask(new AbsolutePath(staging), trustedSource: true, _global, TestModrinthApi.Unreachable(), flame: _flameApi);
        task.SetName(name);
        await task.StartAsync();
        Assert.True(task.WasSuccessful, task.FailReason);
        return staging;
    }

    [Fact]
    public async Task Export_RoundTrip_WritesManifestModlistAndOverrides()
    {
        var staging = await ImportInstance("roundtrip");

        // second mod without local metadata: resolved through the fingerprint API
        var unmetaBytes = "unresolved flame mod content"u8.ToArray();
        var unmetaPath = Path.Combine(staging, "minecraft", "mods", "unmeta.jar");
        await File.WriteAllBytesAsync(unmetaPath, unmetaBytes);
        var unmetaFingerprint = FlamePayloads.Murmur(unmetaBytes);
        var unmetaFileJson = FlamePayloads.FileJson(_server.Url, modId: 222, fileId: 999,
            fileName: "unmeta.jar", downloadUrlPath: null,
            fileFingerprint: long.Parse(unmetaFingerprint, CultureInfo.InvariantCulture),
            isAvailable: true);
        _server.FingerprintJson = FlamePayloads.FingerprintJson(new Dictionary<string, string>
        {
            [unmetaFingerprint] = unmetaFileJson,
        });
        // single addon with an empty slug -> fetched through GET /mods/{id} ({"data":{...}})
        _server.ProjectJson = new JsonObject
        {
            ["data"] = JsonNode.Parse(FlamePayloads.ProjectJson(id: 222, name: "Unmeta Mod", slug: "unmeta-mod",
                websiteUrl: "https://www.curseforge.com/projects/unmeta-mod")),
        }.ToJsonString();

        // the export reads the instance through the typed settings stack — the staged
        // instance is Flint format (instance.json), written by the import task itself
        var instance = new MinecraftInstance(
            new InstanceJsonSettingsObject(new AbsolutePath(staging), _global.Environment), new AbsolutePath(staging));
        var output = TempPath("exported.zip");
        var export = new FlamePackExportTask(new FlamePackExportTask.Options
        {
            Flame = _flameApi,
            Name = "Exported Pack",
            Version = "2.0.0",
            Author = "exporter",
            OptionalFiles = true,
            Instance = instance,
            Output = new AbsolutePath(output),
        });
        await export.StartAsync();

        Assert.True(export.WasSuccessful, export.FailReason);
        Assert.True(File.Exists(output));

        using var zip = await ZipFile.OpenReadAsync(output);
        var names = zip.Entries.Select(e => e.FullName).ToHashSet();

        // resolved mods are excluded from the overrides zip
        Assert.Contains("overrides/options.txt", names);
        Assert.Contains("overrides/config/settings.txt", names);
        Assert.Contains("overrides/mods/override.jar", names);
        Assert.DoesNotContain("overrides/mods/test.jar", names);
        Assert.DoesNotContain("overrides/mods/unmeta.jar", names);
        Assert.Contains("manifest.json", names);
        Assert.Contains("modlist.html", names);

        var manifestEntry = zip.GetEntry("manifest.json");
        Assert.NotNull(manifestEntry);
        string manifestJson;
        using (var reader = new StreamReader(await manifestEntry.OpenAsync()))
        {
            manifestJson = await reader.ReadToEndAsync();
        }

        var manifest = JsonNode.Parse(manifestJson)?.AsObject();
        Assert.NotNull(manifest);
        Assert.Equal("minecraftModpack", manifest["manifestType"]?.GetValue<string>());
        Assert.Equal(1, manifest["manifestVersion"]?.GetValue<int>());
        Assert.Equal("Exported Pack", manifest["name"]?.GetValue<string>());
        Assert.Equal("2.0.0", manifest["version"]?.GetValue<string>());
        Assert.Equal("exporter", manifest["author"]?.GetValue<string>());
        Assert.Equal("overrides", manifest["overrides"]?.GetValue<string>());

        var minecraft = manifest["minecraft"]?.AsObject();
        Assert.NotNull(minecraft);
        Assert.Equal("1.18.2", minecraft["version"]?.GetValue<string>());
        var loader = minecraft["modLoaders"]?.AsArray().Single()?.AsObject();
        Assert.NotNull(loader);
        Assert.Equal("forge-40.1.0", loader["id"]?.GetValue<string>());
        Assert.True(loader["primary"]?.GetValue<bool>());

        var files = manifest["files"]?.AsArray();
        Assert.NotNull(files);
        Assert.Equal(2, files.Count);
        var fileIds = files.Select(f => (long?)f?["fileID"]).ToList();
        Assert.Contains(456L, fileIds); // from the local .pw.toml metadata
        Assert.Contains(999L, fileIds); // from the fingerprint API
        Assert.All(files, f => Assert.True((long?)f?["projectID"] is 123 or 222));
        Assert.All(files, f => Assert.True(f?["required"]?.GetValue<bool>()));

        var modlistEntry = zip.GetEntry("modlist.html");
        Assert.NotNull(modlistEntry);
        string modlist;
        using (var reader = new StreamReader(await modlistEntry.OpenAsync()))
        {
            modlist = await reader.ReadToEndAsync();
        }

        Assert.StartsWith("<ul>", modlist);
        Assert.EndsWith("</ul>", modlist);
        // the metadata-resolved mod has no parsed author details -> listed without authors
        Assert.Contains("<li><a href=\"https://www.curseforge.com/projects/123\">Test Mod</a></li>", modlist);
        // the API-resolved mod got its name/author filled in from the project response
        Assert.Contains("<li><a href=\"https://www.curseforge.com/projects/222\">Unmeta Mod (by alice)</a></li>",
            modlist);
    }

    [Fact]
    public async Task Export_UnresolvedModsStayInOverrides()
    {
        var staging = await ImportInstance("unresolved");

        // no fingerprint matches: the fingerprinted mod stays packed in overrides and is
        // absent from the manifest files
        var unmetaBytes = "unresolved flame mod content"u8.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(staging, "minecraft", "mods", "unmeta.jar"), unmetaBytes);
        _server.FingerprintJson = """{"data":{"exactMatches":[]}}""";

        var instance = new MinecraftInstance(
            new InstanceJsonSettingsObject(new AbsolutePath(staging), _global.Environment), new AbsolutePath(staging));
        var output = TempPath("exported-unresolved.zip");
        var export = new FlamePackExportTask(new FlamePackExportTask.Options
        {
            Flame = _flameApi,
            Name = "Exported Pack",
            Version = "2.0.0",
            Author = "exporter",
            OptionalFiles = false,
            Instance = instance,
            Output = new AbsolutePath(output),
        });
        await export.StartAsync();

        Assert.True(export.WasSuccessful, export.FailReason);
        using var zip = await ZipFile.OpenReadAsync(output);
        Assert.Contains("overrides/mods/unmeta.jar", zip.Entries.Select(e => e.FullName));

        var manifestEntry = zip.GetEntry("manifest.json");
        Assert.NotNull(manifestEntry);
        using var reader = new StreamReader(await manifestEntry.OpenAsync());
        var manifest = JsonNode.Parse(await reader.ReadToEndAsync())?.AsObject();
        Assert.NotNull(manifest);
        var files = manifest["files"]?.AsArray();
        Assert.NotNull(files);
        var file = Assert.Single(files)?.AsObject();
        Assert.NotNull(file);
        Assert.Equal(123, file["projectID"]?.GetValue<long>());
        Assert.Equal(456, file["fileID"]?.GetValue<long>());
    }
}
