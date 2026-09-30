// SPDX-License-Identifier: GPL-3.0-only
// End-to-end tests for the Java download tasks against a local HTTP server (TestHttpServer
// pattern from NetStackTests): archive installs (zip + a hand-built ustar tar.gz), manifest
// installs (files/directories/symlinks with sha1 validation) and the macOS SymlinkTask.
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Flint.Tests;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Java;
using FlintLauncher.Core.Tasks;
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
        catch (DirectoryNotFoundException)
        {
        }
    }

    private string TempPath(string name) => Path.Combine(_dir, name);

    private static string Sha1(byte[] data) => Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();

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
        File.WriteAllText(Path.Combine(sourceDir, "Contents/Home/bin/java"), "java-binary");
        File.WriteAllText(Path.Combine(sourceDir, "release"), "JAVA_VERSION=\"17.0.9\"");
        Assert.True(MMCZip.CompressDir(zipPath, TempPath("jdk-src")));

        using var server = new TestHttpServer(ctx =>
        {
            ctx.Response.StatusCode = 200;
            var body = File.ReadAllBytes(zipPath);
            ctx.Response.ContentLength64 = body.Length;
            ctx.Response.OutputStream.Write(body);
        });

        var finalPath = TempPath("install-zip");
        var task = new ArchiveDownloadTask(new Uri($"{server.Url}/jdk-17.0.9.zip"), finalPath);

        await task.StartAsync();

        Assert.Equal(TaskState.Succeeded, task.State);
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
        var task = new ArchiveDownloadTask(new Uri($"{server.Url}/jdk.zip"), finalPath,
            "sha1", new string('0', 40));

        await task.StartAsync();

        Assert.Equal(TaskState.Failed, task.State);
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
        Assert.Equal("jdk-17.0.9/", names![0]);
        var task = new ArchiveDownloadTask(new Uri($"{server.Url}/jdk-17.0.9.tar.gz"), finalPath);

        await task.StartAsync();

        Assert.True(task.State == TaskState.Succeeded, $"task failed: {task.FailReason}");
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
        byte[] manifestBody = [];
        using var server = new TestHttpServer(ctx =>
        {
            byte[] body;
            switch (ctx.Request.Url!.AbsolutePath)
            {
                case "/manifest":
                    body = manifestBody;
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
        manifestBody = Encoding.UTF8.GetBytes($$"""
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
            """);

        var finalPath = TempPath("install-manifest");
        var task = new ManifestDownloadTask(new Uri($"{server.Url}/manifest"), finalPath);

        await task.StartAsync();

        Assert.Equal(TaskState.Succeeded, task.State);
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
        var task = new ManifestDownloadTask(new Uri($"{server.Url}/manifest"), finalPath,
            "sha1", new string('1', 40));

        await task.StartAsync();

        Assert.Equal(TaskState.Failed, task.State);
    }

    [Fact]
    public async Task ManifestDownloadTask_FailsWhenFileChecksumMismatch()
    {
        Directory.CreateDirectory(_dir);
        byte[] manifestBody = [];
        using var server = new TestHttpServer(ctx =>
        {
            ctx.Response.StatusCode = 200;
            var body = ctx.Request.Url!.AbsolutePath == "/manifest" ? manifestBody : "wrong-bytes"u8.ToArray();
            ctx.Response.ContentLength64 = body.Length;
            ctx.Response.OutputStream.Write(body);
        });
        manifestBody = Encoding.UTF8.GetBytes($$"""
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
            """);

        var finalPath = TempPath("install-manifest-badfile");
        var task = new ManifestDownloadTask(new Uri($"{server.Url}/manifest"), finalPath);

        await task.StartAsync();

        Assert.Equal(TaskState.Failed, task.State);
    }

    // ---------------------------------------------------------------- symlink task

    [Fact]
    public async Task SymlinkTask_LinksContentsHomeIntoTarget()
    {
        var target = TempPath("symlink-target");
        var home = Path.Combine(target, "Contents", "Home");
        Directory.CreateDirectory(Path.Combine(home, "bin"));
        Directory.CreateDirectory(Path.Combine(home, "lib"));
        File.WriteAllText(Path.Combine(home, "bin/java"), "java-binary");
        File.WriteAllText(Path.Combine(home, "lib/native.dylib"), "dylib");
        File.WriteAllText(Path.Combine(home, "release"), "JAVA_VERSION=17");

        var task = new SymlinkTask(target);
        await task.StartAsync();

        Assert.Equal(TaskState.Succeeded, task.State);
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
        File.WriteAllText(Path.Combine(target, "bin/java"), "java-binary");

        var alreadyThere = new SymlinkTask(target);
        await alreadyThere.StartAsync();
        Assert.Equal(TaskState.Succeeded, alreadyThere.State);

        var emptyDir = TempPath("symlink-empty");
        Directory.CreateDirectory(emptyDir);
        var empty = new SymlinkTask(emptyDir);
        await empty.StartAsync();
        Assert.Equal(TaskState.Failed, empty.State);
        Assert.Contains("Failed to find Java binary path", empty.FailReason);
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
        GZip.Compress(tar.ToArray(), out var gz);
        return gz;
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
        private readonly HttpListener _listener = new();

        public TestHttpServer(Action<HttpListenerContext> handler)
        {
            // hold a reserved port until the instant this listener takes it over
            using var lease = PortAllocator.Reserve();
            var port = lease.Port;
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            lease.Dispose();
            _listener.Start();
            Url = $"http://127.0.0.1:{port}";
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
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
