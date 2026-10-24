// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2023-2024 Trial97 <alexandru.tripon97@gmail.com>
//
// Port of launcher/java/download/SymlinkTask.{h,cpp}: on macOS an extracted JDK layout is
// <target>/Contents/Home/bin/java, but the launcher expects <target>/bin/java. The task finds
// the Contents/Home folder and symlinks everything inside it into the target folder.
// FS::create_link becomes System.IO.File.CreateSymbolicLink (available since .NET 6, AOT-safe);
// existing destinations are skipped (the C++ std::filesystem call would throw on them, but the
// task re-runs only when the wanted bin path is missing anyway).

using FlintLauncher.Core.Tasks;
using TruePath;
using TruePath.SystemIo;

namespace FlintLauncher.Core.Java.Download;

public sealed class SymlinkTask(AbsolutePath finalPath) : FlintTask
{
    /// <summary>Port of the free function findBinPath: first &lt;root&gt;/&lt;pattern&gt;, then one level down.</summary>
    public static AbsolutePath? FindBinPath(AbsolutePath root, LocalPath pattern)
    {
        var path = root / pattern;
        if (path.Exists())
        {
            return path;
        }

        if (root.ExistsDirectory())
        {
            foreach (var entry in root.GetDirectories())
            {
                path = new AbsolutePath(entry) / pattern;
                if (path.Exists())
                {
                    return path;
                }
            }
        }

        return null;
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        EmitStatus("Checking for Java binary path");
        var binPath = new LocalPath("bin/java");
        var wantedPath = finalPath / binPath;
        if (wantedPath.Exists())
        {
            EmitSucceeded();
            return Task.CompletedTask;
        }

        EmitStatus("Searching for Java binary path");
        var contentsPartialPath = new LocalPath("Contents") / "Home" / binPath;
        var relativePathToBin = FindBinPath(finalPath, contentsPartialPath);
        if (relativePathToBin is null)
        {
            EmitFailed("Failed to find Java binary path");
            return Task.CompletedTask;
        }

        // relativePathToBin ends with ".../bin/java": chop the "bin/java" tail off
        var folderToLink = relativePathToBin.Value.Value[..^binPath.Value.Length];

        EmitStatus("Collecting folders to symlink");
        var entries = Directory.GetFileSystemEntries(folderToLink);
        SetProgress(0, entries.Length);

        EmitStatus("Symlinking Java binary path");
        var linked = 0;
        foreach (var entry in entries)
        {
            var source = Path.GetFullPath(entry);
            string a1 = Path.GetFileName(entry);
            var destination = finalPath / a1;
            var destinationExists = destination.Exists();
            if (!destinationExists)
            {
                try
                {
                    File.CreateSymbolicLink(destination.Value, source);
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
