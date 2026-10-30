// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// End-to-end tests for the Java download tasks against a local HTTP server (TestHttpServer
// pattern from NetRequestTests): archive installs (zip + a hand-built ustar tar.gz), manifest
// installs (files/directories/symlinks with sha1 validation) and the macOS SymlinkTask.

using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Java;
using FlintLauncher.Core.Java.Download;
using TruePath;
using Xunit;

namespace FlintLauncher.Tests;

public class JavaDownloadTests : IDisposable
{
    private const int Mode0755 = 0b111101101; // 0o755
    private const int Mode0644 = 0b110100100; // 0o644

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"flint-java-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException) { }
    }

    private string TempPath(string name) => Path.Combine(_dir, name);

    private static string Sha1(byte[] data) => Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();

    /// <summary>Runs a download and returns the failure message, asserting it succeeded when
    /// <paramref name="expectSuccess"/> is set (the reason is then empty).</summary>
    private static async Task<string> RunDownloadAsync(Task download, bool expectSuccess)
    {
        if (expectSuccess)
        {
            await download;
            return "";
        }

        var failure = await Record.ExceptionAsync(() => download);
        Assert.NotNull(failure);
        return failure.Message;
    }

    // ------------------------------------------------------------------ archive (zip)

    [Fact]
    public async Task ArchiveDownloadTask_ExtractsZip_StrippingTopLevelFolder()
    {
        Directory.CreateDirectory(_dir);
        var zipPath = TempPath("jdk.zip");
        // synthetic "jdk" zip built with the project's own MMCZip; the tree mirrors a macOS
        // JDK archive whose every path sits under one top-level folder
        var sourceDir = TempPath("jdk-src/jdk-17.0.9");
        Directory.CreateDirectory(Path.Combine(sourceDir, "Contents/Home/bin"));
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "Contents/Home/bin/java"), "java-binary");
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "release"), "JAVA_VERSION=\"17.0.9\"");
        Assert.True(MmcZip.CompressDir(new AbsolutePath(zipPath), new AbsolutePath(TempPath("jdk-src"))));

        using var server = new TestHttpServer(ctx =>
        {
            ctx.Response.StatusCode = 200;
            var body = File.ReadAllBytes(zipPath);
            ctx.Response.ContentLength64 = body.Length;
            ctx.Response.OutputStream.Write(body);
        });

        var finalPath = TempPath("install-zip");
        var task = ArchiveDownloadTask.DownloadAsync(new Uri($"{server.Url}/jdk-17.0.9.zip"), new AbsolutePath(finalPath),
            TestNet.Http, CancellationToken.None);
        await task;
        Assert.Equal("java-binary", await File.ReadAllTextAsync(Path.Combine(finalPath, "Contents/Home/bin/java")));
        Assert.Equal("JAVA_VERSION=\"17.0.9\"", await File.ReadAllTextAsync(Path.Combine(finalPath, "release")));
    }

    [Fact]
    public async Task ArchiveDownloadTask_FailsOnChecksumMismatch()
    {
        Directory.CreateDirectory(_dir);
        var body = "not-a-jdk-at-all"u8.ToArray();
        using var server = new TestHttpServer(ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentLength64 = body.Length;
            ctx.Response.OutputStream.Write(body);
        });

        var finalPath = TempPath("install-bad-sha");
        var task = ArchiveDownloadTask.DownloadAsync(new Uri($"{server.Url}/jdk.zip"), new AbsolutePath(finalPath),
            TestNet.Http, CancellationToken.None, checksumType: "sha1", checksumHash: new string('0', 40));
        await Assert.ThrowsAnyAsync<Exception>(() => task);
        Assert.Empty(Directory.Exists(finalPath) ? Directory.GetFileSystemEntries(finalPath) : []);
    }

    // -------------------------------------------------------------- archive (tar.gz)

    [Fact]
    public async Task ArchiveDownloadTask_ExtractsTarGz_PreservingLayoutAndExecBits()
    {
        Directory.CreateDirectory(_dir);
        var tarGz = BuildTarGz(
            ("jdk-17.0.9/", null, '5', Mode0755, ""),
            ("jdk-17.0.9/Contents/Home/bin/java", "java-binary-bytes"u8.ToArray(), '0', Mode0755, ""),
            ("jdk-17.0.9/Contents/Home/release", "JAVA_VERSION=\"17.0.9\""u8.ToArray(), '0', Mode0644, ""),
            ("jdk-17.0.9/Contents/Home/lib/", null, '5', Mode0755, ""),
            ("jdk-17.0.9/Contents/Home/bin/javac", null, '2', Mode0755, "java"));
        var tarPath = TempPath("jdk.tar.gz");
        await File.WriteAllBytesAsync(tarPath, tarGz);

        using var server = new TestHttpServer(ctx =>
        {
            ctx.Response.StatusCode = 200;
            var body = File.ReadAllBytes(tarPath);
            ctx.Response.ContentLength64 = body.Length;
            ctx.Response.OutputStream.Write(body);
        });

        var finalPath = TempPath("install-tgz");
        // the task runs a listing pass first (C++: collectFiles) to find the strip folder
        var names = TarArchive.ListTarGzFile(tarPath, out var listError);
        Assert.True(names is not null, $"ListTarGzFile failed: {listError}");
        Assert.Equal("jdk-17.0.9/", names[0]);
        var task = ArchiveDownloadTask.DownloadAsync(new Uri($"{server.Url}/jdk-17.0.9.tar.gz"), new AbsolutePath(finalPath),
            TestNet.Http, CancellationToken.None);
        await task;
        Assert.Equal("java-binary-bytes", await File.ReadAllTextAsync(Path.Combine(finalPath, "Contents/Home/bin/java")));
        Assert.Equal("JAVA_VERSION=\"17.0.9\"", await File.ReadAllTextAsync(Path.Combine(finalPath, "Contents/Home/release")));
        Assert.True(Directory.Exists(Path.Combine(finalPath, "Contents/Home/lib")));
        // exec bit from the tar mode
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(Path.Combine(finalPath, "Contents/Home/bin/java"));
            Assert.True(mode.HasFlag(UnixFileMode.UserExecute));
            var nonExec = File.GetUnixFileMode(Path.Combine(finalPath, "Contents/Home/release"));
            Assert.False(nonExec.HasFlag(UnixFileMode.UserExecute));
            // the symlink entry became a symlink pointing at the raw target
            var link = new FileInfo(Path.Combine(finalPath, "Contents/Home/bin/javac"));
            Assert.True(link.LinkTarget is not null, "javac must be a symlink");
            Assert.Equal("java", link.LinkTarget);
        }
    }

    // ----------------------------------------------------------------- manifest

    [Fact]
    public async Task ManifestDownloadTask_InstallsFilesLinksAndDirectories()
    {
        Directory.CreateDirectory(_dir);
        var javaBytes = "#!/bin/sh\necho java"u8.ToArray();
        var releaseBytes = "release-info"u8.ToArray();
        // late-bound payload holder: the manifest can only be built once the server (and its
        // URL) exist, so the handler reads through the holder instead of a captured variable
        // the test reassigns (access-to-modified-closure)
        var manifestJson = new string?[1];
        using var server = new TestHttpServer(ctx =>
        {
            var requestUrl = ctx.Request.Url ?? throw new InvalidOperationException("the HTTP request has no URL");
            byte[] body;
            switch (requestUrl.AbsolutePath)
            {
                case "/manifest":
                    body = manifestJson[0] is { } json ? Encoding.UTF8.GetBytes(json) : [];
                    break;
                case "/files/java":
                    body = javaBytes;
                    break;
                case "/files/release":
                    body = releaseBytes;
                    break;
                default:
                    ctx.Response.StatusCode = 404;
                    return;
            }

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentLength64 = body.Length;
            ctx.Response.OutputStream.Write(body);
        });
        // the manifest can only be built once the server (and its URL) exist; the handler above
        // is not invoked before the task starts. NOTE: braces are kept apart so no "}}" run
        // collides with the $$ interpolation delimiters.
        manifestJson[0] = $$"""
                                                {
                                                  "files": {
                                                    "bin/": {"type": "directory"},
                                                    "bin/java": {
                                                      "type": "file",
                                                      "executable": true,
                                                      "downloads": {
                                                        "raw": {
                                                          "url": "{{server.Url}}/files/java",
                                                          "sha1": "{{Sha1(javaBytes)}}"
                                                        }
                                                      }
                                                    },
                                                    "bin/javac": {"type": "link", "target": "java"},
                                                    "lib/": {"type": "directory"},
                                                    "release": {
                                                      "type": "file",
                                                      "executable": false,
                                                      "downloads": {
                                                        "raw": {
                                                          "url": "{{server.Url}}/files/release",
                                                          "sha1": ""
                                                        }
                                                      }
                                                    }
                                                  }
                                                }
                                                """;

        var finalPath = TempPath("install-manifest");
        var task = ManifestDownloadTask.DownloadAsync(new Uri($"{server.Url}/manifest"), new AbsolutePath(finalPath),
            TestNet.Http, CancellationToken.None);
        await task;
        Assert.Equal(Encoding.UTF8.GetString(javaBytes), await File.ReadAllTextAsync(Path.Combine(finalPath, "bin/java")));
        Assert.Equal("release-info", await File.ReadAllTextAsync(Path.Combine(finalPath, "release")));
        Assert.True(Directory.Exists(Path.Combine(finalPath, "bin")));
        Assert.True(Directory.Exists(Path.Combine(finalPath, "lib")));
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(Path.Combine(finalPath, "bin/java"));
            Assert.True(mode.HasFlag(UnixFileMode.UserExecute));
            var link = new FileInfo(Path.Combine(finalPath, "bin/javac"));
            Assert.True(link.LinkTarget is not null, "javac must be a symlink");
            Assert.Equal("java", link.LinkTarget);
        }
    }

    [Fact]
    public async Task ManifestDownloadTask_FailsWhenManifestChecksumMismatch()
    {
        Directory.CreateDirectory(_dir);
        using var server = new TestHttpServer(ctx =>
        {
            ctx.Response.StatusCode = 200;
            var body = "{\"files\":{}}"u8.ToArray();
            ctx.Response.ContentLength64 = body.Length;
            ctx.Response.OutputStream.Write(body);
        });

        var finalPath = TempPath("install-manifest-bad");
        var task = ManifestDownloadTask.DownloadAsync(new Uri($"{server.Url}/manifest"), new AbsolutePath(finalPath),
            TestNet.Http, CancellationToken.None, checksumType: "sha1", checksumHash: new string('1', 40));
        await Assert.ThrowsAnyAsync<Exception>(() => task);
    }

    [Fact]
    public async Task ManifestDownloadTask_FailsWhenFileChecksumMismatch()
    {
        Directory.CreateDirectory(_dir);
        // late-bound payload holder (see the checksum-ok test for the rationale)
        var manifestJson = new string?[1];
        using var server = new TestHttpServer(ctx =>
        {
            ctx.Response.StatusCode = 200;
            var requestUrl = ctx.Request.Url ?? throw new InvalidOperationException("the HTTP request has no URL");
            var body = requestUrl.AbsolutePath == "/manifest"
                ? (manifestJson[0] is { } json ? Encoding.UTF8.GetBytes(json) : [])
                : "wrong-bytes"u8.ToArray();
            ctx.Response.ContentLength64 = body.Length;
            ctx.Response.OutputStream.Write(body);
        });
        manifestJson[0] = $$"""
                         {
                           "files": {
                             "bin/java": {
                               "type": "file",
                               "executable": true,
                               "downloads": {
                                 "raw": {
                                   "url": "{{server.Url}}/files/java",
                                   "sha1": "{{new string('9', 40)}}"
                                 }
                               }
                             }
                           }
                         }
                         """;

        var finalPath = TempPath("install-manifest-badfile");
        var task = ManifestDownloadTask.DownloadAsync(new Uri($"{server.Url}/manifest"), new AbsolutePath(finalPath),
            TestNet.Http, CancellationToken.None);
        await Assert.ThrowsAnyAsync<Exception>(() => task);
    }

    // ---------------------------------------------------------------- symlink task

    [Fact]
    public async Task SymlinkTask_LinksContentsHomeIntoTarget()
    {
        var target = TempPath("symlink-target");
        var home = Path.Combine(target, "Contents", "Home");
        Directory.CreateDirectory(Path.Combine(home, "bin"));
        Directory.CreateDirectory(Path.Combine(home, "lib"));
        await File.WriteAllTextAsync(Path.Combine(home, "bin/java"), "java-binary");
        await File.WriteAllTextAsync(Path.Combine(home, "lib/native.dylib"), "dylib");
        await File.WriteAllTextAsync(Path.Combine(home, "release"), "JAVA_VERSION=17");

        SymlinkTask.Run(new AbsolutePath(target));
        if (OperatingSystem.IsWindows())
        {
            return; // symlink creation needs privileges on Windows; the C++ path is macOS-gated too
        }

        Assert.True(Directory.Exists(Path.Combine(target, "bin")));
        Assert.Equal("java-binary", await File.ReadAllTextAsync(Path.Combine(target, "bin", "java")));
        Assert.Equal("dylib", await File.ReadAllTextAsync(Path.Combine(target, "lib", "native.dylib")));
        Assert.Equal("JAVA_VERSION=17", await File.ReadAllTextAsync(Path.Combine(target, "release")));
    }

    [Fact]
    public async Task SymlinkTask_SucceedsWhenBinPathAlreadyThere_AndFailsWithoutContentsHome()
    {
        var target = TempPath("symlink-flat");
        Directory.CreateDirectory(Path.Combine(target, "bin"));
        await File.WriteAllTextAsync(Path.Combine(target, "bin/java"), "java-binary");

        SymlinkTask.Run(new AbsolutePath(target));

        var emptyDir = TempPath("symlink-empty");
        Directory.CreateDirectory(emptyDir);
        var failure = Assert.ThrowsAny<Exception>(() => SymlinkTask.Run(new AbsolutePath(emptyDir)));
        Assert.Contains("Failed to find Java binary path", failure.Message);
    }

    // ------------------------------------------------------------------- tar edges

    [Fact]
    public void TarArchive_RefusesEntriesEscapingTheTargetDirectory()
    {
        Directory.CreateDirectory(_dir);
        var tarPath = TempPath("slip.tar.gz");
        File.WriteAllBytes(tarPath, BuildTarGz(
            ("../../escaped.txt", "pwned"u8.ToArray(), '0', Mode0644, "")));

        var target = TempPath("slip-out");
        Directory.CreateDirectory(target);
        var extracted = TarArchive.ExtractTarGzFile(tarPath, target, null, out var error);

        Assert.Null(extracted);
        Assert.Contains("outside of the target path", error);
        Assert.False(File.Exists(Path.Combine(_dir, "escaped.txt")));
        // nothing survives a refused pass, not even the entries extracted before the escape
        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    [Fact]
    public void TarArchive_StripsOnlyTheGivenPrefix_AndSkipsTheRest()
    {
        Directory.CreateDirectory(_dir);
        var tarPath = TempPath("mixed.tar.gz");
        File.WriteAllBytes(tarPath, BuildTarGz(
            ("keep/a.txt", "A"u8.ToArray(), '0', Mode0644, ""),
            ("drop/b.txt", "B"u8.ToArray(), '0', Mode0644, "")));

        var target = TempPath("mixed-out");
        var extracted = TarArchive.ExtractTarGzFile(tarPath, target, "keep/", out var error);

        Assert.True(extracted is not null, error);
        Assert.Equal("A", File.ReadAllText(Path.Combine(target, "a.txt")));
        Assert.False(File.Exists(Path.Combine(target, "b.txt")));
        Assert.False(Directory.Exists(Path.Combine(target, "drop")));
    }

    [Fact]
    public void TarArchive_ListingReturnsEveryEntryName()
    {
        Directory.CreateDirectory(_dir);
        var tarPath = TempPath("list.tar.gz");
        File.WriteAllBytes(tarPath, BuildTarGz(
            ("jdk-17.0.9/", null, '5', Mode0755, ""),
            ("jdk-17.0.9/bin/java", "x"u8.ToArray(), '0', Mode0755, "")));

        var names = TarArchive.ListTarGzFile(tarPath, out var error);

        Assert.True(names is not null, error);
        Assert.Equal(["jdk-17.0.9/", "jdk-17.0.9/bin/java"], names);
    }

    [Fact]
    public void TarArchive_ReportsUnreadableArchivesAsErrors()
    {
        Directory.CreateDirectory(_dir);
        var notAnArchive = TempPath("not-an-archive.tar.gz");
        File.WriteAllText(notAnArchive, "this is not gzip data");

        Assert.Null(TarArchive.ListTarGzFile(notAnArchive, out var listError));
        Assert.NotEmpty(listError);

        Assert.Null(TarArchive.ExtractTarGzFile(notAnArchive, TempPath("bad-out"), null, out var extractError));
        Assert.NotEmpty(extractError);
    }

    // ------------------------------------------------------------------- helpers

    private static byte[] BuildTarGz(params (string Name, byte[]? Data, char TypeFlag, int Mode, string LinkTarget)[] entries)
    {
        using var tar = new MemoryStream();
        foreach (var (name, data, typeFlag, mode, linkTarget) in entries)
        {
            tar.Write(BuildTarHeader(name, typeFlag, mode, data?.Length ?? 0, linkTarget));
            if (data is { Length: > 0 })
            {
                tar.Write(data);
                var padding = (512 - data.Length % 512) % 512;
                if (padding > 0)
                {
                    tar.Write(new byte[padding]);
                }
            }
        }

        tar.Write(new byte[1024]); // two zero blocks end the archive
        using var gz = new MemoryStream();
        tar.Position = 0;
        using (var compressor = new GZipStream(gz, CompressionLevel.Optimal, leaveOpen: true))
        {
            tar.CopyTo(compressor);
        }

        return gz.ToArray();
    }

    private static byte[] BuildTarHeader(string name, char typeFlag, int mode, long size, string linkTarget)
    {
        var header = new byte[512];
        Encoding.ASCII.GetBytes(name).CopyTo(header, 0);
        WriteOctalField(header, 100, 8, mode);
        WriteOctalField(header, 108, 8, 0); // uid
        WriteOctalField(header, 116, 8, 0); // gid
        WriteOctalField(header, 124, 12, size);
        WriteOctalField(header, 136, 12, 0); // mtime
        for (var i = 148; i < 156; i++)
        {
            header[i] = (byte)' '; // checksum placeholder
        }

        header[156] = (byte)typeFlag;
        Encoding.ASCII.GetBytes(linkTarget).CopyTo(header, 157);
        Encoding.ASCII.GetBytes("ustar\0").CopyTo(header, 257);
        Encoding.ASCII.GetBytes("00").CopyTo(header, 263);
        // classic checksum layout: 6 octal digits, NUL, space (the reader ignores it, but keep
        // the header realistic)
        var raw = Convert.ToString(header.Sum(b => (long)b), 8).PadLeft(6, '0');
        Encoding.ASCII.GetBytes(raw).CopyTo(header, 148);
        header[154] = 0;
        header[155] = (byte)' ';
        return header;
    }

    private static void WriteOctalField(byte[] header, int offset, int count, long value)
    {
        // (count - 1) octal digits + NUL terminator
        var text = Convert.ToString(value, 8).PadLeft(count - 1, '0');
        Encoding.ASCII.GetBytes(text).CopyTo(header, offset);
        header[offset + count - 1] = 0;
    }

    private sealed class TestHttpServer : IDisposable
    {
        private readonly HttpListener _listener;

        public TestHttpServer(Action<HttpListenerContext> handler)
        {
            // TestPorts owns the port lease AND the rebind-retry (HttpListener cannot adopt
            // the lease socket, so losing the takeover window retries on a fresh port)
            (_listener, Url) = PortAllocator.StartHttpListener();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    try
                    {
                        var context = await _listener.GetContextAsync();
                        try
                        {
                            handler(context);
                        }
                        finally
                        {
                            context.Response.Close();
                        }
                    }
                    catch (HttpListenerException)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                }
            });
        }

        public string Url { get; }

        public void Dispose()
        {
            try
            {
                _listener.Close();
            }
            catch (ObjectDisposedException) { }
        }
    }
}
