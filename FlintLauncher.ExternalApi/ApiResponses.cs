// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

namespace FlintLauncher.ExternalApi;

/// <summary>Response-reading patterns endpoints may adopt in their own
/// <see cref="IApiOperation{TResult}.ReadResponseAsync"/>; nothing here runs unless an
/// endpoint calls it.</summary>
public static class ApiResponses
{
    /// <summary>The body excerpt cap of <see cref="EnsureSuccessAsync"/>'s failure message.</summary>
    public const int MaxErrorExcerptLength = 4096;

    /// <summary>The pattern for an endpoint whose non-2xx answers carry no data of their own
    /// (an error page instead of the payload): a 2xx passes; anything else raises
    /// HttpRequestException carrying the status, the request URL and up to
    /// <see cref="MaxErrorExcerptLength"/> characters of the body. The body is best-effort —
    /// a read that fails contributes an empty excerpt, and the status failure still
    /// surfaces.</summary>
    public static async Task EnsureSuccessAsync(this HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var excerpt = "";
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(body);
            var buffer = new char[MaxErrorExcerptLength + 1];
            var read = await reader.ReadBlockAsync(buffer, ct).ConfigureAwait(false);
            excerpt = new string(buffer, 0, Math.Min(read, MaxErrorExcerptLength));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // the excerpt is best-effort: the status failure below is the failure
        }

        throw new HttpRequestException(
            $"HTTP status {(int)response.StatusCode} for {response.RequestMessage?.RequestUri}\n{excerpt}",
            inner: null,
            statusCode: response.StatusCode);
    }
}
