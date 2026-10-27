// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022 flowln <flowlnlnln@gmail.com>
//
// Port of launcher/net/Request.{h,cpp}'s download half plus net/FileSink.h/.cpp and
// net/MetaCacheSink.h/.cpp, fused into one task: the transfer (idle timeout, the transport's
// retry gates, status gate with the truncated error body), the atomic temp-file commit
// (PSaveFile), the incremental validators, the throttled progress, and the cached plane's
// If-None-Match revalidation with the full etag/Last-Modified/max-age/Age entry bookkeeping
// (the C++ MetaCacheSink::finalizeCache table, 7-day default expiry included).

using System.Diagnostics;
using System.Security.Cryptography;
using System.Globalization;
using System.Text.RegularExpressions;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Tasks;
using TruePath;
using TruePath.SystemIo;

namespace FlintLauncher.Core.Net;

[Flags]
public enum RequestOptions
{
    NoOptions = 0,

    /// <summary>Deliberately unconsumed (C++ Request.cpp's failed-request local fallback
    /// was never ported); carried so the MakeCached call sites read like upstream.</summary>
    AcceptLocalFiles = 1,

    MakeEternal = 2,
    AutoRetry = 4,

    /// <summary>Deliberately unconsumed: the C++ ApiHeaderProxy was host-gated and produced
    /// no headers for the meta/library hosts this flag's call sites download from.</summary>
    AddApiHeaders = 8,
}

