// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// The FileOps cases of the port of tests/FileSystem_test.cpp (QTemporaryDir is replaced by
// a per-test temp directory). The Qt path-math cases (PathCombine/PathDepth/PathTruncate)
// went with the helpers they tested: path arithmetic is TruePath's now, tested upstream.

using FlintLauncher.Core.IO;
using TruePath;
using Xunit;

namespace FlintLauncher.Tests;

public class FileOpsTests
{
    private static string FixtureFolder() =>
        Path.Combine(AppContext.BaseDirectory, "TestData", "FileSystem", "test_folder");

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "flint-fs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ----------------------------------------------------------------------- copy

    private static async Task CopyTestAsync(Func<string, Task> test, string folder)
    {
        // first try variant without trailing /
        Assert.False(folder.EndsWith('/'));
        await test(folder);

        // then variant with trailing /
        folder += '/';
        Assert.True(folder.EndsWith('/'));
        await test(folder);
    }

    [Fact]
    public async Task TestCopy()
    {
        await CopyTestAsync(async folder =>
        {
            using var tempDirScope = new TempDirScope();
            var targetDir = new AbsolutePath(tempDirScope.Path) / "test_folder";
            Assert.True(FileOps.Copy(new AbsolutePath(folder), targetDir));

            var mcmeta = Path.Combine(targetDir.Value, "pack.mcmeta");
            Assert.True(File.Exists(mcmeta));
            Assert.True(Directory.Exists(Path.Combine(targetDir.Value, "assets")));
            Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(FixtureFolder(), "pack.mcmeta")),
                await File.ReadAllBytesAsync(mcmeta));
        }, FixtureFolder());
    }

    [Fact]
    public async Task TestCopyWithBlacklist()
    {
        await CopyTestAsync(async folder =>
        {
            using var tempDirScope = new TempDirScope();
            var targetDir = new AbsolutePath(tempDirScope.Path) / "test_folder";
            Assert.True(FileOps.Copy(new AbsolutePath(folder), targetDir, matcher: path => path.Contains("mcmeta")));

            Assert.False(File.Exists(Path.Combine(targetDir.Value, "pack.mcmeta")));
            Assert.True(Directory.Exists(Path.Combine(targetDir.Value, "assets")));
            // the non-matching files still land, content intact
            Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(FixtureFolder(), ".secret_folder", ".secret_file.txt")),
                await File.ReadAllBytesAsync(Path.Combine(targetDir.Value, ".secret_folder", ".secret_file.txt")));
        }, FixtureFolder());
    }

    [Fact]
    public async Task TestCopyWithWhitelist()
    {
        await CopyTestAsync(async folder =>
        {
            using var tempDirScope = new TempDirScope();
            var targetDir = new AbsolutePath(tempDirScope.Path) / "test_folder";
            Assert.True(FileOps.Copy(new AbsolutePath(folder), targetDir, matcher: path => path.Contains("mcmeta"), whitelist: true));

            var mcmeta = Path.Combine(targetDir.Value, "pack.mcmeta");
            Assert.True(File.Exists(mcmeta));
            Assert.False(Directory.Exists(Path.Combine(targetDir.Value, "assets")));
            Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(FixtureFolder(), "pack.mcmeta")),
                await File.ReadAllBytesAsync(mcmeta));
        }, FixtureFolder());
    }

    [Fact]
    public async Task TestCopyWithDotHidden()
    {
        await CopyTestAsync(async folder =>
        {
            using var tempDirScope = new TempDirScope();
            var targetDir = new AbsolutePath(tempDirScope.Path) / "test_folder";
            Assert.True(FileOps.Copy(new AbsolutePath(folder), targetDir));

            var secret = Path.Combine(targetDir.Value, ".secret_folder", ".secret_file.txt");
            Assert.True(Directory.Exists(Path.Combine(targetDir.Value, ".secret_folder")));
            Assert.True(File.Exists(secret));
            Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(FixtureFolder(), ".secret_folder", ".secret_file.txt")),
                await File.ReadAllBytesAsync(secret));
        }, FixtureFolder());
    }

    [Fact]
    public async Task TestCopySingleFile()
    {
        using var tempDirScope = new TempDirScope();
        var file = Path.Combine(FixtureFolder(), "pack.mcmeta");
        var target = Path.Combine(tempDirScope.Path, "pack.mcmeta");
        Assert.True(FileOps.Copy(new AbsolutePath(file), new AbsolutePath(target)));
        Assert.Equal(await File.ReadAllBytesAsync(file), await File.ReadAllBytesAsync(target));
    }

    // ------------------------------------------------------- write/read/move/delete

    [Fact]
    public async Task WriteRead_RoundTrip_AtomicLayout()
    {
        using var tempDirScope = new TempDirScope();
        // nested folders are created by Write (ensureExists)
        var path = Path.Combine(tempDirScope.Path, "nested", "file.bin");
        var data = new byte[] { 1, 2, 3, 0xFF, 0, 4 };
        FileOps.Write(new AbsolutePath(path), data);
        Assert.Equal(data, await File.ReadAllBytesAsync(path));

        // overwriting leaves no temp files behind
        FileOps.Write(new AbsolutePath(path), new byte[] { 9 });
        Assert.Equal(new byte[] { 9 }, await File.ReadAllBytesAsync(path));
        Assert.Equal(["file.bin"], Directory.GetFiles(Path.Combine(tempDirScope.Path, "nested"))
            .Select(Path.GetFileName).OrderBy(n => n));
    }

    [Fact]
    public async Task Move_And_DeletePath()
    {
        using var tempDirScope = new TempDirScope();
        var srcFile = Path.Combine(tempDirScope.Path, "a.txt");
        await File.WriteAllTextAsync(srcFile, "hello");
        var dstFile = Path.Combine(tempDirScope.Path, "sub", "b.txt");
        Assert.True(FileOps.Move(new AbsolutePath(srcFile), new AbsolutePath(dstFile)));
        Assert.False(File.Exists(srcFile));
        Assert.Equal("hello", await File.ReadAllTextAsync(dstFile));

        var srcDir = Path.Combine(tempDirScope.Path, "folder");
        Directory.CreateDirectory(Path.Combine(srcDir, "inner"));
        await File.WriteAllTextAsync(Path.Combine(srcDir, "inner", "c.txt"), "x");
        var dstDir = Path.Combine(tempDirScope.Path, "folder2");
        Assert.True(FileOps.Move(new AbsolutePath(srcDir), new AbsolutePath(dstDir)));
        Assert.False(Directory.Exists(srcDir));
        Assert.True(File.Exists(Path.Combine(dstDir, "inner", "c.txt")));

        Assert.True(FileOps.DeletePath(new AbsolutePath(dstDir)));
        Assert.False(Directory.Exists(dstDir));

        // deleting a missing path succeeds (fs::remove_all semantics)
        Assert.True(FileOps.DeletePath(new AbsolutePath(Path.Combine(tempDirScope.Path, "missing"))));
    }

    [Fact]
    public async Task UpdateTimestamp_BumpsModifiedTime()
    {
        using var tempDirScope = new TempDirScope();
        var path = Path.Combine(tempDirScope.Path, "stamp.txt");
        await File.WriteAllTextAsync(path, "x");
        File.SetLastWriteTime(path, DateTime.Now.AddDays(-10));
        Assert.True(FileOps.UpdateTimestamp(new AbsolutePath(path)));
        Assert.True((DateTime.Now - File.GetLastWriteTime(path)).Duration() < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task RemoveFiles_Removes_All_Existing()
    {
        using var tempDirScope = new TempDirScope();
        var f1 = Path.Combine(tempDirScope.Path, "1.txt");
        var f2 = Path.Combine(tempDirScope.Path, "2.txt");
        await File.WriteAllTextAsync(f1, "1");
        await File.WriteAllTextAsync(f2, "2");
        Assert.True(FileOps.RemoveFiles([f1, f2]));
        Assert.False(File.Exists(f1));
        Assert.False(File.Exists(f2));
    }

    /// <summary>QTemporaryDir replacement with auto-remove.</summary>
    private sealed class TempDirScope : IDisposable
    {
        public string Path { get; } = TempDir();

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException) { }
        }
    }
}
