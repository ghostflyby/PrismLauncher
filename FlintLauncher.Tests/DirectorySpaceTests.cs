// SPDX-License-Identifier: GPL-3.0-only
// Tests for the watched-directory tenancy (the restored C++ watcher glue): pulse-driven
// reconcile is deterministic; the one real-FSW smoke test carries a generous budget
// because the macOS backend's start latency makes event timing inherently loose.

using FlintLauncher.Core.IO;
using TruePath;
using TruePath.SystemIo;
using Xunit;

namespace FlintLauncher.Tests;

public class DirectorySpaceTests : IDisposable
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

    private MaterializedDirectory Materialize(string name = "watched")
    {
        var dir = MaterializedDirectory.Ensure(_root / name);
        Assert.NotNull(dir);
        return dir;
    }

    [Fact]
    public void Pulse_RunsTheRegisteredReconcile()
    {
        var space = new DirectorySpace();
        var dir = Materialize();
        var runs = 0;
        space.Register(dir, () => runs++);

        space.Pulse(dir.Path);
        Assert.Equal(1, runs);
        space.Pulse();
        Assert.Equal(2, runs);
    }

    [Fact]
    public void Register_IsIdempotent_PerRoot()
    {
        var space = new DirectorySpace();
        var dir = Materialize();
        var runs = 0;
        space.Register(dir, () => runs++);
        space.Register(dir, () => runs++); // same root — must not stack a second watch

        space.Pulse();
        Assert.Equal(1, runs);
    }

    [Fact]
    public void Unregister_StopsTheReconcile()
    {
        var space = new DirectorySpace();
        var dir = Materialize();
        var runs = 0;
        space.Register(dir, () => runs++);

        space.Unregister(dir.Path);
        space.Pulse();
        Assert.Equal(0, runs);
    }

    [Fact]
    public void Dispatcher_WrapsEveryReconcile()
    {
        var dispatched = 0;
        var space = new DirectorySpace(action =>
        {
            dispatched++;
            action();
        });
        var dir = Materialize();
        var runs = 0;
        space.Register(dir, () => runs++);

        space.Pulse();
        Assert.Equal(1, runs);
        Assert.Equal(1, dispatched);
    }

    [Fact]
    public void SyncTenantRoots_FollowsTheDiff()
    {
        var space = new DirectorySpace();
        var first = Materialize("first");
        var second = Materialize("second");
        var runs = 0;
        space.Register(first, () => runs++);

        space.SyncTenantRoots([first.Path], [second.Path], () => runs++);

        space.Pulse(); // all registered roots: only "second" reconciles now
        Assert.Equal(1, runs);
    }

    // the sequences are consumed exactly once each — a one-shot or mutating enumerable
    // must not corrupt the diff through a second pass
    [Fact]
    public void SyncTenantRoots_EnumeratesEachSequenceOnce()
    {
        var space = new DirectorySpace();
        var first = Materialize("first");
        var second = Materialize("second");
        var runs = 0;
        space.Register(first, () => runs++);

        var oldEnumerations = 0;
        var newEnumerations = 0;
        IEnumerable<AbsolutePath> OldRoots()
        {
            oldEnumerations++;
            yield return first.Path;
        }

        IEnumerable<AbsolutePath> NewRoots()
        {
            newEnumerations++;
            yield return second.Path;
        }

        space.SyncTenantRoots(OldRoots(), NewRoots(), () => runs++);

        Assert.Equal(1, oldEnumerations);
        Assert.Equal(1, newEnumerations);
        space.Pulse();
        Assert.Equal(1, runs);
    }

    [Fact]
    public void Move_RenamesTheRoot_Rekeys_AndFiresTenantMoved()
    {
        var space = new DirectorySpace();
        var from = Materialize("from");
        (from.Path / "marker.txt").WriteAllBytes([1]);
        var target = _root / "to";

        var movedTo = new List<AbsolutePath>();
        // the tenant re-point (IconPool.SetIconsDir in the real wiring): the reconcile
        // reads wherever the tenant currently lives
        var current = from.Path;
        space.TenantMoved += (_, newDir) =>
        {
            movedTo.Add(newDir);
            current = newDir;
        };

        AbsolutePath? reconciledAt = null;
        var reconciles = 0;
        space.Register(from, () =>
        {
            reconciles++;
            reconciledAt = Directory.Exists(current.Value) ? current : null;
        });

        Assert.True(space.Move(from.Path, target));

        // the move announced the new location, re-keyed the watch, and reconciled once
        // at the NEW location as part of the move (the tenant re-point handler ran first)
        Assert.Equal([target], movedTo);
        Assert.True((target / "marker.txt").ExistsFile());
        Assert.Equal(1, reconciles);
        Assert.Equal(target, reconciledAt);
        Assert.False(from.Path.ExistsDirectory()); // it MOVED — the old path is gone

        // later pulses keep reconciling the re-keyed registration
        space.Pulse(target);
        Assert.Equal(2, reconciles);

        // a move whose source is not a registered root is refused
        Assert.False(space.Move(from.Path, _root / "never"));
    }

    // the deterministic root-loss recovery: after the root vanishes the pulse still
    // reconciles (tenants orphan); once it returns, RearmAll re-binds and the next pulse
    // reconciles over the restored content
    [Fact]
    public void RootLoss_StillReconciles_AndRearm_Recovers()
    {
        var space = new DirectorySpace();
        var dir = Materialize();
        (dir.Path / "marker.txt").WriteAllText("x");
        List<string> seen = [];
        var runs = 0;
        space.Register(dir, () =>
        {
            runs++;
            seen = Directory.Exists(dir.Path.Value)
                ? [.. Directory.EnumerateFileSystemEntries(dir.Path.Value)]
                : [];
        });

        var movedAway = _root / "moved-away";
        Directory.Move(dir.Path.Value, movedAway.Value);
        space.Pulse(dir.Path);
        Assert.True(runs >= 1);
        Assert.Empty(seen); // orphaned — the reconcile saw the vanished root

        Directory.Move(movedAway.Value, dir.Path.Value); // the root returns (same content)
        space.RearmAll();
        space.Pulse(dir.Path);
        Assert.True(runs >= 2);
        Assert.Contains(seen, entry => entry.EndsWith("marker.txt", StringComparison.Ordinal));
    }

    // the only real-FSW test: proves the FileSystemWatcher wiring delivers events end to
    // end. Generous poll budget — the macOS backend's start latency is seconds, not ms.
    [Fact]
    public void RealWatcher_FileCreation_TriggersReconcile()
    {
        var space = new DirectorySpace();
        var dir = Materialize();
        var runs = 0;
        space.Register(dir, () => runs++);
        space.Pulse();
        Assert.Equal(1, runs); // baseline through the pulse path

        (dir.Path / "created-by-test.txt").WriteAllText("x");

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (runs == 1 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(100);
        }

        Assert.True(runs >= 2, $"the real watcher never fired within the budget (runs={runs})");
    }
}
