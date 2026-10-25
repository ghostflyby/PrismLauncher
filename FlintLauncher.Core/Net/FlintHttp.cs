// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// The one-shot fetch helpers over the persistent client pair: they carry the semantics the
// retired NetRequest gave every plain query — the RequestTimeout inactivity window, the
// network-level manual retry gate, and the uniform failure shape ("HTTP status <code> for
// <url>" plus the truncated response body) as a <see cref="FlintHttpException"/>. The
// download-plane tasks (cached/file transfers with progress and validators) live elsewhere;
// the message (headers, content, per-request retry flags) is the caller's to build.

using System.Text;

namespace FlintLauncher.Core.Net;

/// <summary>The uniform query failure: the status-gate text of the old NetRequest
/// ("HTTP status &lt;code&gt; for &lt;url&gt;" plus up to 4 KiB of the response body).</summary>
public sealed class FlintHttpException(string reason) : Exception(reason)
{
    public string Reason => Message;
}

public static class FlintHttp
{
    public const int MaxErrorBodyLength = 4096;

    /// <summary>Send and read the body as UTF-8 text with the standard failure gate.</summary>
    public static async Task<string> GetStringAsync(HttpClient http, NetSettings settings,
        HttpRequestMessage message, CancellationToken cancellationToken = default)
    {
        var bytes = await SendAsync(http, settings, message, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Send and read the body with the standard failure gate. The message is
    /// disposed here; a non-2xx status (or an idle-timeout) raises
    /// <see cref="FlintHttpException"/>.</summary>
    public static async Task<byte[]> SendAsync(HttpClient http, NetSettings settings,
        HttpRequestMessage message, CancellationToken cancellationToken = default)
    {
        var requestTimeout = settings.Current.RequestTimeout;
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(requestTimeout);

        message.Options.Set(RetryHandler.ManualRetryOption, true);
        message.Options.Set(RetryHandler.TimeoutSourceOption, timeoutSource);

        HttpResponseMessage response;
        try
        {
            using (message)
            {
                response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead,
                    timeoutSource.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new FlintHttpException(
                $"transfer timed out: no data for {requestTimeout.TotalSeconds:F0} seconds (RequestTimeout)");
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            // connection-level failure (DNS, refused, reset) after the retry gate — the
            // old NetRequest surfaced it as job.FailReason, so the envelopes keep the text
            throw new FlintHttpException(e.Message);
        }

        using (response)
        {
            var statusCode = (int)response.StatusCode;
            if (statusCode is not (>= 200 and < 300))
            {
                // the error body is best-effort (the old NetRequest read it defensively too)
                var body = "";
                try
                {
                    timeoutSource.CancelAfter(requestTimeout);
                    body = await response.Content.ReadAsStringAsync(timeoutSource.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new FlintHttpException(
                        $"transfer timed out: no data for {requestTimeout.TotalSeconds:F0} seconds (RequestTimeout)");
                }
                catch (Exception)
                {
                }

                if (body.Length > MaxErrorBodyLength)
                {
                    body = body[..MaxErrorBodyLength];
                }

                throw new FlintHttpException(
                    $"HTTP status {statusCode} for {message.RequestUri}\n{body}");
            }

            timeoutSource.CancelAfter(requestTimeout);
            try
            {
                return await response.Content.ReadAsByteArrayAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // a body read stalled past the window: a network failure, not a user abort
                throw new FlintHttpException(
                    $"transfer timed out: no data for {requestTimeout.TotalSeconds:F0} seconds (RequestTimeout)");
            }
        }
    }
}
