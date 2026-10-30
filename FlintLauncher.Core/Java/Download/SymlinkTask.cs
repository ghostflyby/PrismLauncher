// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2023-2024 Trial97 <alexandru.tripon97@gmail.com>
//
// A JDK extracted on macOS keeps its binary at <target>/Contents/Home/bin/java, while the
// launcher expects <target>/bin/java. Run() finds the Contents/Home folder and symlinks its
// entries into the target. A destination that already exists is skipped, so a partially
// linked tree can be completed.

using TruePath;
using TruePath.SystemIo;

namespace FlintLauncher.Core.Java.Download;

public static class SymlinkTask
{
    /// <summary>The folder holding the wanted relative path: either directly under
    /// <paramref name="root"/> or one level down (the JDK archive's top-level folder).</summary>
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

    /// <summary>Links the Contents/Home layout into <paramref name="finalPath"/>. Throws when
    /// no binary path can be found; a target that already carries bin/java is left untouched.</summary>
    public static void Run(AbsolutePath finalPath)
    {
        var binPath = new LocalPath("bin/java");
        var wantedPath = finalPath / binPath;
        if (wantedPath.Exists())
        {
            return;
        }

        var contentsPartialPath = new LocalPath("Contents") / "Home" / binPath;
        var relativePathToBin = FindBinPath(finalPath, contentsPartialPath)
                                ?? throw new InvalidOperationException("Failed to find Java binary path");

        // relativePathToBin ends with ".../bin/java": chop the "bin/java" tail off
        var folderToLink = relativePathToBin.Value[..^binPath.Value.Length];
        foreach (var entry in Directory.GetFileSystemEntries(folderToLink))
        {
            var source = Path.GetFullPath(entry);
            var destination = finalPath / Path.GetFileName(entry);
            if (!destination.Exists())
            {
                File.CreateSymbolicLink(destination.Value, source);
            }
        }
    }
}
