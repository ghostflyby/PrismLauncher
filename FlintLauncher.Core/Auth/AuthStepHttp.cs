// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022 Sefa Eyeoglu <contact@scrumplex.net>
//
// A small HTTP runner used by the auth steps (no direct C++ counterpart file).
//
// Deviations:
//  - Upstream steps go through Net::Request/NetJob on QNetworkAccessManager. FlintLauncher.Core's
//    the plain request executor discards non-2xx response bodies, but the XSTS error responses (HTTP 401 with an
//    XErr object) and the device-code error JSON must be readable by the steps, so the auth layer
//    performs its requests through this helper on the shared net stack instead. The 429/503
//    auto-retry (3 attempts, Retry-After honored) mirrors RequestOptions.AutoRetry.
//  - QNetworkReply::NetworkError has no counterpart: a response carries its HttpStatusCode and a
//    request that got none carries the exception (TimeoutException, HttpRequestException), so the
//    Net::isApplicationError/isServerError classification becomes status-class tests on the response.
//  - QNetworkReply::errorString() texts are replaced by plain "HTTP status <code> for <url>" /
//    exception messages; the user-visible step messages embed them only in generic failures.

using System.Net;
using System.Text;
using FlintLauncher.Core.Net;

namespace FlintLauncher.Core.Auth;

/// <summary>Request description for <see cref="AuthStepHttp.SendAsync"/>.</summary>
public sealed class AuthStepRequest
{
    public required Uri Url { get; init; }

    /// <summary>Raw request body (form-encoded or JSON), or null for GET.</summary>
    public string? Body { get; init; }

    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } = [];

    /// <summary>Retry on HTTP 429/503 like Net::Request::enableAutoRetry(true).</summary>
    public bool AutoRetry { get; init; }
}

/// <summary>Result of <see cref="AuthStepHttp.SendAsync"/>.</summary>
public sealed class AuthStepResponse
{
    /// <summary>The status the service answered with; null when no response arrived.</summary>
    public HttpStatusCode? StatusCode { get; init; }

    /// <summary>Why no response arrived: a <see cref="TimeoutException"/> when the inactivity window
    /// elapsed, otherwise the <see cref="HttpRequestException"/>/<see cref="IOException"/> the transport
    /// threw. Null once a response of any status arrived.</summary>
    public Exception? Exception { get; init; }

    public string ErrorString { get; init; } = "";

    public byte[] Body { get; init; } = [];

    public string BodyText => Encoding.UTF8.GetString(Body);

    public bool Success => StatusCode is { } status && (int)status is >= 200 and < 300;

    /// <summary>The service answered, but neither with success nor with a server error (5xx): the
    /// request itself is what it objected to, not its own availability.</summary>
    public bool IsClientError =>
        StatusCode is { } status && !Success && status < HttpStatusCode.InternalServerError;
}

/// <summary>Minimal HTTP runner for the auth steps (see file header for why it exists).</summary>
public static class AuthStepHttp
{
    public static async Task<AuthStepResponse> SendAsync(AuthStepRequest request, HttpClient http,
        CancellationToken cancellationToken)
    {
        // RequestTimeout port (QNetworkReply::setTransferTimeout): the persistent client
        // carries no total-duration timeout, so each phase (headers, body) gets an inactivity
        // window instead. The window rides the request options; the transport's retry gate
        // arms it with its RequestTimeout and resets it around a 429/503 wait (the manual
        // retry gate stays off for auth — the C++ steps ran setAskRetry(false)).
        using var message = new HttpRequestMessage(
            request.Body is null ? HttpMethod.Get : HttpMethod.Post, request.Url);
        using var window = new RetryHandler.Window(message, cancellationToken);
        if (request.Body is not null)
        {
            message.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(request.Body));
        }

        foreach (var (name, value) in request.Headers)
        {
            if (message.Content is not null && name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                // Content-Type is a content header; request headers silently drop it
                message.Content.Headers.TryAddWithoutValidation(name, value);
            }
            else
            {
                message.Headers.TryAddWithoutValidation(name, value);
            }
        }

        message.Options.Set(RetryHandler.AutoRetryOption, request.AutoRetry);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead,
                window.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // user abort: let the task machinery route this to the aborted state
        }
        catch (OperationCanceledException) when (window.TimedOut)
        {
            // the RequestTimeout window elapsed before the headers arrived
            var timeout = new TimeoutException("Operation timed out");
            return new AuthStepResponse
            {
                Exception = timeout,
                ErrorString = timeout.Message,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return new AuthStepResponse
            {
                Exception = ex,
                ErrorString = ex.Message,
            };
        }

        using (response)
        {
            var statusCode = response.StatusCode;
            byte[] body;
            try
            {
                window.Progress();
                body = await response.Content.ReadAsByteArrayAsync(window.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (window.TimedOut)
            {
                var timeout = new TimeoutException("Operation timed out");
                return new AuthStepResponse
                {
                    Exception = timeout,
                    ErrorString = timeout.Message,
                };
            }
            catch (Exception)
            {
                body = [];
            }

            return new AuthStepResponse
            {
                StatusCode = statusCode,
                ErrorString = $"HTTP status {(int)statusCode} for {request.Url}",
                Body = body,
            };
        }
    }
}
