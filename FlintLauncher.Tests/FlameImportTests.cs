// SPDX-License-Identifier: GPL-3.0-only
// End-to-end tests for the CurseForge import: a synthetic manifest.zip (manifest.json with a
// forge-40.1.0 loader + overrides/) run through the InstanceImportTask dispatch and the
// FlameCreationTask, against the stub CurseForge API. Asserts the instance layout, the forge
// component version, the resolved mod file downloaded from the stub and the per-mod .pw.toml
// written with the [update.curseforge] table, plus the blocked-mods and untrusted-mods
// behaviors.

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.ModPlatform.Flame;
using FlintLauncher.Core.Tasks;
using Xunit;

namespace FlintLauncher.Tests;

[Collection("FlameApi")]
public class FlameImportTests : IDisposable
{
    private readonly FlameApi _flameApi = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"flint-flame-import-{Guid.NewGuid():N}");
    private readonly TestGlobalSettings _global = new();
    private readonly StubFlameServer _server = new();

    private readonly byte[] _downloadBytes = "resolved flame mod binary payload"u8.ToArray();
    private readonly string _downloadSha1;

    public FlameImportTests()
    {
        Directory.CreateDirectory(_root);
        _flameApi.BaseUrl = _server.Url;
        _downloadSha1 = Convert.ToHexString(SHA1.HashData(_downloadBytes))
            .ToLowerInvariant();
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

    private string TempPath(string name) => Path.Combine(_root, name);

    private string TempPath(string name1, string name2) => Path.Combine(_root, name1, name2);

    private void RegisterResolvedMod(long projectId = 123, long fileId = 456, string fileName = "test.jar",
        bool blocked = false)
    {
        var fileJson = FlamePayloads.FileJson(_server.Url, modId: projectId, fileId: fileId, fileName: fileName,
            downloadUrlPath: blocked ? null : "mod.bin", sha1: blocked ? null : _downloadSha1);
        _server.FilesJson = new JsonObject { ["data"] = new JsonArray(JsonNode.Parse(fileJson)) }.ToJsonString();
        _server.ProjectsJson = new JsonObject
        {
            ["data"] = new JsonArray(JsonNode.Parse(FlamePayloads.ProjectJson(id: projectId))),
        }.ToJsonString();
        if (!blocked)
        {
            _server.Downloads["mod.bin"] = _downloadBytes;
        }
    }

    private InstanceList CreateList()
    {
        var instancesDir = TempPath("instances");
        Directory.CreateDirectory(instancesDir);
        return new InstanceList([instancesDir]) { Environment = _global };
    }

    // ------------------------------------------------------------- dispatch (e2e)

    [Fact]
    public async Task FlameZipImport_EndToEnd_CreatesInstanceWithForgeAndMetadata()
    {
        var pack = FlamePackBuilder.Create(projectId: 123, fileId: 456);
        RegisterResolvedMod();
        var zipPath = TempPath("pack.zip");
        Assert.True(pack.WriteZip(zipPath));

        var list = CreateList();
        var task = new InstanceImportTask(new Uri(zipPath), trustedSource: true, flame: _flameApi);
        task.SetName("Flame Imported");
        var wrapped = list.WrapInstanceTask(task);
        await wrapped.StartAsync();

        Assert.True(task.WasSuccessful, task.FailReason);

        var staging = TempPath("instances", "Flame Imported");
        // the overrides landed in the game root
        Assert.Equal(pack.OptionsTxt, await File.ReadAllTextAsync(Path.Combine(staging, "minecraft", "options.txt")));
        Assert.Equal(pack.ConfigTxt,
            await File.ReadAllTextAsync(Path.Combine(staging, "minecraft", "config", "settings.txt")));
        Assert.Equal(pack.ModBytes,
            await File.ReadAllBytesAsync(Path.Combine(staging, "minecraft", "mods", "override.jar")));
        // the resolved mod file was downloaded from the stub
        Assert.Equal(_downloadBytes, await File.ReadAllBytesAsync(Path.Combine(staging, "minecraft", "mods", "test.jar")));
        // the manifest is kept inside flame/ for later version changes
        Assert.True(File.Exists(Path.Combine(staging, "flame", "manifest.json")));

        // loader components: minecraft 1.18.2 + forge 40.1.0
        using (var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(staging, "mmc-pack.json"))))
        {
            var components = doc.RootElement.GetProperty("components").EnumerateArray()
                .Select(c => (Uid: c.GetProperty("uid").GetString(), Version: c.GetProperty("version").GetString()))
                .ToList();
            Assert.Contains(components, c => c.Uid == "net.minecraft" && c.Version == "1.18.2");
            Assert.Contains(components, c => c.Uid == "net.minecraftforge" && c.Version == "40.1.0");
        }
        Assert.Contains("Flame Imported", await File.ReadAllTextAsync(Path.Combine(staging, "instance.json")));

        // per-mod .pw.toml with the Flame ([update.curseforge]) table
        var metadataToml = await File.ReadAllTextAsync(Path.Combine(staging, "minecraft", "mods", ".index", "test-mod.pw.toml"));
        Assert.Contains("[update.curseforge]", metadataToml);
        Assert.Contains("file-id = 456", metadataToml);
        Assert.Contains("project-id = 123", metadataToml);
        Assert.Contains("mode = \"metadata:curseforge\"", metadataToml);
        Assert.Contains("filename = \"test.jar\"", metadataToml);
        Assert.Contains("hash-format = \"sha1\"", metadataToml);
        Assert.Contains($"hash = \"{_downloadSha1}\"", metadataToml);
        Assert.Contains("x-prismlauncher-loaders = [\"forge\"]", metadataToml);
        Assert.Contains("x-prismlauncher-mc-versions = [\"1.18.2\"]", metadataToml);
        Assert.Contains("x-prismlauncher-release-type = \"Release\"", metadataToml);
    }

    [Fact]
    public async Task FlameCreationTask_FabricLoader_SetsFabricComponent()
    {
        var pack = FlamePackBuilder.Create(mcVersion: "1.20.1", loader: "fabric-0.14.21");
        RegisterResolvedMod();
        var staging = TempPath("staging-fabric");
        Directory.CreateDirectory(staging);
        var zipPath = TempPath("pack-fabric.zip");
        Assert.True(pack.WriteZip(zipPath));
        Assert.NotNull(MmcZip.ExtractDir(zipPath, "", staging));

        var task = new FlameCreationTask(staging, trustedSource: true, _global, flame: _flameApi);
        task.SetName("Fabric Flame");
        await task.StartAsync();

        Assert.True(task.WasSuccessful, task.FailReason);
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(staging, "mmc-pack.json")));
        var components = doc.RootElement.GetProperty("components").EnumerateArray()
            .Select(c => (Uid: c.GetProperty("uid").GetString(), Version: c.GetProperty("version").GetString()))
            .ToList();
        Assert.Contains(components, c => c.Uid == "net.minecraft" && c.Version == "1.20.1");
        Assert.Contains(components, c => c.Uid == "net.fabricmc.fabric-loader" && c.Version == "0.14.21");
    }

    // ---------------------------------------------------------------- blocked mods

    [Fact]
    public async Task BlockedMods_FailInstallListingThem()
    {
        var pack = FlamePackBuilder.Create();
        RegisterResolvedMod(blocked: true);
        var staging = TempPath("staging-blocked");
        Directory.CreateDirectory(staging);
        var zipPath = TempPath("pack-blocked.zip");
        Assert.True(pack.WriteZip(zipPath));
        Assert.NotNull(MmcZip.ExtractDir(zipPath, "", staging));

        var task = new FlameCreationTask(staging, trustedSource: true, _global, flame: _flameApi);
        task.SetName("Blocked Pack");
        await task.StartAsync();

        Assert.Equal(TaskState.Failed, task.State);
        Assert.Contains("not available for download", task.FailReason);
        // the blocked mod data is exposed for the BlockedModsDialog flow
        var blocked = Assert.Single(task.BlockedMods);
        Assert.Equal("test.jar", blocked.Name);
        Assert.Equal("https://www.curseforge.com/projects/test-mod/download/456", blocked.WebsiteUrl);
        Assert.False(blocked.Matched);
        // nothing was downloaded
        Assert.False(File.Exists(Path.Combine(staging, "minecraft", "mods", "test.jar")));
    }

    [Fact]
    public async Task BlockedMods_MatchedLocallyAreCopiedIntoTheInstance()
    {
        var pack = FlamePackBuilder.Create();
        RegisterResolvedMod(blocked: true);
        var staging = TempPath("staging-matched");
        Directory.CreateDirectory(staging);
        var zipPath = TempPath("pack-matched.zip");
        Assert.True(pack.WriteZip(zipPath));
        Assert.NotNull(MmcZip.ExtractDir(zipPath, "", staging));

        var localCopy = TempPath("local-test.jar");
        await File.WriteAllBytesAsync(localCopy, _downloadBytes);

        var task = new FlameCreationTask(staging, trustedSource: true, _global, flame: _flameApi)
        {
            // stand-in for the BlockedModsDialog matching step
            BlockedModLocalFileResolver = _ => localCopy,
        };
        task.SetName("Matched Pack");
        await task.StartAsync();

        Assert.True(task.WasSuccessful, task.FailReason);
        // the matched local file was copied into the instance's mods folder
        Assert.Equal(_downloadBytes,
            await File.ReadAllBytesAsync(Path.Combine(staging, "minecraft", "mods", "test.jar")));
    }

    // -------------------------------------------------------------- untrusted mods

    [Fact]
    public async Task UntrustedMods_FailWhenNotTrusted()
    {
        // the pack ships a mod inside its overrides: importing from an untrusted source
        // fails listing it (the C++ dialog defaults to rejecting)
        var pack = FlamePackBuilder.Create();
        RegisterResolvedMod();
        var staging = TempPath("staging-untrusted");
        Directory.CreateDirectory(staging);
        var zipPath = TempPath("pack-untrusted.zip");
        Assert.True(pack.WriteZip(zipPath));
        Assert.NotNull(MmcZip.ExtractDir(zipPath, "", staging));

        var task = new FlameCreationTask(staging, trustedSource: false, _global, flame: _flameApi);
        task.SetName("Untrusted Pack");
        await task.StartAsync();

        Assert.Equal(TaskState.Failed, task.State);
        Assert.Contains("untrusted", task.FailReason);
        Assert.Contains("mods/override.jar", task.FailReason);
    }

    [Fact]
    public async Task UntrustedMods_TrustedSourceInstalls()
    {
        var pack = FlamePackBuilder.Create();
        RegisterResolvedMod();
        var staging = TempPath("staging-trusted");
        Directory.CreateDirectory(staging);
        var zipPath = TempPath("pack-trusted.zip");
        Assert.True(pack.WriteZip(zipPath));
        Assert.NotNull(MmcZip.ExtractDir(zipPath, "", staging));

        var task = new FlameCreationTask(staging, trustedSource: true, _global, flame: _flameApi);
        task.SetName("Trusted Pack");
        await task.StartAsync();

        Assert.True(task.WasSuccessful, task.FailReason);
        Assert.True(File.Exists(Path.Combine(staging, "minecraft", "mods", "override.jar")));
    }

    // ------------------------------------------------------------------ loaders

    [Fact]
    public async Task UnknownLoader_WarnsAndInstallsVanillaComponents()
    {
        var pack = FlamePackBuilder.Create(loader: "liteloader-1.12");
        RegisterResolvedMod();
        var staging = TempPath("staging-unknown-loader");
        Directory.CreateDirectory(staging);
        await File.WriteAllTextAsync(Path.Combine(staging, "manifest.json"), pack.ManifestJson);

        var task = new FlameCreationTask(staging, trustedSource: true, _global, flame: _flameApi);
        task.SetName("Unknown Loader");
        await task.StartAsync();

        Assert.True(task.WasSuccessful, task.FailReason);
        Assert.Contains(task.Warnings, w => w.Contains("Unknown mod loader in manifest"));
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(staging, "mmc-pack.json")));
        var components = doc.RootElement.GetProperty("components").EnumerateArray()
            .Select(c => c.GetProperty("uid").GetString()).ToList();
        Assert.Equal(["net.minecraft"], components);
    }

    [Fact]
    public async Task Overrides_PathTraversal_IsRejected()
    {
        // upstream #6183: the manifest is untrusted — an overrides path escaping the staging
        // root must abort the import, not move arbitrary directories
        var pack = FlamePackBuilder.Create();
        RegisterResolvedMod();
        var manifestObj = JsonObject.Parse(pack.ManifestJson)!.AsObject();
        manifestObj["overrides"] = "../outside";
        var staging = TempPath("staging-traversal");
        Directory.CreateDirectory(staging);
        await File.WriteAllTextAsync(Path.Combine(staging, "manifest.json"), manifestObj.ToJsonString());

        var outsideMarker = TempPath("outside");
        Directory.CreateDirectory(outsideMarker);
        await File.WriteAllTextAsync(Path.Combine(outsideMarker, "sentinel.txt"), "must stay");

        var task = new FlameCreationTask(staging, trustedSource: true, _global, flame: _flameApi);
        task.SetName("Traversal");
        await task.StartAsync();

        Assert.Equal(TaskState.Failed, task.State);
        Assert.Contains("security risk", task.FailReason);
        // the targeted directory was not touched
        Assert.True(Directory.Exists(outsideMarker));
        Assert.True(File.Exists(Path.Combine(outsideMarker, "sentinel.txt")));
        Assert.False(Directory.Exists(Path.Combine(staging, "minecraft")));
    }

    [Fact]
    public async Task RecommendedLoaderVersion_WithoutLookupHook_Fails()
    {
        var pack = FlamePackBuilder.Create(loader: "forge-recommended");
        RegisterResolvedMod();
        // rebuild the manifest with the "recommended" loader version
        var manifestObj = JsonObject.Parse(pack.ManifestJson)!.AsObject();
        ((JsonObject)((JsonArray)manifestObj["minecraft"]!["modLoaders"]!)[0]!)["id"] = "forge-recommended";
        var staging = TempPath("staging-recommended");
        Directory.CreateDirectory(staging);
        await File.WriteAllTextAsync(Path.Combine(staging, "manifest.json"), manifestObj.ToJsonString());

        var task = new FlameCreationTask(staging, trustedSource: true, _global, flame: _flameApi);
        task.SetName("Recommended");
        await task.StartAsync();

        Assert.Equal(TaskState.Failed, task.State);
        Assert.Contains("Failed to get local metadata index", task.FailReason);

        // with a lookup hook the recommended version resolves (per-task hook, no global reset)
        var staging2 = TempPath("staging-recommended2");
        Directory.CreateDirectory(staging2);
        await File.WriteAllTextAsync(Path.Combine(staging2, "manifest.json"), manifestObj.ToJsonString());
        var task2 = new FlameCreationTask(staging2, trustedSource: true, _global, flame: _flameApi)
        {
            RecommendedVersionLookup = (uid, loaderType, mcVersion) =>
            {
                Assert.Equal("net.minecraftforge", uid);
                Assert.Equal("forge", loaderType);
                Assert.Equal("1.18.2", mcVersion);
                return "40.2.0";
            },
        };
        await task2.StartAsync();
        Assert.True(task2.WasSuccessful, task2.FailReason);
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(staging2, "mmc-pack.json")));
        Assert.Contains(doc.RootElement.GetProperty("components").EnumerateArray(),
            c => c.GetProperty("uid").GetString() == "net.minecraftforge"
                 && c.GetProperty("version").GetString() == "40.2.0");
    }

    [Fact]
    public async Task MissingOverridesFolder_WarnsButInstalls()
    {
        var pack = FlamePackBuilder.Create();
        RegisterResolvedMod();
        // strip the overrides from the zip by writing a manifest-only staging folder
        var staging = TempPath("staging-missing-overrides");
        Directory.CreateDirectory(staging);
        await File.WriteAllTextAsync(Path.Combine(staging, "manifest.json"), pack.ManifestJson);

        var task = new FlameCreationTask(staging, trustedSource: true, _global, flame: _flameApi);
        task.SetName("Missing Overrides");
        await task.StartAsync();

        Assert.True(task.WasSuccessful, task.FailReason);
        Assert.Contains(task.Warnings, w => w.Contains("overrides folder (overrides) is missing"));
    }

    [Fact]
    public async Task BadManifest_FailsWithClearMessage()
    {
        var staging = TempPath("staging-bad-manifest");
        Directory.CreateDirectory(staging);
        await File.WriteAllTextAsync(Path.Combine(staging, "manifest.json"),
            """{"manifestType":"minecraftModpack","manifestVersion":9}""");

        var task = new FlameCreationTask(staging, trustedSource: true, _global, flame: _flameApi);
        task.SetName("Bad Manifest");
        await task.StartAsync();

        Assert.Equal(TaskState.Failed, task.State);
        Assert.Contains("Could not understand pack manifest", task.FailReason);
        Assert.Contains("Unknown manifest version", task.FailReason);
    }

    [Fact]
    public async Task OptionalFile_InstallsDisabled()
    {
        // file 456 is required=false in the manifest and comes without a selected optional
        // list -> installs with the ".disabled" suffix (headless equivalent of the dialog)
        var pack = FlamePackBuilder.Create();
        // rewrite the manifest so the file is optional
        var manifestObj = JsonObject.Parse(pack.ManifestJson)!.AsObject();
        ((JsonObject)((JsonArray)manifestObj["files"]!)[0]!)["required"] = false;
        var staging = TempPath("staging-optional");
        Directory.CreateDirectory(staging);
        await File.WriteAllTextAsync(Path.Combine(staging, "manifest.json"), manifestObj.ToJsonString());

        RegisterResolvedMod();
        var task = new FlameCreationTask(staging, trustedSource: true, _global, flame: _flameApi);
        task.SetName("Optional Pack");
        await task.StartAsync();

        Assert.True(task.WasSuccessful, task.FailReason);
        Assert.True(File.Exists(Path.Combine(staging, "minecraft", "mods", "test.jar.disabled")));
        Assert.False(File.Exists(Path.Combine(staging, "minecraft", "mods", "test.jar")));
        // optional blocked mods are flagged disabled in the blocked-mods data
    }
}
