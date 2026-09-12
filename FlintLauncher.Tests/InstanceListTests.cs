// SPDX-License-Identifier: GPL-3.0-only
// Tests for the InstanceList port. Everything synthetic (temp dirs); instance.cfg and
// instgroups.json are exercised through the real QSettings-compatible INI layer and the
// Qt-style JSON writer.
using System.Text.Json;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Settings;
using Xunit;

namespace Flint.Tests;

public class InstanceListTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"flint-instlist-{Guid.NewGuid():N}");
    private readonly string _instancesDir;
    private readonly string _groupFile;
    private readonly TestGlobalSettings _global = new();

    public InstanceListTests()
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
        catch (DirectoryNotFoundException)
        {
        }
    }

    private InstanceList CreateList() => new(_global, [_instancesDir]) { GroupFilePath = _groupFile };

    private void MakeInstanceDir(string id, string instanceType = "OneSix", string? name = null)
    {
        var dir = Path.Combine(_instancesDir, id);
        Directory.CreateDirectory(dir);
        var lines = new List<string> { "InstanceType=" + instanceType };
        if (name is not null)
        {
            lines.Add("name=" + name);
        }
        File.WriteAllLines(Path.Combine(dir, "instance.cfg"), lines);
    }

    // ---------------------------------------------------------------------------- scan

    [Fact]
    public void LoadList_FindsInstanceDirs_IdsAreFolderNames()
    {
        MakeInstanceDir("aaa", name: "A");
        MakeInstanceDir("bbb", name: "B");
        MakeInstanceDir("ccc", name: "C");
        // junk: a folder without instance.cfg and a plain file must be ignored
        Directory.CreateDirectory(Path.Combine(_instancesDir, "junk"));
        File.WriteAllText(Path.Combine(_instancesDir, "stray.txt"), "x");

        var list = CreateList();
        Assert.Equal(InstListError.NoError, list.LoadList());

        Assert.Equal(3, list.Count);
        Assert.NotNull(list.GetInstanceById("aaa"));
        Assert.NotNull(list.GetInstanceById("bbb"));
        Assert.NotNull(list.GetInstanceById("ccc"));
        Assert.Null(list.GetInstanceById("junk"));
        Assert.Null(list.GetInstanceById(""));

        Assert.Equal("A", list.GetInstanceById("aaa")!.Name());
        Assert.Equal("B", list.GetInstanceById("bbb")!.Name());
        // id = folder name
        Assert.Equal("aaa", list.GetInstanceById("aaa")!.Id());
        Assert.Equal(Path.Combine(_instancesDir, "aaa"), list.GetInstanceById("aaa")!.InstanceRoot());
        Assert.Equal(Path.Combine(_instancesDir, "bbb"), list.GetInstanceById("bbb")!.InstanceRoot());
        Assert.Equal(Path.Combine(_instancesDir, "ccc"), list.GetInstanceById("ccc")!.InstanceRoot());
    }

    [Fact]
    public void LoadList_KeepsExistingInstances_AndDropsVanishedOnes()
    {
        MakeInstanceDir("stay", name: "Stay");
        MakeInstanceDir("gone", name: "Gone");
        var list = CreateList();
        Assert.Equal(InstListError.NoError, list.LoadList());
        Assert.Equal(2, list.Count);

        // remove one instance dir externally, add another one
        Directory.Delete(Path.Combine(_instancesDir, "gone"), recursive: true);
        MakeInstanceDir("new1", name: "New");
        Assert.Equal(InstListError.NoError, list.LoadList());

        Assert.Equal(2, list.Count);
        Assert.Null(list.GetInstanceById("gone"));
        Assert.NotNull(list.GetInstanceById("stay"));
        Assert.NotNull(list.GetInstanceById("new1"));
    }

    [Fact]
    public void GetInstanceById_AlsoMatchesUuid()
    {
        MakeInstanceDir("byid", name: "Byid");
        var list = CreateList();
        list.LoadList();

        var inst = list.GetInstanceById("byid")!;
        Assert.Same(inst, list.GetInstanceById(inst.Uuid));
    }

    // ---------------------------------------------------------------- instgroups.json

    [Fact]
    public void Instgroups_RoundTrip()
    {
        MakeInstanceDir("a", name: "A");
        MakeInstanceDir("b", name: "B");
        MakeInstanceDir("c", name: "C");
        // upstream format: formatVersion is the STRING "1", groups are name -> {hidden, instances}
        File.WriteAllText(_groupFile,
            """
            {
                "formatVersion": "1",
                "groups": {
                    "Group": {
                        "hidden": true,
                        "instances": ["a", "b"]
                    }
                }
            }
            """);

        var list = CreateList();
        Assert.Equal(InstListError.NoError, list.LoadList()); // loadInstance triggers loadGroupList

        // instance group queries right
        Assert.Equal("Group", list.GetInstanceGroup("a"));
        Assert.Equal("Group", list.GetInstanceGroup("b"));
        Assert.Equal("", list.GetInstanceGroup("c"));
        Assert.Equal(["Group"], list.GetGroups());
        // the "hidden" key became the collapsed state
        Assert.True(list.IsGroupCollapsed("Group"));
        Assert.False(list.IsGroupCollapsed("Other"));

        // setInstanceGroup persists compatible JSON
        list.SetInstanceGroup("c", "Other");
        Assert.Equal("Other", list.GetInstanceGroup("c"));

        using (var doc = JsonDocument.Parse(File.ReadAllText(_groupFile)))
        {
            var root = doc.RootElement;
            Assert.Equal("1", root.GetProperty("formatVersion").GetString());
            var groups = root.GetProperty("groups");
            Assert.Equal(2, groups.EnumerateObject().Count());

            var group = groups.GetProperty("Group");
            Assert.True(group.GetProperty("hidden").GetBoolean());
            Assert.Equal(["a", "b"], group.GetProperty("instances").EnumerateArray()
                .Select(v => v.GetString()).ToList());

            var other = groups.GetProperty("Other");
            Assert.False(other.GetProperty("hidden").GetBoolean());
            Assert.Equal(["c"], other.GetProperty("instances").EnumerateArray()
                .Select(v => v.GetString()).ToList());
        }

        // the group list survived the file write in memory, too
        Assert.Equal(["Group", "Other"], list.GetGroups());
    }

    [Fact]
    public void Instgroups_UngroupedHiddenKey_IsRoundTripped()
    {
        MakeInstanceDir("solo", name: "Solo");
        File.WriteAllText(_groupFile,
            """
            {
                "formatVersion": "1",
                "groups": {},
                "ungrouped": {
                    "hidden": true
                }
            }
            """);

        var list = CreateList();
        list.LoadList();

        // empty string represents ungrouped "group"
        Assert.True(list.IsGroupCollapsed(""));
        Assert.Equal("", list.GetInstanceGroup("solo"));

        // and it is written back
        list.SetGroupCollapsed("", true);
        using var doc = JsonDocument.Parse(File.ReadAllText(_groupFile));
        Assert.True(doc.RootElement.GetProperty("ungrouped").GetProperty("hidden").GetBoolean());
    }

    [Fact]
    public void Groups_DeleteAndRename_UpdateIndexAndFile()
    {
        MakeInstanceDir("one", name: "One");
        MakeInstanceDir("two", name: "Two");
        var list = CreateList();
        list.LoadList();

        list.SetInstanceGroup("one", "G");
        list.SetInstanceGroup("two", "G");

        list.RenameGroup("G", "H");
        Assert.Equal("H", list.GetInstanceGroup("one"));
        Assert.Equal("H", list.GetInstanceGroup("two"));
        Assert.Equal(["H"], list.GetGroups());

        list.DeleteGroup("H");
        Assert.Equal("", list.GetInstanceGroup("one"));
        Assert.Equal("", list.GetInstanceGroup("two"));
        Assert.Empty(list.GetGroups());

        using var doc = JsonDocument.Parse(File.ReadAllText(_groupFile));
        Assert.Empty(doc.RootElement.GetProperty("groups").EnumerateObject());
    }

    [Fact]
    public void SetInstanceGroup_ForUnknownInstance_IsNoOp()
    {
        MakeInstanceDir("p", name: "P");
        var list = CreateList();
        // no instance with this id is loaded -> nothing to index, nothing to save
        list.SetInstanceGroup("p", "Early");
        Assert.Equal("", list.GetInstanceGroup("p"));
        Assert.False(File.Exists(_groupFile));
    }

    [Fact]
    public void Instgroups_LegacyLocation_IsMigrated()
    {
        MakeInstanceDir("m", name: "M");
        // legacy location: inside the (primary) instances dir
        var legacyFile = Path.Combine(_instancesDir, "instgroups.json");
        File.WriteAllText(legacyFile, """{"formatVersion": "1", "groups": {"Old": {"instances": ["m"]}}}""");

        var list = CreateList();
        list.LoadList();

        Assert.Equal("Old", list.GetInstanceGroup("m"));
        // migrated to the primary group file location
        Assert.True(File.Exists(_groupFile));
        using var doc = JsonDocument.Parse(File.ReadAllText(_groupFile));
        Assert.Equal(["m"], doc.RootElement.GetProperty("groups").GetProperty("Old")
            .GetProperty("instances").EnumerateArray().Select(v => v.GetString()).ToList());
    }

    // --------------------------------------------------------------- type dispatch

    [Fact]
    public void UnknownInstanceType_YieldsBrokenPlaceholder()
    {
        MakeInstanceDir("legacy1", instanceType: "Legacy");
        MakeInstanceDir("weird", instanceType: "Unknown");

        var list = CreateList();
        Assert.Equal(InstListError.NoError, list.LoadList());

        Assert.Equal(2, list.Count);
        foreach (var inst in list.Instances)
        {
            Assert.IsType<NullInstance>(inst);
            Assert.True(inst.HasVersionBroken);
            Assert.False(inst.CanLaunch());
        }
        Assert.NotNull(list.GetInstanceById("legacy1"));
        Assert.NotNull(list.GetInstanceById("weird"));
    }

    [Fact]
    public void MissingInstanceType_LoadsAsOneSix()
    {
        // a hand-written instance.cfg without an InstanceType key (old instances)
        var dir = Path.Combine(_instancesDir, "notype");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "instance.cfg"), "name=NoType\n");

        var list = CreateList();
        list.LoadList();
        Assert.IsType<FlintLauncher.Core.Instances.MinecraftInstance>(list.GetInstanceById("notype"));
        Assert.Equal("NoType", list.GetInstanceById("notype")!.Name());
    }

    // ----------------------------------------------------------- total playtime migration

    [Fact]
    public void TotalPlayTime_MigratedOnceOnFirstLoad()
    {
        MakeInstanceDir("played", name: "P");
        File.AppendAllText(Path.Combine(_instancesDir, "played", "instance.cfg"), "totalTimePlayed=3600\n");

        var playtime = new TestPlaytimeSettings();
        var list = CreateList();
        list.PlaytimeSettings = playtime;

        list.LoadList();
        Assert.Equal(3600, playtime.TotalPlayTime);
        Assert.True(playtime.TotalPlayTimeMigrated);

        // second load does not accumulate again
        list.LoadList();
        Assert.Equal(3600, playtime.TotalPlayTime);
    }

    // ---------------------------------------------------------------- staging/commit

    private sealed class StubInstanceTask : InstanceTask
    {
        protected override Task ExecuteAsync(CancellationToken cancellationToken)
        {
            EmitSucceeded();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void StagedInstance_CommitsIntoInstancesDir()
    {
        var list = CreateList();
        // probe the (empty) list first — like the launcher's startup scan — so the commit
        // can persist the group into instgroups.json
        Assert.Equal(InstListError.NoError, list.LoadList());

        Assert.Null(list.GetStagedInstancePath(Path.Combine(_root, "not-configured")));

        var staging = list.GetStagedInstancePath("");
        Assert.NotNull(staging);
        Assert.Contains(".tmp", staging);
        Assert.True(Directory.Exists(staging));

        File.WriteAllText(Path.Combine(staging!, "instance.cfg"), "InstanceType=OneSix\nname=Staged Instance\n");

        var task = new StubInstanceTask { TargetDir = "", Group = "StagedGroup" };
        task.SetName("Staged Instance");
        Assert.True(list.CommitStagedInstance(staging!, task, task.Group));

        // the staging folder moved into place, named after the task's name
        Assert.True(Directory.Exists(Path.Combine(_instancesDir, "Staged Instance")));
        Assert.False(Directory.Exists(staging!));

        list.LoadList();
        Assert.NotNull(list.GetInstanceById("Staged Instance"));
        Assert.Equal("StagedGroup", list.GetInstanceGroup("Staged Instance"));
        using (var doc = JsonDocument.Parse(File.ReadAllText(_groupFile)))
        {
            Assert.Equal(["Staged Instance"], doc.RootElement.GetProperty("groups")
                .GetProperty("StagedGroup").GetProperty("instances").EnumerateArray()
                .Select(v => v.GetString()).ToList());
        }
    }

    [Fact]
    public async Task WrapInstanceTask_RunsChildAndCommits()
    {
        var list = CreateList();
        Assert.Equal(InstListError.NoError, list.LoadList());

        var task = new StubInstanceTask();
        task.SetName("Wrapped");
        var wrapped = list.WrapInstanceTask(task);
        await wrapped.StartAsync();

        Assert.True(wrapped.WasSuccessful);
        // the wrapper's own staging folder was committed into place
        Assert.True(Directory.Exists(Path.Combine(_instancesDir, "Wrapped")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_instancesDir, ".tmp")));
    }

    [Fact]
    public async Task FailedInstanceTask_CleansUpStaging_AndFails()
    {
        var list = CreateList();

        var wrapped = list.WrapInstanceTask(new FailingInstanceTask());
        await wrapped.StartAsync();
        Assert.Equal(FlintLauncher.Core.Tasks.TaskState.Failed, wrapped.State);
        Assert.Equal("boom", wrapped.FailReason);

        // the staging folder was cleaned up: .tmp is empty again
        var tempRoot = Path.Combine(_instancesDir, ".tmp");
        Assert.True(Directory.Exists(tempRoot));
        Assert.Empty(Directory.EnumerateFileSystemEntries(tempRoot));
    }

    private sealed class FailingInstanceTask : InstanceTask
    {
        protected override Task ExecuteAsync(CancellationToken cancellationToken)
        {
            EmitFailed("boom");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void CommitStagedInstance_RejectsUnconfiguredTargetDir()
    {
        var list = CreateList();
        var task = new StubInstanceTask { TargetDir = Path.Combine(_root, "elsewhere") };
        task.SetName("X");
        var staging = Path.Combine(_root, "fake-staging");
        Directory.CreateDirectory(staging);
        Assert.False(list.CommitStagedInstance(staging, task, ""));
    }

    [Fact]
    public void CommitStagedInstance_NameCollisionGetsNumericSuffix()
    {
        MakeInstanceDir("Taken", name: "Taken");
        var list = CreateList();
        var task = new StubInstanceTask();
        task.SetName("Taken");

        var staging = list.GetStagedInstancePath("");
        Assert.NotNull(staging);
        File.WriteAllText(Path.Combine(staging!, "instance.cfg"), "InstanceType=OneSix\nname=Taken\n");

        Assert.True(list.CommitStagedInstance(staging!, task, ""));
        // FS::DirNameFromString picked the next free folder name
        Assert.True(Directory.Exists(Path.Combine(_instancesDir, "Taken(1)")));
    }

    [Fact]
    public void DeleteInstance_RemovesFolderAndListEntry()
    {
        MakeInstanceDir("doomed", name: "Doomed");
        var list = CreateList();
        list.LoadList();
        Assert.NotNull(list.GetInstanceById("doomed"));

        list.DeleteInstance("doomed");

        Assert.False(Directory.Exists(Path.Combine(_instancesDir, "doomed")));
        Assert.Null(list.GetInstanceById("doomed"));
    }

    [Fact]
    public void InstanceFoldersChanged_ReloadsList()
    {
        MakeInstanceDir("in-old", name: "Old");
        var list = CreateList();
        list.LoadList();
        Assert.Equal(1, list.Count);

        var otherDir = Path.Combine(_root, "other-instances");
        Directory.CreateDirectory(otherDir);
        Directory.CreateDirectory(Path.Combine(otherDir, "in-new"));
        File.WriteAllText(Path.Combine(otherDir, "in-new", "instance.cfg"), "InstanceType=OneSix\nname=New\n");

        list.SetInstanceDirs([otherDir]);

        Assert.Equal(otherDir, list.PrimaryDir());
        Assert.Equal(1, list.Count);
        Assert.Null(list.GetInstanceById("in-old"));
        Assert.NotNull(list.GetInstanceById("in-new"));
    }

    [Fact]
    public void LinkedInstances_AreFoundById()
    {
        MakeInstanceDir("base", name: "Base");
        MakeInstanceDir("link", name: "Link");
        var list = CreateList();
        list.LoadList();

        var link = list.GetInstanceById("link")!;
        link.SetLinkedInstances(["base"]);

        Assert.Equal(["link"], list.GetLinkedInstancesById("base"));
        Assert.Equal([], list.GetLinkedInstancesById("link"));
    }
}
