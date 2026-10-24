// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Tests for the IconFiles port (IconUtils + the file-level IconList operations).

using FlintLauncher.Core.Icons;
using FlintLauncher.Core.IO;
using TruePath;
using TruePath.SystemIo;
using Xunit;

namespace FlintLauncher.Tests;

public class IconFilesTests : IDisposable
{
    private readonly AbsolutePath _root = (Temporary.CreateTempFolder() / $"flint-icons-{Guid.NewGuid():N}");
    private readonly AbsolutePath _iconsDir;

    public IconFilesTests()
    {
        _root.CreateDirectory();
        _iconsDir = (_root / "icons");
        _iconsDir.CreateDirectory();
    }

    public void Dispose()
    {
        try
        {
            _root.DeleteDirectoryRecursively();
        }
        catch (DirectoryNotFoundException) { }
    }

    private static void WriteFile(AbsolutePath path, string content = "fake png bytes")
    {
        var dir = Assert.NotNull(path.Parent);
        dir.CreateDirectory();
        path.WriteAllText(content);
    }

    [Fact]
    public void IsIconSuffix_AndFilter_MatchUpstream()
    {
        Assert.True(IconFiles.IsIconSuffix("png"));
        Assert.True(IconFiles.IsIconSuffix("svg"));
        Assert.True(IconFiles.IsIconSuffix("webp"));
        Assert.False(IconFiles.IsIconSuffix("txt"));
        Assert.False(IconFiles.IsIconSuffix(""));

        Assert.Equal("(*.svg *.png *.ico *.gif *.jpg *.jpeg *.webp)", IconFiles.GetIconFilter());
    }

    [Fact]
    public void InstallIcon_AndLookupByKey()
    {
        var source = (_root / "fake.png");
        WriteFile(source);

        Assert.True(IconFiles.InstallIcon(_iconsDir, source));

        // lookup by key finds the installed file
        Assert.True(IconFiles.IconFileExists(_iconsDir, "fake"));
        Assert.Equal((_iconsDir / "fake.png"), IconFiles.GetIconFile(_iconsDir, "fake"));
        Assert.False(IconFiles.IconFileExists(_iconsDir, "missing"));

        // installing under an explicit name
        Assert.True(IconFiles.InstallIcon(_iconsDir, source, "renamed.png"));
        Assert.True(IconFiles.IconFileExists(_iconsDir, "renamed"));
    }

    [Fact]
    public void InstallIcon_RejectsInvalidInputs()
    {
        // missing file
        Assert.False(IconFiles.InstallIcon(_iconsDir, (_root / "nope.png")));

        // unsupported suffix
        var txt = (_root / "notes.txt");
        WriteFile(txt);
        Assert.False(IconFiles.InstallIcon(_iconsDir, txt));
        Assert.False(IconFiles.IconFileExists(_iconsDir, "notes"));

        // a directory is not an icon file
        Assert.False(IconFiles.InstallIcon(_iconsDir, _root));

        // existing target is not overwritten (QFile::copy semantics)
        var png = (_root / "clash.png");
        WriteFile(png, "new content");
        WriteFile((_iconsDir / "clash.png"), "old content");
        Assert.False(IconFiles.InstallIcon(_iconsDir, png));
        Assert.Equal("old content", (_iconsDir / "clash.png").ReadAllText());
    }

    [Fact]
    public void FindBestIconIn_MatchesCompleteBaseName_AndValidSuffixes()
    {
        WriteFile((_iconsDir / "my.icon.png"));
        WriteFile((_iconsDir / "grass.svg"));
        WriteFile((_iconsDir / "grass.png"));
        WriteFile((_iconsDir / "other.txt"));

        // completeBaseName ("my.icon") and plain file name both match
        Assert.Equal((_iconsDir / "my.icon.png"), IconFiles.FindBestIconIn(_iconsDir, "my.icon"));
        Assert.Equal((_iconsDir / "my.icon.png"), IconFiles.FindBestIconIn(_iconsDir, "my.icon.png"));

        // any of the valid-suffix files is a hit (QSet/QDirIterator order is unspecified upstream)
        var grass = Assert.NotNull(IconFiles.FindBestIconIn(_iconsDir, "grass"));

        Assert.True(grass.Value.EndsWith("grass.svg") || grass.Value.EndsWith("grass.png"));

        // invalid suffix and missing keys find nothing
        Assert.Null(IconFiles.FindBestIconIn(_iconsDir, "other"));
        Assert.Null(IconFiles.FindBestIconIn(_iconsDir, "nope"));
        Assert.Null(IconFiles.FindBestIconIn(_root / "gone", "grass"));
    }

