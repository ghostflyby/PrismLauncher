// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// The tenancy wiring end to end: cooperative root moves re-point every tenant's own
// state (the list's configured roots, the folder models' Dir), and instances leaving
// the list release their folder-model watches instead of stranding them in retry.

using FlintLauncher.Core.Instances;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Minecraft.Resources;
using FlintLauncher.Core.Minecraft.Worlds;
using TruePath;
using TruePath.SystemIo;
using Xunit;

namespace FlintLauncher.Tests;

public class WatchTenancyWiringTests : IDisposable
{
    private readonly AbsolutePath _root = Temporary.CreateTempFolder();
    private readonly DirectorySpace _space = new();

    public void Dispose()
    {
        _space.Dispose();
        try
        {
            _root.DeleteDirectoryRecursively();
        }
        catch (DirectoryNotFoundException) { }
    }

    private void MakeInstanceDir(AbsolutePath instancesDir, string id)
    {
        var dir = instancesDir / id;
        dir.CreateDirectory();
        (dir / "instance.cfg").WriteAllLines(["InstanceType=OneSix", "name=" + id]);
        TestLegacyConversion.Convert(instancesDir);
    }

    private InstanceList CreateList(AbsolutePath instancesDir) =>
        new([MaterializedDirectory.Ensure(instancesDir)!],
            new TestGlobalSettings(),
            _root / "groups.json",
            [instancesDir / "instgroups.json"]);

    // the instances root moves (cooperative move stands in for the anchor-driven
    // external move): the configured root set follows, so the next discovery reads the
    // NEW location instead of emptying the list over the dead path
    [Fact]
    public void InstanceRootMove_RepointsTheConfiguredRoots_AndDiscoveryFollows()
    {
        var instances = _root / "instances";
        MakeInstanceDir(instances, "aaa");
        var list = CreateList(instances);
        list.AttachWatcher(_space);
        Assert.Equal(InstListError.NoError, list.LoadList());
        Assert.Single(list);

        var moved = _root / "instances-elsewhere";
        Assert.True(_space.Move(instances, moved));

        Assert.Equal([moved], list.InstanceDirs);
        Assert.True(_space.IsRegistered(moved));
        Assert.False(_space.IsRegistered(instances));

        Assert.Equal(InstListError.NoError, list.LoadList());
        Assert.Single(list);
        // the kept object must read the NEW location — a stale InstanceRoot would send
        // every launch/read against the dead path
        var inst = list.GetInstanceById("aaa");
        Assert.NotNull(inst);
        Assert.Equal(moved / "aaa", inst.InstanceRoot);
        Assert.Single(list);
    }

    // a deleted instance must not leave its folder-model watches retrying against the
    // deleted directories forever
    [Fact]
    public void DeleteInstance_ReleasesTheFolderModelWatches()
    {
        var instances = _root / "instances";
        MakeInstanceDir(instances, "gone");
        var list = CreateList(instances);
        list.AttachWatcher(_space);
        Assert.Equal(InstListError.NoError, list.LoadList());

        var inst = Assert.IsType<MinecraftInstance>(list.GetInstanceById("gone"));
        var mods = inst.LoaderModList();
        Assert.True(_space.IsRegistered(mods.Dir));

        list.DeleteInstance("gone");

        Assert.False(_space.IsRegistered(mods.Dir));
        Assert.Empty(list);
    }

    // the same release on the discovery path: an externally deleted instance vanishes on
    // the next reconcile, taking its folder-model watches with it
    [Fact]
    public void VanishedInstance_DropsItsFolderModelWatches()
    {
        var instances = _root / "instances";
        MakeInstanceDir(instances, "vanished");
        var list = CreateList(instances);
        list.AttachWatcher(_space);
        Assert.Equal(InstListError.NoError, list.LoadList());

        var inst = Assert.IsType<MinecraftInstance>(list.GetInstanceById("vanished"));
        var worlds = inst.WorldList();
        Assert.True(_space.IsRegistered(worlds.Dir));

        (instances / "vanished").DeleteDirectoryRecursively();
        _space.Pulse(instances);

        Assert.Empty(list);
        Assert.False(_space.IsRegistered(worlds.Dir));
    }

    // the folder models re-point their own directory handle when their root moves, so
    // the reconcile reads the new location instead of clearing over the dead one
    [Fact]
    public void WorldListFollowsItsRootMove()
    {
        var saves = MaterializedDirectory.Ensure(_root / "saves")!;
        var world = saves.Path / "New World";
        world.CreateDirectory();
        (world / "level.dat").WriteAllBytes([1, 2, 3]);

        var list = new WorldList(saves);
        list.AttachWatcher(_space);
        Assert.Single(list);

        var moved = _root / "saves-elsewhere";
        Assert.True(_space.Move(saves.Path, moved));

        Assert.Equal(moved, list.Dir);
        Assert.Single(list);
        Assert.Equal("New World", list[0].FolderName);
    }

    [Fact]
    public void ModFolderModelFollowsItsRootMove()
    {
        var mods = MaterializedDirectory.Ensure(_root / "mods")!;
        (mods.Path / "somemod.jar").WriteAllBytes([1]);

        var model = new ModFolderModel(mods, isIndexed: false);
        model.AttachWatcher(_space);
        model.Update();
        Assert.Single(model);

        var moved = _root / "mods-elsewhere";
        Assert.True(_space.Move(mods.Path, moved));

        Assert.Equal(moved, model.Dir);
        Assert.Single(model);
    }

    // a failing moved announcement is swallowed at the notification boundary — it must
    // not escape onto the watch's timer thread (an unhandled exception there kills the
    // process); the re-key already happened, so the registration stays consistent
    [Fact]
    public void ThrowingMovedConsumer_DoesNotEscapeTheAnnouncement()
    {
        var dir = MaterializedDirectory.Ensure(_root / "watched")!;
        var space = new DirectorySpace();
        space.Register(dir, () => { }, moved: (_, _) => throw new InvalidOperationException("broken tenant"));

        var moved = _root / "watched-elsewhere";
        var movedOk = false;
        try
        {
            movedOk = space.Move(dir.Path, moved);
        }
        catch (InvalidOperationException)
        {
            Assert.Fail("the moved announcement escaped to the caller");
        }

        Assert.True(movedOk);
        Assert.True(space.IsRegistered(moved));
    }
}
