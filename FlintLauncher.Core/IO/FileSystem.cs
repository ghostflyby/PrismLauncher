// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/FileSystem.{h,cpp} (platform-neutral subset) and the atomic-write
// semantics of launcher/PSaveFile.h (QSaveFile: write a temp file next to the target,
// then rename over it).
//
// Path handling notes:
//  - QDir::cleanPath is reimplemented as CleanPath (always uses '/' separators so the
//    ported C++ tests' expectations hold on every platform; Qt uses the native
//    separator for toNativeSeparators-based helpers such as pathDepth/pathTruncate).
//  - QFileInfo::path() is reimplemented as FileInfoPath.
using System.Globalization;
using System.Text;

namespace FlintLauncher.Core.IO;

/// <summary>Port of FS::FileSystemException.</summary>
public sealed class FileSystemException(string message) : Exception(message)
{
}

public static class FileSystem
{
    // --------------------------------------------------------------- read/write

    /// <summary>
    /// Port of FS::ensureExists: creates the folder or throws FileSystemException.
    /// </summary>
    private static void EnsureExists(string dir)
    {
        var full = Path.GetFullPath(dir);
        if (!EnsureFolderPathExists(full))
        {
            throw new FileSystemException("Unable to create folder " + Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar)) +
                                          " (" + full + ")");
        }
    }

    /// <summary>
    /// Port of FS::write: atomic save per QSaveFile/PSaveFile — the data is written to a
    /// temporary file in the same directory and then renamed over the target, so a crash
    /// never leaves a partially written target behind.
    /// </summary>
    public static void Write(string filename, byte[] data)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(filename));
        if (!string.IsNullOrEmpty(dir))
        {
            EnsureExists(dir);
        }
        var temp = filename + "." + TempSuffix();
        try
        {
            File.WriteAllBytes(temp, data);
            File.Move(temp, filename, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            TryDelete(temp);
            throw new FileSystemException("Error writing data to " + filename + ": " + e.Message);
        }
    }

    /// <summary>Port of FS::appendSafe: append by rewriting the file atomically.</summary>
    public static void AppendSafe(string filename, byte[] data)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(filename));
        if (!string.IsNullOrEmpty(dir))
        {
            EnsureExists(dir);
        }
        byte[] buffer;
        try
        {
            buffer = Read(filename);
        }
        catch (FileSystemException)
        {
            buffer = [];
        }
        var merged = new byte[buffer.Length + data.Length];
        buffer.CopyTo(merged, 0);
        data.CopyTo(merged, buffer.Length);
        Write(filename, merged);
    }

    /// <summary>Port of FS::append: plain append (not atomic).</summary>
    public static void Append(string filename, byte[] data)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(filename));
        if (!string.IsNullOrEmpty(dir))
        {
            EnsureExists(dir);
        }
        try
        {
            using var stream = new FileStream(filename, FileMode.Append, FileAccess.Write);
            stream.Write(data, 0, data.Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new FileSystemException("Couldn't open " + filename + " for writing: " + e.Message);
        }
    }

    /// <summary>Port of FS::read. Throws FileSystemException when the file can't be read.</summary>
    public static byte[] Read(string filename)
    {
        try
        {
            return File.ReadAllBytes(filename);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new FileSystemException("Unable to open " + filename + " for reading: " + e.Message);
        }
    }

    /// <summary>
    /// Port of FS::updateTimestamp: bump the last access and modification times of an
    /// existing file to now (utime(path, nullptr) equivalent). Returns false on failure.
    /// </summary>
    public static bool UpdateTimestamp(string filename)
    {
        try
        {
            var now = DateTime.Now;
            File.SetLastAccessTime(filename, now);
            File.SetLastWriteTime(filename, now);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                 or NotSupportedException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ folders

    /// <summary>
    /// Port of FS::ensureFilePathExists: creates all folders in the path; the last segment
    /// is treated as a file name and is ignored.
    /// </summary>
    public static bool EnsureFilePathExists(string filenamepath)
    {
        try
        {
            Directory.CreateDirectory(FileInfoPath(filenamepath));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                 or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Port of FS::ensureFolderPathExists: creates all folders in the path; the last
    /// segment is treated as a folder name and is created.
    /// </summary>
    public static bool EnsureFolderPathExists(string folderPathName)
    {
        if (Directory.Exists(folderPathName))
        {
            return true;
        }
        try
        {
            Directory.CreateDirectory(folderPathName);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                 or NotSupportedException)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- copy/move

    /// <summary>
    /// Port of FS::copy (the builder class flattened into one method). Recursively copies
    /// files (including hidden ones) from <paramref name="src"/> to <paramref name="dst"/>.
    /// If src is a file it is copied onto the full dst path. The matcher receives the
    /// relative path (with '/' separators); a match is skipped in blacklist mode
    /// (whitelist=false) or kept in whitelist mode (whitelist=true). Mirrors the C++
    /// default of not overwriting existing destination files.
    /// </summary>
    public static bool Copy(string src, string dst, Func<string, bool>? matcher = null, bool whitelist = false,
                            bool overwrite = false, bool dryRun = false, List<string>? failedPaths = null)
    {
        var failed = false;
        void CopyFile(string srcPath, string relativeDstPath)
        {
            if (matcher != null && matcher(relativeDstPath) != whitelist)
            {
                return;
            }
            var dstPath = PathCombine(dst, relativeDstPath);
            if (!dryRun)
            {
                EnsureFilePathExists(dstPath);
                try
                {
                    File.Copy(srcPath, dstPath, overwrite);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                         or NotSupportedException)
                {
                    failed = true;
                    failedPaths?.Add(dstPath);
                    return;
                }
            }
        }

        if (Directory.Exists(src))
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                // QDirIterator::Files | Hidden — include hidden files/directories
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            foreach (var file in Directory.EnumerateFiles(src, "*", options))
            {
                var relative = Path.GetRelativePath(src, file).Replace('\\', '/');
                CopyFile(file, relative);
            }
        }
        else if (File.Exists(src))
        {
            // If the root src is not a directory, copy it onto the dst path directly.
            CopyFile(src, "");
        }
        else
        {
            return false;
        }
        return !failed;
    }

    /// <summary>
    /// Port of FS::move: rename, falling back to copy + delete (e.g. across devices).
    /// </summary>
    public static bool Move(string source, string dest)
    {
        EnsureFilePathExists(dest);
        try
        {
            if (Directory.Exists(source) && !File.Exists(source))
            {
                Directory.Move(source, dest);
            }
            else
            {
                File.Move(source, dest, overwrite: false);
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                 or NotSupportedException)
        {
            // moveByCopy: copy then remove the original
            if (Copy(source, dest, overwrite: true) && DeletePath(source))
            {
                return true;
            }
            return false;
        }
    }

    /// <summary>Port of FS::deletePath: delete a file or a folder recursively.</summary>
    public static bool DeletePath(string path)
    {
        try
        {
            if (Directory.Exists(path) && !File.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
            return true; // fs::remove_all on a missing path is a success
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                 or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Port of FS::deleteContents: empty a folder but keep the folder itself.</summary>
    public static bool DeleteContents(string path)
    {
        if (!Directory.Exists(path))
        {
            if (File.Exists(path))
            {
                return false; // attempted to delete contents of a non-directory
            }
            return true;
        }
        var ret = true;
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                if (!DeletePath(entry))
                {
                    ret = false;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        return ret;
    }

    /// <summary>Port of FS::removeFiles: remove each file in the list.</summary>
    public static bool RemoveFiles(IReadOnlyList<string> listFile)
    {
        var ret = true;
        foreach (var file in listFile)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
                else
                {
                    ret = false; // QFile::remove fails for missing files
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                ret = false;
            }
        }
        return ret;
    }

    // -------------------------------------------------------------- path combine

    /// <summary>Port of FS::PathCombine (two arguments).</summary>
    public static string PathCombine(string path1, string path2)
    {
        if (path1.Length == 0)
        {
            return path2;
        }
        if (path2.Length == 0)
        {
            return path1;
        }
        return CleanPath(path1 + '/' + path2);
    }

    /// <summary>Port of FS::PathCombine (three arguments).</summary>
    public static string PathCombine(string path1, string path2, string path3)
    {
        return PathCombine(PathCombine(path1, path2), path3);
    }

    /// <summary>Port of FS::PathCombine (four arguments).</summary>
    public static string PathCombine(string path1, string path2, string path3, string path4)
    {
        return PathCombine(PathCombine(path1, path2, path3), path4);
    }

    /// <summary>Port of FS::AbsolutePath: the directory portion of the path, made absolute.</summary>
    public static string AbsolutePath(string path)
    {
        var dir = FileInfoPath(path);
        return Path.GetFullPath(dir);
    }

    /// <summary>
    /// Port of FS::pathDepth: "foo.txt" -> 0, "bar/foo.txt" -> 1, "/baz/bar/foo.txt" -> 2.
    /// </summary>
    public static int PathDepth(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return 0;
        }
        var parts = SplitSegments(FileInfoPath(path));
        var numParts = parts.Count;
        numParts -= parts.Count(p => p == ".");
        numParts -= 2 * parts.Count(p => p == "..");
        return numParts;
    }

    /// <summary>Port of FS::pathTruncate: cut off path segments until the depth limit.</summary>
    public static string PathTruncate(string path, int depth)
    {
        if (string.IsNullOrEmpty(path) || depth < 0)
        {
            return "";
        }
        var trunc = FileInfoPath(path);
        if (PathDepth(trunc) > depth)
        {
            return PathTruncate(trunc, depth);
        }
        var parts = SplitSegments(trunc);
        if (parts.Count > 0 && parts[0] == "." && !path.StartsWith('.'))
        {
            parts.RemoveAt(0);
        }
        if (path.StartsWith('/'))
        {
            parts.Insert(0, "");
        }
        return string.Join('/', parts);
    }

    /// <summary>
    /// Port of FS::NormalizePath: paths inside the current directory become relative to it,
    /// everything else becomes absolute.
    /// </summary>
    public static string NormalizePath(string path)
    {
        var currentAbsolute = CleanPath(Directory.GetCurrentDirectory());
        var newAbsolute = QDirAbsolutePath(path);
        if (newAbsolute.StartsWith(currentAbsolute, StringComparison.Ordinal))
        {
            return RelativeFilePath(currentAbsolute, newAbsolute);
        }
        return newAbsolute;
    }

    // ------------------------------------------------------------ name handling

    private const string BadChars = "<>:\"|?*\r\n!";

    /// <summary>QChar::isPrint equivalent (format/surrogate/private-use/unassigned are not printable; space is).</summary>
    private static bool IsPrint(char c)
    {
        var category = char.GetUnicodeCategory(c);
        return category is not (UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate
            or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned);
    }

    private static string RemoveChars(string source, char replace, string extraChars = "")
    {
        var badChars = BadChars + extraChars;
        var sb = new StringBuilder(source.Length);
        foreach (var c in source)
        {
            if (c < 0x20 || !IsPrint(c) || badChars.Contains(c))
            {
                sb.Append(replace);
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>Port of FS::RemoveInvalidFilenameChars.</summary>
    public static string RemoveInvalidFilenameChars(string str, char replaceWith = '-')
    {
        return RemoveChars(str, replaceWith, "\\/");
    }

    /// <summary>Port of FS::RemoveInvalidPathChars.</summary>
    public static string RemoveInvalidPathChars(string str, char replaceWith = '-')
    {
        return RemoveChars(str, replaceWith);
    }

    /// <summary>
    /// Port of FS::DirNameFromString: derive a non-clashing directory name inside inDir.
    /// </summary>
    public static string DirNameFromString(string str, string inDir = ".")
    {
        return DirNameFromString(str, [inDir]);
    }

    /// <summary>
    /// Port of FS::DirNameFromString(string, QStringList inDirs): derive a directory name
    /// that clashes with none of the given dirs (#6110 dedupes instance folder names across
    /// every configured instances directory).
    /// </summary>
    public static string DirNameFromString(string str, IReadOnlyList<string> inDirs)
    {
        var num = 0;
        var baseName = RemoveInvalidFilenameChars(str, '-');
        string dirName;
        do
        {
            if (num == 0)
            {
                dirName = baseName;
            }
            else
            {
                dirName = baseName + "(" + num + ")";
            }
            // If it's over 9000
            if (num > 9000)
            {
                return "";
            }
            num++;
        } while (inDirs.Any(dir => Directory.Exists(PathCombine(dir, dirName)) || File.Exists(PathCombine(dir, dirName))));
        return dirName;
    }

    /// <summary>
    /// Port of FS::checkProblemticPathJava: does the path contain '!'? (breaks Java).
    /// </summary>
    public static bool CheckProblematicPathJava(string folderPath)
    {
        return folderPath.Contains('!');
    }

    /// <summary>Port of FS::getUniqueResourceName: find a non-clashing name next to a disabled mod.</summary>
    public static string GetUniqueResourceName(string filePath)
    {
        var newFileName = filePath;
        if (!newFileName.EndsWith(".disabled"))
        {
            return newFileName; // prioritize enabled mods
        }
        newFileName = newFileName[..^9];
        if (!File.Exists(newFileName) && !Directory.Exists(newFileName))
        {
            return filePath;
        }
        var fileInfo = new FileInfo(filePath);
        var baseName = Path.GetFileNameWithoutExtension(filePath);
        var path = fileInfo.DirectoryName ?? "";

        var counter = 1;
        string candidate;
        do
        {
            candidate = counter == 1
                ? PathCombine(path, baseName + ".duplicate")
                : PathCombine(path, baseName + ".duplicate" + counter);
            counter++;
        } while (File.Exists(candidate) || Directory.Exists(candidate));
        return candidate;
    }

    /// <summary>Port of FS::nearestExistentAncestor.</summary>
    public static string NearestExistentAncestor(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            return path;
        }
        var dir = Path.GetFullPath(path);
        if (IsRoot(dir))
        {
            return Directory.Exists(dir) ? dir : "";
        }
        do
        {
            var parent = CleanPath(dir + "/..");
            if (parent == dir)
            {
                break; // reached the root
            }
            dir = parent;
        } while (!Directory.Exists(dir) && !File.Exists(dir) && !IsRoot(dir));
        return Directory.Exists(dir) || File.Exists(dir) ? dir : "";
    }

    // ------------------------------------------------------------- Qt path shims

    /// <summary>QFileInfo::path(): the directory portion; "" for empty, "." with no separator.</summary>
    private static string FileInfoPath(string path)
    {
        if (path.Length == 0)
        {
            return "";
        }
        var trimmed = path.TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        if (idx < 0)
        {
            return ".";
        }
        if (idx == 0)
        {
            return "/";
        }
        return trimmed[..idx];
    }

    /// <summary>
    /// QDir::cleanPath equivalent: collapses ".", ".." and duplicate/trailing separators.
    /// Always emits '/' separators; unresolvable ".." are kept in relative paths and
    /// dropped at the root of absolute ones, like Qt.
    /// </summary>
    private static string CleanPath(string path)
    {
        if (path.Length == 0)
        {
            return path;
        }

        var isAbsolute = path[0] == '/';
        // Windows drive prefix, e.g. "C:/foo" -> keep "C:" anchored like Qt does.
        var drivePrefix = path.Length >= 2 && path[1] == ':' ? path[..2] : null;
        var remainder = drivePrefix != null ? path[2..] : path;
        var isDriveAbsolute = drivePrefix != null && remainder.Length > 0 && (remainder[0] == '/' || remainder[0] == '\\');

        var stack = new List<string>();
        foreach (var rawPart in remainder.Split('/', '\\'))
        {
            var part = rawPart;
            if (part.Length == 0 || part == ".")
            {
                continue;
            }
            if (part == "..")
            {
                if (stack.Count > 0 && stack[^1] != "..")
                {
                    stack.RemoveAt(stack.Count - 1);
                }
                else if (!isAbsolute && !isDriveAbsolute)
                {
                    stack.Add("..");
                }
                // at the root of an absolute path ".." collapses away
                continue;
            }
            stack.Add(part);
        }

        var joined = string.Join('/', stack);
        if (isAbsolute)
        {
            return "/" + joined;
        }
        if (drivePrefix != null)
        {
            return isDriveAbsolute ? drivePrefix + "/" + joined : drivePrefix + joined;
        }
        return joined.Length == 0 ? "." : joined;
    }

    /// <summary>QDir(path).absolutePath(): relative paths anchor at the current directory.</summary>
    private static string QDirAbsolutePath(string path)
    {
        if (path.Length == 0)
        {
            return CleanPath(Directory.GetCurrentDirectory());
        }
        if (path[0] == '/' || (path.Length >= 2 && path[1] == ':'))
        {
            return CleanPath(path);
        }
        return CleanPath(Directory.GetCurrentDirectory() + "/" + path);
    }

    /// <summary>QDir::relativeFilePath equivalent (slash separators).</summary>
    private static string RelativeFilePath(string fromDir, string toPath)
    {
        var fromParts = SplitSegments(fromDir);
        var toParts = SplitSegments(toPath);
        var common = 0;
        while (common < fromParts.Count && common < toParts.Count && fromParts[common] == toParts[common])
        {
            common++;
        }
        var result = new List<string>();
        for (var i = common; i < fromParts.Count; i++)
        {
            result.Add("..");
        }
        for (var i = common; i < toParts.Count; i++)
        {
            result.Add(toParts[i]);
        }
        return result.Count == 0 ? "." : string.Join('/', result);
    }

    private static List<string> SplitSegments(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Split('/', '\\'))
        {
            if (part.Length != 0)
            {
                parts.Add(part);
            }
        }
        return parts;
    }

    private static bool IsRoot(string path)
    {
        return path.Length == 0 || path == "/" || (path.Length == 3 && path[1] == ':' && (path[2] == '/' || path[2] == '\\'));
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

    private static string TempSuffix()
    {
        // QSaveFile appends a ".XXXXXX" style unique suffix in the same directory.
        return Guid.NewGuid().ToString("N")[..6];
    }

    // ------------------------------------------------------------- not ported
    //
    // The following FS:: members are intentionally left out of this port:
    //  - copy/clone/create_link builder classes with Qt signals (copy is ported as the
    //    static FileSystem.Copy above; links/clones need OS-specific APIs)
    //  - create_link, ExternalLinkFileProcess, filelink (symlinks/hard links, local
    //    socket server, privilege elevation)
    //  - clone_file, clone, canClone*, win_ioctl_clone, linux_ficlone,
    //    macos_bsd_clonefile (reflink/clone syscalls and ioctls)
    //  - FilesystemType, statFS, getFilesystemType*, canLinkOnFS/canLink, hardLinkCount,
    //    nearestExistentAncestor's QStorageInfo consumers (QStorageInfo based; note
    //    nearestExistentAncestor itself is ported above as it is plain path walking)
    //  - trash (QFile::moveToTrash / freedesktop portal, desktop integration)
    //  - getDesktopDir, getApplicationsDir (QStandardPaths)
    //  - createShortcut, quoteArgs (GUI/desktop shell integration)
    //  - ResolveExecutable (QStandardPaths::findExecutable)
    //  - overrideFolder (only used together with the skipped desktop/migration flows;
    //    trivially expressible via Copy(..., overwrite: true) if needed)
    //  - getPathNameInLocal8bit, shortPathName, fitsInLocal8bit (Windows 8.3 names)
    //  - getCutNumbers: does not exist in this revision of PrismLauncher's FileSystem
    //    (nothing to port)
}
