// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/net/Request.{h,cpp} (HTTP methods, sinks, validators, 429/503 auto-retry)
// on top of HttpClient instead of QNetworkAccessManager.

using System.Diagnostics;
using System.Security.Cryptography;
using FlintLauncher.Core.Tasks;
using TruePath;

namespace FlintLauncher.Core.Net;

[Flags]
public enum RequestOptions
{
    NoOptions = 0,
    AcceptLocalFiles = 1,
    MakeEternal = 2,
    AutoRetry = 4,
    AddApiHeaders = 8,
}

public enum HttpMethodValue
{
    Get,
    Post,
    Put,
    Patch,
    Delete,
    Head,
    Options,
    Trace,
}

public sealed class NetRequest(
    Uri url,
    RequestOptions options = RequestOptions.NoOptions,
    HttpMethodValue method = HttpMethodValue.Get,
    byte[]? postData = null,
    string? name = null,
    NetStack? stack = null)
    : FlintTask
{
    public const int MaxAutoRetries = 3;

    private readonly NetStack _stack = stack ?? NetStack.Default;

    private readonly List<(string name, string value)> _headers = [];
    private Sink? _sink;
    private int _retryCount;

    /// <summary>Set for makeCached-style requests: the entry is re-registered after success.</summary>
    internal HttpMetaCache? MetaCache { get; init; }

    internal MetaEntry? CachedEntry { get; init; }

    public string TaskName { get; } = name ?? url.ToString();

    public Uri Url => url;

    public Sink? RequestSink
    {
        get => _sink;
        set => _sink = value;
    }

    public void AddHeader(string name, string value) => _headers.Add((name, value));

    public void AddValidator(Validator validator) => _sink?.AddValidator(validator);

    // ---------------------------------------------------------------- factories

    /// <summary>Port of Net::Request::makeCached.</summary>
    public static NetRequest MakeCached(HttpMetaCache cache, Uri url, MetaEntry entry, RequestOptions options = RequestOptions.NoOptions, NetStack? stack = null)
    {
        var checksum = new ChecksumValidator(HashAlgorithmName.MD5, entry.Md5Sum.Length == 0 ? null : Convert.FromHexString(entry.Md5Sum));
        var sink = new MetaCacheSink(entry, checksum, isEternal: options.HasFlag(RequestOptions.MakeEternal))
        {
            MetaCache = cache,
        };
        var request = new NetRequest(url, options | RequestOptions.AcceptLocalFiles, stack: stack)
        {
            RequestSink = sink,
            MetaCache = cache,
            CachedEntry = entry,
        };
        request.AddValidator(checksum);
        return request;
    }

    /// <summary>Port of Net::Request::makeByteArray; the response lands in <see cref="Body"/>.</summary>
    public static NetRequest MakeByteArray(Uri url, RequestOptions options = RequestOptions.NoOptions, NetStack? stack = null)
    {
        var sink = new ByteArraySink();
        return new NetRequest(url, options, stack: stack)
        {
            RequestSink = sink,
        };
    }

    public static NetRequest MakeByteArray(Uri url, byte[] postData, RequestOptions options = RequestOptions.NoOptions, NetStack? stack = null)
    {
        return new NetRequest(url, options, HttpMethodValue.Post, postData, stack: stack)
        {
            RequestSink = new ByteArraySink(),
        };
    }

    public static NetRequest MakeFile(Uri url, AbsolutePath path, RequestOptions options = RequestOptions.NoOptions, NetStack? stack = null)
    {
        return new NetRequest(url, options, stack: stack)
        {
            RequestSink = new FileSink(path),
        };
    }

    /// <summary>Buffer content when constructed via MakeByteArray.</summary>
    public byte[] Body => (_sink as ByteArraySink)?.Data ?? [];

    public string BodyAsString => (_sink as ByteArraySink)?.DataAsString ?? "";

    // ---------------------------------------------------------------- execution

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        // Port of the NetJob manual-retry gate (NumberOfManualRetries): a request that failed
        // on the network level (connection refused/reset, DNS, the RequestTimeout idle timeout)
        // is retried up to the stack's ManualRetries times before it finally fails. HTTP error
        // statuses are not retried here (the AutoRetry option covers 429/503 like the C++).
        var attempt = 0;
        while (true)
        {
            try
            {
                await ExecuteOnceAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (HttpRequestException e) when (attempt < _stack.ManualRetries &&
                                                 !cancellationToken.IsCancellationRequested)
            {
                EmitStatus($"Network error ({e.Message}); retrying ({_stack.ManualRetries - attempt} tries left)");
            }

            attempt++;
        }
    }

    private async Task ExecuteOnceAsync(CancellationToken cancellationToken)
    {
        var spec = new NetRequestSpec();
        foreach (var (name, value) in _headers)
        {
            spec.Headers[name] = value;
        }

        // RequestTimeout port (QNetworkReply::setTransferTimeout): an inactivity timeout that
        // cancels the linked token. CancelAfter replaces the pending delay on each re-arm, so
        // the timeout state stays inside the CTS: a Timer callback would instead capture the
        // using-scoped source from a threadpool context and depend on disposal ordering.
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        void ArmIdleTimer(CancellationTokenSource source)
        {
            source.CancelAfter(_stack.RequestTimeout);
        }

        try
        {
            HttpResponseMessage response;
            var effectiveSink = _sink;
            while (true)
            {
                if (effectiveSink is not null && !effectiveSink.Init(spec))
                {
                    EmitFailed(effectiveSink.FailReason);
                    return;
                }

                using var message = new HttpRequestMessage(ToHttpMethod(method), url);
                foreach (var (name, value) in spec.Headers)
                {
                    message.Headers.TryAddWithoutValidation(name, value);
                }

                if (postData is not null)
                {
                    message.Content = new ByteArrayContent(postData);
                }

                ArmIdleTimer(timeoutSource);
                response = await _stack.Client.SendAsync(
                    message,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutSource.Token).ConfigureAwait(false);

                // automatically handle HTTP 429 Too Many Requests / 503 errors and retry
                var statusCode = (int)response.StatusCode;
                if (options.HasFlag(RequestOptions.AutoRetry) &&
                    statusCode is 429 or 503 &&
                    _retryCount < MaxAutoRetries)
                {
                    _retryCount++;
                    var delay = TimeSpan.Zero;
                    if (response.Headers.RetryAfter?.Delta is { } delta)
                    {
                        delay = delta;
                    }
                    else if (response.Headers.RetryAfter?.Date is { } date)
                    {
                        delay = date - DateTimeOffset.Now;
                    }

                    if (delay < TimeSpan.Zero)
                    {
                        delay = TimeSpan.Zero;
                    }

                    response.Dispose();
                    EmitStatus($"HTTP {statusCode}, retrying in {delay.TotalSeconds} seconds");
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                break;
            }

            using (response)
            {
                var responseInfo = new NetResponseInfo
                {
                    StatusCode = (int)response.StatusCode,
                };
                foreach (var header in response.Headers)
                {
                    responseInfo.Headers[header.Key] = string.Join(", ", header.Value);
                }

                foreach (var header in response.Content.Headers)
                {
                    responseInfo.Headers[header.Key] = string.Join(", ", header.Value);
                }

                var statusCode = (int)response.StatusCode;
                if (statusCode == 304 && (effectiveSink?.HasLocalData() ?? false))
                {
                    // not modified: local data still valid, finalize without writing
                    if (!effectiveSink.Finalize(responseInfo))
                    {
                        EmitFailed(effectiveSink.FailReason);
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

                    if (body.Length > 4096)
                    {
                        body = body[..4096];
                    }

                    EmitFailed($"HTTP status {statusCode} for {url}\n{body}");
                    return;
                }

                // stream the body into the sink with progress reporting
                var totalBytes = response.Content.Headers.ContentLength ?? -1;
                var receivedBytes = 0L;
                var lastProgressTime = Stopwatch.StartNew();
                var buffer = new byte[64 * 1024];
                await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (true)
                    {
                        ArmIdleTimer(timeoutSource);
                        var read = await stream.ReadAsync(buffer, timeoutSource.Token).ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        if (effectiveSink is not null && !effectiveSink.Write(buffer[..read]))
                        {
                            EmitFailed(effectiveSink.FailReason);
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

                if (effectiveSink is not null && !effectiveSink.Finalize(responseInfo))
                {
                    EmitFailed(effectiveSink.FailReason);
                    return;
                }

                // like Net::Download::downloadFinished: re-register the cache entry after success
                if (MetaCache is not null && CachedEntry is not null)
                {
                    _ = MetaCache.UpdateEntry(CachedEntry);
                }

                EmitSucceeded();
            }
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested &&
                                                 !cancellationToken.IsCancellationRequested)
        {
            // our inactivity timer fired: surface as a network error so the manual-retry gate
            // sees it (the C++ setTransferTimeout surfaces as OperationCanceledError too)
            throw new HttpRequestException(
                $"transfer timed out: no data for {_stack.RequestTimeout.TotalSeconds:F0} seconds (RequestTimeout)");
        }
    }

    private static HttpMethod ToHttpMethod(HttpMethodValue method) => method switch
    {
        HttpMethodValue.Get => HttpMethod.Get,
        HttpMethodValue.Post => HttpMethod.Post,
        HttpMethodValue.Put => HttpMethod.Put,
        HttpMethodValue.Patch => HttpMethod.Patch,
        HttpMethodValue.Delete => HttpMethod.Delete,
        HttpMethodValue.Head => HttpMethod.Head,
        HttpMethodValue.Options => HttpMethod.Options,
        HttpMethodValue.Trace => HttpMethod.Trace,
        _ => HttpMethod.Get,
    };
}
