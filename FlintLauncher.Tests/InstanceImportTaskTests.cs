// SPDX-License-Identifier: GPL-3.0-only
// Tests for the InstanceImportTask dispatch: modpack type detection by marker file
// (modrinth.index.json -> Modrinth, manifest.json -> Flame (FlameCreationTask, P6-b),
// bin/modpack.jar -> Technic (TechnicPackProcessor, P6-b), instance.cfg -> plain folder
// import, unknown -> error), plus the download-from-URL path through the meta cache.
using System.IO.Compression;
using System.Text.Json;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.ModPlatform.Modrinth;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Tasks;
using Xunit;

namespace Flint.Tests;

[Collection("ModrinthApi")]
public class InstanceImportTaskTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"flint-import-{Guid.NewGuid():N}");
    private readonly TestGlobalSettings _global = new();
    private readonly StubModrinthServer _server = new();

    private readonly ModrinthApi _modrinth = new();

    public InstanceImportTaskTests()
    {
        Directory.CreateDirectory(_root);
        _modrinth.BaseUrl = _server.Url;
        _modrinth.DownloadHost = "127.0.0.1";
    }

    public void Dispose()
    {
        _server.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private InstanceList CreateList()
    {
        var instancesDir = Path.Combine(_root, "instances");
        Directory.CreateDirectory(instancesDir);
        return new InstanceList(_global, [instancesDir]);
    }

    private async Task<FlintTask> RunImport(Uri source, IReadOnlyDictionary<string, string>? extraInfo = null,
        HttpMetaCache? metaCache = null)
    {
        var list = CreateList();
        var task = new InstanceImportTask(source, trustedSource: true, extraInfo, _modrinth)
        {
            MetaCache = metaCache,
        };
        task.SetName("Imported");
        var wrapped = list.WrapInstanceTask(task);
        await wrapped.StartAsync();
        return task;
    }

    private string WriteMrpack()
    {
        var pack = MrPackBuilder.Create(_server.Url);
        _server.Downloads["mod.bin"] = pack.ModBytes;
        _server.VersionFiles[pack.ModSha512] = MrPackBuilder.VersionJson(_server.Url, pack);
        _server.Projects["projId1"] = MrPackBuilder.ProjectJson();

        var path = Path.Combine(_root, "pack.mrpack");
        Assert.True(pack.WriteZip(path));
        return path;
    }

    [Fact]
    public async Task MrpackZip_DispatchesToModrinthCreation()
    {
        var mrpackPath = WriteMrpack();

        var task = await RunImport(new Uri(mrpackPath));

        Assert.True(task.WasSuccessful, task.FailReason);
        // the instance was created from the Modrinth pack
        var instanceDir = Path.Combine(_root, "instances", "Imported");
        Assert.True(Directory.Exists(instanceDir));
        Assert.True(File.Exists(Path.Combine(instanceDir, "minecraft", "mods", "test.jar")));
        Assert.True(File.Exists(Path.Combine(instanceDir, "mrpack", "modrinth.index.json")));
        Assert.True(File.Exists(Path.Combine(instanceDir, "mmc-pack.json")));
        // the net.minecraft component came from the pack's dependencies
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(instanceDir, "mmc-pack.json")));
        var components = doc.RootElement.GetProperty("components").EnumerateArray().ToList();
        Assert.Contains(components, c => c.GetProperty("uid").GetString() == "net.minecraft"
                                         && c.GetProperty("version").GetString() == "1.20.1");
    }

    [Fact]
    public async Task MultiMcZip_PlainFolderImport()
    {
        var zipPath = Path.Combine(_root, "multimc.zip");
        var dir = Path.Combine(_root, "pack-src");
        var packDir = Path.Combine(dir, "MyModpack");
        Directory.CreateDirectory(Path.Combine(packDir, ".minecraft"));
        File.WriteAllText(Path.Combine(packDir, "instance.cfg"),
            "InstanceType=OneSix\nname=Original Name\niconKey=default\n");
        File.WriteAllText(Path.Combine(packDir, ".minecraft", "options.txt"), "keepMe=1\n");
        Assert.True(FlintLauncher.Core.IO.MMCZip.CompressDir(zipPath, packDir));

        var task = await RunImport(new Uri(zipPath));

        Assert.True(task.WasSuccessful, task.FailReason);
        // the extracted pack folder landed in the staging instance dir and was renamed
        var instanceDir = Path.Combine(_root, "instances", "Imported");
        Assert.True(Directory.Exists(instanceDir));
        var cfg = File.ReadAllText(Path.Combine(instanceDir, "instance.cfg"));
        Assert.Contains("name=Imported", cfg);
        Assert.True(File.Exists(Path.Combine(instanceDir, ".minecraft", "options.txt")));
    }

    [Fact]
    public async Task FlameZip_DispatchesToFlameCreation()
    {
        // the Flame branch now dispatches to FlameCreationTask; this pack has no files, so
        // the full flow runs without network and creates a vanilla-components instance
        var zipPath = Path.Combine(_root, "flame.zip");
        var dir = Path.Combine(_root, "flame-src");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"),
            """{"manifestType":"minecraftModpack","manifestVersion":1,"minecraft":{"version":"1.20.1"},"files":[],"overrides":"overrides"}""");
        Assert.True(FlintLauncher.Core.IO.MMCZip.CompressDir(zipPath, dir));

        var task = await RunImport(new Uri(zipPath));

        Assert.True(task.WasSuccessful, task.FailReason);
        var instanceDir = Path.Combine(_root, "instances", "Imported");
        Assert.True(Directory.Exists(instanceDir));
        Assert.True(File.Exists(Path.Combine(instanceDir, "flame", "manifest.json")));
        Assert.True(File.Exists(Path.Combine(instanceDir, "mmc-pack.json")));
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(instanceDir, "mmc-pack.json")));
        Assert.Contains(doc.RootElement.GetProperty("components").EnumerateArray(),
            c => c.GetProperty("uid").GetString() == "net.minecraft"
                 && c.GetProperty("version").GetString() == "1.20.1");
    }

    [Fact]
    public async Task TechnicZip_DispatchesToTechnicPackProcessor()
    {
        // the Technic branch now runs the TechnicPackProcessor; a bin/version.json marker
        // makes this the plain modded flavor, so the flow is fully offline
        var zipPath = Path.Combine(_root, "technic.zip");
        var dir = Path.Combine(_root, "technic-src");
        Directory.CreateDirectory(Path.Combine(dir, "bin"));
        Directory.CreateDirectory(Path.Combine(dir, "modpack"));
        File.WriteAllText(Path.Combine(dir, "bin", "version.json"),
            """{"inheritsFrom":"1.20.1","id":"technic-test","libraries":[]}""");
        File.WriteAllText(Path.Combine(dir, "modpack", "options.txt"), "keepMe=1\n");
        Assert.True(FlintLauncher.Core.IO.MMCZip.CompressDir(zipPath, dir));

        var task = await RunImport(new Uri(zipPath));

        Assert.True(task.WasSuccessful, task.FailReason);
        var instanceDir = Path.Combine(_root, "instances", "Imported");
        Assert.True(Directory.Exists(instanceDir));
        // the pack contents landed in minecraft/ and the version.json flavor set the component
        Assert.True(File.Exists(Path.Combine(instanceDir, "minecraft", "modpack", "options.txt")));
        Assert.Contains("name=Imported", File.ReadAllText(Path.Combine(instanceDir, "instance.cfg")));
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(instanceDir, "mmc-pack.json")));
        Assert.Contains(doc.RootElement.GetProperty("components").EnumerateArray(),
            c => c.GetProperty("uid").GetString() == "net.minecraft"
                 && c.GetProperty("version").GetString() == "1.20.1");
    }

    [Fact]
    public async Task TechnicZip_WithCorruptModpackJar_Fails()
    {
        // a bin/modpack.jar marker that is not a zip fails the pack processor
        var zipPath = Path.Combine(_root, "technic-bad.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("bin/modpack.jar");
            using var writer = entry.Open();
            var bytes = "jar bytes"u8.ToArray();
            writer.Write(bytes);
        }

        var task = await RunImport(new Uri(zipPath));

        Assert.Equal(TaskState.Failed, task.State);
        Assert.Contains("bin/modpack.jar", task.FailReason);
    }

    [Fact]
    public async Task UnknownZip_FailsWithClearMessage()
    {
        var zipPath = Path.Combine(_root, "unknown.zip");
        var dir = Path.Combine(_root, "unknown-src");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "readme.txt"), "nothing in here");
        Assert.True(FlintLauncher.Core.IO.MMCZip.CompressDir(zipPath, dir));

        var task = await RunImport(new Uri(zipPath));

        Assert.Equal(TaskState.Failed, task.State);
        Assert.Contains("does not contain a recognized modpack type", task.FailReason);
    }

    [Fact]
    public async Task UrlImport_DownloadsThroughMetaCache()
    {
        var mrpackPath = WriteMrpack();

        var cacheDir = Path.Combine(_root, "cache");
        var cache = new HttpMetaCache(Path.Combine(_root, "metacache"));
        cache.AddBase("general", cacheDir);

        // the C++ import URL shape (modrinth data/<id>/versions/<id>/...) — the pack id is
        // derived from it when no extra info is given
        var packId = "qvIfYCYJ";
        var source = new Uri($"{_server.Url}/data/{packId}/versions/1.0.0/pack.mrpack");
        _server.Downloads["pack.mrpack"] = await File.ReadAllBytesAsync(mrpackPath);

        var task = await RunImport(source, metaCache: cache);

        Assert.True(task.WasSuccessful, task.FailReason);
        var instanceDir = Path.Combine(_root, "instances", "Imported");
        Assert.True(File.Exists(Path.Combine(instanceDir, "minecraft", "mods", "test.jar")));
        // the download went through the cache ("general" base)
        Assert.True(Directory.GetFiles(cacheDir, "*", SearchOption.AllDirectories).Any(),
            "the pack should have been cached");
    }

    [Fact]
    public async Task UnreadableZip_Fails()
    {
        var badPath = Path.Combine(_root, "bad.zip");
        await File.WriteAllBytesAsync(badPath, "this is not a zip file"u8.ToArray());

        var task = await RunImport(new Uri(badPath));

        Assert.Equal(TaskState.Failed, task.State);
        Assert.Contains("Unable to open supplied modpack zip file", task.FailReason);
    }
}
