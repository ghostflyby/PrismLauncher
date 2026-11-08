// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022 Sefa Eyeoglu <contact@scrumplex.net>
//
// The executor the auth steps send their account service operations through (no direct C++
// counterpart file).
//
// Deviations:
//  - Upstream steps go through Net::Request/NetJob on QNetworkAccessManager. The XSTS error
//    responses (HTTP 401 with an XErr object) and the device-code error JSON must be
//    readable by the steps, so the operations read every answer whole. The 429/503 auto-retry
//    (3 attempts, Retry-After honored) mirrors RequestOptions.AutoRetry.
//  - QNetworkReply::NetworkError has no counterpart: a failure carries the status the service
//    answered with, or the exception of a request that got none (TimeoutException,
//    HttpRequestException), so the Net::isApplicationError/isServerError classification becomes
//    status-class tests on MojangApiException.
//  - QNetworkReply::errorString() texts are replaced by plain "HTTP status <code> for <url>" /
//    exception messages; the user-visible step messages embed them only in generic failures.

using FlintLauncher.Core.Net;
using FlintLauncher.ExternalApi;
using FlintLauncher.ExternalApi.Mojang;

namespace FlintLauncher.Core.Auth;

/// <summary>The auth steps' executor (see file header for why it exists).</summary>
public static class AuthStepHttp
{
    /// <summary>Sends an account service operation; <paramref name="autoRetry"/> opts in to
    /// the 429/503 retry like Net::Request::enableAutoRetry(true). A failure, an elapsed
    /// inactivity window included, surfaces as <see cref="MojangApiException"/>; the caller's
    /// cancellation propagates.</summary>
    public static async Task<TResult> SendAsync<TResult>(IApiOperation<TResult> operation, HttpClient http,
        bool autoRetry, CancellationToken cancellationToken)
    {
        // RequestTimeout port (QNetworkReply::setTransferTimeout): the persistent client
        // carries no total-duration timeout, so each phase (headers, body) gets an inactivity
        // window instead. The window rides the request options; the transport's retry gate
        // arms it with its RequestTimeout and resets it around a 429/503 wait (the manual
        // retry gate stays off for auth — the C++ steps ran setAskRetry(false)).
        using var window = new RetryHandler.Window(cancellationToken);
        try
        {
            return await operation.SendTo(http, HttpCompletionOption.ResponseHeadersRead, window.Token,
                request =>
                {
                    request.Options.Set(RetryHandler.AutoRetryOption, autoRetry);
                    request.Options.Set(RetryHandler.TimeoutSourceOption, window);
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (window.TimedOut)
        {
            // the RequestTimeout window elapsed before the headers or the body arrived
            throw ApiOperation.WrapFailure(operation, new TimeoutException("Operation timed out"));
        }
    }
}
