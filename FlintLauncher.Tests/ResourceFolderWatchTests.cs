// SPDX-License-Identifier: GPL-3.0-only
// The mod folder model's watcher tenancy (the C++ startWatching parity): external changes
// to the mods directory reconcile the resident list through a pulse — no test waits on
// real watcher timing (DirectorySpaceTests covers the real-event wiring once).

using FlintLauncher.Core.IO;
using FlintLauncher.Core.Minecraft.Resources;
using TruePath;
using TruePath.SystemIo;
using Xunit;

namespace FlintLauncher.Tests;

public class ResourceFolderWatchTests : IDisposable
{
    // an empty but structurally valid zip archive — Resource/Mod validity is extension-
    // driven and Mod's zip parse only opens the archive
    private static readonly byte[] EmptyZip =
    [
        0x50, 0x4B, 0x05, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00,
    ];

    private readonly AbsolutePath _root = Temporary.CreateTempFolder();

    public void Dispose()
    {
        try
        {
            _root.DeleteDirectoryRecursively();
        }
        catch (DirectoryNotFoundException) { }
    }

    private (ModFolderModel Model, DirectorySpace Space) CreateWatched(string name = "mods")
    {
        var dir = MaterializedDirectory.Ensure(_root / name)!;
        var model = new ModFolderModel(dir, isIndexed: false);
        var space = new DirectorySpace();
        model.AttachWatcher(space);
        return (model, space);
    }

    [Fact]
    public void Pulse_ReloadsAfterExternalAdd()
    {
        var (model, space) = CreateWatched();
        model.Update();
        Assert.Equal(0, model.Size);

        (model.Dir / "coolmod.jar").WriteAllBytes(EmptyZip);
        space.Pulse(model.Dir);

        Assert.Equal(1, model.Size);
        Assert.Equal("coolmod.jar", model.At(0).Fileinfo.Name);
    }

    [Fact]
    public void Pulse_ReloadsAfterExternalDelete()
    {
        var (model, space) = CreateWatched();
        (model.Dir / "coolmod.jar").WriteAllBytes(EmptyZip);
        model.Update();
        Assert.Equal(1, model.Size);

        (model.Dir / "coolmod.jar").Delete();
        space.Pulse(model.Dir);

        Assert.Equal(0, model.Size);
    }

    // the C++ IsWatching parity: with the watcher attached, install does not run a
    // synchronous Update — the file lands, and the coalesced reconcile lists it
    [Fact]
    public void InstallResource_WithWatcher_DefersToTheReconcile()
    {
        var (model, space) = CreateWatched("installed-target");
        model.Update();
        var source = MaterializedDirectory.Ensure(_root / "source")!.Path;
        var jar = source / "installed.jar";
        jar.WriteAllBytes(EmptyZip);

        Assert.True(model.InstallResource(jar.Value));
        Assert.Equal(0, model.Size); // no synchronous refresh while watching

        space.Pulse(model.Dir);
        Assert.Equal(1, model.Size);
        Assert.Equal("installed.jar", model.At(0).Fileinfo.Name);
    }

    // the m_lock parity: background reconciles racing endpoint reads/writes must not tear
    // the model (smoke — no timing assertions, just "no exception, consistent tail state")
    [Fact]
    public async Task ConcurrentUpdateAndReads_StayConsistent()
    {
        var (model, space) = CreateWatched("concurrent");
        model.Update();
        for (var i = 0; i < 8; i++)
        {
            (model.Dir / $"mod{i}.jar").WriteAllBytes(EmptyZip);
        }

        const int rounds = 300;
        var reader = Task.Run(() =>
        {
            for (var i = 0; i < rounds; i++)
            {
                _ = model.Size;
                _ = model.AllResources();
                _ = model.Find("mod0");
            }
        });
        var mutator = Task.Run(() =>
        {
            for (var i = 0; i < rounds; i++)
            {
                model.Update();
            }
        });

        await Task.WhenAll(reader, mutator);
        space.Pulse(model.Dir);
        Assert.Equal(8, model.Size);
    }
}
