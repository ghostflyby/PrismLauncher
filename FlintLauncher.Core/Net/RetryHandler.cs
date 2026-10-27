// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// The transport-level retry gate (the C++ Net::Request retry loops moved below the
// executors): a request that failed on the network level is re-sent up to the gate's own
// MaxManualRetries times, and HTTP 429/503 is re-sent honoring Retry-After (at most three
// times) — both strictly per request, through the option keys below, so callers that must
// not retry (the auth steps; the C++ setAskRetry(false)) simply leave the flags unset.
//
// The idle-timeout interplay: the sender creates a <see cref="Window"/> and this gate owns
// the window's transport half — it arms the window with its own RequestTimeout when the
// request enters, resets it before and after every wait or re-send (so a long Retry-After
// does not eat the next attempt's budget, the C++ retry re-executed with a fresh transfer
// timeout too), and the sender keeps renewing it across its body reads through the same
// object.

namespace FlintLauncher.Core.Net;

public sealed class RetryHandler : DelegatingHandler
{
    /// <summary>The 429/503 retry cap (the C++ 4-retry/60s gate's attempt half).</summary>
    public const int MaxAutoRetries = 3;

    /// <summary>The C++ gate's delay half: a Retry-After never holds the request longer
    /// than this (a huge or HTTP-date delay would otherwise park a bare-token caller for
    /// days).</summary>
    public static readonly TimeSpan MaxRetryAfterDelay = TimeSpan.FromSeconds(60);

    /// <summary>Opt in to the HTTP 429/503 retry (the RequestOptions.AutoRetry port).</summary>
    public static readonly HttpRequestOptionsKey<bool> AutoRetryOption = new("Flint.AutoRetry429");

    /// <summary>Opt in to the network-level manual retry gate (NumberOfManualRetries).</summary>
    public static readonly HttpRequestOptionsKey<bool> ManualRetryOption = new("Flint.ManualRetry");

    /// <summary>The request option the gate finds the sender's <see cref="Window"/> under;
    /// the Window itself installs it, callers never touch this key.</summary>
    private static readonly HttpRequestOptionsKey<Window> TimeoutSourceOption = new("Flint.TimeoutSource");

    /// <summary>The C++ RequestTimeout default: the window when the setting is left
    /// unconfigured, and the clamp fallback for a below-a-second value.</summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(60);

    private volatile int _maxManualRetries = 1;
    private long _requestTimeoutTicks = DefaultRequestTimeout.Ticks;

    /// <summary>How many times the manual retry gate re-sends a network-level failure (the
    /// NumberOfManualRetries port); a negative value retries nothing.</summary>
    public int MaxManualRetries
    {
        get => _maxManualRetries;
        set => _maxManualRetries = value < 0 ? 0 : value;
    }

    /// <summary>The RequestTimeout inactivity window (the C++ RequestTimeout): the executors
    /// arm it around each wait or read, so a value below a second falls back to the 60s
    /// default instead of cancelling instantly.</summary>
    public TimeSpan RequestTimeout
    {
        get => new(Interlocked.Read(ref _requestTimeoutTicks));
        set => Interlocked.Exchange(ref _requestTimeoutTicks,
            value < TimeSpan.FromSeconds(1) ? DefaultRequestTimeout.Ticks : value.Ticks);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var manual = request.Options.TryGetValue(ManualRetryOption, out var manualRetry) && manualRetry;
        var autoRetry = request.Options.TryGetValue(AutoRetryOption, out var retry429) && retry429;
        var hasWindow = request.Options.TryGetValue(TimeoutSourceOption, out var window);
        var manualRetries = MaxManualRetries;

        // the sender created the Window and handed it over; this gate arms it here with its
        // own RequestTimeout so the send is covered, and re-arms it around every wait or
        // re-send — a request without a Window simply runs without a window
        var sendToken = cancellationToken;
        if (hasWindow)
        {
            window!.Arm(RequestTimeout);
            sendToken = window.Token;
        }

        var autoRetryCount = 0;
        var manualCount = 0;
        while (true)
        {
            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(request, sendToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (manual && manualCount < manualRetries &&
                                               !cancellationToken.IsCancellationRequested)
            {
                manualCount++;
                window?.Progress();
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

                if (delay > MaxRetryAfterDelay)
                {
                    delay = MaxRetryAfterDelay;
                }

                response.Dispose();
                window?.Progress();
                await Task.Delay(delay, sendToken).ConfigureAwait(false);
                window?.Progress();
                continue;
            }

            return response;
        }
    }

    /// <summary>The exchange-scoped inactivity window: the sender creates it before the
    /// request goes out (passing its own cancellation token so both end the same channel)
    /// and keeps it across every post-response read — calling <see cref="Progress"/> at each
    /// progress checkpoint. Every progress restarts the countdown, so a steady transfer runs
    /// unbounded and only a real stall fires the cancellation; a pipeline without the gate
    /// never arms the window, progress no-ops and the exchange ends only through the
    /// caller's own cancellation.</summary>
    public sealed class Window : CancellationTokenSource
    {
        private readonly CancellationTokenRegistration _userRegistration;
        private readonly CancellationToken _userToken;
        private TimeSpan _window;

        public Window(HttpRequestMessage request, CancellationToken userToken)
        {
            _userToken = userToken;
            // the user's cancellation ends the exchange through this same source; the
            // registration is released on Dispose so a later user cancellation cannot fire
            // into a disposed source
            _userRegistration = userToken.Register(static w => ((Window)w!).Cancel(), this);
            request.Options.Set(TimeoutSourceOption, this);
        }

        /// <summary>The progress checkpoint: restart the countdown from now.</summary>
        public void Progress()
        {
            if (_window > TimeSpan.Zero)
            {
                CancelAfter(_window);
            }
        }

        /// <summary>Whether the current cancellation was the window's expiry (the caller's
        /// own token is untouched) — the test behind the timeout conversions.</summary>
        public bool TimedOut => IsCancellationRequested && !_userToken.IsCancellationRequested;

        /// <summary>The failure text of an expiry, carrying the armed window's length.</summary>
        public string TimeoutMessage =>
            $"transfer timed out: no data for {_window.TotalSeconds:F0} seconds (RequestTimeout)";

        /// <summary>The gate arms the window when the request enters it; an un-armed window
        /// (a pipeline without the gate) simply never fires.</summary>
        internal void Arm(TimeSpan window)
        {
            _window = window;
            Progress();
        }

        protected override void Dispose(bool disposing)
        {
            _userRegistration.Dispose();
            base.Dispose(disposing);
        }
    }
}
