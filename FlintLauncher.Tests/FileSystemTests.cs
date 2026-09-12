// SPDX-License-Identifier: GPL-3.0-only
// Port of the platform-neutral cases of tests/FileSystem_test.cpp
// (QTemporaryDir is replaced by a per-test temp directory).
using FlintLauncher.Core.IO;
using Xunit;

namespace FlintLauncher.Tests;

public class FileSystemTests
{
    private const string BothSlash = "/foo/";
    private const string TrailingSlash = "foo/";
    private const string LeadingSlash = "/foo";

    private static string FixtureFolder() =>
        Path.Combine(AppContext.BaseDirectory, "TestData", "FileSystem", "test_folder");

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "flint-fs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // --------------------------------------------------------------- path combine

    [Fact]
    public void TestPathCombine()
    {
        Assert.Equal("/foo/foo", FileSystem.PathCombine(BothSlash, BothSlash));
        Assert.Equal("foo/foo", FileSystem.PathCombine(TrailingSlash, TrailingSlash));
        Assert.Equal("/foo/foo", FileSystem.PathCombine(LeadingSlash, LeadingSlash));

        Assert.Equal("/foo/foo/foo", FileSystem.PathCombine(BothSlash, BothSlash, BothSlash));
        Assert.Equal("foo/foo/foo", FileSystem.PathCombine(TrailingSlash, TrailingSlash, TrailingSlash));
        Assert.Equal("/foo/foo/foo", FileSystem.PathCombine(LeadingSlash, LeadingSlash, LeadingSlash));
    }

    [Theory]
    [InlineData("/abc/def/ghi/jkl", "/abc/def", "ghi/jkl")]
    [InlineData("/abc/def/ghi/jkl", "/abc/def/", "ghi/jkl")]
    public void TestPathCombine1(string result, string path1, string path2)
    {
        Assert.Equal(result, FileSystem.PathCombine(path1, path2));
    }

    [Theory]
    [InlineData("/abc/def/ghi/jkl", "/abc", "def", "ghi/jkl")]
    [InlineData("/abc/def/ghi/jkl", "/abc/", "def", "ghi/jkl")]
    [InlineData("/abc/def/ghi/jkl", "/abc", "def/", "ghi/jkl")]
    [InlineData("/abc/def/ghi/jkl", "/abc/", "def/", "ghi/jkl")]
    public void TestPathCombine2(string result, string path1, string path2, string path3)
    {
        Assert.Equal(result, FileSystem.PathCombine(path1, path2, path3));
    }

    [Fact]
    public void TestPathDepth()
    {
        Assert.Equal(0, FileSystem.PathDepth(""));
        Assert.Equal(0, FileSystem.PathDepth("."));
        Assert.Equal(0, FileSystem.PathDepth("foo.txt"));
        Assert.Equal(0, FileSystem.PathDepth("./foo.txt"));
        Assert.Equal(1, FileSystem.PathDepth("./bar/foo.txt"));
        Assert.Equal(0, FileSystem.PathDepth("../bar/foo.txt"));
        Assert.Equal(1, FileSystem.PathDepth("/bar/foo.txt"));
        Assert.Equal(2, FileSystem.PathDepth("baz/bar/foo.txt"));
        Assert.Equal(2, FileSystem.PathDepth("/baz/bar/foo.txt"));
        Assert.Equal(2, FileSystem.PathDepth("./baz/bar/foo.txt"));
        Assert.Equal(1, FileSystem.PathDepth("/baz/../bar/foo.txt"));
    }

    [Fact]
    public void TestPathTrunc()
    {
        Assert.Equal("", FileSystem.PathTruncate("", 0));
        Assert.Equal("", FileSystem.PathTruncate("foo.txt", 0));
        Assert.Equal("", FileSystem.PathTruncate("foo.txt", 1));
        Assert.Equal("./bar", FileSystem.PathTruncate("./bar/foo.txt", 0));
        Assert.Equal("./bar", FileSystem.PathTruncate("./bar/foo.txt", 1));
        Assert.Equal("/bar", FileSystem.PathTruncate("/bar/foo.txt", 1));
        Assert.Equal("bar", FileSystem.PathTruncate("bar/foo.txt", 1));
        Assert.Equal("baz/bar", FileSystem.PathTruncate("baz/bar/foo.txt", 2));
    }

    // ----------------------------------------------------------------------- copy

    private static void CopyTest(Action<string> test, string folder)
    {
        // first try variant without trailing /
        Assert.False(folder.EndsWith('/'));
        test(folder);

        // then variant with trailing /
        folder += '/';
        Assert.True(folder.EndsWith('/'));
        test(folder);
    }

