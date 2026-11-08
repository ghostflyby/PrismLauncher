// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// The one-shot fetch helpers over the persistent client: they carry the semantics the C++
// NetRequest gave every plain query — the RequestTimeout inactivity window, the
// network-level manual retry gate, and the uniform failure shape ("HTTP status <code> for
// <url>" plus the truncated response body) as a <see cref="FlintHttpException"/>. The
// download-plane tasks (cached/file transfers with progress and validators) live elsewhere;
// the message (headers, content, per-request retry flags) is the caller's to build.

using System.Text;
using FlintLauncher.ExternalApi;

namespace FlintLauncher.Core.Net;

/// <summary>The uniform query failure: the status-gate text of the C++ NetRequest
/// ("HTTP status &lt;code&gt; for &lt;url&gt;" plus up to 4 KiB of the response body).</summary>
public sealed class FlintHttpException(string reason) : Exception(reason)
{
    public string Reason => Message;
}

public static class FlintHttp
{
    public const int MaxErrorBodyLength = 4096;

    /// <summary>Sends a wire-layer operation with the transport policy of the fetch helpers
    /// below: the RequestTimeout inactivity window over the send and the body read, and the
    /// network-level manual retry gate. Every failure — a stalled exchange included — surfaces
    /// as the operation's own failure exception.</summary>
    public static Task<TResult> FetchAsync<TResult>(this IApiOperation<TResult> operation,
        HttpClient http, CancellationToken cancellationToken = default) =>
        SendWindowedAsync(operation, http, manualRetry: true, cancellationToken);

    /// <summary>Sends a wire-layer operation exactly once — the policy of the uploads, which
    /// must not be re-sent — under the RequestTimeout inactivity window. Every failure, a
    /// stalled exchange included, surfaces as the operation's own failure exception.</summary>
    public static Task<TResult> SendOnceAsync<TResult>(this IApiOperation<TResult> operation,
        HttpClient http, CancellationToken cancellationToken = default) =>
        SendWindowedAsync(operation, http, manualRetry: false, cancellationToken);

    private static async Task<TResult> SendWindowedAsync<TResult>(IApiOperation<TResult> operation,
        HttpClient http, bool manualRetry, CancellationToken cancellationToken)
    {
        using var window = new RetryHandler.Window(cancellationToken);
        try
        {
            return await operation.SendTo(http, HttpCompletionOption.ResponseHeadersRead, window.Token, request =>
            {
                request.Options.Set(RetryHandler.ManualRetryOption, manualRetry);
                request.Options.Set(RetryHandler.TimeoutSourceOption, window);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (window.TimedOut)
        {
            // the self-scheduled timeout is this exchange's own failure, not the caller's
            // cancellation
            throw ApiOperation.WrapFailure(operation, new TimeoutException(window.TimeoutMessage));
        }
    }

    /// <summary>Send and read the body as UTF-8 text with the standard failure gate.</summary>
    public static async Task<string> GetStringAsync(HttpClient http,
        HttpRequestMessage message, CancellationToken cancellationToken = default)
    {
        var bytes = await SendAsync(http, message, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Send and read the body with the standard failure gate. The message is
    /// disposed here; a non-2xx status (or an idle-timeout) raises
    /// <see cref="FlintHttpException"/>.</summary>
    public static async Task<byte[]> SendAsync(HttpClient http,
        HttpRequestMessage message, CancellationToken cancellationToken = default)
    {
        using var window = new RetryHandler.Window(cancellationToken);
        message.Options.Set(RetryHandler.ManualRetryOption, true);
        message.Options.Set(RetryHandler.TimeoutSourceOption, window);

        HttpResponseMessage response;
        try
        {
            using (message)
            {
                response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead,
                    window.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (window.TimedOut)
        {
            throw new FlintHttpException(window.TimeoutMessage);
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            // connection-level failure (DNS, refused, reset) after the retry gate — the
            // transport's text is the reason callers surface
            throw new FlintHttpException(e.Message);
        }

        using (response)
        {
            var statusCode = (int)response.StatusCode;
            if (statusCode is not (>= 200 and < 300))
            {
                // the error body is best-effort (the C++ NetRequest read it defensively too)
                var body = "";
                try
                {
                    window.Progress();
                    body = await response.Content.ReadAsStringAsync(window.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (window.TimedOut)
                {
                    // the self-scheduled timeout is this operation's own semantic — a domain
                    // error, not a swallowed cancellation
                    throw new FlintHttpException(window.TimeoutMessage);
                }
                // a body read that fails contributes an empty body — the status line below
                // is the message. The caller's own cancellation (abort) is NOT caught: wrap
                // non-OCE, propagate OCE.
                // ReSharper disable once EmptyGeneralCatchClause
                catch (Exception e) when (e is not OperationCanceledException)
                {
                }

                if (body.Length > MaxErrorBodyLength)
                {
                    body = body[..MaxErrorBodyLength];
                }

                throw new FlintHttpException(
                    $"HTTP status {statusCode} for {message.RequestUri}\n{body}");
            }

            window.Progress();
            try
            {
                return await response.Content.ReadAsByteArrayAsync(window.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (window.TimedOut)
            {
                // a body read stalled past the window: a network failure, not a user abort
                throw new FlintHttpException(window.TimeoutMessage);
            }
        }
    }
}
