// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/net/NetUtils.h (error classification) plus a small HTTP runner used by the
// auth steps (no direct C++ counterpart file).
//
// Deviations:
//  - Upstream steps go through Net::Request/NetJob on QNetworkAccessManager. FlintLauncher.Core's
//    NetRequest discards non-2xx response bodies, but the XSTS error responses (HTTP 401 with an
//    XErr object) and the device-code error JSON must be readable by the steps, so the auth layer
//    performs its requests through this helper on the shared net stack instead. The 429/503
//    auto-retry (3 attempts, Retry-After honored) mirrors RequestOptions.AutoRetry.
//  - QNetworkReply::NetworkError is represented by its Qt integer code (see QtNetworkError) so
//    AccountData::networkError and the isApplicationError/isServerError classification port 1:1.
//  - QNetworkReply::errorString() texts are replaced by plain "HTTP status <code> for <url>" /
//    exception messages; the user-visible step messages embed them only in generic failures.
using System.Net;
using FlintLauncher.Core.Net;

namespace FlintLauncher.Core.Auth;

/// <summary>The subset of QNetworkReply::NetworkError the auth layer reasons about (Qt codes).</summary>
public enum QtNetworkError
{
    NoError = 0,
    TimeoutError = 4,
    OperationCanceledError = 5,
    ProxyAuthenticationRequiredError = 105,
    ContentAccessDenied = 201,
    ContentOperationNotPermittedError = 202,
    ContentNotFoundError = 203,
    AuthenticationRequiredError = 204,
    ContentConflictError = 206,
    ContentGoneError = 207,
    UnknownContentError = 299,
    InternalServerError = 401,
    OperationNotImplementedError = 402,
    ServiceUnavailableError = 403,
    UnknownServerError = 499,
    ProtocolInvalidOperationError = 302,
    UnknownNetworkError = 99,
}

/// <summary>Port of Net::isApplicationError / Net::isServerError (Qt QNetworkReply error codes).</summary>
public static class AuthNetUtils
{
    public static bool IsApplicationError(int e)
    {
        // ProtocolInvalidOperationError, AuthenticationRequiredError, ContentAccessDenied,
        // ContentNotFoundError, ContentOperationNotPermittedError, ProxyAuthenticationRequiredError,
        // ContentConflictError, ContentGoneError, InternalServerError, OperationNotImplementedError,
        // ServiceUnavailableError, UnknownServerError, UnknownContentError
        return e is 302 or 204 or 201 or 203 or 202 or 105 or 206 or 207 or 401 or 402 or 403 or 499 or 299;
    }

    // 500 class errors; microsoft may send these when the auth services are down. We treat this
    // as a reason to launch in offline mode.
    public static bool IsServerError(int e)
    {
        return e is 401 or 402 or 403 or 499; // InternalServer, NotImplemented, ServiceUnavailable, UnknownServer
    }
}

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
    /// <summary>Qt QNetworkReply error code (0 == NoError).</summary>
    public int Error { get; init; }

    public string ErrorString { get; init; } = "";

    /// <summary>HTTP status code, 0 when the request failed on the transport level.</summary>
    public int StatusCode { get; init; }

    public byte[] Body { get; init; } = [];

    public string BodyText => System.Text.Encoding.UTF8.GetString(Body);

    public bool Success => Error == (int)QtNetworkError.NoError;
}

/// <summary>Minimal HTTP runner for the auth steps (see file header for why it exists).</summary>
public static class AuthStepHttp
{
    public const int MaxAutoRetries = 3;

    public static async Task<AuthStepResponse> SendAsync(AuthStepRequest request, CancellationToken cancellationToken)
    {
        var retries = 0;
        while (true)
        {
            using var message = new HttpRequestMessage(
                request.Body is null ? HttpMethod.Get : HttpMethod.Post, request.Url);
            if (request.Body is not null)
            {
                message.Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(request.Body));
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

            HttpResponseMessage response;
            try
            {
                response = await NetStack.Default.Client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // user abort: let the task machinery route this to the aborted state
            }
            catch (TaskCanceledException)
            {
                // HttpClient timeout
                return new AuthStepResponse
                {
                    Error = (int)QtNetworkError.TimeoutError,
                    ErrorString = "Operation timed out",
                };
            }
            catch (Exception ex) when (ex is HttpRequestException or System.IO.IOException)
            {
                return new AuthStepResponse
                {
                    Error = (int)QtNetworkError.UnknownNetworkError,
                    ErrorString = ex.Message,
                };
            }

            using (response)
            {
                var statusCode = (int)response.StatusCode;
                if (request.AutoRetry &&
                    statusCode is 429 or 503 &&
                    retries < MaxAutoRetries)
                {
                    retries++;
                    var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.Zero;
                    if (delay < TimeSpan.Zero)
                    {
                        delay = TimeSpan.Zero;
                    }
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    }
                    continue;
                }

                byte[] body;
                try
                {
                    body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    body = [];
                }

                return new AuthStepResponse
                {
                    Error = StatusCodeToQtError(statusCode),
                    ErrorString = $"HTTP status {statusCode} for {request.Url}",
                    StatusCode = statusCode,
                    Body = body,
                };
            }
        }
    }

    /// <summary>Maps HTTP status codes onto the QNetworkReply codes (qtbase qhttpthreaddelegate mapping).</summary>
    private static int StatusCodeToQtError(int status) => status switch
    {
        >= 200 and < 300 => (int)QtNetworkError.NoError,
        401 => (int)QtNetworkError.AuthenticationRequiredError,
        403 => (int)QtNetworkError.ContentAccessDenied,
        404 => (int)QtNetworkError.ContentNotFoundError,
        405 => (int)QtNetworkError.ContentOperationNotPermittedError,
        409 => (int)QtNetworkError.ContentConflictError,
        410 => (int)QtNetworkError.ContentGoneError,
        500 => (int)QtNetworkError.InternalServerError,
        501 => (int)QtNetworkError.OperationNotImplementedError,
        503 => (int)QtNetworkError.ServiceUnavailableError,
        < 500 => (int)QtNetworkError.UnknownContentError,
        _ => (int)QtNetworkError.UnknownServerError,
    };
}
