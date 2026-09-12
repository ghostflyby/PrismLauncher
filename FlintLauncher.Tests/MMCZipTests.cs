// SPDX-License-Identifier: GPL-3.0-only
// Focused round-trip tests for the port of launcher/MMCZip.{h,cpp}.
using System.IO.Compression;
using FlintLauncher.Core.IO;
using Xunit;

namespace FlintLauncher.Tests;

public class MMCZipTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "flint-zip-" + Guid.NewGuid().ToString("N"));

    public MMCZipTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// source/
    ///   a.txt
    ///   sub/b.txt
    ///   sub/empty/
    /// </summary>
    private string MakeSourceDir()
    {
        var src = Path.Combine(_tempDir, "src");
        Directory.CreateDirectory(Path.Combine(src, "sub", "empty"));
        File.WriteAllText(Path.Combine(src, "a.txt"), "alpha");
        File.WriteAllText(Path.Combine(src, "sub", "b.txt"), "beta");
        return src;
    }

    [Fact]
    public void CompressDir_ExtractDir_RoundTrip()
    {
        var src = MakeSourceDir();
        var zipPath = Path.Combine(_tempDir, "out.zip");
        Assert.True(MMCZip.CompressDir(zipPath, src));
        Assert.True(File.Exists(zipPath));

        var dst = Path.Combine(_tempDir, "dst");
        var extracted = MMCZip.ExtractDir(zipPath, dst);
        Assert.NotNull(extracted);
        Assert.Contains(Path.Combine(dst, "a.txt"), extracted);
        Assert.Equal("alpha", File.ReadAllText(Path.Combine(dst, "a.txt")));
        Assert.Equal("beta", File.ReadAllText(Path.Combine(dst, "sub", "b.txt")));
        // directory entry preserved for the empty folder
        Assert.True(Directory.Exists(Path.Combine(dst, "sub", "empty")));
    }

    [Fact]
    public void ExtractDir_Subdirectory_Only()
    {
        var src = MakeSourceDir();
        var zipPath = Path.Combine(_tempDir, "out.zip");
        Assert.True(MMCZip.CompressDir(zipPath, src));

        var dst = Path.Combine(_tempDir, "dst-sub");
        var extracted = MMCZip.ExtractDir(zipPath, "sub/", dst);
        Assert.NotNull(extracted);
        Assert.False(File.Exists(Path.Combine(dst, "a.txt")));
        Assert.Equal("beta", File.ReadAllText(Path.Combine(dst, "b.txt")));
    }

    [Fact]
    public void ExtractFile_Single_Entry()
    {
        var src = MakeSourceDir();
        var zipPath = Path.Combine(_tempDir, "out.zip");
        Assert.True(MMCZip.CompressDir(zipPath, src));

        var target = Path.Combine(_tempDir, "only-a.txt");
        Assert.True(MMCZip.ExtractFile(zipPath, "a.txt", target));
        Assert.Equal("alpha", File.ReadAllText(target));
        Assert.False(MMCZip.ExtractFile(zipPath, "missing.txt", target));
    }

    [Fact]
    public void MergeZipFiles_Skips_Duplicates_And_Filters()
    {
        var zip1 = Path.Combine(_tempDir, "1.zip");
        var zip2 = Path.Combine(_tempDir, "2.zip");
        using (var s = File.Create(zip1))
        using (var z = new ZipArchive(s, ZipArchiveMode.Create))
        {
            var e = z.CreateEntry("common.txt");
            using (var w = new StreamWriter(e.Open()))
            {
                w.Write("one");
            }
            z.CreateEntry("only1.txt");
        }
        using (var s = File.Create(zip2))
        using (var z = new ZipArchive(s, ZipArchiveMode.Create))
        {
            var e = z.CreateEntry("common.txt");
            using (var w = new StreamWriter(e.Open()))
            {
                w.Write("two");
            }
            z.CreateEntry("META-INF/x");
        }

        var mergedPath = Path.Combine(_tempDir, "merged.zip");
        using (var s = File.Create(mergedPath))
        using (var into = new ZipArchive(s, ZipArchiveMode.Create))
        {
            var contained = new HashSet<string>();
            Assert.True(MMCZip.MergeZipFiles(into, zip2, contained, filter: key => !key.Contains("META-INF")));
            Assert.True(MMCZip.MergeZipFiles(into, zip1, contained));
        }

        using var merged = ZipFile.OpenRead(mergedPath);
        Assert.Equal(["common.txt", "only1.txt"], merged.Entries.Select(e => e.FullName).OrderBy(n => n));
        // the first writer wins for duplicates
        using var reader = merged.Entries.First(e => e.FullName == "common.txt").Open();
        using var text = new StreamReader(reader);
        Assert.Equal("two", text.ReadToEnd());
    }

    [Fact]
    public void ExtractDir_Rejects_ZipSlip()
    {
        var zipPath = Path.Combine(_tempDir, "evil.zip");
        using (var s = File.Create(zipPath))
        using (var z = new ZipArchive(s, ZipArchiveMode.Create))
        {
            var e = z.CreateEntry("../evil.txt");
            using (var w = new StreamWriter(e.Open()))
            {
                w.Write("nope");
            }
        }

        var dst = Path.Combine(_tempDir, "dst-slip");
        var extracted = MMCZip.ExtractDir(zipPath, dst);
        Assert.Null(extracted);
        Assert.False(File.Exists(Path.Combine(_tempDir, "evil.txt")));
    }

    [Fact]
    public void Empty_Zip_Yields_Nothing()
    {
        var zipPath = Path.Combine(_tempDir, "empty.zip");
        using (var s = File.Create(zipPath))
        using (var z = new ZipArchive(s, ZipArchiveMode.Create))
        {
            // zero entries; the resulting EOCD-only file is 22 bytes
        }
        Assert.Equal(22, new FileInfo(zipPath).Length);
        var extracted = MMCZip.ExtractDir(zipPath, Path.Combine(_tempDir, "dst-empty"));
        Assert.NotNull(extracted);
        Assert.Empty(extracted);
    }
}
