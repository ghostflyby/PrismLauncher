// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Tests for the "apply update" orchestration (ModUpdateTask over per-mod
// ResourceDownloadTasks, the headless port of ModFolderPage::updateMods): an existing mod
// with old .pw.toml metadata, the Flame check-update pass returning a newer file, then the
// apply pass replacing the jar and refreshing its metadata; plus the failure semantics (a
// failing download does not abort its siblings, the model still refreshes, and the task
// fails with the download's reason).

using System.Security.Cryptography;
using System.Text.Json.Nodes;
using FlintLauncher.ExternalApi;
using FlintLauncher.ExternalApi.Flame;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Minecraft.Resources;
using FlintLauncher.Core.ModPlatform;
using FlintLauncher.Core.ModPlatform.Flame;
using FlintLauncher.Core.ModPlatform.Packwiz;
using FlintLauncher.Core.Versioning;
using TruePath;
using Xunit;

namespace FlintLauncher.Tests;

[Collection("FlameApi")]
public class ModUpdateTaskTests : IDisposable
{
    private readonly FlameClient _flameApi = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"flint-mod-update-{Guid.NewGuid():N}");
    private readonly StubFlameServer _server = new();
    private readonly ModInstallTestEnv _env;

    public ModUpdateTaskTests()
    {
        Directory.CreateDirectory(_root);
        _flameApi.Options.BaseUrl = _server.Url;
        _env = new ModInstallTestEnv(_root);
    }

    public void Dispose()
    {
        _server.Dispose();
        _env.Dispose();
    }

    private string ModsDir => Path.Combine(_root, "mods");

    private string IndexDir => Path.Combine(ModsDir, ".index");

    private static string Sha1(byte[] bytes) => Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();

    /// <summary>An instance (mc 1.18.2 + forge) with test.jar installed, tracked by metadata.</summary>
    private (MinecraftInstance Instance, ModFolderModel Model, Mod Mod) SetupInstalledMod(byte[] oldBytes)
    {
        Directory.CreateDirectory(IndexDir);
        File.WriteAllBytes(Path.Combine(ModsDir, "test.jar"), oldBytes);
        Packwiz.UpdateModIndex(new AbsolutePath(IndexDir), new Packwiz.Mod
        {
            Slug = "test-mod",
            Name = "Test Mod",
            Filename = "test.jar",
            Provider = ResourceProvider.Flame,
            ProjectId = "123",
            FileId = "456",
            VersionNumber = "1.0.0",
            HashFormat = "sha1",
            Hash = Sha1(oldBytes),
            Mode = "metadata:curseforge",
            Loaders = ModLoaderType.Forge,
        });

        var model = new ModFolderModel(MaterializedDirectory.Ensure(new AbsolutePath(ModsDir))!, isIndexed: true);
        model.Update();
        var mod = model.AllMods().First(m => m.Fileinfo.Name == "test.jar");
        var instance = _env.CreateInstance("1.18.2", ModLoaderType.Forge, "40.1.0");
        return (instance, model, mod);
    }

    private void ServeNewerFile(byte[] newBytes, string fileName = "test.jar")
    {
        _server.FilesJson = new JsonObject
        {
            ["data"] = new JsonArray(JsonNode.Parse(FlamePayloads.FileJson(_server.Url, fileId: 789,
                displayName: "Test Mod 2.0", fileName: fileName, fileDate: "2024-06-01T00:00:00Z",
                sha1: Sha1(newBytes), gameVersions: """["1.18.2", "forge"]"""))),
        }.ToJsonString();
        _server.Downloads["mod.bin"] = newBytes;
    }

    [Fact]
    public async Task ApplyUpdate_CheckPassFeedsDownload_FileAndMetadataUpdated()
    {
        var (instance, model, mod) = SetupInstalledMod("old jar content"u8.ToArray());
        var newBytes = "brand new jar content"u8.ToArray();
        ServeNewerFile(newBytes);

        // 1. check: the Flame update pass finds the newer file 789
        var check = new FlameCheckUpdate([mod], [new McVersion("1.18.2")], [ModLoaderType.Forge],
            _ => MetadataHandler.Get(new AbsolutePath(IndexDir), "test-mod"), flame: _flameApi, http: TestNet.Http);
        await check.StartAsync();
        Assert.True(check.WasSuccessful, check.FailReason);
        var update = Assert.Single(check.GetUpdates());

        // 2. apply: the fake pack of the C++ check task + a ResourceDownloadTask per update
        var metadata = MetadataHandler.Get(new AbsolutePath(IndexDir), "test-mod");
        var pack = new IndexedPack
        {
            AddonId = metadata.ProjectId,
            Slug = metadata.Slug,
            Name = update.Name,
            Provider = ResourceProvider.Flame,
        };
        var download = new ResourceDownloadTask(pack, update.Version, model, TestModrinthApi.Unreachable().Client, TestModrinthApi.Unreachable().Http,
            isIndexed: true, downloadReason: "update", instance: instance);

        var updateTask = new ModUpdateTask(model, [download]);
        await updateTask.StartAsync();

        Assert.True(updateTask.WasSuccessful, updateTask.FailReason);
        Assert.Equal(newBytes, await File.ReadAllBytesAsync(Path.Combine(ModsDir, "test.jar")));

        // the metadata was refreshed to the new file id/version
        var refreshed = MetadataHandler.Get(new AbsolutePath(IndexDir), "test-mod");
        Assert.True(refreshed.IsValid);
        Assert.Equal("789", refreshed.FileId);
        Assert.Equal("Test Mod 2.0", refreshed.VersionNumber);
        Assert.Equal(Sha1(newBytes), refreshed.Hash);

        // the ModUpdateTask's folder refresh re-attached the new metadata to the model
        var installed = model.AllMods().First(m => m.Fileinfo.Name == "test.jar");
        var attached = model.GetMetadata(installed);
        Assert.NotNull(attached);
        Assert.Equal("789", attached.FileId);
    }

    [Fact]
    public async Task ApplyUpdate_FailingDownloadDoesNotAbortSiblings_StillRefreshes()
    {
        var (instance, model, _) = SetupInstalledMod("old jar content"u8.ToArray());

        // a good download for a second mod...
        var goodBytes = "good jar bytes"u8.ToArray();
        _server.Downloads["good.bin"] = goodBytes;
        var goodPack = new IndexedPack { AddonId = "124", Slug = "good-mod", Name = "Good Mod", Provider = ResourceProvider.Flame };
        var goodVersion = new IndexedVersion
        {
            AddonId = "124",
            FileId = "457",
            Version = "2.0.0",
            FileName = "good.jar",
            DownloadUrl = $"{_server.Url}/good.bin",
            HashType = "sha1",
            Hash = Sha1(goodBytes),
        };
        var goodDownload = new ResourceDownloadTask(goodPack, goodVersion, model, TestModrinthApi.Unreachable().Client, TestModrinthApi.Unreachable().Http,
            instance: instance);

        // ...and one whose checksum cannot match
        var badPack = new IndexedPack { AddonId = "125", Slug = "bad-mod", Name = "Bad Mod", Provider = ResourceProvider.Flame };
        var badVersion = new IndexedVersion
        {
            AddonId = "125",
            FileId = "458",
            Version = "0.1.0",
            FileName = "bad.jar",
            DownloadUrl = $"{_server.Url}/good.bin",
            HashType = "sha1",
            Hash = Sha1("totally different content"u8.ToArray()),
        };
        var badDownload = new ResourceDownloadTask(badPack, badVersion, model, TestModrinthApi.Unreachable().Client, TestModrinthApi.Unreachable().Http,
            instance: instance);

        var updateTask = new ModUpdateTask(model, [badDownload, goodDownload]);
        await updateTask.StartAsync();

        // the batch fails (like the C++ ConcurrentTask reports at the end)...
        Assert.False(updateTask.WasSuccessful);
        Assert.NotEqual("", updateTask.FailReason);
        Assert.False(badDownload.WasSuccessful);

        // ...but the sibling still installed, and the model refresh still ran
        Assert.True(goodDownload.WasSuccessful, goodDownload.FailReason);
        Assert.Equal(goodBytes, await File.ReadAllBytesAsync(Path.Combine(ModsDir, "good.jar")));
        var installed = model.AllMods().FirstOrDefault(m => m.Fileinfo.Name == "good.jar");
        Assert.NotNull(installed);
    }

    [Fact]
    public async Task ApplyUpdate_NoDownloads_StillRefreshesModel()
    {
        var (_, model, _) = SetupInstalledMod("jar content"u8.ToArray());

        var updateTask = new ModUpdateTask(model, []);
        await updateTask.StartAsync();

        Assert.True(updateTask.WasSuccessful, updateTask.FailReason);
        Assert.NotNull(model.AllMods().FirstOrDefault(m => m.Fileinfo.Name == "test.jar"));
    }
}
