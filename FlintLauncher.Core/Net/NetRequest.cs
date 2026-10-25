// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022 flowln <flowlnlnln@gmail.com>
// SPDX-FileCopyrightText: 2022 Sefa Eyeoglu <contact@scrumplex.net>
// SPDX-FileCopyrightText: 2023 Rachel Powers <508861+Ryex@users.noreply.github.com>
// SPDX-FileCopyrightText: 2023 Trial97 <alexandru.tripon97@gmail.com>
// SPDX-FileCopyrightText: 2023 TheKodeToad <TheKodeToad@proton.me>
//
// This file incorporates work covered by the following copyright and
// permission notice:
//
//     Copyright 2013-2021 MultiMC Contributors
//
//     Licensed under the Apache License, Version 2.0 (the "License");
//     you may not use this file except in compliance with the License.
//     You may obtain a copy of the License at
//
//         http://www.apache.org/licenses/LICENSE-2.0
//
//     Unless required by applicable law or agreed to in writing, software
//     distributed under the License is distributed on an "AS IS" BASIS,
//     WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//     See the License for the specific language governing permissions and
//     limitations under the License.
//
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
    HttpClient http,
    NetSettings settings,
    RequestOptions options = RequestOptions.NoOptions,
    HttpMethodValue method = HttpMethodValue.Get,
    byte[]? postData = null,
    string? name = null)
    : FlintTask
{
    private readonly HttpClient _http = http;
    private readonly NetSettings _settings = settings;

    private readonly List<(string name, string value)> _headers = [];
    private Sink? _sink;

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

    /// <summary>Port of Net::Request::makeCached. The download rides the named client pair
    /// when one is given, else the cache's own client pair — the cache is the download
    /// plane.</summary>
    public static NetRequest MakeCached(HttpMetaCache cache, Uri url, MetaEntry entry, RequestOptions options = RequestOptions.NoOptions, HttpClient? http = null, NetSettings? settings = null)
    {
        var checksum = new ChecksumValidator(HashAlgorithmName.MD5, entry.Md5Sum.Length == 0 ? null : Convert.FromHexString(entry.Md5Sum));
        var sink = new MetaCacheSink(entry, checksum, isEternal: options.HasFlag(RequestOptions.MakeEternal))
        {
            MetaCache = cache,
        };
        var request = new NetRequest(url, http ?? cache.Http, settings ?? cache.Settings, options | RequestOptions.AcceptLocalFiles)
        {
            RequestSink = sink,
            MetaCache = cache,
            CachedEntry = entry,
        };
        request.AddValidator(checksum);
        return request;
    }

    /// <summary>Port of Net::Request::makeByteArray; the response lands in <see cref="Body"/>.</summary>
    public static NetRequest MakeByteArray(Uri url, HttpClient http, NetSettings settings, RequestOptions options = RequestOptions.NoOptions)
    {
        var sink = new ByteArraySink();
        return new NetRequest(url, http, settings, options)
        {
            RequestSink = sink,
        };
    }

    public static NetRequest MakeByteArray(Uri url, byte[] postData, HttpClient http, NetSettings settings, RequestOptions options = RequestOptions.NoOptions)
    {
        return new NetRequest(url, http, settings, options, HttpMethodValue.Post, postData)
        {
            RequestSink = new ByteArraySink(),
        };
    }

    public static NetRequest MakeFile(Uri url, AbsolutePath path, HttpClient http, NetSettings settings, RequestOptions options = RequestOptions.NoOptions)
    {
        return new NetRequest(url, http, settings, options)
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
        // the network-level manual retry gate (NumberOfManualRetries) and the 429/503
        // auto-retry live in the transport's RetryHandler, opted in per request below; this
        // executor only converts the idle-timeout cancellation into the network error the
        // rest of the flow expects (the C++ setTransferTimeout surfaces as
        // OperationCanceledError too)
        var requestTimeout = _settings.Current.RequestTimeout;
        var spec = new NetRequestSpec();
        foreach (var (name, value) in _headers)
        {
            spec.Headers[name] = value;
        }

        // RequestTimeout port (QNetworkReply::setTransferTimeout): an inactivity timeout that
        // cancels the linked token. CancelAfter replaces the pending delay on each re-arm, so
        // the timeout state stays inside the CTS: a Timer callback would instead capture the
        // using-scoped source from a threadpool context and depend on disposal ordering. The
        // source rides the request options so the RetryHandler resets the window around its
        // retry waits.
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        void ArmIdleTimer()
        {
            timeoutSource.CancelAfter(requestTimeout);
        }

        try
        {
            var effectiveSink = _sink;
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

            message.Options.Set(RetryHandler.ManualRetryOption, true);
            message.Options.Set(RetryHandler.AutoRetryOption, options.HasFlag(RequestOptions.AutoRetry));
            message.Options.Set(RetryHandler.TimeoutSourceOption, timeoutSource);

            ArmIdleTimer();
            var response = await _http.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutSource.Token).ConfigureAwait(false);

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
                $"transfer timed out: no data for {requestTimeout.TotalSeconds:F0} seconds (RequestTimeout)");
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
