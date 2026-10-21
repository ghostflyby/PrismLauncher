// SPDX-License-Identifier: GPL-3.0-only
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
        var name = FileNaming.DirNameFromString("My Instance!", new LocalPath(_tempRoot));
        Assert.Equal("My Instance-", name);
        Directory.CreateDirectory(Path.Combine(_tempRoot, name));
        Assert.Equal("My Instance-(1)", FileNaming.DirNameFromString("My Instance!", new LocalPath(_tempRoot)));
    }

    [Fact]
    public void DirNameFromString_DedupesAcrossDirectoryLists()
    {
        // #6110: DirNameFromString(string, QStringList) avoids clashes against every given
        // dir, so instance folder names stay unique across all configured instance roots
        var otherDir = Path.Combine(_tempRoot, "second-root");
        Directory.CreateDirectory(otherDir);
        Directory.CreateDirectory(Path.Combine(otherDir, "My Instance-"));

        var dirs = new[] { new LocalPath(_tempRoot), new LocalPath(otherDir) };
        // "My Instance-" clashes inside second-root even though first-root is empty
        Assert.Equal("My Instance-(1)", FileNaming.DirNameFromString("My Instance!", dirs));
        // and the single-dir overload still only considers its own dir
        Assert.Equal("My Instance-", FileNaming.DirNameFromString("My Instance!", new LocalPath(_tempRoot)));
    }

    [Fact]
    public async Task GetUniqueResourceName()
    {
        // enabled mods are prioritized and returned untouched
        Assert.Equal("/x/mod.jar", FileNaming.GetUniqueResourceName(new LocalPath("/x/mod.jar")).Value);

        // disabled file whose stripped name does not exist: the original path is returned as-is
        var disabled = Path.Combine(_tempRoot, "mod.jar.disabled");
        await File.WriteAllTextAsync(disabled, "x");
        Assert.Equal(disabled, FileNaming.GetUniqueResourceName(new LocalPath(disabled)).Value);

        // once "mod.jar" exists, the next free ".duplicate" name is chosen
        await File.WriteAllTextAsync(Path.Combine(_tempRoot, "mod.jar"), "x");
        Assert.Equal(Path.Combine(_tempRoot, "mod.jar.duplicate"),
            FileNaming.GetUniqueResourceName(new LocalPath(disabled)).Value);
        await File.WriteAllTextAsync(Path.Combine(_tempRoot, "mod.jar.duplicate"), "x");
        Assert.Equal(Path.Combine(_tempRoot, "mod.jar.duplicate2"),
            FileNaming.GetUniqueResourceName(new LocalPath(disabled)).Value);
    }
}
