// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/net/Request.{h,cpp} (HTTP methods, sinks, validators, 429/503 auto-retry)
// on top of HttpClient instead of QNetworkAccessManager.
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using FlintLauncher.Core.Tasks;

namespace FlintLauncher.Core.Net;

[Flags]
public enum RequestOptions
{
    NoOptions = 0,
    AcceptLocalFiles = 1,
    MakeEternal = 2,
    AutoRetry = 4,
    AddAPIHeaders = 8,
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

public sealed class NetRequest : FlintTask
{
    public const int MaxAutoRetries = 3;

    private readonly HttpMethodValue _method;
    private readonly Uri _url;
    private readonly byte[]? _postData;
    private readonly RequestOptions _options;

    private readonly List<(string name, string value)> _headers = [];
    private Sink? _sink;
    private int _retryCount;

    /// <summary>Set for makeCached-style requests: the entry is re-registered after success.</summary>
    internal HttpMetaCache? MetaCache { get; set; }

    internal MetaEntry? CachedEntry { get; set; }

    public NetRequest(
        Uri url,
        RequestOptions options = RequestOptions.NoOptions,
        HttpMethodValue method = HttpMethodValue.Get,
        byte[]? postData = null,
        string? name = null)
    {
        _url = url;
        _options = options;
        _method = method;
        _postData = postData;
        TaskName = name ?? url.ToString();
    }

    public string TaskName { get; }

    public Uri Url => _url;

    public Sink? RequestSink
    {
        get => _sink;
        set => _sink = value;
    }

    public void AddHeader(string name, string value) => _headers.Add((name, value));

    public void AddValidator(Validator validator) => _sink?.AddValidator(validator);

    // ---------------------------------------------------------------- factories

    /// <summary>Port of Net::Request::makeCached.</summary>
    public static NetRequest MakeCached(HttpMetaCache cache, Uri url, MetaEntry entry, RequestOptions options = RequestOptions.NoOptions)
    {
        var checksum = new ChecksumValidator(HashAlgorithmName.MD5, entry.Md5Sum.Length == 0 ? null : Convert.FromHexString(entry.Md5Sum));
        var sink = new MetaCacheSink(entry, checksum, isEternal: options.HasFlag(RequestOptions.MakeEternal))
        {
            MetaCache = cache,
        };
        var request = new NetRequest(url, options | RequestOptions.AcceptLocalFiles)
        {
            RequestSink = sink,
            MetaCache = cache,
            CachedEntry = entry,
        };
        request.AddValidator(checksum);
        return request;
    }

    /// <summary>Port of Net::Request::makeByteArray; the response lands in <see cref="Body"/>.</summary>
    public static NetRequest MakeByteArray(Uri url, RequestOptions options = RequestOptions.NoOptions)
    {
        var sink = new ByteArraySink();
        return new NetRequest(url, options)
        {
            RequestSink = sink,
        };
    }

    public static NetRequest MakeByteArray(Uri url, byte[] postData, RequestOptions options = RequestOptions.NoOptions)
    {
        return new NetRequest(url, options, HttpMethodValue.Post, postData)
        {
            RequestSink = new ByteArraySink(),
        };
    }

    public static NetRequest MakeFile(Uri url, string path, RequestOptions options = RequestOptions.NoOptions)
    {
        return new NetRequest(url, options)
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
        // is retried up to Http.ManualRetries times before it finally fails. HTTP error
        // statuses are not retried here (the AutoRetry option covers 429/503 like the C++).
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await ExecuteOnceAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (HttpRequestException e) when (attempt < Http.ManualRetries &&
                                                 !cancellationToken.IsCancellationRequested)
            {
                EmitStatus($"Network error ({e.Message}); retrying ({Http.ManualRetries - attempt} tries left)");
            }
        }
    }

