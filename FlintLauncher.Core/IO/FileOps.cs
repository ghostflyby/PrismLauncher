// SPDX-License-Identifier: GPL-3.0-only
// The fault-tolerant file operations of launcher/FileSystem.{h,cpp} (FS::copy, FS::move,
// FS::deletePath, FS::ensure*PathExists) and the atomic save of launcher/PSaveFile.h — the
// parts of the C++ FS namespace that have no BCL counterpart. Path arithmetic is TruePath's
// (LocalPath); plain reads and existence checks go straight to System.IO at the call sites.
//
// Error contract, inherited from FS::: the bool-returning members swallow I/O failures and
// report them as false so a caller surfaces one failure message instead of unwinding; Write is
// the exception and throws IOException like the BCL writers it wraps. Collections handed to
// callers for display (failedPaths, the extracted-file lists) stay plain strings.

using TruePath;

namespace FlintLauncher.Core.IO;

public static class FileOps
{
    // --------------------------------------------------------------------- write

    /// <summary>
    /// Atomic save per QSaveFile/PSaveFile: the data is written to a temporary file in the
    /// same directory (created on demand) and then renamed over the target, so a crash never
    /// leaves a partially written target behind.
    /// </summary>
    /// <exception cref="IOException">The folder could not be created or the data could not be written.</exception>
    public static void Write(LocalPath file, ReadOnlySpan<byte> data)
    {
        if (file.Parent is { Value.Length: > 0 } dir && !EnsureFolderPathExists(dir))
        {
            throw new IOException($"Unable to create folder {dir.FileName} ({dir.Value})");
        }

        var temp = file.Value + "." + Guid.NewGuid().ToString("N")[..6];
        try
        {
            File.WriteAllBytes(temp, data);
            File.Move(temp, file.Value, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            TryDelete(temp);
            throw new IOException($"Error writing data to {file.Value}: {e.Message}", e);
        }
    }

    /// <summary>
    /// Port of FS::updateTimestamp: bump the last access and modification times of an
    /// existing file to now (utime(path, nullptr) equivalent).
    /// </summary>
    public static bool UpdateTimestamp(LocalPath file)
    {
        try
        {
            var now = DateTime.Now;
            File.SetLastAccessTime(file.Value, now);
            File.SetLastWriteTime(file.Value, now);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------- folders

    /// <summary>
    /// Port of FS::ensureFilePathExists: creates every folder leading up to the file; the
    /// last segment is the file name and is not created.
    /// </summary>
    public static bool EnsureFilePathExists(LocalPath file)
    {
        // an empty parent is the current directory (a bare file name): nothing to create
        return file.Parent is not { Value.Length: > 0 } dir || EnsureFolderPathExists(dir);
    }

    /// <summary>Port of FS::ensureFolderPathExists: creates the folder and all of its parents.</summary>
    public static bool EnsureFolderPathExists(LocalPath folder)
    {
        // no Directory.Exists pre-check: CreateDirectory is idempotent and thread-safe
        try
        {
            Directory.CreateDirectory(folder.Value);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException)
        {
            return false;
        }
    }

    // ----------------------------------------------------------------- copy/move

    /// <summary>
    /// Port of FS::copy (the builder class flattened into one method). Recursively copies
    /// files (including hidden ones) from <paramref name="src"/> to <paramref name="dst"/>; if
    /// src is a file it is copied onto the full dst path. The matcher receives the relative
    /// path with '/' separators; a match is skipped in blacklist mode (whitelist=false) or kept
    /// in whitelist mode. Mirrors the C++ default of not overwriting existing destination files.
    /// </summary>
    public static bool Copy(LocalPath src, LocalPath dst, Func<string, bool>? matcher = null, bool whitelist = false,
        bool overwrite = false, bool dryRun = false, List<string>? failedPaths = null)
    {
        var failed = false;

        void CopyFile(string srcPath, string relativeDstPath)
        {
            if (matcher != null && matcher(relativeDstPath) != whitelist)
            {
                return;
            }

            if (dryRun)
            {
                return;
            }

            var dstPath = dst / relativeDstPath;
            EnsureFilePathExists(dstPath);
            try
            {
                File.Copy(srcPath, dstPath.Value, overwrite);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                          or NotSupportedException)
            {
                failed = true;
                failedPaths?.Add(dstPath.Value);
            }
        }

        if (Directory.Exists(src.Value))
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                // QDirIterator::Files | Hidden — include hidden files/directories
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            foreach (var file in Directory.EnumerateFiles(src.Value, "*", options))
            {
                CopyFile(file, Path.GetRelativePath(src.Value, file).Replace('\\', '/'));
            }
        }
        else if (File.Exists(src.Value))
        {
            CopyFile(src.Value, "");
        }
        else
        {
            return false;
        }

        return !failed;
    }

    /// <summary>Port of FS::move: rename, falling back to copy + delete (e.g. across devices).</summary>
    public static bool Move(LocalPath source, LocalPath dest)
    {
        EnsureFilePathExists(dest);
        try
        {
            if (Directory.Exists(source.Value) && !File.Exists(source.Value))
            {
                Directory.Move(source.Value, dest.Value);
            }
            else
            {
                File.Move(source.Value, dest.Value, overwrite: false);
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException)
        {
            // moveByCopy: copy then remove the original
            return Copy(source, dest, overwrite: true) && DeletePath(source);
        }
    }

    /// <summary>Port of FS::deletePath: delete a file or a folder recursively; a missing path is a success.</summary>
    public static bool DeletePath(LocalPath path)
    {
        try
        {
            if (Directory.Exists(path.Value) && !File.Exists(path.Value))
            {
                Directory.Delete(path.Value, recursive: true);
            }
            else if (File.Exists(path.Value))
            {
                File.Delete(path.Value);
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Port of FS::removeFiles: remove each file in the list; a missing file counts as a
    /// failure (QFile::remove semantics). Takes the string lists the archive extractors keep.
    /// </summary>
    public static bool RemoveFiles(IEnumerable<string> files)
    {
        var ret = true;
        foreach (var file in files)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
                else
                {
                    ret = false;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                ret = false;
            }
        }

        return ret;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // best effort cleanup of the temporary file
        }
    }
}
