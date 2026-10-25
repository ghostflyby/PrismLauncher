// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// The transport-level retry gate (the C++ Net::Request retry loops moved below the
// executors): a request that failed on the network level is re-sent up to the settings'
// ManualRetries times, and HTTP 429/503 is re-sent honoring Retry-After (at most three
// times) — both strictly per request, through the option keys below, so callers that must
// not retry (the auth steps; the C++ setAskRetry(false)) simply leave the flags unset.
//
// The idle-timeout interplay: the calling executor arms its RequestTimeout window and hands
// the CancellationTokenSource itself through <see cref="TimeoutSourceOption"/>; this handler
// resets the window before and after every wait or re-send, so a long Retry-After does not
// eat the next attempt's budget (the C++ retry re-executed with a fresh transfer timeout
// too).

namespace FlintLauncher.Core.Net;

public sealed class RetryHandler : DelegatingHandler
{
    /// <summary>The 429/503 retry cap (the C++ 4-retry/60s gate's attempt half).</summary>
    public const int MaxAutoRetries = 3;

    /// <summary>Opt in to the HTTP 429/503 retry (the RequestOptions.AutoRetry port).</summary>
    public static readonly HttpRequestOptionsKey<bool> AutoRetryOption = new("Flint.AutoRetry429");

    /// <summary>Opt in to the network-level manual retry gate (NumberOfManualRetries).</summary>
    public static readonly HttpRequestOptionsKey<bool> ManualRetryOption = new("Flint.ManualRetry");

    /// <summary>The executor's timeout source, re-armed around each retry wait; unset means
    /// the caller runs without an inactivity window and the handler leaves it alone.</summary>
    public static readonly HttpRequestOptionsKey<CancellationTokenSource> TimeoutSourceOption =
        new("Flint.TimeoutSource");

    public RetryHandler(NetSettings settings)
    {
        Settings = settings;
    }

    public NetSettings Settings { get; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var manual = request.Options.TryGetValue(ManualRetryOption, out var manualRetry) && manualRetry;
        var autoRetry = request.Options.TryGetValue(AutoRetryOption, out var retry429) && retry429;
        var hasTimeoutSource = request.Options.TryGetValue(TimeoutSourceOption, out var timeoutSource);
        var manualRetries = Settings.Current.ManualRetries;

        var autoRetryCount = 0;
        var manualCount = 0;
        while (true)
        {
            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (manual && manualCount < manualRetries &&
                                               !cancellationToken.IsCancellationRequested)
            {
                manualCount++;
                ReArm(timeoutSource, hasTimeoutSource);
                continue;
            }

            var statusCode = (int)response.StatusCode;
            if (autoRetry && statusCode is 429 or 503 && autoRetryCount < MaxAutoRetries)
            {
                autoRetryCount++;
                var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.Zero;
                if (delay < TimeSpan.Zero)
                {
                    delay = TimeSpan.Zero;
                }

                // a HTTP-date Retry-After is relative to now (the C++ parse rule)
                if (response.Headers.RetryAfter?.Date is { } date)
                {
                    delay = date - DateTimeOffset.Now;
                    if (delay < TimeSpan.Zero)
                    {
                        delay = TimeSpan.Zero;
                    }
                }

                response.Dispose();
                ReArm(timeoutSource, hasTimeoutSource);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                ReArm(timeoutSource, hasTimeoutSource);
                continue;
            }

            return response;
        }
    }

    private void ReArm(CancellationTokenSource? timeoutSource, bool hasTimeoutSource)
    {
        if (!hasTimeoutSource)
        {
            return;
        }

        // CancelAfter(-1) drops the pending window; the next arm starts a fresh one, so the
        // wait and every re-send get a full RequestTimeout of inactivity budget
        timeoutSource!.CancelAfter(Timeout.InfiniteTimeSpan);
        timeoutSource.CancelAfter(Settings.Current.RequestTimeout);
    }
}
