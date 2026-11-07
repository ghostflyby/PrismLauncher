// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// The moved-root recovery of the watch layer, driven through the synchronous
// anchor/Rearm path — no test waits on real watcher timing.

using FlintLauncher.Core.Icons;
using FlintLauncher.Core.IO;
using TruePath;
using TruePath.SystemIo;
using Xunit;

namespace FlintLauncher.Tests;

public class RootMovedTests : IDisposable
{
    private readonly AbsolutePath _root = Temporary.CreateTempFolder();

    public void Dispose()
    {
        try
        {
            _root.DeleteDirectoryRecursively();
        }
        catch (DirectoryNotFoundException) { }
    }

    [Fact]
    public void RenamedRoot_Rearm_RepointsTheWatch_AndFiresMoved()
    {
        if (!OperatingSystem.IsLinux())
        {
            // the anchor re-derives paths only on Linux (/proc/self/fd); Windows keeps
            // backoff recovery and macOS has no safe re-derivation channel (fcntl is
            // variadic — the arm64 ABI makes a fixed-signature P/Invoke undefined)
            return;
        }

        var icons = MaterializedDirectory.Ensure(_root / "icons")!;
        (icons.Path / "keep.png").WriteAllBytes([1, 2, 3]);
        var pool = new IconPool(icons);

        var movedEvents = new List<(AbsolutePath Old, AbsolutePath New)>();
        var space = new DirectorySpace();
        space.TenantMoved += (oldDir, newDir) =>
        {
            movedEvents.Add((oldDir, newDir));
            if (oldDir == icons.Path)
            {
                pool.SetIconsDir(newDir); // the host wiring's re-point
            }
        };
        space.Register(icons, pool.Rescan);
        pool.AttachWatcher(space);
        space.Pulse(icons.Path);
        Assert.True(pool.IconFileExists("keep"));

        // the user renames the library root while the watch holds it
        var renamed = _root / "icons-renamed";
        Directory.Move(icons.Path.Value, renamed.Value);

        space.RearmAll(); // the anchor re-derives the new location; the space re-keys

        Assert.Equal(renamed, pool.IconsDir);
        Assert.True(pool.IconFileExists("keep"));
        Assert.Single(movedEvents);
        Assert.Equal(icons.Path, movedEvents[0].Old);
        Assert.Equal(renamed, movedEvents[0].New);
    }
}
