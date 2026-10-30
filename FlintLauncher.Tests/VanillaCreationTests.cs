// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Tests for the VanillaCreationTask port: folder layout, the instance.json identity keys and
// the initial mmc-pack.json, verified through the real PackProfile loader. A fresh
// instance is Flint format from birth — no instance.cfg is written.

using System.Text.Json;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Instances.Creation;
using FlintLauncher.Core.IO;
using TruePath;
using Xunit;

namespace FlintLauncher.Tests;

public class VanillaCreationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"flint-vanilla-{Guid.NewGuid():N}");
    private readonly string _instancesDir;
    private readonly string _groupFile;
    private readonly TestGlobalSettings _global = new();

    public VanillaCreationTests()
    {
        Directory.CreateDirectory(_root);
        _instancesDir = Path.Combine(_root, "instances");
        Directory.CreateDirectory(_instancesDir);
        _groupFile = Path.Combine(_root, "instgroups.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException) { }
    }

    private InstanceList CreateList() =>
        new([MaterializedDirectory.Ensure(new AbsolutePath(_instancesDir))!],
            _global,
            new AbsolutePath(_groupFile),
            []);

    private VanillaCreationTask CreateTask(string version, string name)
    {
        var task = new VanillaCreationTask(version);
        // what NewInstanceDialog submits
        task.SetName(name);
        task.Icon = "flint-icon";
        task.Group = "Vanilla";
        task.SetParentEnvironment(_global.Environment);
        return task;
    }

    [Fact]
    public async Task Create_WritesFolderInstanceJsonAndMmcPack()
    {
        var list = CreateList();
        // probe the (empty) list first — like the launcher's startup scan — so the commit
        // can persist the group into instgroups.json
        Assert.Equal(InstListError.NoError, list.LoadList());

        var task = CreateTask("1.20.1", "Test Instance");

        await list.WrapInstanceTask(task)(TestContext.Current.CancellationToken);

        var dir = Path.Combine(_instancesDir, "Test Instance");
        Assert.True(Directory.Exists(dir));

        // instance.json: name/iconKey/uuid written by the typed instance shell (no INI)
        var jsonPath = Path.Combine(dir, "instance.json");
        Assert.True(File.Exists(jsonPath));
        var json = await File.ReadAllTextAsync(jsonPath);
        Assert.Contains("Test Instance", json);
        Assert.Contains("flint-icon", json);
        Assert.Contains("uuid", json);
        Assert.False(File.Exists(Path.Combine(dir, "instance.cfg")));

        // mmc-pack.json: the initial net.minecraft component
        using (var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(dir, "mmc-pack.json"))))
        {
            var root = doc.RootElement;
            Assert.Equal(1, root.GetProperty("formatVersion").GetInt32());
            var components = root.GetProperty("components");
            var arr = components.EnumerateArray().ToList();
            var entry = Assert.Single(arr);
            Assert.Equal("net.minecraft", entry.GetProperty("uid").GetString());
            Assert.Equal("1.20.1", entry.GetProperty("version").GetString());
            Assert.True(entry.GetProperty("important").GetBoolean());
        }

        // reload from disk: the created folder is discoverable and loadable
        list.LoadList();
        var inst = Assert.IsType<MinecraftInstance>(list.GetInstanceById("Test Instance"));
        Assert.Equal("Test Instance", inst.Name());
        Assert.Equal("Vanilla", list.GetInstanceGroup("Test Instance"));

        // PackProfile.Load finds the net.minecraft component with the version
        var profile = inst.GetPackProfile();
        Assert.True(profile.Load());
        var component = profile.GetComponent("net.minecraft");
        Assert.NotNull(component);
        Assert.Equal("1.20.1", component.Version);
        Assert.True(component.Important);
    }

    [Fact]
    public async Task Create_WithLoader_AddsLoaderComponent()
    {
        var list = CreateList();
        var task = new VanillaCreationTask("1.20.1", "net.fabricmc.fabric-loader", "0.15.0");
        task.SetName("Fab");
        task.Icon = "default";
        task.SetParentEnvironment(_global.Environment);

        await list.WrapInstanceTask(task)(TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(_instancesDir, "Fab", "mmc-pack.json")));
        var uids = doc.RootElement.GetProperty("components").EnumerateArray()
            .Select(c => c.GetProperty("uid").GetString()).ToList();
        Assert.Equal(["net.minecraft", "net.fabricmc.fabric-loader"], uids);

        var fabric = doc.RootElement.GetProperty("components").EnumerateArray().Last();
        Assert.Equal("0.15.0", fabric.GetProperty("version").GetString());
        // only net.minecraft is marked important
        Assert.False(fabric.TryGetProperty("important", out _));
    }

    [Fact]
    public async Task Create_NameCollisionGetsFreshFolder()
    {
        var list = CreateList();
        var first = CreateTask("1.20.1", "Test Instance");
        await list.WrapInstanceTask(first)(TestContext.Current.CancellationToken);

        var second = CreateTask("1.20.1", "Test Instance");
        await list.WrapInstanceTask(second)(TestContext.Current.CancellationToken);

        Assert.True(Directory.Exists(Path.Combine(_instancesDir, "Test Instance(1)")));

        list.LoadList();
        Assert.Equal(2, list.Count);
        Assert.NotNull(list.GetInstanceById("Test Instance"));
        Assert.NotNull(list.GetInstanceById("Test Instance(1)"));
    }

    [Fact]
    public async Task Create_WithoutParentEnvironment_FailsTheTask()
    {
        var task = new VanillaCreationTask("1.20.1");
        task.SetName("Orphan");
        task.StagingPath = new AbsolutePath(Path.Combine(_root, "orphan"));
        Directory.CreateDirectory(task.StagingPath.Value);

        // without a parent environment the task cannot build its staged instance
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => task.ExecuteAsync(TestContext.Current.CancellationToken));
        Assert.NotEmpty(ex.Message);
    }

    [Fact]
    public async Task Create_StagingFolderIsConsumed()
    {
        var list = CreateList();
        var task = CreateTask("1.21.4", "Consumed");

        await list.WrapInstanceTask(task)(TestContext.Current.CancellationToken);

        var tempRoot = Path.Combine(_instancesDir, ".tmp");
        Assert.True(Directory.Exists(tempRoot));
        Assert.Empty(Directory.EnumerateFileSystemEntries(tempRoot));

        // the staging folder itself became the instance folder (Flint format)
        Assert.True(File.Exists(Path.Combine(_instancesDir, "Consumed", "instance.json")));
    }
}
