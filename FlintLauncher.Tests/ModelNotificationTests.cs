// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// The BCL notification contracts on the directory-backed models: Reset-only INCC (the
// update model is reconcile-and-swap — a pass that changed membership fires exactly ONE
// reset, a pass that changed nothing fires none) and INPC on the instance view properties.

using System.Collections.Specialized;
using FlintLauncher.Core.Config;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Minecraft.Resources;
using TruePath;
using TruePath.SystemIo;
using Xunit;

namespace FlintLauncher.Tests;

public class ModelNotificationTests : IDisposable
{
    private readonly AbsolutePath _root = Temporary.CreateTempFolder();
    private readonly AbsolutePath _instancesDir;
    private readonly TestGlobalSettings _global = new();

    public ModelNotificationTests()
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

    private static void MakeInstanceDir(AbsolutePath instancesDir, string id, string name)
    {
        var dir = MaterializedDirectory.Ensure(instancesDir / id)!;
        (dir.Path / "instance.cfg").WriteAllLines(["InstanceType=OneSix", "name=" + name]);
    }

    private InstanceList CreateList() =>
        new([MaterializedDirectory.Ensure(_instancesDir)!],
            _global,
            _instancesDir / ".test-groups.json",
            []);

    [Fact]
    public void Reconcile_FiresExactlyOneReset_PerChangingPass()
    {
        MakeInstanceDir(_instancesDir, "in-a", "A");
        var list = CreateList();
        list.LoadList();

        var resets = 0;
        var counts = 0;
        list.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                resets++;
            }
        };
        list.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(list.Count))
            {
                counts++;
            }
        };

        using var space = new DirectorySpace();
        list.AttachWatcher(space);

        MakeInstanceDir(_instancesDir, "in-b", "B");
        (_instancesDir / "in-a").DeleteDirectoryRecursively(); // one pass: remove + add
        space.Pulse(_instancesDir);
        Assert.Equal(1, resets); // the pre-interface version raised twice here
        Assert.Equal(1, counts);

        // a pass with no membership change is silent
        space.Pulse(_instancesDir);
        Assert.Equal(1, resets);
        Assert.Equal(1, counts);

        // a caller-driven reload over the same state is silent too
        list.LoadList();
        Assert.Equal(1, resets);
    }

    [Fact]
    public void BaseInstance_Setter_RaisesTypedINPC()
    {
        var settings = new InstanceJsonSettingsObject(_root / "in-x", _global);
        var inst = new NullInstance(settings, _root / "in-x");

        var names = new List<string?>();
        inst.PropertyChanged += (_, e) => names.Add(e.PropertyName);

        inst.SetName("renamed");
        Assert.Contains("Name", names);
        Assert.DoesNotContain(string.Empty, names);

        inst.NotifyChanged(); // the whole-view notification (group membership and kin)
        Assert.Contains(string.Empty, names);
    }

    [Fact]
    public void ResourceFolder_NoChange_Update_IsSilent()
    {
        var mods = MaterializedDirectory.Ensure(_root / "mods")!;
        var model = new ModFolderModel(mods, isIndexed: false);
        (mods.Path / "a.jar").WriteAllBytes([
            0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        ]);
        model.Update(); // baseline snapshot: one resource
        Assert.Equal(1, model.Size);

        var resets = 0;
        model.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                resets++;
            }
        };

        model.Update(); // nothing changed on disk — the changed-flag must stay quiet
        Assert.Equal(0, resets);

        (mods.Path / "a.jar").Delete();
        model.Update();
        Assert.Equal(1, resets);
        Assert.Equal(0, model.Size);
    }
}
