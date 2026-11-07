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

    // The production recovery chain, WITHOUT the test-driven RearmAll: on linux the FSW
    // never reports the rename of the watched root itself, so the production trigger is
    // the watch's own existence sweep — DirectorySpace threads the cadence down, the sweep
    // takes the lost state, and the anchor re-derives the move. The 100ms cadence here is
    // the test seam for what runs at 5s in production.
    [Fact]
    public async Task RenamedRoot_TheProductionChain_FiresMoved_WithoutARearm()
    {
        if (!OperatingSystem.IsLinux())
        {
            // the anchor re-derives paths only on Linux (/proc/self/fd); Windows recovers at
            // the same path through backoff and macOS has no safe re-derivation channel —
            // see the sync test above
            return;
        }

        var icons = MaterializedDirectory.Ensure(_root / "icons")!;
        (icons.Path / "keep.png").WriteAllBytes([1, 2, 3]);
        var pool = new IconPool(icons);

        var moved = new TaskCompletionSource<(AbsolutePath Old, AbsolutePath New)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var renamed = _root / "icons-renamed";
        var rescanned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var space = new DirectorySpace(watchSweepInterval: TimeSpan.FromMilliseconds(100));
        space.TenantMoved += (oldDir, newDir) =>
        {
            moved.TrySetResult((oldDir, newDir));
            // the host wiring's re-point (Register is idempotent per root, so AttachWatcher's
            // own moved callback never lands on top of the registration above)
            if (oldDir == icons.Path)
            {
                pool.SetIconsDir(newDir);
            }
        };
        // the rescan is the end state: this reconcile fires once the watch re-adopted the
        // root at its new path, so completing here makes the await deterministic (a task
        // completed at TenantMoved would race the post-rebind reconcile)
        space.Register(icons, () =>
        {
            pool.Rescan();
            if (renamed == pool.IconsDir)
            {
                rescanned.TrySetResult();
            }
        });
        pool.AttachWatcher(space);
        space.Pulse(icons.Path);
        Assert.True(pool.IconFileExists("keep"));

        Directory.Move(icons.Path.Value, renamed.Value);

        var winner = await Task.WhenAny(moved.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(winner == moved.Task,
            "the rename never surfaced through the watch layer within 10s — production recovery is blind to it");
        winner = await Task.WhenAny(rescanned.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(winner == rescanned.Task, "the pool was never rescanned at the renamed path");

        Assert.Equal(renamed, pool.IconsDir);
        Assert.True(pool.IconFileExists("keep"));
    }
}
