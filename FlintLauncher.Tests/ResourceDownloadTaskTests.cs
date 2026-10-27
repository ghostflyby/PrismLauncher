// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Tests for ResourceDownloadTask (the install/update download of an IndexedVersion into an
// existing instance's mods folder): jar lands in mods/, the .pw.toml metadata is written,
// a checksum mismatch fails cleanly without leaving payload behind, and the previously
// installed file is only removed after the new download succeeded.

using System.Security.Cryptography;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Minecraft.Resources;
using FlintLauncher.Core.ModPlatform;
using FlintLauncher.Core.ModPlatform.Modrinth;
using FlintLauncher.Core.Tasks;
using TruePath;
using Xunit;
using PackwizModel = FlintLauncher.Core.ModPlatform.Packwiz.Packwiz;

using FlintLauncher.Core.Net;
using FlintLauncher.ExternalApi;

namespace FlintLauncher.Tests;

[Collection("ModrinthApi")]
public class ResourceDownloadTaskTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"flint-res-download-{Guid.NewGuid():N}");
    private readonly StubModrinthServer _server = new();

    private readonly byte[] _jarBytes = "test mod jar content 0123456789"u8.ToArray();

    private readonly ModrinthClient _modrinth;
    private readonly HttpClient _http;

    public ResourceDownloadTaskTests()
    {
        Directory.CreateDirectory(_root);
        (_modrinth, _http, _) = TestModrinthApi.At(_server.Url);
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

    private string ModsDir => Path.Combine(_root, "mods");

    private string IndexDir => Path.Combine(ModsDir, ".index");

    private ModFolderModel CreateModel()
    {
        var model = new ModFolderModel(MaterializedDirectory.Ensure(new AbsolutePath(ModsDir))!, isIndexed: true);
        return model;
    }

    private (IndexedPack Pack, IndexedVersion Version) MakeVersion(string fileName, string downloadName, byte[]? bytes = null)
    {
        var payload = bytes ?? _jarBytes;
        _server.Downloads[downloadName] = payload;
        var sha1 = Convert.ToHexString(SHA1.HashData(payload)).ToLowerInvariant();

        var pack = new IndexedPack
        {
            AddonId = "projId1",
            Slug = "test-mod",
            Name = "Test Mod",
            Provider = ResourceProvider.Modrinth,
        };
        var version = new IndexedVersion
        {
            AddonId = "projId1",
            FileId = "verId1",
            Version = "1.0.0",
            VersionNumber = "1.0.0",
            FileName = fileName,
            DownloadUrl = $"{_server.Url}/{downloadName}",
            HashType = "sha1",
            Hash = sha1,
            McVersion = ["1.20.1"],
            Loaders = ModLoaderType.Fabric,
        };
        return (pack, version);
    }

    [Fact]
    public async Task Download_WritesFileAndMetadata()
    {
        var (pack, version) = MakeVersion("test.jar", "mod.bin");
        var model = CreateModel();

        var task = new ResourceDownloadTask(pack, version, model, modrinth: _modrinth, http: _http);
        await task.StartAsync();

        Assert.True(task.WasSuccessful, task.FailReason);
        Assert.Equal(Path.Combine(ModsDir, "test.jar"), Path.Combine(ModsDir, task.GetFilename()));
        Assert.Equal("verId1", task.GetVersionId());
        Assert.Equal(ResourceProvider.Modrinth, task.GetProvider());
        Assert.Equal("Test Mod", task.GetName());
        Assert.Same(pack, task.GetPack());

        var jar = Path.Combine(ModsDir, "test.jar");
        Assert.True(File.Exists(jar));
        Assert.Equal(_jarBytes, await File.ReadAllBytesAsync(jar));

        // the per-mod metadata was written before the download ran
        var metadata = MetadataHandler.Get(new AbsolutePath(IndexDir), "test-mod");
        Assert.True(metadata.IsValid);
        Assert.Equal("test-mod", metadata.Slug);
        Assert.Equal("test.jar", metadata.Filename);
        Assert.Equal(ResourceProvider.Modrinth, metadata.Provider);
        Assert.Equal("projId1", metadata.ProjectId);
        Assert.Equal("verId1", metadata.FileId);
        Assert.Equal("1.0.0", metadata.VersionNumber);
        Assert.Equal("url", metadata.Mode);
        Assert.Equal(version.DownloadUrl, metadata.Url);
        Assert.Equal("sha1", metadata.HashFormat);
        Assert.Equal(version.Hash, metadata.Hash);

        // the model's metadata hook sees it after a folder update
        model.Update();
        var installed = FindMod(model, "test.jar");
        Assert.NotNull(installed);
        var attached = model.GetMetadata(installed);
        Assert.NotNull(attached);
        Assert.Equal("verId1", attached.FileId);
    }

    /// <summary>The folder scan also lists the hidden .index dir (QDir would not); look up by name.</summary>
    private static Mod? FindMod(ModFolderModel model, string fileName) =>
        model.AllMods().FirstOrDefault(m => m.Fileinfo.Name == fileName);

    [Fact]
    public async Task Download_ShaMismatch_FailsCleanly()
    {
        var (_, version) = MakeVersion("test.jar", "mod.bin");
        version.Hash = Convert.ToHexString(SHA1.HashData("not the payload"u8.ToArray())).ToLowerInvariant();
        var model = CreateModel();

        var task = new ResourceDownloadTask(pack: new IndexedPack
        {
            AddonId = "projId1",
            Slug = "test-mod",
            Name = "Test Mod",
            Provider = ResourceProvider.Modrinth,
        }, version, model, modrinth: _modrinth, http: _http);
        await task.StartAsync();

        Assert.False(task.WasSuccessful);
        Assert.NotEqual("", task.FailReason);

        // the atomic sink leaves no payload behind (the .flint-tmp leftover of a failed
        // finalize is the shared Net sink's behavior, not this task's)
        var jar = Path.Combine(ModsDir, "test.jar");
        Assert.True(!File.Exists(jar) || new FileInfo(jar).Length == 0);
    }

    [Fact]
    public async Task Download_OldFileRemovedOnlyAfterSuccess()
    {
        Directory.CreateDirectory(IndexDir);
        var oldBytes = "old jar bytes"u8.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(ModsDir, "old.jar"), oldBytes);
        PackwizModel.UpdateModIndex(new AbsolutePath(IndexDir), new PackwizModel.Mod
        {
            Slug = "test-mod",
            Name = "Test Mod",
            Filename = "old.jar",
            Provider = ResourceProvider.Modrinth,
            ProjectId = "projId1",
            FileId = "verOld",
            VersionNumber = "0.9.0",
            Mode = "url",
        });
        var model = CreateModel();
        model.Update();
        Assert.NotNull(FindMod(model, "old.jar"));

        var (pack, version) = MakeVersion("new.jar", "newmod.bin", "brand new jar bytes"u8.ToArray());
        var task = new ResourceDownloadTask(pack, version, model, modrinth: _modrinth, http: _http);
        await task.StartAsync();

        Assert.True(task.WasSuccessful, task.FailReason);
        Assert.False(File.Exists(Path.Combine(ModsDir, "old.jar")), "the old file should be gone");
        Assert.Null(FindMod(model, "old.jar"));
        Assert.NotNull(FindMod(model, "new.jar"));

        // the metadata now points at the new file (same slug -> same index entry, refreshed)
        var metadata = MetadataHandler.Get(new AbsolutePath(IndexDir), "test-mod");
        Assert.True(metadata.IsValid);
        Assert.Equal("new.jar", metadata.Filename);
        Assert.Equal("verId1", metadata.FileId);
    }

    [Fact]
    public async Task Download_DownloadFailure_FailsTask()
    {
        var (pack, version) = MakeVersion("test.jar", "mod.bin");
        // nothing served at the URL -> 404
        _server.Downloads.Clear();
        var model = CreateModel();

        var task = new ResourceDownloadTask(pack, version, model, modrinth: _modrinth, http: _http);
        await task.StartAsync();

        Assert.False(task.WasSuccessful);
        Assert.Equal(TaskState.Failed, task.State);
    }
}
