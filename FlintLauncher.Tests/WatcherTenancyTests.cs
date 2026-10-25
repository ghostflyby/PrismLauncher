// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// The adopted directory-model tenants (IconPool/SkinsLibrary/WorldList) in watcher mode:
// the resident state is refreshed by coalesced watcher events, surfaced through the
// synchronous pulse — no test waits on real watcher timing (DirectorySpaceTests covers
// the real-event wiring once).

using FlintLauncher.Core.Icons;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Minecraft.Worlds;
using FlintLauncher.Core.Skins;
using TruePath;
using TruePath.SystemIo;
using Xunit;

namespace FlintLauncher.Tests;

public class WatcherTenancyTests : IDisposable
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

    // ------------------------------------------------------------------ IconPool

    [Fact]
    public void IconPool_Watched_ExternalDrop_ReachesTheIndex()
    {
        var icons = MaterializedDirectory.Ensure(_root / "icons")!;
        var pool = new IconPool(icons);
        pool.AttachWatcher(_space);
        Assert.Empty(pool.Entries()); // watched: serves the resident index

        (icons.Path / "dropped.png").WriteAllBytes([1, 2, 3]);
        _space.Pulse(icons.Path);

        Assert.True(pool.IconFileExists("dropped"));
    }

    [Fact]
    public void IconPool_SetIconsDir_RepointsTheWatch_AndRescans()
    {
        var icons = MaterializedDirectory.Ensure(_root / "icons")!;
        (icons.Path / "old.png").WriteAllBytes([1]);
        var pool = new IconPool(icons);
        pool.AttachWatcher(_space);
        Assert.True(pool.IconFileExists("old"));

        var other = MaterializedDirectory.Ensure(_root / "other-icons")!;
        (other.Path / "new.png").WriteAllBytes([2]);
        pool.SetIconsDir(other.Path);

        Assert.Equal(other.Path, pool.IconsDir);
        Assert.True(pool.IconFileExists("new"));
        Assert.False(pool.IconFileExists("old"));
    }

    // ------------------------------------------------------------------ SkinsLibrary

    [Fact]
    public void SkinsLibrary_Watched_ExternalIndexEdit_ReachesTheList()
    {
        var skins = MaterializedDirectory.Ensure(_root / "skins")!;
        var library = new SkinsLibrary(skins);
        library.AttachWatcher(_space);
        Assert.Empty(library.List());

        // an external edit of the store index (repair/import scenario): the entry's
        // texture resolves as <skinsDir>/<name>.png by convention
        (skins.Path / "imported.png").WriteAllBytes([1, 2, 3]);
        File.WriteAllText((skins.Path / "index.json").Value,
            """{"skins": [{"name": "imported", "model": "classic"}]}""");
        _space.Pulse(skins.Path);

        Assert.Equal("imported", library.List().Single().Name());
    }

    // ------------------------------------------------------------------ WorldList

    [Fact]
    public void WorldList_Watched_ExternalWorldDir_ReachesTheList()
    {
        var saves = MaterializedDirectory.Ensure(_root / "saves")!;
        var list = new WorldList(saves);
        list.AttachWatcher(_space);
        list.Update();
        Assert.Equal(0, list.Count);

        var world = saves.Path / "New World";
        world.CreateDirectory();
        (world / "level.dat").WriteAllBytes([1, 2, 3]);
        _space.Pulse(saves.Path);

        Assert.Equal(1, list.Count);
        Assert.Equal("New World", list[0].FolderName);
    }
}
