// SPDX-License-Identifier: GPL-3.0-only
// File transfer primitives for the Prism→Flint data import: CoW reflink (APFS clonefile /
// Linux FICLONE), move and copy, with a "destination exists → skip" idempotency rule that
// matches FS::copy (the migration can be re-run and simply resumes).
//
// Platform notes:
//  - macOS: clonefile(2) (APFS, same volume) clones a whole file via P/Invoke.
//  - Linux: FICLONE ioctl (Btrfs/XFS/ZFS 2.2+/NFS 4.2, same volume).
//  - Windows: no reflink attempt (ReFS block cloning needs an ioctl of its own) — CoW
//    degrades to copy.
//  - Move: same-volume rename is O(1); cross-volume degrades to clone/copy + source delete
//    after success.
//
// P/Invoke notes: path arguments travel as NUL-terminated UTF-8 byte arrays — blittable, so
// no marshaler engine, no unsafe, and the declarations stay AOT-clean (LibraryImport would
// force AllowUnsafeBlocks project-wide).

using System.Runtime.InteropServices;
using System.Security;
using System.Text;

namespace FlintLauncher.Core.IO;

/// <summary>How the import moves bytes for one item.</summary>
public enum ImportMode
{
    /// <summary>Reflink/CoW where the filesystem supports it, plain copy otherwise. Source
    /// is kept intact. The default.</summary>
    CopyOnWrite,

    /// <summary>Same-volume rename when possible, otherwise clone/copy + source delete after
    /// success.</summary>
    Move,

    /// <summary>Plain byte copy, source kept.</summary>
    Copy,
}

/// <summary>How one file was transferred (plan/diagnostics output).</summary>
public enum TransferMethod
{
    /// <summary>Destination already had the content — nothing done (idempotent resume).</summary>
    SkippedDestinationExists,
    SameVolumeRename,
    Reflink,
    Copy,
}

