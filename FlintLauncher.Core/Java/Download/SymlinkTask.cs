// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/java/download/SymlinkTask.{h,cpp}: on macOS an extracted JDK layout is
// <target>/Contents/Home/bin/java, but the launcher expects <target>/bin/java. The task finds
// the Contents/Home folder and symlinks everything inside it into the target folder.
// FS::create_link becomes System.IO.File.CreateSymbolicLink (available since .NET 6, AOT-safe);
// existing destinations are skipped (the C++ std::filesystem call would throw on them, but the
// task re-runs only when the wanted bin path is missing anyway).

using FlintLauncher.Core.IO;
using FlintLauncher.Core.Tasks;

namespace FlintLauncher.Core.Java.Download;

public sealed class SymlinkTask : FlintTask
{
    private readonly string _path;

    public SymlinkTask(string finalPath)
    {
        _path = finalPath;
    }

    /// <summary>Port of the free function findBinPath: first &lt;root&gt;/&lt;pattern&gt;, then one level down.</summary>
    public static string FindBinPath(string root, string pattern)
    {
        var path = FileSystem.PathCombine(root, pattern);
        if (File.Exists(path) || Directory.Exists(path))
        {
            return path;
        }

        if (Directory.Exists(root))
        {
            foreach (var entry in Directory.GetDirectories(root))
            {
                path = FileSystem.PathCombine(Path.GetFullPath(entry), pattern);
                if (File.Exists(path) || Directory.Exists(path))
                {
                    return path;
                }
            }
        }

        return "";
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        EmitStatus("Checking for Java binary path");
        var binPath = FileSystem.PathCombine("bin", "java");
        var wantedPath = FileSystem.PathCombine(_path, binPath);
        if (File.Exists(wantedPath) || Directory.Exists(wantedPath))
        {
            EmitSucceeded();
            return Task.CompletedTask;
        }

        EmitStatus("Searching for Java binary path");
        var contentsPartialPath = FileSystem.PathCombine("Contents", "Home", binPath);
        var relativePathToBin = FindBinPath(_path, contentsPartialPath);
        if (relativePathToBin.Length == 0)
        {
            EmitFailed("Failed to find Java binary path");
            return Task.CompletedTask;
        }

        // relativePathToBin ends with ".../bin/java": chop the "bin/java" tail off
        var folderToLink = relativePathToBin[..^binPath.Length];

        EmitStatus("Collecting folders to symlink");
        var entries = Directory.GetFileSystemEntries(folderToLink);
        SetProgress(0, entries.Length);

        EmitStatus("Symlinking Java binary path");
        var linked = 0;
        foreach (var entry in entries)
        {
            var source = Path.GetFullPath(entry);
            var destination = FileSystem.PathCombine(_path, Path.GetFileName(entry));
            var destinationExists = File.Exists(destination) || Directory.Exists(destination);
            if (!destinationExists)
            {
                try
                {
                    File.CreateSymbolicLink(destination, source);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    EmitFailed(e.Message);
                    return Task.CompletedTask;
                }
            }

            linked++;
            SetProgress(linked, entries.Length);
        }

        EmitSucceeded();
        return Task.CompletedTask;
    }
}