    [Fact]
    public void TestCopy()
    {
        CopyTest(folder =>
        {
            using var tempDirScope = new TempDirScope();
            var targetDir = FileSystem.PathCombine(tempDirScope.Path, "test_folder");
            Assert.True(FileSystem.Copy(folder, targetDir));

            Assert.True(File.Exists(Path.Combine(targetDir, "pack.mcmeta")));
            Assert.True(Directory.Exists(Path.Combine(targetDir, "assets")));
        }, FixtureFolder());
    }

    [Fact]
    public void TestCopyWithBlacklist()
    {
        CopyTest(folder =>
        {
            using var tempDirScope = new TempDirScope();
            var targetDir = FileSystem.PathCombine(tempDirScope.Path, "test_folder");
            Assert.True(FileSystem.Copy(folder, targetDir, matcher: path => path.Contains("mcmeta")));

            Assert.False(File.Exists(Path.Combine(targetDir, "pack.mcmeta")));
            Assert.True(Directory.Exists(Path.Combine(targetDir, "assets")));
        }, FixtureFolder());
    }

    [Fact]
    public void TestCopyWithWhitelist()
    {
        CopyTest(folder =>
        {
            using var tempDirScope = new TempDirScope();
            var targetDir = FileSystem.PathCombine(tempDirScope.Path, "test_folder");
            Assert.True(FileSystem.Copy(folder, targetDir, matcher: path => path.Contains("mcmeta"), whitelist: true));

            Assert.True(File.Exists(Path.Combine(targetDir, "pack.mcmeta")));
            Assert.False(Directory.Exists(Path.Combine(targetDir, "assets")));
        }, FixtureFolder());
    }

    [Fact]
    public void TestCopyWithDotHidden()
    {
        CopyTest(folder =>
        {
            using var tempDirScope = new TempDirScope();
            var targetDir = FileSystem.PathCombine(tempDirScope.Path, "test_folder");
            Assert.True(FileSystem.Copy(folder, targetDir));

            Assert.True(Directory.Exists(Path.Combine(targetDir, ".secret_folder")));
            Assert.True(File.Exists(Path.Combine(targetDir, ".secret_folder", ".secret_file.txt")));
        }, FixtureFolder());
    }

    [Fact]
    public void TestCopySingleFile()
    {
        using var tempDirScope = new TempDirScope();
        var file = Path.Combine(FixtureFolder(), "pack.mcmeta");
        var target = Path.Combine(tempDirScope.Path, "pack.mcmeta");
        Assert.True(FileSystem.Copy(file, target));
        Assert.True(File.Exists(target));
    }

    // ------------------------------------------------------- write/read/move/delete

    [Fact]
    public void WriteRead_RoundTrip_AtomicLayout()
    {
        using var tempDirScope = new TempDirScope();
        // nested folders are created by Write (ensureExists)
        var path = Path.Combine(tempDirScope.Path, "nested", "file.bin");
        var data = new byte[] { 1, 2, 3, 0xFF, 0, 4 };
        FileSystem.Write(path, data);
        Assert.Equal(data, FileSystem.Read(path));

        // overwriting leaves no temp files behind
        FileSystem.Write(path, new byte[] { 9 });
        Assert.Equal(new byte[] { 9 }, FileSystem.Read(path));
        Assert.Equal(["file.bin"], Directory.GetFiles(Path.Combine(tempDirScope.Path, "nested"))
            .Select(Path.GetFileName).OrderBy(n => n));
    }

    [Fact]
    public void Append_And_AppendSafe()
    {
        using var tempDirScope = new TempDirScope();
        var path = Path.Combine(tempDirScope.Path, "log.txt");
        FileSystem.Append(path, "a"u8.ToArray());
        FileSystem.Append(path, "b"u8.ToArray());
        Assert.Equal("ab", System.Text.Encoding.UTF8.GetString(FileSystem.Read(path)));

        FileSystem.AppendSafe(path, "c"u8.ToArray());
        Assert.Equal("abc", System.Text.Encoding.UTF8.GetString(FileSystem.Read(path)));
    }

