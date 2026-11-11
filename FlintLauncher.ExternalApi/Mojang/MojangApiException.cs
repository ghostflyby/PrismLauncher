// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

using System.Net;

namespace FlintLauncher.ExternalApi.Mojang;

/// <summary>The per-API failure: wraps the inner exception (transport, status or decode) with
/// the account service failure message. Cancellation is never wrapped. An endpoint whose
/// refusal carries structured data refines this type (the XSTS denial).</summary>
public class MojangApiException(string message, Exception innerException)
    : Exception(message, innerException)
{
    /// <summary>The status the service answered with; null when no answer arrived or the
    /// answer could not be decoded.</summary>
    public HttpStatusCode? StatusCode => (InnerException as HttpRequestException)?.StatusCode;

    /// <summary>The service answered, but neither with success nor with a server error (5xx):
    /// the request itself is what it objected to, not its own availability.</summary>
    public bool IsClientError => StatusCode < HttpStatusCode.InternalServerError;
}

/// <summary>The XSTS refusal: the authorization endpoint answered 401 and its body decides
/// which refusal it was (see the Denial shapes). The ladder of its error codes is the
/// caller's to walk.</summary>
public sealed class MojangXstsDeniedException(Uri url, MojangXstsAnswer denial)
    : MojangApiException($"HTTP status {(int)HttpStatusCode.Unauthorized} for {url}",
        new HttpRequestException(
            $"HTTP status {(int)HttpStatusCode.Unauthorized} for {url}",
            inner: null, statusCode: HttpStatusCode.Unauthorized))
{
    /// <summary>The shape of the refusal: the XErr code, or why the body could not name
    /// one.</summary>
    public MojangXstsAnswer Denial { get; } = denial;
}
