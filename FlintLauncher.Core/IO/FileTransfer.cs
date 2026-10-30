// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// File transfer primitives for the Prism→Flint data import, with a "destination exists →
// skip" idempotency rule that matches FS::copy (the migration can be re-run and simply
// resumes).
//
// CoW is the runtime's: File.Copy clones extents where the platform supports it (APFS on
// macOS, ReFS on Windows) and copies bytes elsewhere, so there is no reflink call to make and
// no "did the volume support it" question to answer. Same-volume moves stay renames.

using System.Runtime.InteropServices;
using TruePath;

namespace FlintLauncher.Core.IO;

/// <summary>How the import moves bytes for one item.</summary>
public enum ImportMode
{
    /// <summary>Copy (CoW where the runtime's copy can clone extents), source kept. The
    /// default.</summary>
    CopyOnWrite,

    /// <summary>Same-volume rename when possible, otherwise copy + source delete after
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
    Copy,
}

public static partial class FileTransfer
{
    /// <summary>Transfers one file per mode. The destination is never overwritten; an
    /// existing destination returns SkippedDestinationExists (idempotent). Move degrades
    /// rename → copy (+ source delete on success); both modes return false only when every
    /// mechanism failed (the source is left intact).</summary>
    public static bool TransferFile(string src, string dst, ImportMode mode, out TransferMethod method)
    {
        method = TransferMethod.SkippedDestinationExists;
        if (File.Exists(dst))
        {
            return true;
        }

        try
        {
            if (mode == ImportMode.Move && TryRename(src, dst))
            {
                method = TransferMethod.SameVolumeRename;
                return true;
            }

            CopyInto(src, dst);
            method = TransferMethod.Copy;

            if (mode == ImportMode.Move)
            {
                // the content is in place; failing to delete the source is not a failure
                TryDeleteFile(src);
            }

            return true;
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
    internal static IEnumerable<string> EnumerateFilesDeep(AbsolutePath dir)
    {
        var pending = new Stack<string>();
        pending.Push(dir.Value);
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

    // ------------------------------------------------------------------------ unix stat
    //
    // The instance addressing chain needs the instance directory's inode: stable
    // across renames, zero-write, resolvable even when the instance cfg is broken. Same
    // LibraryImport convention as the stat below (UTF-8 string marshalling, the stat
    // buffer pinned in place). Windows has no libc binding and returns 0 — the
    // addressing chain skips the inode link there (NTFS FileId can come later).
    //
    // The native side writes the WHOLE struct stat, so the managed buffer must be at least
    // as large as the largest ABI in play (144 bytes on x86-64 Linux, 136 on macOS, 128 on
    // arm64 Linux); 256 covers them all. Only st_ino is read, at offset 8 — the offset it
    // holds on every LP64 ABI this port ships for (macOS, glibc/musl x86-64 + arm64).

    /// <summary>Native size the stat buffer must cover (see the block comment above).</summary>
    private const int StatBufferSize = 256;

    /// <summary>The unix inode of <paramref name="path"/>; 0 when unavailable (Windows, a
    /// missing path, or a libc without a plain "stat" export — older glibc routed stat(2)
    /// through __xstat). A 0 result simply skips the inode link of the chain.</summary>
    public static long StatInode(AbsolutePath path)
    {
        if (OperatingSystem.IsWindows() || (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()))
        {
            return 0;
        }

        try
        {
            var buf = new byte[StatBufferSize];
            return Stat(path.Value, buf) == 0 ? BitConverter.ToInt64(buf, 8) : 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return 0; // no usable stat export — the chain runs on uuid/dirname only
        }
    }

    [LibraryImport("libc", EntryPoint = "stat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Stat(string path, Span<byte> buf);

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

    private static void CopyInto(string src, string dst)
    {
        EnsureDirectoryFor(dst);
        File.Copy(src, dst, overwrite: false);
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