    private async Task ExecuteOnceAsync(CancellationToken cancellationToken)
    {
        var spec = new NetRequestSpec { Url = _url };
        foreach (var (name, value) in _headers)
        {
            spec.Headers[name] = value;
        }

        // RequestTimeout port (QNetworkReply::setTransferTimeout): a one-shot inactivity timer
        // cancelling the linked token; rearmed before every await that can transfer data.
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var idleTimer = new Timer(_ => timeoutSource.Cancel());

        void ArmIdleTimer()
        {
            try
            {
                _ = idleTimer.Change(Http.RequestTimeout, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                // the request finished while a re-arm was queued; nothing to do
            }
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

                using var message = new HttpRequestMessage(ToHttpMethod(_method), _url);
                foreach (var (name, value) in spec.Headers)
                {
                    message.Headers.TryAddWithoutValidation(name, value);
                }
                if (_postData is not null)
                {
                    message.Content = new ByteArrayContent(_postData);
                }

                ArmIdleTimer();
                response = await Http.Client.SendAsync(
                    message,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutSource.Token).ConfigureAwait(false);

                // automatically handle HTTP 429 Too Many Requests / 503 errors and retry
                var statusCode = (int)response.StatusCode;
                if (_options.HasFlag(RequestOptions.AutoRetry) &&
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
                    Url = _url,
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
                    catch (IOException)
                    {
                    }
                    if (body.Length > 4096)
                    {
                        body = body[..4096];
                    }
                    EmitFailed($"HTTP status {statusCode} for {_url}\n{body}");
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
                        ArmIdleTimer();
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
                $"transfer timed out: no data for {Http.RequestTimeout.TotalSeconds:F0} seconds (RequestTimeout)");
        }
    }

    private static System.Net.Http.HttpMethod ToHttpMethod(HttpMethodValue method) => method switch
    {
        HttpMethodValue.Get => System.Net.Http.HttpMethod.Get,
        HttpMethodValue.Post => System.Net.Http.HttpMethod.Post,
        HttpMethodValue.Put => System.Net.Http.HttpMethod.Put,
        HttpMethodValue.Patch => System.Net.Http.HttpMethod.Patch,
        HttpMethodValue.Delete => System.Net.Http.HttpMethod.Delete,
        HttpMethodValue.Head => System.Net.Http.HttpMethod.Head,
        HttpMethodValue.Options => System.Net.Http.HttpMethod.Options,
        HttpMethodValue.Trace => System.Net.Http.HttpMethod.Trace,
        _ => System.Net.Http.HttpMethod.Get,
    };
}

/// <summary>Shared HttpClient for all Flint network activity (replaces QNetworkAccessManager).
/// The proxy/timeout/retry settings the C++ application reads from the global settings
/// (Application::updateProxySettings, NumberOfManualRetries, RequestTimeout) reach this class
/// through <see cref="Configure"/> — the host calls it at startup and after every settings
/// write (Services.RuntimeNetConfig).</summary>
public static class Http
{
    public static string UserAgent { get; set; } = "Flint/0.1.0";

    /// <summary>Port of the NumberOfManualRetries setting: how often a request that failed on
    /// the network level is retried (NetJob::executeNextStylesheet gate). Consumed by
    /// <see cref="NetRequest"/>.</summary>
    public static int ManualRetries { get; set; } = 1;

    /// <summary>Port of the RequestTimeout setting (QNetworkReply::setTransferTimeout): the
    /// transfer is aborted when no data flows for this long (an inactivity timeout, not a
    /// total-duration cap, so large steady downloads keep working).</summary>
    public static TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);

    private static (string Type, string Host, int Port, string User, string Pass) _proxy =
        ("None", "", 0, "", "");

    private static (string Ua, string Type, string Host, int Port, string User, string Pass)? _applied;

    /// <summary>The proxy configuration currently baked into <see cref="Client"/>
    /// (Application::updateProxySettings state; diagnostics/tests).</summary>
    public static (string Type, string Host, int Port, string User, string Pass) ProxyConfig => _proxy;

    private static volatile HttpClient _client = CreateClient();

    public static HttpClient Client => _client;

    /// <summary>Port of Application::updateProxySettings (+ the UserAgent the client is built
    /// with): rebuild the shared client so the proxy/user-agent take effect. Proxy semantics
    /// match the C++ ProxyPage combo: "SOCKS5" and "HTTP" build an explicit proxy, "None"
    /// disables proxying and anything else uses the system configuration. Idempotent: an
    /// unchanged configuration does not rebuild the client.</summary>
    public static void Configure(string userAgent, string proxyType, string addr, int port,
        string user, string pass)
    {
        var ua = userAgent ?? UserAgent;
        var type = string.IsNullOrWhiteSpace(proxyType) ? "None" : proxyType.Trim();
        var host = addr ?? "";
        // an HTTP/SOCKS proxy without a host cannot be used — fall back to "no proxy"
        if (type is "HTTP" or "SOCKS5" && host.Length == 0)
        {
            type = "None";
        }
        var next = (ua, type, host, port, user ?? "", pass ?? "");
        if (_applied is { } applied && applied.Equals(next))
        {
            return;
        }
        _applied = next;
        UserAgent = ua;
        _proxy = (type, host, port, user ?? "", pass ?? "");
        _client = CreateClient();
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            Proxy = BuildProxy(),
        };
        var client = new HttpClient(handler, disposeHandler: true);
        // user-supplied user-agent overrides may not be valid product tokens; ignore those
        _ = client.DefaultRequestHeaders.UserAgent.TryParseAdd(UserAgent);
        client.Timeout = TimeSpan.FromMinutes(10);
        return client;
    }

    private static IWebProxy? BuildProxy() => _proxy.Type switch
    {
        "HTTP" => NamedProxy(new Uri($"http://{_proxy.Host}:{_proxy.Port}")),
        "SOCKS5" => NamedProxy(new Uri($"socks5://{_proxy.Host}:{_proxy.Port}")),
        // explicit "no proxy"
        "None" => null,
        // C++ "DefaultProxy": the system proxy configuration
        _ => HttpClient.DefaultProxy,
    };

    private static WebProxy NamedProxy(Uri uri)
    {
        var proxy = new WebProxy(uri);
        if (_proxy.User.Length != 0)
        {
            proxy.Credentials = new System.Net.NetworkCredential(_proxy.User, _proxy.Pass);
        }
        return proxy;
    }
}
