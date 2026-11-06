// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

using System.Runtime.InteropServices;
using TruePath;

namespace FlintLauncher.Core.IO;

/// <summary>A stable handle on one directory: the anchor keeps answering "where does
/// the directory I pinned live now?" and "is this path still the directory I pinned?"
/// even after the directory was renamed or moved, when its path no longer describes it.
/// <para>Anchor and path differ in what survives a move: a path is just a name — after
/// a rename it either points at nothing or at a different directory; an anchor is
/// pinned to the directory itself. Identity comparison via <see cref="MatchesIdentity"/>
/// and location re-derivation via <see cref="TryCurrentPath"/> both go through the
/// anchor.</para>
/// <para>An anchor is a transient witness: acquire it, validate or re-derive, dispose
/// it. Holding one open long-term keeps a deleted directory pinned on disk and can
/// surprise external tools that expect the space to be reclaimed.</para>
/// <para>On platforms without anchor support <see cref="TryAcquire"/> returns null;
/// consumers keep their path-based fallbacks (existence polling) exactly as before —
/// the anchor is strictly an upgrade when available.</para></summary>
public sealed partial class DirectoryAnchor : IDisposable
{
    private const int FGetPathMacOs = 50;
    private const int PathBufferSize = 1024;

    private int _fd;

    private DirectoryAnchor(int fd, DirectoryIdentity identity, AbsolutePath acquiredAt)
    {
        _fd = fd;
        Identity = identity;
        AcquiredAt = acquiredAt;
    }

    /// <summary>The identity pinned at acquire time. Stable across renames of the
    /// directory.</summary>
    public DirectoryIdentity Identity { get; }

    /// <summary>The path the anchor was acquired at — NOT kept current; ask
    /// <see cref="TryCurrentPath"/> for where the directory lives now.</summary>
    public AbsolutePath AcquiredAt { get; }

    /// <summary>Pins the directory currently at <paramref name="path"/>. Null when the
    /// platform provides no anchor support or the path cannot be opened.</summary>
    public static DirectoryAnchor? TryAcquire(AbsolutePath path)
    {
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
        {
            return null;
        }

        var fd = Open(path.Value, 0); // O_RDONLY — a directory opens read-only on both
        if (fd < 0)
        {
            return null;
        }

        var identity = DirectoryIdentity.Of(path);
        return identity.IsAvailable
            ? new DirectoryAnchor(fd, identity, path)
            : Kill(fd); // no usable identity — the fd is worthless, do not leak it
    }

    /// <summary>Where the pinned directory lives NOW — re-derived through the anchor,
    /// so it follows renames and moves. Null when the pinned directory no longer exists
    /// anywhere (it was deleted: treat the tenant as orphaned) or the platform cannot
    /// re-derive locations — the caller keeps its path-based fallback instead.</summary>
    public AbsolutePath? TryCurrentPath()
    {
        if (_fd < 0)
        {
            return null;
        }

        if (OperatingSystem.IsLinux())
        {
            var buf = new byte[PathBufferSize];
            var len = ReadLink($"/proc/self/fd/{_fd}", buf, (ulong)buf.Length);
            if (len <= 0)
            {
                return null;
            }

            var target = System.Text.Encoding.UTF8.GetString(buf, 0, (int)len);
            return Directory.Exists(target) ? new AbsolutePath(target) : null;
        }

        if (OperatingSystem.IsMacOS())
        {
            // F_GETPATH through the register-filling padding form — see the P/Invoke note
            var buf = new byte[PathBufferSize];
            return FcntlGetPath(_fd, FGetPathMacOs, 0, 0, 0, 0, 0, 0, buf) == 0
                ? ReadPath(buf)
                : null;
        }

        return null; // anchored on a platform without re-derivation — caller's fallback
    }

    private static AbsolutePath? ReadPath(ReadOnlySpan<byte> buf)
    {
        var end = buf.IndexOf((byte)0);
        if (end <= 0)
        {
            return null;
        }

        var target = System.Text.Encoding.UTF8.GetString(buf[..end]);
        return Directory.Exists(target) ? new AbsolutePath(target) : null;
    }

    /// <summary>Whether the pinned identity still lives at <paramref name="path"/>
    /// (rename ⇒ false-but-alive, delete ⇒ false-and-dead; tell them apart via
    /// <see cref="TryCurrentPath"/>).</summary>
    public bool MatchesIdentity(AbsolutePath path) =>
        DirectoryIdentity.Of(path) == Identity;

    // macOS fcntl is variadic; the arm64 Darwin ABI passes the first anonymous argument
    // of a variadic call ON THE STACK while named arguments take x0-x1 — so the P/Invoke
    // pads x2-x7 (six longs) to push buf into the stack slot the callee reads. Same
    // trick ships in production (dotnet/runtime#48752). fd and cmd are the named pair.
    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static partial int FcntlGetPath(int fd, int cmd, long p2, long p3, long p4,
        long p5, long p6, long p7, Span<byte> buf);

    public void Dispose()
    {
        var fd = Interlocked.Exchange(ref _fd, -1);
        if (fd >= 0)
        {
            Close(fd);
        }
    }

    private static DirectoryAnchor? Kill(int fd)
    {
        Close(fd);
        return null;
    }

    // O_RDONLY = 0: a directory opens read-only on Linux and macOS; O_DIRECTORY/CLOEXEC
    // differ per platform and add nothing for a transient witness
    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial void Close(int fd);

    [LibraryImport("libc", EntryPoint = "readlink", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial long ReadLink(string path, Span<byte> buf, ulong size);
}