    [Fact]
    public void Move_And_DeletePath()
    {
        using var tempDirScope = new TempDirScope();
        var srcFile = Path.Combine(tempDirScope.Path, "a.txt");
        File.WriteAllText(srcFile, "hello");
        var dstFile = Path.Combine(tempDirScope.Path, "sub", "b.txt");
        Assert.True(FileSystem.Move(srcFile, dstFile));
        Assert.False(File.Exists(srcFile));
        Assert.Equal("hello", File.ReadAllText(dstFile));

        var srcDir = Path.Combine(tempDirScope.Path, "folder");
        Directory.CreateDirectory(Path.Combine(srcDir, "inner"));
        File.WriteAllText(Path.Combine(srcDir, "inner", "c.txt"), "x");
        var dstDir = Path.Combine(tempDirScope.Path, "folder2");
        Assert.True(FileSystem.Move(srcDir, dstDir));
        Assert.False(Directory.Exists(srcDir));
        Assert.True(File.Exists(Path.Combine(dstDir, "inner", "c.txt")));

        Assert.True(FileSystem.DeletePath(dstDir));
        Assert.False(Directory.Exists(dstDir));

        // deleting a missing path succeeds (fs::remove_all semantics)
        Assert.True(FileSystem.DeletePath(Path.Combine(tempDirScope.Path, "missing")));
    }

    [Fact]
    public void UpdateTimestamp_BumpsModifiedTime()
    {
        using var tempDirScope = new TempDirScope();
        var path = Path.Combine(tempDirScope.Path, "stamp.txt");
        File.WriteAllText(path, "x");
        File.SetLastWriteTime(path, DateTime.Now.AddDays(-10));
        Assert.True(FileSystem.UpdateTimestamp(path));
        Assert.True((DateTime.Now - File.GetLastWriteTime(path)).Duration() < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void RemoveFiles_Removes_All_Existing()
    {
        using var tempDirScope = new TempDirScope();
        var f1 = Path.Combine(tempDirScope.Path, "1.txt");
        var f2 = Path.Combine(tempDirScope.Path, "2.txt");
        File.WriteAllText(f1, "1");
        File.WriteAllText(f2, "2");
        Assert.True(FileSystem.RemoveFiles([f1, f2]));
        Assert.False(File.Exists(f1));
        Assert.False(File.Exists(f2));
    }

    // ------------------------------------------------------------- name handling

    [Theory]
    [InlineData("a<b>c:d\"e|f?g*h", "a-b-c-d-e-f-g-h")]
    [InlineData("a/b\\c", "a-b-c")]
    public void RemoveInvalidFilenameChars(string input, string expected)
    {
        // default replacement char is '-' (FS::RemoveInvalidFilenameChars)
        Assert.Equal(expected, FileSystem.RemoveInvalidFilenameChars(input));
    }

    [Fact]
    public void RemoveInvalidPathChars_KeepsSeparators()
    {
        // g_badChars alone (no "\\/" extra): '/' and '\' are printable and kept
        Assert.Equal("a/b\\c", FileSystem.RemoveInvalidPathChars("a/b\\c"));
    }

    [Fact]
    public void DirNameFromString_AvoidsClashes()
    {
        using var tempDirScope = new TempDirScope();
        var name = FileSystem.DirNameFromString("My Instance!", tempDirScope.Path);
        Assert.Equal("My Instance-", name);
        Directory.CreateDirectory(Path.Combine(tempDirScope.Path, name));
        Assert.Equal("My Instance-(1)", FileSystem.DirNameFromString("My Instance!", tempDirScope.Path));
    }

    [Fact]
    public void GetUniqueResourceName()
    {
        using var tempDirScope = new TempDirScope();
        // enabled mods are prioritized and returned untouched
        Assert.Equal("/x/mod.jar", FileSystem.GetUniqueResourceName("/x/mod.jar"));

        // disabled file whose stripped name does not exist: the original path is returned as-is
        var disabled = Path.Combine(tempDirScope.Path, "mod.jar.disabled");
        File.WriteAllText(disabled, "x");
        Assert.Equal(disabled, FileSystem.GetUniqueResourceName(disabled));

        // once "mod.jar" exists, the next free ".duplicate" name is chosen
        File.WriteAllText(Path.Combine(tempDirScope.Path, "mod.jar"), "x");
        Assert.Equal(Path.Combine(tempDirScope.Path, "mod.jar.duplicate"),
            FileSystem.GetUniqueResourceName(disabled));
        File.WriteAllText(Path.Combine(tempDirScope.Path, "mod.jar.duplicate"), "x");
        Assert.Equal(Path.Combine(tempDirScope.Path, "mod.jar.duplicate2"),
            FileSystem.GetUniqueResourceName(disabled));
    }

    [Fact]
    public void CheckProblematicPathJava()
    {
        Assert.True(FileSystem.CheckProblematicPathJava("/some!/path"));
        Assert.False(FileSystem.CheckProblematicPathJava("/some/path"));
    }

    /// <summary>QTemporaryDir replacement with auto-remove.</summary>
    private sealed class TempDirScope : IDisposable
    {
        public TempDirScope()
        {
            Path = FileSystemTests.TempDir();
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
