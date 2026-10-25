// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Tests for the Net stack: sinks/validators offline, HttpMetaCache persistence (Prism-compatible
// JSON index), and live requests against a local HTTP server (304 revalidation, checksums, retry).

using System.Net;
using System.Security.Cryptography;
using System.Text;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Tasks;
using TruePath;
using TruePath.SystemIo;
using Xunit;

namespace FlintLauncher.Tests;

public class NetStackTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"flint-net-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException) { }
    }

    private string TempPath(string name) => Path.Combine(_dir, name);

    // ---------------------------------------------------------------- sinks/validators

    [Fact]
    public void ChecksumValidator_Matches_And_Mismatches()
    {
        var data = "hello world"u8.ToArray();
        var expected = Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();

        var ok = ChecksumValidator.FromHex(HashAlgorithmName.SHA1, expected);
        var spec = new NetRequestSpec();
        Assert.True(ok.Init(spec));
        Assert.True(ok.Write(data));
        Assert.True(ok.Validate(new NetResponseInfo { StatusCode = 200 }));

        var bad = ChecksumValidator.FromHex(HashAlgorithmName.SHA1, new string('0', 40));
        Assert.True(bad.Init(spec));
        Assert.True(bad.Write(data));
        Assert.False(bad.Validate(new NetResponseInfo { StatusCode = 200 }));
    }

    [Fact]
    public void ByteArraySink_StoresData()
    {
        var sink = new ByteArraySink();
        var spec = new NetRequestSpec();
        Assert.True(sink.Init(spec));
        Assert.True(sink.Write("chunk1 "u8.ToArray()));
        Assert.True(sink.Write("chunk2"u8.ToArray()));
        Assert.Equal("chunk1 chunk2", sink.DataAsString);
        Assert.True(sink.HasLocalData());
    }

    // ---------------------------------------------------------------- HttpMetaCache

    [Fact]
    public void MetaCache_SaveLoad_RoundTrip()
    {
        var indexPath = TempPath("metacache");
        var basePath = TempPath("cache-root");
        Directory.CreateDirectory(basePath);

        long timestamp;
        {
            using var cache = new HttpMetaCache(new AbsolutePath(indexPath));
            cache.AddBase("META", new AbsolutePath(basePath));

            var file = Path.Combine(basePath, "net.minecraft/index.json");
            var fileDir = Path.GetDirectoryName(file);
            Assert.NotNull(fileDir);
            Directory.CreateDirectory(fileDir);
            File.WriteAllText(file, "{}");


            var entry = cache.ResolveEntry("META", "net.minecraft/index.json");
            Assert.True(entry.IsStale);

            entry.IsStale = false;
            entry.Etag = "\"abc123\"";
            entry.Md5Sum = Convert.ToHexString(MD5.HashData("{}"u8.ToArray())).ToLowerInvariant();
            // mirror ResolveEntry: the stored timestamp must equal the file's mtime or the
            // next resolve re-hashes (1ms UtcNow-vs-mtime rounding makes that flaky)
            timestamp = new DateTimeOffset(File.GetLastWriteTimeUtc(
                Path.Combine(basePath, "net.minecraft/index.json"))).ToUnixTimeMilliseconds();
            entry.LocalChangedTimestamp = timestamp;
            entry.RemoteChangedTimestamp = "Tue, 01 Jan 2030 00:00:00 GMT";
            entry.IsEternal = true;
            Assert.True(cache.UpdateEntry(entry));

            // a non-ASCII base rides the same write: QJsonDocument leaves unicode alone, so
            // the relaxed encoder must not turn it into \uXXXX escapes
            const string unicodeBase = "路径/é/日本語";
            cache.AddBase(unicodeBase, new AbsolutePath(basePath));
            var unicodeEntry = cache.ResolveEntry(unicodeBase, "net.minecraft/index.json");
            unicodeEntry.IsStale = false;
            unicodeEntry.IsEternal = true;
            Assert.True(cache.UpdateEntry(unicodeEntry));
            cache.SaveNow();
        }

        // the persisted JSON must look like Prism's metacache format
        var json = File.ReadAllText(indexPath);
        Assert.Contains("\"version\":\"1\"", json);
        Assert.Contains("\"base\":\"META\"", json);
        Assert.Contains("\"path\":\"net.minecraft/index.json\"", json);
        Assert.Contains("\"etag\":\"\\\"abc123\\\"\"", json);
        // unicode survives verbatim (the PrismLauncher-identical Qt escaping)
        Assert.Contains("\"base\":\"路径/é/日本語\"", json);
        Assert.DoesNotContain("\\u8DEF", json);

        using var cache2 = new HttpMetaCache(new AbsolutePath(indexPath));
        cache2.AddBase("META", new AbsolutePath(basePath));
        cache2.Load();

        var resolved = cache2.ResolveEntry("META", "net.minecraft/index.json", "\"abc123\"");
        Assert.False(resolved.IsStale);
        Assert.Equal("\"abc123\"", resolved.Etag);
        Assert.Equal(timestamp, resolved.LocalChangedTimestamp);
    }

    [Fact]
    public void MetaCache_DisownsOnEtagMismatch()
    {
        var indexPath = TempPath("metacache");
        var basePath = TempPath("cache-root2");
        Directory.CreateDirectory(basePath);
        var file = Path.Combine(basePath, "f.json");
        File.WriteAllText(file, "{}");

        using var cache = new HttpMetaCache(new AbsolutePath(indexPath));
        cache.AddBase("general", new AbsolutePath(basePath));
        var entry = cache.ResolveEntry("general", "f.json");
        entry.IsStale = false;
        entry.Etag = "\"old\"";
        entry.Md5Sum = Convert.ToHexString(MD5.HashData("{}"u8.ToArray())).ToLowerInvariant();
        entry.LocalChangedTimestamp = new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeMilliseconds();
        entry.IsEternal = true;
        Assert.True(cache.UpdateEntry(entry));

        var mismatched = cache.ResolveEntry("general", "f.json", "\"new\"");
        Assert.True(mismatched.IsStale);

        // C++ semantics: an etag mismatch DISOWNS the entry (removes it from the map), so even
        // a later resolve with the correct etag returns a fresh stale entry; recovery happens
        // through a re-download + UpdateEntry.
        var matched = cache.ResolveEntry("general", "f.json", "\"old\"");
        Assert.True(matched.IsStale);
    }

    [Fact]
    public void MetaCache_Load_SkipsUnreadableLedger()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // posix chmod-based negative case
        }

        var indexPath = TempPath("metacache-unreadable");
        var basePath = TempPath("cache-root3");
        Directory.CreateDirectory(basePath);
        var file = Path.Combine(basePath, "f.json");
        File.WriteAllText(file, "{}");

        // seed a valid ledger exactly like MetaCache_SaveLoad_RoundTrip
        using (var cache = new HttpMetaCache(new AbsolutePath(indexPath)))
        {
            cache.AddBase("general", new AbsolutePath(basePath));
            var entry = cache.ResolveEntry("general", "f.json");
            entry.IsStale = false;
            entry.Etag = "\"seed\"";
            entry.Md5Sum = Convert.ToHexString(MD5.HashData("{}"u8.ToArray())).ToLowerInvariant();
            entry.LocalChangedTimestamp = new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeMilliseconds();
            entry.IsEternal = true;
            Assert.True(cache.UpdateEntry(entry));
            cache.SaveNow();
        }

        // C++ Load() silently skips a ledger it cannot open and rebuilds from scratch: the
        // metacache is a true cache whose loss only costs re-downloads, so an unreadable
        // ledger file (here: chmod 000) must not make the host fail to start.
        File.SetUnixFileMode(indexPath, UnixFileMode.None);

        var cache2 = new HttpMetaCache(new AbsolutePath(indexPath));
        cache2.AddBase("general", new AbsolutePath(basePath));
        var ex = Record.Exception(cache2.Load);
        Assert.Null(ex);

        // the skipped ledger means a cold cache: the next resolve takes the rebuild path
        var resolved = cache2.ResolveEntry("general", "f.json");
        Assert.True(resolved.IsStale);

        // restore before Dispose(): SaveNow() rewrites the ledger, which needs write access
        File.SetUnixFileMode(indexPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        cache2.Dispose();
    }

    [Fact]
    public void MetaCache_Load_SkipsLedgerLockedByExclusiveHandle()
    {
        var indexPath = TempPath("metacache-locked");
        var basePath = TempPath("cache-root4");
        Directory.CreateDirectory(basePath);
        var file = Path.Combine(basePath, "f.json");
        File.WriteAllText(file, "{}");

        // seed a valid ledger (as above) so the test can tell "skipped" from "loaded"
        using (var cache = new HttpMetaCache(new AbsolutePath(indexPath)))
        {
            cache.AddBase("general", new AbsolutePath(basePath));
            var entry = cache.ResolveEntry("general", "f.json");
            entry.IsStale = false;
            entry.Etag = "\"seed\"";
            entry.Md5Sum = Convert.ToHexString(MD5.HashData("{}"u8.ToArray())).ToLowerInvariant();
            entry.LocalChangedTimestamp = new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeMilliseconds();
            entry.IsEternal = true;
            Assert.True(cache.UpdateEntry(entry));
            cache.SaveNow();
        }

        // C++ Load() opens the ledger read-only and silently returns when that fails, so a
        // ledger held under an exclusive handle (FileShare.None) must not make Load() throw.
        using (File.Open(indexPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            using var cache2 = new HttpMetaCache(new AbsolutePath(indexPath));
            cache2.AddBase("general", new AbsolutePath(basePath));
            var ex = Record.Exception(cache2.Load);
            Assert.Null(ex);

            // where the share mode is enforced (Windows, macOS) the read fails and Load()
            // takes the silent-skip path — a cold cache resolve; on platforms without
            // in-process share enforcement Load() simply succeeds, which is equally valid.
            var resolved = cache2.ResolveEntry("general", "f.json");
            if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            {
                Assert.True(resolved.IsStale);
            }
        }
    }

    [Fact]
    public void MetaCache_ResolveEntry_UnreadableCachedFile_BecomesStale()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // posix chmod-based negative case
        }

        var indexPath = TempPath("metacache-unreadable-cache");
        var basePath = TempPath("cache-root5");
        Directory.CreateDirectory(basePath);
        var file = Path.Combine(basePath, "locked.json");
        File.WriteAllText(file, "{}");

        // seed a valid entry exactly like MetaCache_SaveLoad_RoundTrip
        using var cache = new HttpMetaCache(new AbsolutePath(indexPath));
        cache.AddBase("general", new AbsolutePath(basePath));
        var entry = cache.ResolveEntry("general", "locked.json");
        entry.IsStale = false;
        entry.Etag = "\"seed\"";
        entry.Md5Sum = Convert.ToHexString(MD5.HashData("{}"u8.ToArray())).ToLowerInvariant();
        entry.LocalChangedTimestamp = new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeMilliseconds();
        entry.IsEternal = true;
        Assert.True(cache.UpdateEntry(entry));
        cache.SaveNow();

        // C++ degrades a cached file that exists but cannot be opened to a stale (disowned)
        // entry so the download task rebuilds it; the resolve must not throw.
        File.SetUnixFileMode(file, UnixFileMode.None);
        var resolved = cache.ResolveEntry("general", "locked.json");
        Assert.True(resolved.IsStale);

        // the bad entry was disowned: a second resolve returns a fresh stale entry too
        var again = cache.ResolveEntry("general", "locked.json");
        Assert.True(again.IsStale);
    }

    // ---------------------------------------------------------------- live requests

    [Fact]
    public async Task NetRequest_DownloadsToFile_WithChecksum()
    {
        using var server = new TestHttpServer(ctx =>
        {
            var body = "file-content-0123456789"u8.ToArray();
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentLength64 = body.Length;
            ctx.Response.OutputStream.Write(body);
        });

        var target = TempPath("download.bin");
        var request = NetRequest.MakeFile(new Uri(server.Url + "/file"), new AbsolutePath(target), TestNet.Stack);
        request.AddValidator(ChecksumValidator.FromHex(
            HashAlgorithmName.SHA1,
            Convert.ToHexString(SHA1.HashData("file-content-0123456789"u8.ToArray())).ToLowerInvariant()));

        await request.StartAsync();

        Assert.Equal(TaskState.Succeeded, request.State);
        Assert.Equal("file-content-0123456789", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task NetRequest_MetaCacheSink_Uses304Revalidation()
    {
        var servedBody = "cached-body"u8.ToArray();
        var requestCount = 0;
        using var server = new TestHttpServer(ctx =>
        {
            requestCount++;
            var etag = "\"v1\"";
            if (ctx.Request.Headers["If-None-Match"] == etag)
            {
                ctx.Response.StatusCode = 304;
                return;
            }

            ctx.Response.StatusCode = 200;
            ctx.Response.Headers["ETag"] = etag;
            ctx.Response.ContentLength64 = servedBody.Length;
            ctx.Response.OutputStream.Write(servedBody);
        });

        var indexPath = TempPath("metacache");
        var basePath = TempPath("cache");
        Directory.CreateDirectory(basePath);
        var cache = new HttpMetaCache(new AbsolutePath(indexPath));
        cache.AddBase("general", new AbsolutePath(basePath));

        // first fetch: downloads and records the entry
        var entry = cache.ResolveEntry("general", "thing.txt");
        var first = NetRequest.MakeCached(cache, new Uri(server.Url + "/thing.txt"), entry, RequestOptions.MakeEternal);
        await first.StartAsync();
        Assert.Equal(TaskState.Succeeded, first.State);
        Assert.Equal(1, requestCount);
        Assert.Equal("cached-body", await entry.GetFullPath().ReadAllTextAsync());
        Assert.Equal("\"v1\"", entry.Etag);

        // second fetch: server answers 304, local data is used (no rewrite)
        var entry2 = cache.ResolveEntry("general", "thing.txt");
        Assert.False(entry2.IsStale);
        var second = NetRequest.MakeCached(cache, new Uri(server.Url + "/thing.txt"), entry2);
        await second.StartAsync();
        Assert.Equal(TaskState.Succeeded, second.State);
        Assert.Equal(2, requestCount);
        Assert.Equal("cached-body", await entry2.GetFullPath().ReadAllTextAsync());
    }

    [Fact]
    public async Task NetRequest_AutoRetry_On429()
    {
        var attempts = 0;
        using var server = new TestHttpServer(ctx =>
        {
            attempts++;
            if (attempts < 3)
            {
                ctx.Response.StatusCode = 429;
                ctx.Response.Headers["Retry-After"] = "0";
                return;
            }

            ctx.Response.StatusCode = 200;
            var body = "ok"u8.ToArray();
            ctx.Response.ContentLength64 = body.Length;
            ctx.Response.OutputStream.Write(body);
        });

        var request = NetRequest.MakeByteArray(new Uri(server.Url + "/x"), TestNet.Stack, RequestOptions.AutoRetry);
        await request.StartAsync();

        Assert.Equal(TaskState.Succeeded, request.State);
        Assert.Equal(3, attempts);
        Assert.Equal("ok", request.BodyAsString);
    }

    [Fact]
    public async Task NetRequest_Fails_On404_WithStatus()
    {
        using var server = new TestHttpServer(ctx => { ctx.Response.StatusCode = 404; });

        var request = NetRequest.MakeByteArray(new Uri(server.Url + "/missing"), TestNet.Stack);
        await request.StartAsync();

        Assert.Equal(TaskState.Failed, request.State);
        Assert.Contains("404", request.FailReason);
    }

    [Fact]
    public async Task NetJob_RunsManyRequests()
    {
        using var server = new TestHttpServer(ctx =>
        {
            var url = ctx.Request.Url ?? throw new InvalidOperationException("the HTTP request has no URL");
            var body = Encoding.UTF8.GetBytes(url.AbsolutePath);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentLength64 = body.Length;
            ctx.Response.OutputStream.Write(body);
        });

        var job = new NetJob("test-job");
        var requests = new List<NetRequest>();
        for (var i = 0; i < 5; i++)
        {
            var request = NetRequest.MakeByteArray(new Uri($"{server.Url}/item{i}"), TestNet.Stack);
            requests.Add(request);
            job.AddNetRequest(request);
        }

        await job.StartAsync();

        Assert.Equal(TaskState.Succeeded, job.State);
        foreach (var (request, i) in requests.Select((r, i) => (r, i)))
        {
            Assert.Equal(TaskState.Succeeded, request.State);
            Assert.Equal($"/item{i}", request.BodyAsString);
        }
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