public static class FileTransfer
{
    /// <summary>Transfers one file per mode. The destination is never overwritten; an
    /// existing destination returns SkippedDestinationExists (idempotent). Move degrades
    /// rename → reflink → plain copy (+ source delete on success), CoW degrades
    /// reflink → plain copy — both return false only when every mechanism failed (the
    /// source is left intact).</summary>
    public static bool TransferFile(string src, string dst, ImportMode mode, out TransferMethod method)
    {
        method = TransferMethod.SkippedDestinationExists;
        if (File.Exists(dst))
        {
            return true;
        }

        try
        {
            switch (mode)
            {
                case ImportMode.Move:
                    if (TryRename(src, dst))
                    {
                        method = TransferMethod.SameVolumeRename;
                        return true;
                    }

                    if (TryClone(src, dst))
                    {
                        method = TransferMethod.Reflink;
                    }
                    else if (!CopyInto(src, dst))
                    {
                        // cross-volume with neither rename nor reflink: the plain-copy
                        // degradation the type documents ("clone/copy + source delete")
                        // — without it a cross-volume Move is an unconditional false and
                        // every per-file walk fails forever
                        return false;
                    }
                    else
                    {
                        method = TransferMethod.Copy;
                    }

                    // the content is in place; failing to delete the source is not a failure
                    TryDeleteFile(src);
                    return true;
                case ImportMode.CopyOnWrite:
                    if (TryClone(src, dst))
                    {
                        method = TransferMethod.Reflink;
                        return true;
                    }

                    // reflink unavailable (ext4/Windows/cross-volume): degrade to a plain
                    // copy like the Move branch above — a hard false makes every per-file
                    // import fail wholesale on non-CoW volumes, with the whole task
                    // EmitFailed'ing file by file
                    if (!CopyInto(src, dst))
                    {
                        return false;
                    }

                    method = TransferMethod.Copy;
                    return true;
                default:
                    if (!CopyInto(src, dst))
                    {
                        return false;
                    }

                    method = TransferMethod.Copy;
                    return true;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Moves a whole directory when src and dst sit on the same volume (one rename,
    /// sub-second for any size); returns false when the volume differs and the caller must
    /// walk files item by item. The destination's parent is created first — a rename(2)
    /// cannot create it, and letting the missing parent surface as DirectoryNotFoundException
    /// would silently degrade the wholesale move into a per-file walk that leaves the source
    /// behind.</summary>
    public static bool TryMoveDirectory(string src, string dst)
    {
        try
        {
            EnsureDirectoryFor(dst);
            Directory.Move(src, dst);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _ = e; // cross-volume rename surfaces as IOException
            return false;
        }
    }

    /// <summary>Same-volume probe for a directory pair (used to plan rename vs per-file).</summary>
    public static bool SameVolume(string a, string b)
    {
        var rootA = Path.GetPathRoot(Path.GetFullPath(a))?.TrimEnd('/', '\\');
        var rootB = Path.GetPathRoot(Path.GetFullPath(b))?.TrimEnd('/', '\\');
        return string.Equals(rootA, rootB,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>Every file below <paramref name="dir"/>, hidden ones included — the one
    /// enumeration the migration walks plan and transfer through, so the counts and the
    /// walk always agree. FILE reparse points (symlinks to files) ARE yielded: the C++
    /// FS::copy default follows file symlinks and copies the target's content
    /// (FileSystem.h:159 <c>m_followSymlinks = true</c>, ResolveSymlinks enumeration,
    /// FileSystem.cpp:461). DIRECTORY reparse points are NOT descended into — a plain
    /// <c>AttributesToSkip = ReparsePoint</c> enumeration would skip the file links too
    /// (silently pruning them from the import), and <c>AttributesToSkip = 0</c> would
    /// descend directory links and loop on link cycles, so the recursion is driven by
    /// hand.</summary>
    internal static IEnumerable<string> EnumerateFilesDeep(string dir)
    {
        var pending = new Stack<string>();
        pending.Push(dir);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            string[] entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(current, "*", new EnumerationOptions
                {
                    AttributesToSkip = FileAttributes.None,
                    IgnoreInaccessible = true,
                }).ToArray();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue; // unreadable directory — the IgnoreInaccessible stance, per directory
            }

            foreach (var entry in entries)
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    continue; // vanished between enumeration and stat — not transferable
                }

                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    if (!attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        pending.Push(entry); // directory reparse point: never descended (cycles)
                    }
                }
                else
                {
                    yield return entry; // includes file reparse points (copied by content)
                }
            }
        }
    }

    /// <summary>Test seam: replaces the platform reflink attempt so the copy fallbacks run
    /// on every OS (null = the real platform attempt — clonefile / FICLONE).</summary>
    internal static Func<string, string, bool>? CloneAttemptHook { get; set; }

    /// <summary>CoW clone into a not-yet-existing destination; false when the platform or
    /// volume does not support reflinks (the transfer modes fall back to a plain copy). An
    /// existing destination counts as success (idempotent, like FS::copy).</summary>
    public static bool TryClone(string src, string dst)
    {
        var hook = CloneAttemptHook;
        if (hook is not null)
        {
            return hook(src, dst);
        }

        EnsureDirectoryFor(dst);
        if (OperatingSystem.IsMacOS())
        {
            return CloneFileMacOs(src, dst);
        }

        if (OperatingSystem.IsLinux())
        {
            return CloneLinux(src, dst);
        }

        return false;
    }

    // ------------------------------------------------------------------ unix stat (D2)
    //
    // The instance addressing chain (DATA D2) needs the instance directory's inode: stable
    // across renames, zero-write, resolvable even when the instance cfg is broken. Same
    // blittable convention as clonefile below (NUL-terminated UTF-8 byte[] args, plain
    // DllImport, no marshaler, no unsafe). Windows has no libc binding and returns 0 — the
    // addressing chain skips the inode link there (NTFS FileId can come later, D2).
    //
    // The native side writes the WHOLE struct stat, so the managed buffer must be at least
    // as large as the largest ABI in play (144 bytes on x86-64 Linux, 136 on macOS, 128 on
    // arm64 Linux); 256 covers them all. Only st_ino is read, at offset 8 — the offset it
    // holds on every LP64 ABI this port ships for (macOS, glibc/musl x86-64 + arm64).

    /// <summary>Native size the stat buffer must cover (see the block comment above).</summary>
    private const int StatBufferSize = 256;

    /// <summary>The unix inode of <paramref name="path"/>; 0 when unavailable (Windows, a
    /// missing path, or a libc without a plain "stat" export — older glibc routed stat(2)
    /// through __xstat). A 0 result simply skips the inode link of the D2 chain.</summary>
    public static long StatInode(string path)
    {
        if (OperatingSystem.IsWindows() || (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()))
        {
            return 0;
        }

        try
        {
            var buf = new byte[StatBufferSize];
            return stat(NullTerminatedUtf8(path), buf) == 0 ? BitConverter.ToInt64(buf, 8) : 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return 0; // no usable stat export — the chain runs on uuid/dirname only
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int stat(byte[] path, byte[] buf);

    // ------------------------------------------------------------------ macOS clonefile
    //
    // byte[] path args (UTF-8 + NUL) are blittable: no marshaler engine, no unsafe, and the
    // P/Invoke stays AOT-clean (LibraryImport would force AllowUnsafeBlocks project-wide).

    [DllImport("libc", SetLastError = true)]
    private static extern int clonefile(byte[] src, byte[] dst, nint flags);

    private static byte[] NullTerminatedUtf8(string path) => Encoding.UTF8.GetBytes(path + "\0");

    private static bool CloneFileMacOs(string src, string dst)
    {
        // EEXIST (17): destination already there — idempotent success, like FS::copy
        return clonefile(NullTerminatedUtf8(src), NullTerminatedUtf8(dst), 0) == 0
               || Marshal.GetLastWin32Error() == 17;
    }

    // ------------------------------------------------------------------ Linux FICLONE

    private const ulong FicloneIoctl = 0x40049409;

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, ulong request, int srcFd);

    private static bool CloneLinux(string src, string dst)
    {
        using var srcHandle = File.Open(src, FileMode.Open, FileAccess.Read, FileShare.Read);
        bool cloned;
        // the destination handle must close before the half-written clone is unlinked
        using (var dstHandle = File.Open(dst, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var srcFd = (int)srcHandle.SafeFileHandle.DangerousGetHandle();
            var dstFd = (int)dstHandle.SafeFileHandle.DangerousGetHandle();
            cloned = ioctl(dstFd, FicloneIoctl, srcFd) == 0;
        }

        if (cloned)
        {
            // FICLONE clones extents only — the CreateNew destination keeps its
            // 0666&~umask mode, so a 0600 accounts.json would land world-readable (B5)
            CarryUnixFileMode(src, dst);
            return true;
        }

        try
        {
            File.Delete(dst);
        }
        catch (IOException) { }

        return false;
    }

    // ------------------------------------------------------------------ shared helpers

    private static bool TryRename(string src, string dst)
    {
        try
        {
            EnsureDirectoryFor(dst);
            File.Move(src, dst);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _ = e; // cross-device or destination appeared — caller falls back
            return false;
        }
    }

    private static bool CopyInto(string src, string dst)
    {
        EnsureDirectoryFor(dst);
        File.Copy(src, dst, overwrite: false);
        // File.Copy's mode preservation is not contractual across runtimes/platforms — the
        // migration copies credential-bearing files (accounts.json is 0600), so the source
        // bits are carried explicitly
        CarryUnixFileMode(src, dst);
        return true;
    }

    /// <summary>Carries the source's Unix permission bits onto a freshly created destination.
    /// Best effort: a filesystem without permission storage (FAT/exFAT) rejects the chmod —
    /// the C++ copy proceeds without modes there too, so the transfer still counts as done.
    /// Windows has no such bits.</summary>
    internal static void CarryUnixFileMode(string src, string dst)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(dst, File.GetUnixFileMode(src));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or SecurityException or NotSupportedException)
        {
        }
    }

    private static void EnsureDirectoryFor(string dst)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(dst));
        if (string.IsNullOrEmpty(dir))
        {
            return; // root-level destination, nothing to create
        }

        Directory.CreateDirectory(dir);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
