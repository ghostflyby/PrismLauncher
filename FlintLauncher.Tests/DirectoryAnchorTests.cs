// SPDX-License-Identifier: GPL-3.0-only
// Identity and move-stable path re-derivation for watched roots. Platforms without
// anchor support defer to the path-based backoff, so every anchor test gates on the
// platform.

using FlintLauncher.Core.IO;
using TruePath;
using TruePath.SystemIo;
using Xunit;

namespace FlintLauncher.Tests;

public class DirectoryAnchorTests : IDisposable
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

    private DirectoryAnchor? Acquire(string name = "watched")
    {
        var dir = MaterializedDirectory.Ensure(_root / name)!;
        return DirectoryAnchor.TryAcquire(dir.Path);
    }

    [Fact]
    public void Identity_IsStable_AcrossARename()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // the anchor is POSIX-only
        }

        var dir = MaterializedDirectory.Ensure(_root / "a")!;
        using var anchor = DirectoryAnchor.TryAcquire(dir.Path);
        Assert.NotNull(anchor);
        Assert.True(anchor.Identity.IsAvailable);

        var moved = _root / "b";
        Directory.Move(dir.Path.Value, moved.Value);

        Assert.True(anchor.MatchesIdentity(moved));  // the same inode lives at the new path
        Assert.False(anchor.MatchesIdentity(dir.Path));

        // the re-derived path is the same directory (F_GETPATH reports the physical
        // /private/var spelling of the /var symlink — compare by identity, not lexically)
        var current = Assert.NotNull(anchor.TryCurrentPath());
        Assert.True(DirectoryIdentity.Of(current) == anchor.Identity);
    }

    [Fact]
    public void Anchor_OfDeletedDirectory_ReportsGone()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = MaterializedDirectory.Ensure(_root / "doomed")!;
        using var anchor = DirectoryAnchor.TryAcquire(dir.Path);
        Assert.NotNull(anchor);

        dir.Path.DeleteDirectoryRecursively();

        // the fd keeps the dead inode alive; the path is gone — the caller's signal to
        // treat the tenant as orphaned instead of chasing a rename
        Assert.False(anchor.MatchesIdentity(dir.Path));
        Assert.Null(anchor.TryCurrentPath());
    }
}
