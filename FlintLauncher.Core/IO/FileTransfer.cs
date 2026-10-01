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
    /// existing destination returns SkippedDestinationExists (idempotent). Returns false
    /// when the transfer failed (the source is left intact).</summary>
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
                    if (!TryClone(src, dst))
                    {
                        return false;
                    }
                    method = TransferMethod.Reflink;
                    // the content is in place; failing to delete the source is not a failure
                    TryDeleteFile(src);
                    return true;
                case ImportMode.CopyOnWrite:
                    if (!TryClone(src, dst))
                    {
                        return false;
                    }
                    method = TransferMethod.Reflink;
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
    /// walk files item by item.</summary>
    public static bool TryMoveDirectory(string src, string dst)
    {
        try
        {
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

    /// <summary>CoW clone into a not-yet-existing destination; false when the platform or
    /// volume does not support reflinks (caller falls back to copy). An existing destination
    /// counts as success (idempotent, like FS::copy).</summary>
    public static bool TryClone(string src, string dst)
    {
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
        using var dstHandle = File.Open(dst, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var srcFd = (int)srcHandle.SafeFileHandle.DangerousGetHandle();
        var dstFd = (int)dstHandle.SafeFileHandle.DangerousGetHandle();
        if (ioctl(dstFd, FicloneIoctl, srcFd) == 0)
        {
            return true;
        }
        dstHandle.Dispose(); // release before removing the half-written clone
        try
        {
            File.Delete(dst);
        }
        catch (IOException)
        {
        }
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
        return true;
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
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
