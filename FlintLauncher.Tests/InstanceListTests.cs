// SPDX-License-Identifier: GPL-3.0-only
// Tests for the InstanceList port. Everything synthetic (temp dirs); instance.cfg and
// instgroups.json are exercised through the real QSettings-compatible INI layer and the
// Qt-style JSON writer.

using System.Globalization;
using System.Text.Json;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.State;
using FlintLauncher.Core.Tasks;
using Xunit;

namespace FlintLauncher.Tests;

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
        catch (DirectoryNotFoundException) { }
    }

    private InstanceList CreateList() =>
        new([_instancesDir]) { Environment = _global, GroupFilePath = _groupFile };

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

    // DATA D2: the addressing chain (inode → uuid → dirname) is the endpoint entry point
    [Fact]
    public void ResolveInstance_FollowsUuidThenDirname()
    {
        MakeInstanceDir("alpha", name: "Alpha");
        var list = CreateList();
        list.LoadList();

        var inst = list.ResolveInstance("alpha")!;
        Assert.Same(inst, list.ResolveInstance(inst.Uuid)!);
        Assert.Null(list.ResolveInstance("missing"));
        Assert.Null(list.ResolveInstance(""));
    }

    [Fact]
    public void ResolveInstance_InodeLink()
    {
        MakeInstanceDir("statted", name: "Statted");
        var list = CreateList();
        list.LoadList();

        var inst = list.GetInstanceById("statted")!;
        if (OperatingSystem.IsWindows())
        {
            // D2: no inode support on Windows — the chain skips the link (InodeOf = 0)
            Assert.Equal(0, list.InodeOf(inst));
            return;
        }

        var inode = list.InodeOf(inst);
        Assert.NotEqual(0, inode);
        Assert.Same(inst, list.ResolveInstance(inode.ToString(CultureInfo.InvariantCulture))!);

        // a numeric reference that is NOT a live inode falls through to uuid/dirname
        Assert.Null(list.ResolveInstance(long.MaxValue.ToString(CultureInfo.InvariantCulture)));
    }

    // DOCUMENTED BOUNDARY (D2): a pure-numeric dirname still resolves (as itself) when no
    // live inode carries that number — the inode link only wins on a real index hit
    [Fact]
    public void ResolveInstance_PureNumericDirname_ResolvesViaDirnameWithoutInodeHit()
    {
        MakeInstanceDir("7770001777", name: "Numeric");
        var list = CreateList();
        list.LoadList();

        var inst = list.ResolveInstance("7770001777");
        Assert.NotNull(inst);
        Assert.Equal("Numeric", inst.Name());
    }

    // DATA D3: a committed folder whose metadata carries a uuid lands in the group file
    // under that uuid (the persistent reference form)
    [Fact]
    public void StagedInstance_GroupMemberIsTheCommittedUuid()
    {
        var list = CreateList();
        Assert.Equal(InstListError.NoError, list.LoadList());

        var staging = list.GetStagedInstancePath("");
        Assert.NotNull(staging);
        File.WriteAllText(Path.Combine(staging, "instance.cfg"),
            "InstanceType=OneSix\nname=Uuid Staged\nuuid=abc123def456\n");

        var task = new StubInstanceTask { TargetDir = "", Group = "UuidGroup" };
        task.SetName("Uuid Staged");
        Assert.True(list.CommitStagedInstance(staging, task, task.Group));

        using (var doc = JsonDocument.Parse(File.ReadAllText(_groupFile)))
        {
            Assert.Equal(["abc123def456"], doc.RootElement.GetProperty("groups")
                .GetProperty("UuidGroup").GetProperty("instances").EnumerateArray()
                .Select(v => v.GetString()).ToList());
        }

        // after discovery the group query goes through the uuid member…
        list.LoadList();
        Assert.Equal("UuidGroup", list.GetInstanceGroup("Uuid Staged"));
        // …and a further load re-parses the (already uuid-keyed) file without losing it
        list.LoadList();
        Assert.Equal("UuidGroup", list.GetInstanceGroup("Uuid Staged"));
    }

    // ---------------------------------------------------------------- instgroups.json

    [Fact]
    public void Instgroups_RoundTrip()
    {
        MakeInstanceDir("a", name: "A");
        MakeInstanceDir("b", name: "B");
        MakeInstanceDir("c", name: "C");
        // upstream format: formatVersion is the STRING "1", groups are name -> {hidden, instances}
        // (the hidden flag is legacy/Prism payload — read-compat seed, C8/S5)
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

        var state = TestLauncherState.Create();
        var list = CreateList();
        list.LauncherState = state;
        Assert.Equal(InstListError.NoError, list.LoadList()); // group load happens lazily at first group access

        // instance group queries right — the loaded dirname members were converted to the
        // instances' uuids (D3 import conversion)
        Assert.Equal("Group", list.GetInstanceGroup("a"));
        Assert.Equal("Group", list.GetInstanceGroup("b"));
        Assert.Equal("", list.GetInstanceGroup("c"));
        Assert.Equal(["Group"], list.GetGroups());
        // the legacy "hidden" key became the collapsed state
        Assert.True(list.IsGroupCollapsed("Group"));
        Assert.False(list.IsGroupCollapsed("Other"));
        // ...and was promoted into launcher-state.json (the S5 owner of the hidden half)
        Assert.Equal(["Group"], state.Current.Ui.HiddenGroups);

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
            // DATA D3: members are uuid references now (the legacy dirnames converted at
            // load); "id" on the wire keeps serving the dirname
            Assert.Equal(
                new[] { list.GetInstanceById("a")!.Uuid, list.GetInstanceById("b")!.Uuid }.Order(),
                group.GetProperty("instances").EnumerateArray()
                    .Select(v => v.GetString()).ToList().Order());
            // the collapse state is NOT written into the group file anymore — launcher-state
            // owns it (C8 split); the Flint-side file only carries group definitions
            Assert.False(group.TryGetProperty("hidden", out _));

            Assert.Equal([list.GetInstanceById("c")!.Uuid], groups.GetProperty("Other")
                .GetProperty("instances").EnumerateArray().Select(v => v.GetString()).ToList());
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

        var state = TestLauncherState.Create();
        var list = CreateList();
        list.LauncherState = state;
        list.LoadList();

        // empty string represents ungrouped "group"
        Assert.True(list.IsGroupCollapsed(""));
        Assert.Equal("", list.GetInstanceGroup("solo"));
        // the legacy seed promoted into the state
        Assert.True(state.Current.Ui.UngroupedHidden);

        // a later collapse writes the state only — the group file is untouched (the C8/S5
        // split made collapse purely machine-local state; the file carries definitions)
        var before = File.ReadAllText(_groupFile);
        list.SetGroupCollapsed("", false);
        Assert.False(list.IsGroupCollapsed(""));
        Assert.False(state.Current.Ui.UngroupedHidden);
        Assert.Equal(before, File.ReadAllText(_groupFile));
    }

    // L18: toVariant().toInt() never throws upstream — a non-int32-representable number
    // (a decimal, an overflow) must be a silent version-match reject, not a FormatException
    // unwinding through Load into a group-endpoint 500
    [Fact]
    public void Instgroups_DecimalFormatVersion_IsSilentlyRejected()
    {
        File.WriteAllText(_groupFile,
            """
            {
                "formatVersion": 1.5,
                "groups": {
                    "Group": {
                        "instances": []
                    }
                }
            }
            """);

        var list = CreateList();
        Assert.Null(Record.Exception(() => list.GetGroups()));
        Assert.Empty(list.GetGroups());
    }

    [Fact]
    public void Instgroups_OverflowFormatVersion_IsSilentlyRejected()
    {
        File.WriteAllText(_groupFile,
            """
            {
                "formatVersion": 9999999999,
                "groups": {
                    "Group": {
                        "instances": []
                    }
                }
            }
            """);

        var list = CreateList();
        Assert.Null(Record.Exception(() => list.GetGroups()));
        Assert.Empty(list.GetGroups());
    }

    // L19: the load-time promise ("unknown members convert on the next load") is honored
    // in-session now — the discovery round re-resolves dirname members even when the group
    // model was loaded earlier with the instance still undiscovered
    [Fact]
    public void Instgroups_DirnameMembers_ConvertOnTheNextLoadList()
    {
        // no instance dir yet: the first group access loads with the member stuck as a dirname
        File.WriteAllText(_groupFile,
            """
            {
                "formatVersion": "1",
                "groups": {
                    "Group": {
                        "instances": ["late"]
                    }
                }
            }
            """);
        var list = CreateList();
        Assert.Equal(["Group"], list.GetGroups());
        Assert.Equal("", list.GetInstanceGroup("late"));

        // the instance appears; the next LoadList converts and rewrites the file
        MakeInstanceDir("late", name: "Late");
        Assert.Equal(InstListError.NoError, list.LoadList());

        Assert.Equal("Group", list.GetInstanceGroup("late"));
        using var doc = JsonDocument.Parse(File.ReadAllText(_groupFile));
        Assert.Equal([list.GetInstanceById("late")!.Uuid], doc.RootElement.GetProperty("groups")
            .GetProperty("Group").GetProperty("instances").EnumerateArray()
            .Select(v => v.GetString()).ToList());
    }

    [Fact]
    public void CollapseState_LivesInTheLauncherState_AndSurvivesAReload()
    {
        MakeInstanceDir("one", name: "One");
        var state = TestLauncherState.Create();
        var list = CreateList();
        list.LauncherState = state;
        list.LoadList();

        list.SetGroupCollapsed("G", true);
        list.SetGroupCollapsed("", true);
        Assert.Equal(["G"], state.Current.Ui.HiddenGroups);
        Assert.True(state.Current.Ui.UngroupedHidden);

        // a fresh list over the same state restores the collapse state with no group file at all
        var reloaded = CreateList();
        reloaded.LauncherState = state;
        reloaded.LoadList();
        Assert.True(reloaded.IsGroupCollapsed("G"));
        Assert.True(reloaded.IsGroupCollapsed(""));

        // rename and delete follow the state (hidden keys on the group NAME, G4)
        reloaded.RenameGroup("G", "H");
        Assert.Equal(["H"], state.Current.Ui.HiddenGroups);
        reloaded.DeleteGroup("H");
        Assert.Equal([], state.Current.Ui.HiddenGroups);
        Assert.True(state.Current.Ui.UngroupedHidden);
    }

    // the BLOCKING review repro: a legacy/Prism file flag must not resurrect a collapsed
    // state the user has since cleared — the seed promotes into launcher-state.json AND
    // strips the file, making it truly one-shot (C8/S5)
    [Fact]
    public void LegacyHiddenFlag_SeedsOnce_ThenNeverResurrects()
    {
        MakeInstanceDir("a", name: "A");
        MakeInstanceDir("b", name: "B");
        File.WriteAllText(_groupFile,
            """
            {
                "formatVersion": "1",
                "groups": {
                    "B": {
                        "hidden": true,
                        "instances": ["a"]
                    }
                },
                "ungrouped": {
                    "hidden": true
                }
            }
            """);

        var state = TestLauncherState.Create();
        var list = CreateList();
        list.LauncherState = state;
        list.LoadList();
        Assert.True(list.IsGroupCollapsed("B"));
        Assert.True(list.IsGroupCollapsed(""));
        Assert.Equal(["B"], state.Current.Ui.HiddenGroups);
        Assert.True(state.Current.Ui.UngroupedHidden);

        // one-shot completion: the file no longer carries the flags after the seed
        using (var doc = JsonDocument.Parse(File.ReadAllText(_groupFile)))
        {
            var root = doc.RootElement;
            Assert.False(root.GetProperty("groups").GetProperty("B").TryGetProperty("hidden", out _));
            Assert.False(root.TryGetProperty("ungrouped", out _));
        }

        // the user un-collapses both (state-only write; the file stays flag-free)
        list.SetGroupCollapsed("B", false);
        list.SetGroupCollapsed("", false);
        Assert.Equal([], state.Current.Ui.HiddenGroups);
        Assert.False(state.Current.Ui.UngroupedHidden);

        // restart: a fresh list over the same state — the stale file flag is gone, so the
        // collapsed state stays cleared
        var reloaded = CreateList();
        reloaded.LauncherState = state;
        reloaded.LoadList();
        Assert.False(reloaded.IsGroupCollapsed("B"));
        Assert.False(reloaded.IsGroupCollapsed(""));
        Assert.Equal([], state.Current.Ui.HiddenGroups);
        Assert.False(state.Current.Ui.UngroupedHidden);
    }

    // heals installs that already went through a pre-fix resurrect cycle: a file flag that
    // only DUPLICATES the state (Add returned false, nothing newly seeded) is stripped too
    [Fact]
    public void LegacyHiddenFlag_DuplicatingState_IsStrippedToo()
    {
        MakeInstanceDir("a", name: "A");
        // the state already holds the collapse (an earlier load promoted it) while the file
        // flag was left behind — exactly the pre-fix leftover
        var state = TestLauncherState.Create();
        state.Update(s => s with
        {
            Ui = new UiState { HiddenGroups = ["B"] },
        });
        File.WriteAllText(_groupFile,
            """
            {
                "formatVersion": "1",
                "groups": {
                    "B": {
                        "hidden": true,
                        "instances": ["a"]
                    }
                }
            }
            """);

        var list = CreateList();
        list.LauncherState = state;
        list.LoadList();
        Assert.True(list.IsGroupCollapsed("B")); // state still says collapsed

        using (var doc = JsonDocument.Parse(File.ReadAllText(_groupFile)))
        {
            Assert.False(doc.RootElement.GetProperty("groups").GetProperty("B")
                .TryGetProperty("hidden", out _));
        }

        // and now un-collapse sticks across a restart
        list.SetGroupCollapsed("B", false);
        var reloaded = CreateList();
        reloaded.LauncherState = state;
        reloaded.LoadList();
        Assert.False(reloaded.IsGroupCollapsed("B"));
        Assert.Equal([], state.Current.Ui.HiddenGroups);
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

    // G4 pre-wiring: config side first, state side after — a failed group-file write rolls
    // the in-memory rename back and leaves the hidden state under the old name
    [Fact]
    public void RenameGroup_ConfigWriteFailure_RollsBack_AndKeepsStateSide()
    {
        MakeInstanceDir("one", name: "One");
        var state = TestLauncherState.Create();
        // a group-file path whose parent is a FILE: every group-file write fails
        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "x");
        var list = new InstanceList([_instancesDir])
        {
            Environment = _global,
            GroupFilePath = Path.Combine(blocker, "instgroups.json"),
            LauncherState = state,
        };
        list.LoadList();

        list.SetInstanceGroup("one", "G");
        Assert.Equal("G", list.GetInstanceGroup("one"));
        list.SetGroupCollapsed("G", true); // collapse persists to state without touching the file
        Assert.Equal(["G"], state.Current.Ui.HiddenGroups);

        list.RenameGroup("G", "H");

        // the config write failed → the rename rolled back in memory…
        Assert.Equal("G", list.GetInstanceGroup("one"));
        Assert.Equal(["G"], list.GetGroups());
        // …and the state side was never re-pointed (config-first ordering held)
        Assert.Equal(["G"], state.Current.Ui.HiddenGroups);
        Assert.True(list.IsGroupCollapsed("G"));
        Assert.False(list.IsGroupCollapsed("H"));

        // the success path still follows the name (covered by CollapseState_…_AndSurvivesAReload)
    }

    // G4 pre-wiring, delete half: the same failure rolls the in-memory deletion back too
    [Fact]
    public void DeleteGroup_ConfigWriteFailure_RollsBack_AndKeepsStateSide()
    {
        MakeInstanceDir("one", name: "One");
        var state = TestLauncherState.Create();
        // a group-file path whose parent is a FILE: every group-file write fails
        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "x");
        var list = new InstanceList([_instancesDir])
        {
            Environment = _global,
            GroupFilePath = Path.Combine(blocker, "instgroups.json"),
            LauncherState = state,
        };
        list.LoadList();

        list.SetInstanceGroup("one", "G");
        list.SetGroupCollapsed("G", true);
        Assert.Equal(["G"], state.Current.Ui.HiddenGroups);

        list.DeleteGroup("G");

        // the config write failed → the deletion rolled back in memory…
        Assert.Equal("G", list.GetInstanceGroup("one"));
        Assert.Equal(["G"], list.GetGroups());
        Assert.True(list.IsGroupCollapsed("G"));
        // …and the state side was never touched
        Assert.Equal(["G"], state.Current.Ui.HiddenGroups);
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
        // migrated to the primary group file location, with the dirname member converted
        // to the instance's uuid (DATA D3)
        Assert.True(File.Exists(_groupFile));
        using var doc = JsonDocument.Parse(File.ReadAllText(_groupFile));
        Assert.Equal([list.GetInstanceById("m")!.Uuid], doc.RootElement.GetProperty("groups")
            .GetProperty("Old").GetProperty("instances").EnumerateArray()
            .Select(v => v.GetString()).ToList());
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
        Assert.IsType<MinecraftInstance>(list.GetInstanceById("notype"));
        Assert.Equal("NoType", list.GetInstanceById("notype")!.Name());
    }

    // ----------------------------------------------------------- total playtime migration

    [Fact]
    public void TotalPlayTime_MigratedOnceOnFirstLoad()
    {
        MakeInstanceDir("played", name: "P");
        File.AppendAllText(Path.Combine(_instancesDir, "played", "instance.cfg"), "totalTimePlayed=3600\n");

        // the migration hook is launcher-state.json's Playtime state (S5; the former
        // TotalPlayTime/TotalPlayTimeMigrated settings keys retired by CONFIG C9)
        var state = TestLauncherState.Create();
        var list = CreateList();
        list.LauncherState = state;

        list.LoadList();
        Assert.Equal(3600, state.Current.Playtime.Total);
        Assert.True(state.Current.Playtime.Migrated);

        // second load does not accumulate again
        list.LoadList();
        Assert.Equal(3600, state.Current.Playtime.Total);
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

        File.WriteAllText(Path.Combine(staging, "instance.cfg"), "InstanceType=OneSix\nname=Staged Instance\n");

        var task = new StubInstanceTask { TargetDir = "", Group = "StagedGroup" };
        task.SetName("Staged Instance");
        Assert.True(list.CommitStagedInstance(staging, task, task.Group));

        // the staging folder moved into place, named after the task's name
        Assert.True(Directory.Exists(Path.Combine(_instancesDir, "Staged Instance")));
        Assert.False(Directory.Exists(staging));

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
        Assert.Equal(TaskState.Failed, wrapped.State);
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
        File.WriteAllText(Path.Combine(staging, "instance.cfg"), "InstanceType=OneSix\nname=Taken\n");

        Assert.True(list.CommitStagedInstance(staging, task, ""));
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

    // DATA D3: linkedInstances carry uuid references — the Prism/legacy dirname form
    // converts at the discovery round (the group-member import conversion, applied to the
    // per-instance list)

    [Fact]
    public void LinkedInstances_DirnameImports_ConvertToUuids_AtLoadList()
    {
        MakeInstanceDir("base", name: "Base");
        MakeInstanceDir("link", name: "Link");
        // Prism cfg: linkedImports in the QSettings string-list form ("base" = dirname)
        File.AppendAllText(Path.Combine(_instancesDir, "link", "instance.cfg"),
            "linkedInstances=base\n");

        var list = CreateList();
        list.LoadList();

        var baseInst = list.GetInstanceById("base")!;
        var link = list.GetInstanceById("link")!;
        // the imported dirname became the target's uuid (D3 import conversion)
        Assert.Equal([baseInst.Uuid], link.GetLinkedInstances());
        // ...and the conversion persisted (the dual-track write landed the uuid form)
        using (var doc = JsonDocument.Parse(
                   File.ReadAllText(Path.Combine(_instancesDir, "link", "instance.json"))))
        {
            Assert.Equal([baseInst.Uuid], doc.RootElement
                .GetProperty("misc").GetProperty("linkedInstances")
                .EnumerateArray().Select(v => v.GetString()).ToList());
        }

        // consumers answer by either reference form
        Assert.Equal(["link"], list.GetLinkedInstancesById("base"));
        Assert.Equal(["link"], list.GetLinkedInstancesById(baseInst.Uuid));
    }

    [Fact]
    public void LinkedInstances_UnresolvableReferences_StayVerbatim_UntilTheTargetAppears()
    {
        // the link names an instance that does not exist yet
        MakeInstanceDir("late-link", name: "LateLink");
        File.AppendAllText(Path.Combine(_instancesDir, "late-link", "instance.cfg"),
            "linkedInstances=ghost\n");
        var list = CreateList();
        list.LoadList();

        var late = list.GetInstanceById("late-link")!;
        // unresolvable: kept verbatim — the persisted payload still carries the raw dirname
        // (only the uuid identity write of the fresh construction touched the file)
        Assert.Equal(["ghost"], late.GetLinkedInstances());
        var afterFirstLoad = File.ReadAllText(
            Path.Combine(_instancesDir, "late-link", "instance.json"));
        Assert.Contains("ghost", afterFirstLoad);

        // a converged list must not rewrite: another round without the target changes nothing
        list.LoadList();
        Assert.Equal(afterFirstLoad, File.ReadAllText(
            Path.Combine(_instancesDir, "late-link", "instance.json")));

        // the target appears; the next discovery round converts
        MakeInstanceDir("ghost", name: "Ghost");
        list.LoadList();
        Assert.Equal([list.GetInstanceById("ghost")!.Uuid], late.GetLinkedInstances());
        Assert.Equal(["late-link"], list.GetLinkedInstancesById("ghost"));
    }

    [Fact]
    public void LinkedInstances_HalfConvertedLists_CollapseToTheUuidForm()
    {
        // fixed uuid so the cfg payload can carry it alongside the dirname; the link cfg is
        // a modern Prism file (ConfigVersion present — the QSettings codec splits the
        // comma list into the real 2-item list the linkedInstances feature always
        // ships with)
        MakeInstanceDir("base", name: "Base");
        File.AppendAllText(Path.Combine(_instancesDir, "base", "instance.cfg"),
            "uuid=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n");
        MakeInstanceDir("link", name: "Link");
        File.AppendAllText(Path.Combine(_instancesDir, "link", "instance.cfg"),
            "ConfigVersion=1.3\nlinkedInstances=base, aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n");

        var list = CreateList();
        list.LoadList();

        // both entries named the same target — one uuid entry survives
        Assert.Equal(["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"],
            list.GetInstanceById("link")!.GetLinkedInstances());
    }

    [Fact]
    public void AddLinkedInstanceId_ResolvesADirnameToTheUuid_AtWriteTime()
    {
        MakeInstanceDir("base", name: "Base");
        MakeInstanceDir("link", name: "Link");
        var list = CreateList();
        list.LoadList();

        var link = list.GetInstanceById("link")!;
        // the native write path (the C++ lineage passed id(), the dirname — BaseInstance.Id)
        // stores the uuid form: the list stamps the resolver onto discovered instances
        link.AddLinkedInstanceId("base");
        Assert.Equal([list.GetInstanceById("base")!.Uuid], link.GetLinkedInstances());

        // an unknown reference appends verbatim (a not-yet-discovered target converts at
        // the next LoadList round)
        link.AddLinkedInstanceId("not-there-yet");
        Assert.Equal([list.GetInstanceById("base")!.Uuid, "not-there-yet"],
            link.GetLinkedInstances());
    }
}
