// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// The instance list's watcher tenancy (the restored C++ watcher glue): external changes
// to the instance roots reload the list through a pulse — no test waits on real watcher
// timing (DirectorySpaceTests covers the real-event wiring once).

using FlintLauncher.Core.Instances;
using FlintLauncher.Core.IO;
using TruePath;
using TruePath.SystemIo;
using Xunit;

namespace FlintLauncher.Tests;

public class InstanceWatchTests : IDisposable
{
    private readonly AbsolutePath _root = Temporary.CreateTempFolder();
    private readonly AbsolutePath _instancesDir;

    public InstanceWatchTests()
    {
        _instancesDir = MaterializedDirectory.Ensure(_root / "instances")!.Path;
    }

    public void Dispose()
    {
        try
        {
            _root.DeleteDirectoryRecursively();
        }
        catch (DirectoryNotFoundException) { }
    }

    private readonly TestGlobalSettings _global = new();

    private InstanceList CreateList() =>
        new([MaterializedDirectory.Ensure(_instancesDir)!],
            _global,
            _instancesDir / ".test-groups.json",
            []);

    private static void MakeInstanceDir(AbsolutePath instancesDir, string id, string name)
    {
        var dir = MaterializedDirectory.Ensure(instancesDir / id)!;
        (dir.Path / "instance.cfg").WriteAllLines(["InstanceType=OneSix", "name=" + name]);
    }

    [Fact]
    public void Pulse_ReloadsAfterExternalRename()
    {
        MakeInstanceDir(_instancesDir, "in-a", "Old");
        var list = CreateList();
        list.LoadList();
        Assert.NotNull(list.GetInstanceById("in-a"));

        using var space = new DirectorySpace();
        list.AttachWatcher(space);

        // a rename does not change the folder's identity — the reload must re-adopt the
        // instance under its new dirname and drop the stale id (the watched root itself
        // stays put; renaming a watched ROOT is the anchor layer's case)
        Directory.Move((_instancesDir / "in-a").Value, (_instancesDir / "in-b").Value);
        space.Pulse();

        Assert.Equal(1, list.Count);
        Assert.Null(list.GetInstanceById("in-a"));
        var moved = list.GetInstanceById("in-b");
        Assert.NotNull(moved);
        Assert.EndsWith("in-b", moved.InstanceRoot.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Pulse_ReloadsAfterExternalDelete()
    {
        MakeInstanceDir(_instancesDir, "doomed", "Doomed");
        MakeInstanceDir(_instancesDir, "stay", "Stay");
        var list = CreateList();
        list.LoadList();
        Assert.Equal(2, list.Count);

        using var space = new DirectorySpace();
        list.AttachWatcher(space);

        (_instancesDir / "doomed").DeleteDirectoryRecursively();
        space.Pulse();

        Assert.Equal(1, list.Count);
        Assert.Null(list.GetInstanceById("doomed"));
        Assert.NotNull(list.GetInstanceById("stay"));
    }

    [Fact]
    public void AttachWatcher_FollowsSetInstanceDirs()
    {
        MakeInstanceDir(_instancesDir, "in-first", "First");
        var list = CreateList();
        list.LoadList();

        using var space = new DirectorySpace();
        list.AttachWatcher(space);

        var second = MaterializedDirectory.Ensure(_root / "instances-second")!;
        MakeInstanceDir(second.Path, "in-second", "Second");
        list.SetInstanceDirs([second.Path]);

        Assert.Equal(1, list.Count); // SetInstanceDirs reloads right away
        Assert.NotNull(list.GetInstanceById("in-second"));

        // the new root is watched: external changes there pulse-reload, the abandoned
        // root no longer does
        MakeInstanceDir(second.Path, "in-second-late", "Late");
        MakeInstanceDir(_instancesDir, "in-first-late", "LateFirst");
        space.Pulse();

        Assert.Equal(2, list.Count);
        Assert.NotNull(list.GetInstanceById("in-second-late"));
        Assert.Null(list.GetInstanceById("in-first-late"));
    }
}