public sealed class HttpDownloadTask(
    Uri url,
    HttpClient http,
    AbsolutePath target,
    RequestOptions options = RequestOptions.NoOptions,
    byte[]? postData = null,
    string? name = null)
    : FlintTask
{
    private readonly HttpClient _http = http;
    private readonly byte[]? _postData = postData;

    private readonly List<Validator> _validators = [];
    private AbsolutePath? _tempFile;
    private bool _wroteAnyData;

    internal HttpMetaCache? MetaCache { get; init; }

    internal MetaEntry? CachedEntry { get; init; }

    /// <summary>The MakeEternal flag forwarded to the cache bookkeeping.</summary>
    private bool IsEternal => options.HasFlag(RequestOptions.MakeEternal);

    public string TaskName { get; } = name ?? url.ToString();

    public Uri Url { get; } = url;

    public AbsolutePath Target => target;

    public void AddValidator(Validator validator) => _validators.Add(validator);

    /// <summary>Port of Net::Request::makeCached: the cached plane — the transfer writes the
    /// entry's file, revalidates with the entry's etag and re-registers the entry after
    /// success. The MD5 validator (the entry bookkeeping's own) is wired here; callers add
    /// their content validators on top.</summary>
    public static HttpDownloadTask Cached(HttpMetaCache cache, Uri url, MetaEntry entry,
        RequestOptions options = RequestOptions.NoOptions, HttpClient? http = null)
    {
        // the md5 node is bookkeeping only (the C++ MetaCacheSink created it with no
        // expected value): seeding it from the ledger would fail every refresh of changed
        // content and consume the hash before the bookkeeping can record the new sum
        var checksum = new ChecksumValidator(HashAlgorithmName.MD5);
        var task = new HttpDownloadTask(url, http ?? cache.Http,
            entry.GetFullPath(), options | RequestOptions.AcceptLocalFiles)
        {
            MetaCache = cache,
            CachedEntry = entry,
        };
        task.AddValidator(checksum);
        task._md5 = checksum;
        return task;
    }

    /// <summary>Port of Net::Request::makeFile.</summary>
    public static HttpDownloadTask ToFile(Uri url, AbsolutePath path, HttpClient http,
        RequestOptions options = RequestOptions.NoOptions) =>
        new(url, http, path, options);

    private ChecksumValidator? _md5;

    // ---------------------------------------------------------------- execution

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        // RequestTimeout port (QNetworkReply::setTransferTimeout): an inactivity window, not
        // a total duration. The transport's retry gate arms the window for the send; every
        // chunk read below renews it, so a steady transfer runs unbounded and only a real
        // stall fails.
        using var message = new HttpRequestMessage(HttpMethod.Get, Url);
        using var window = new RetryHandler.Window(message, cancellationToken);

        try
        {
            if (!InitTarget())
            {
                Fail(FailReason);
                return;
            }

            if (CachedEntry is { } entry && !entry.IsStale && (HasTargetData() || entry.Md5Sum.Length > 0))
            {
                if (entry.Etag.Length > 0)
                {
                    message.Headers.TryAddWithoutValidation("If-None-Match", entry.Etag);
                }
            }

            if (_postData is not null)
            {
                message.Content = new ByteArrayContent(_postData);
            }

            message.Options.Set(RetryHandler.ManualRetryOption, true);
            message.Options.Set(RetryHandler.AutoRetryOption, options.HasFlag(RequestOptions.AutoRetry));

            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead,
                window.Token).ConfigureAwait(false);

            var statusCode = (int)response.StatusCode;
            if (statusCode == 304 && HasTargetData())
            {
                // not modified: local data is still valid — finalize without writing
                if (!FinalizeTarget(statusCode, response.Headers))
                {
                    Fail(FailReason);
                    return;
                }

                EmitSucceeded();
                return;
            }

            if (statusCode is not 200 and not 201 and not 202 and not 203 and not 206)
            {
                var body = "";
                try
                {
                    body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (IOException) { }

                if (body.Length > FlintHttp.MaxErrorBodyLength)
                {
                    body = body[..FlintHttp.MaxErrorBodyLength];
                }

                Fail($"HTTP status {statusCode} for {Url}\n{body}");
                return;
            }

            // stream the body into the temp file with throttled progress
            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            var receivedBytes = 0L;
            var lastProgressTime = Stopwatch.StartNew();
            var buffer = new byte[64 * 1024];
            await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            {
                while (true)
                {
                    window.Progress();
                    var read = await stream.ReadAsync(buffer, window.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    if (!WriteChunk(buffer[..read]))
                    {
                        Fail(FailReason);
                        return;
                    }

                    receivedBytes += read;
                    // throttle progress emission a bit, like the C++ downloadProgress path
                    if (lastProgressTime.ElapsedMilliseconds > 100 || receivedBytes == totalBytes)
                    {
                        lastProgressTime.Restart();
                        EmitProgress(new TaskStepProgress(Uid)
                        {
                            Current = receivedBytes,
                            Total = totalBytes,
                            State = TaskStepState.Running,
                        });
                    }
                }
            }

            if (!FinalizeTarget(statusCode, response.Headers))
            {
                Fail(FailReason);
                return;
            }

            EmitSucceeded();
        }
        catch (OperationCanceledException) when (window.TimedOut)
        {
            // our inactivity timer fired mid-transfer (the header phase is covered by the
            // transport's manual-retry gate via the option above; this late conversion is
            // the task-level failure surface — a stalled body fails after one window)
            CancelWriting();
            Fail(window.TimeoutMessage);
        }
        catch (Exception)
        {
            // a network error or user abort must not leave the temp file behind (the C++
            // sink's abort deleted it too); the failure state itself is handled by the
            // task machinery / rethrow below
            CancelWriting();
            throw;
        }
    }

    private void Fail(string reason)
    {
        CancelWriting();
        foreach (var validator in _validators)
        {
            validator.Abort();
        }

        EmitFailed(reason);
    }

    // ------------------------------------------------------- target plumbing (the sinks)

    private bool HasTargetData()
    {
        var info = new FileInfo(target.Value);
        return info.Exists && info.Length != 0;
    }

    private bool InitTarget()
    {
        if (!FileOps.EnsureFilePathExists(target))
        {
            FailReason = "Could not create folder for " + target;
            return false;
        }

        _wroteAnyData = false;
        _tempFile = target.Parent is { } parent
            ? parent / (target.FileName + ".flint-tmp")
            : new AbsolutePath(target.Value + ".flint-tmp");
        try
        {
            _tempFile.Value.Create().Dispose();
        }
        catch (IOException e)
        {
            FailReason = $"Could not open {target} for writing: {e.Message}";
            _tempFile = null;
            return false;
        }

        var spec = new NetRequestSpec();
        foreach (var validator in _validators)
        {
            if (validator.Init(spec))
            {
                continue;
            }

            FailReason = "Failed to initialize validators";
            return false;
        }

        return true;
    }

    private bool WriteChunk(byte[] data)
    {
        if (_tempFile is not { } tempFile)
        {
            FailReason = "Writing into a closed file sink";
            return false;
        }

        try
        {
            using var stream = tempFile.Open(FileMode.Append, FileAccess.Write);
            stream.Write(data);
        }
        catch (IOException e)
        {
            FailReason = $"Failed writing into {target}: {e.Message}";
            CancelWriting();
            _wroteAnyData = false;
            return false;
        }

        foreach (var validator in _validators)
        {
            if (validator.Write(data))
            {
                continue;
            }

            FailReason = $"Failed writing into {target}: validators failed";
            CancelWriting();
            _wroteAnyData = false;
            return false;
        }

        _wroteAnyData = true;
        return true;
    }

    private bool FinalizeTarget(int statusCode, System.Net.Http.Headers.HttpResponseHeaders headers)
    {
        // this leaves out 304 Not Modified
        var gotFile = statusCode is 200 or 203;

        // if we wrote any data to the save file, we commit it to the real file; a proper
        // file response is committed even when it was empty
        if (gotFile || _wroteAnyData)
        {
            var responseInfo = new NetResponseInfo { StatusCode = statusCode };
            foreach (var header in headers)
            {
                responseInfo.Headers[header.Key] = string.Join(", ", header.Value);
            }

            foreach (var validator in _validators)
            {
                if (validator.Validate(responseInfo))
                {
                    continue;
                }

                FailReason = "Failed to finalize validators";
                return false;
            }

            if (_tempFile is { } tempFile)
            {
                try
                {
                    tempFile.Move(target, overwrite: true);
                }
                catch (IOException e)
                {
                    FailReason = $"Failed to commit changes to {target}: {e.Message}";
                    CancelWriting();
                    return false;
                }

                _tempFile = null;
            }
        }

        // snapshot before CancelWriting zeroes the flag: the cache bookkeeping needs to
        // know whether THIS transfer wrote (the old sink order zeroed first, so the md5
        // bookkeeping never fired — the new regression test pins the fix)
        var wroteAnyData = _wroteAnyData;
        CancelWriting();
        FinalizeCacheEntry(statusCode, headers, wroteAnyData);
        return true;
    }

    /// <summary>Port of MetaCacheSink::finalizeCache (the cached plane's bookkeeping): md5
    /// only when data was written, etag/last-modified/max-age/Age, then UpdateEntry. Always
    /// succeeds; a no-op for plain file targets.</summary>
    private void FinalizeCacheEntry(int statusCode, System.Net.Http.Headers.HttpResponseHeaders headers,
        bool wroteAnyData)
    {
        if (CachedEntry is not { } entry)
        {
            return;
        }

        string Header(string name) => headers.TryGetValues(name, out var values)
            ? string.Join(", ", values)
            : "";

        if (wroteAnyData && _md5 is { } md5)
        {
            var hash = md5.Hash();
            if (hash.Length > 0)
            {
                entry.Md5Sum = Convert.ToHexStringLower(hash);
            }
        }

        entry.Etag = Header("ETag");

        var lastModified = Header("Last-Modified");
        if (lastModified.Length > 0)
        {
            entry.RemoteChangedTimestamp = lastModified;
        }

        entry.LocalChangedTimestamp = target.ExistsFile()
            ? new DateTimeOffset(target.GetLastWriteTimeUtc()).ToUnixTimeMilliseconds()
            : 0;

        if (IsEternal)
        {
            entry.IsEternal = true;
        }
        else
        {
            var cacheControl = Header("Cache-Control");
            var match = Regex.Match(cacheControl, "max-age=([0-9]+)");
            if (match.Success)
            {
                entry.MaxAge = long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            }
            else
            {
                var expiresHeader = Header("Expires");
                if (expiresHeader.Length > 0
                    && DateTimeOffset.TryParse(expiresHeader, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var expires))
                {
                    entry.MaxAge = expires.ToUnixTimeSeconds() - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                }
                else
                {
                    // MetaCacheSink.cpp: #define MAX_TIME_TO_EXPIRE 1 * 7 * 24 * 60 * 60
                    entry.MaxAge = 7 * 24 * 60 * 60;
                }
            }
        }

        entry.CurrentAge = long.TryParse(Header("Age"), out var age) ? age : 0;

        entry.IsStale = false;
        MetaCache?.UpdateEntry(entry);
    }

    private void CancelWriting()
    {
        if (_tempFile is { } tempFile)
        {
            try
            {
                tempFile.Delete();
            }
            catch (IOException) { }

            _tempFile = null;
        }

        _wroteAnyData = false;
    }
}