    [Fact]
    public void DefaultKey_AliasesToGrass()
    {
        WriteFile((_iconsDir / "grass.png"));
        Assert.Equal((_iconsDir / "grass.png"), IconFiles.GetIconFile(_iconsDir, "default"));
        Assert.True(IconFiles.IconFileExists(_iconsDir, "default"));
    }

    [Fact]
    public void SubdirectoryIcons_AreScannedWithSubdirKeys()
    {
        var nested = (_iconsDir / "sub" / "inner.png");
        WriteFile(nested);

        var paths = IconFiles.GetIconFilePaths(_iconsDir);
        Assert.Equal([nested], paths);

        // key = completeBaseName of the relative path
        Assert.Equal("inner", IconFiles.IconKeyForFile(_iconsDir, nested));
        Assert.True(IconFiles.IconFileExists(_iconsDir, "inner"));

        // display name: "relative dir » base name"
        Assert.Equal("sub » inner", IconFiles.IconDisplayName(_iconsDir, nested));

        // root-level icons are named by their base name only
        WriteFile((_iconsDir / "top.png"));
        Assert.Equal("top", IconFiles.IconDisplayName(_iconsDir, (_iconsDir / "top.png")));
    }

    [Fact]
    public void DeleteIcon_RemovesTheFile()
    {
        WriteFile((_iconsDir / "deleteme.png"));
        Assert.True(IconFiles.IconFileExists(_iconsDir, "deleteme"));

        Assert.True(IconFiles.DeleteIcon(_iconsDir, "deleteme"));
        Assert.False(IconFiles.IconFileExists(_iconsDir, "deleteme"));
        Assert.False((_iconsDir / "deleteme.png").ExistsFile());

        // deleting a missing key fails
        Assert.False(IconFiles.DeleteIcon(_iconsDir, "deleteme"));
    }

    // the IconPool model over the static helpers (key→path index, scan,
    // install/delete — the IconList half; the endpoints consume this, not the statics)
    [Fact]
    public void IconPool_IndexesScansInstallsAndDeletes()
    {
        WriteFile((_iconsDir / "grass.png"));
        WriteFile((_iconsDir / "sub" / "inner.svg"));

        var pool = new IconPool(MaterializedDirectory.Ensure(_iconsDir)!);
        Assert.Equal(_iconsDir, pool.IconsDir);

        // the scan indexes subdirectory icons by their base-name key; entries carry the
        // display name ("dir » name" for nested icons)
        var entries = pool.Entries();
        Assert.Equal(2, entries.Count);
        Assert.Equal((_iconsDir / "grass.png"),
            entries.Single(e => e.Key == "grass").Path);
        Assert.Equal("sub » inner", entries.Single(e => e.Key == "inner").DisplayName);

        // lookups go through the index (and the default→grass alias survives)
        Assert.Equal((_iconsDir / "grass.png"), pool.GetIconFile("grass"));
        Assert.Equal((_iconsDir / "grass.png"), pool.GetIconFile("default"));
        Assert.True(pool.IconFileExists("inner"));
        Assert.False(pool.IconFileExists("missing"));

        // install refreshes the index; the QFile::copy no-overwrite rule holds
        var source = (_root / "fake.png");
        WriteFile(source);
        Assert.True(pool.Install(source, "installed.png"));
        Assert.True(pool.IconFileExists("installed"));
        Assert.False(pool.Install(source, "installed.png")); // target exists
        Assert.False(pool.Install((_root / "nope.png")));

        // delete drops both the file and the index entry
        Assert.True(pool.Delete("installed"));
        Assert.False(pool.IconFileExists("installed"));
        Assert.False((_iconsDir / "installed.png").ExistsFile());
        Assert.False(pool.Delete("installed"));

        // external changes are picked up by the next read (the watcher's replacement)
        WriteFile((_iconsDir / "late.png"));
        Assert.True(pool.IconFileExists("late"));
    }
}
