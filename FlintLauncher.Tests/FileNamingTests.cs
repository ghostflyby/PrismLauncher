// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// The FileNaming cases of the port of tests/FileSystem_test.cpp: sanitization and
// on-disk name dedup. RemoveInvalidFilenameChars is FileNaming-internal now (only
// DirNameFromString sanitizes names), and CheckProblematicPathJava had no production
// callers — both tests are gone with the members.

using FlintLauncher.Core.IO;
using TruePath;
using Xunit;

namespace FlintLauncher.Tests;

public class FileNamingTests : IDisposable
{
    private readonly string _tempRoot = Directory.CreateTempSubdirectory("flint_file_naming_").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void RemoveInvalidPathChars_KeepsSeparators()
    {
        // g_badChars alone (no "\\/" extra): '/' and '\' are printable and kept
        Assert.Equal("a/b\\c", FileNaming.RemoveInvalidPathChars("a/b\\c"));
    }

    [Fact]
    public void DirNameFromString_AvoidsClashes()
    {
        AbsolutePath inDir = new AbsolutePath(_tempRoot);
        var name = FileNaming.DirNameFromString("My Instance!", [inDir]);
        Assert.Equal("My Instance-", name);
        Directory.CreateDirectory(Path.Combine(_tempRoot, name));
        AbsolutePath inDir1 = new AbsolutePath(_tempRoot);
        Assert.Equal("My Instance-(1)", FileNaming.DirNameFromString("My Instance!", [inDir1]));
    }

    [Fact]
    public void DirNameFromString_DedupesAcrossDirectoryLists()
    {
        // #6110: DirNameFromString(string, QStringList) avoids clashes against every given
        // dir, so instance folder names stay unique across all configured instance roots
        var otherDir = Path.Combine(_tempRoot, "second-root");
        Directory.CreateDirectory(otherDir);
        Directory.CreateDirectory(Path.Combine(otherDir, "My Instance-"));

        var dirs = new[] { new AbsolutePath(_tempRoot), new AbsolutePath(otherDir) };
        // "My Instance-" clashes inside second-root even though first-root is empty
        Assert.Equal("My Instance-(1)", FileNaming.DirNameFromString("My Instance!", dirs));
        // and the single-dir overload still only considers its own dir
        AbsolutePath inDir = new AbsolutePath(_tempRoot);
        Assert.Equal("My Instance-", FileNaming.DirNameFromString("My Instance!", [inDir]));
    }

    [Fact]
    public async Task GetUniqueResourceName()
    {
        // enabled mods are prioritized and returned untouched (compared against the input
        // path itself — a literal POSIX string would not survive windows normalization)
        var enabled = new AbsolutePath("/x/mod.jar");
        Assert.Equal(enabled.Value, FileNaming.GetUniqueResourceName(enabled).Value);

        // disabled file whose stripped name does not exist: the original path is returned as-is
        var disabled = Path.Combine(_tempRoot, "mod.jar.disabled");
        await File.WriteAllTextAsync(disabled, "x");
        Assert.Equal(disabled, FileNaming.GetUniqueResourceName(new AbsolutePath(disabled)).Value);

        // once "mod.jar" exists, the next free ".duplicate" name is chosen
        await File.WriteAllTextAsync(Path.Combine(_tempRoot, "mod.jar"), "x");
        Assert.Equal(Path.Combine(_tempRoot, "mod.jar.duplicate"),
            FileNaming.GetUniqueResourceName(new AbsolutePath(disabled)).Value);
        await File.WriteAllTextAsync(Path.Combine(_tempRoot, "mod.jar.duplicate"), "x");
        Assert.Equal(Path.Combine(_tempRoot, "mod.jar.duplicate2"),
            FileNaming.GetUniqueResourceName(new AbsolutePath(disabled)).Value);
    }
}
