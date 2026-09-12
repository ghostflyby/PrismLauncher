// SPDX-License-Identifier: GPL-3.0-only
// Tests for the IconFiles port (IconUtils + the file-level IconList operations).
using FlintLauncher.Core.Icons;
using Xunit;

namespace Flint.Tests;

public class IconFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"flint-icons-{Guid.NewGuid():N}");
    private readonly string _iconsDir;

    public IconFilesTests()
    {
        Directory.CreateDirectory(_root);
        _iconsDir = Path.Combine(_root, "icons");
        Directory.CreateDirectory(_iconsDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static void WriteFile(string path, string content = "fake png bytes")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
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
        var source = Path.Combine(_root, "fake.png");
        WriteFile(source);

        Assert.True(IconFiles.InstallIcon(_iconsDir, source));

        // lookup by key finds the installed file
        Assert.True(IconFiles.IconFileExists(_iconsDir, "fake"));
        Assert.Equal(Path.Combine(_iconsDir, "fake.png"), IconFiles.GetIconFile(_iconsDir, "fake"));
        Assert.False(IconFiles.IconFileExists(_iconsDir, "missing"));

        // installing under an explicit name
        Assert.True(IconFiles.InstallIcon(_iconsDir, source, "renamed.png"));
        Assert.True(IconFiles.IconFileExists(_iconsDir, "renamed"));
    }

    [Fact]
    public void InstallIcon_RejectsInvalidInputs()
    {
        // missing file
        Assert.False(IconFiles.InstallIcon(_iconsDir, Path.Combine(_root, "nope.png")));

        // unsupported suffix
        var txt = Path.Combine(_root, "notes.txt");
        WriteFile(txt);
        Assert.False(IconFiles.InstallIcon(_iconsDir, txt));
        Assert.False(IconFiles.IconFileExists(_iconsDir, "notes"));

        // a directory is not an icon file
        Assert.False(IconFiles.InstallIcon(_iconsDir, _root));

        // existing target is not overwritten (QFile::copy semantics)
        var png = Path.Combine(_root, "clash.png");
        WriteFile(png, "new content");
        WriteFile(Path.Combine(_iconsDir, "clash.png"), "old content");
        Assert.False(IconFiles.InstallIcon(_iconsDir, png));
        Assert.Equal("old content", File.ReadAllText(Path.Combine(_iconsDir, "clash.png")));
    }

    [Fact]
    public void FindBestIconIn_MatchesCompleteBaseName_AndValidSuffixes()
    {
        WriteFile(Path.Combine(_iconsDir, "my.icon.png"));
        WriteFile(Path.Combine(_iconsDir, "grass.svg"));
        WriteFile(Path.Combine(_iconsDir, "grass.png"));
        WriteFile(Path.Combine(_iconsDir, "other.txt"));

        // completeBaseName ("my.icon") and plain file name both match
        Assert.Equal(Path.Combine(_iconsDir, "my.icon.png"), IconFiles.FindBestIconIn(_iconsDir, "my.icon"));
        Assert.Equal(Path.Combine(_iconsDir, "my.icon.png"), IconFiles.FindBestIconIn(_iconsDir, "my.icon.png"));

        // any of the valid-suffix files is a hit (QSet/QDirIterator order is unspecified upstream)
        var grass = IconFiles.FindBestIconIn(_iconsDir, "grass");
        Assert.True(grass.EndsWith("grass.svg") || grass.EndsWith("grass.png"));

        // invalid suffix and missing keys find nothing
        Assert.Equal("", IconFiles.FindBestIconIn(_iconsDir, "other"));
        Assert.Equal("", IconFiles.FindBestIconIn(_iconsDir, "nope"));
        Assert.Equal("", IconFiles.FindBestIconIn(Path.Combine(_root, "gone"), "grass"));
    }

    [Fact]
    public void DefaultKey_AliasesToGrass()
    {
        WriteFile(Path.Combine(_iconsDir, "grass.png"));
        Assert.Equal(Path.Combine(_iconsDir, "grass.png"), IconFiles.GetIconFile(_iconsDir, "default"));
        Assert.True(IconFiles.IconFileExists(_iconsDir, "default"));
    }

    [Fact]
    public void SubdirectoryIcons_AreScannedWithSubdirKeys()
    {
        var nested = Path.Combine(_iconsDir, "sub", "inner.png");
        WriteFile(nested);

        var paths = IconFiles.GetIconFilePaths(_iconsDir);
        Assert.Equal([nested], paths);

        // key = completeBaseName of the relative path
        Assert.Equal("inner", IconFiles.IconKeyForFile(_iconsDir, nested));
        Assert.True(IconFiles.IconFileExists(_iconsDir, "inner"));

        // display name: "relative dir » base name"
        Assert.Equal("sub » inner", IconFiles.IconDisplayName(_iconsDir, nested));

        // root-level icons are named by their base name only
        WriteFile(Path.Combine(_iconsDir, "top.png"));
        Assert.Equal("top", IconFiles.IconDisplayName(_iconsDir, Path.Combine(_iconsDir, "top.png")));
    }

    [Fact]
    public void DeleteIcon_RemovesTheFile()
    {
        WriteFile(Path.Combine(_iconsDir, "deleteme.png"));
        Assert.True(IconFiles.IconFileExists(_iconsDir, "deleteme"));

        Assert.True(IconFiles.DeleteIcon(_iconsDir, "deleteme"));
        Assert.False(IconFiles.IconFileExists(_iconsDir, "deleteme"));
        Assert.False(File.Exists(Path.Combine(_iconsDir, "deleteme.png")));

        // deleting a missing key fails
        Assert.False(IconFiles.DeleteIcon(_iconsDir, "deleteme"));
    }
}
