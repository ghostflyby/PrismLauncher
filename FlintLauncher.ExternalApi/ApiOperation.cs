// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// The base contract for typed endpoint operations: an operation value describes one request
// (CreateRequest, replayable — fresh message and buffered content per call) and owns how its
// response is read into the result type, status semantics included. The shared pipeline sends
// the request through the executor the operation was created from and hands the
// (request, response, body) triple to the operation's reader; everything transport-shaped —
// proxy, user agent, timeouts, retries, resilience — belongs to the executor's handler chain,
// not here. A send, gate or decode failure is wrapped by the executor into the API's own
// failure type; cancellation is never wrapped.

namespace FlintLauncher.ExternalApi;

/// <summary>The execution face of an API navigation root: it sends prepared requests and
/// shapes their failures into the API's own exception type. The configuration the root owns
/// (hosts, tokens) is read per send, by the operations it builds and by the sender.</summary>
public interface IApiExecutor
{
    /// <summary>Sends one request; the message is owned and disposed by the caller.</summary>
    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct);

    /// <summary>Wraps a transport, status-gate or decode failure into the API's failure
    /// type; the pipeline throws the result. Cancellation never reaches this.</summary>
    Exception WrapFailure(Exception failure);
}

/// <summary>An endpoint operation value: everything needed to describe the request is bound
/// at construction, the request content is replayable (buffered or empty), and the value
/// knows the executor it belongs to — so it can be sent repeatedly, independently of whoever
/// built it.</summary>
/// <typeparam name="TResult">The decoded result; always a reference type.</typeparam>
public interface IApiOperation<TResult>
{
    /// <summary>The executor this operation was created from — the send target of
    /// <see cref="ApiOperation.SendTo{TResult}(IApiOperation{TResult}, CancellationToken)"/>.
    /// </summary>
    IApiExecutor Executor { get; }

    /// <summary>Builds a fresh request message with the operation's method, URL, headers and
    /// body; the content must be replayable (buffered or empty).</summary>
    HttpRequestMessage CreateRequest();

    /// <summary>Reads the response into the result type, owning the endpoint's status
    /// semantics: gate through <see cref="ApiOperation.EnsureSuccess"/> (or the endpoint's
    /// own rule) and decode the fully buffered body. The message and the response stay
    /// owned by the pipeline and must not be disposed here.</summary>
    ValueTask<TResult> ReadResponseAsync(HttpRequestMessage request, HttpResponseMessage response,
        byte[] body, CancellationToken ct);
}

/// <summary>The shared execution pipeline for all typed API operations: send through the
/// executor, buffer the body, and hand the triple to the operation's own reader. Owns the
/// disposal of the request and the response.</summary>
public static class ApiOperation
{
    /// <summary>The body excerpt cap of the status-gate message.</summary>
    public const int MaxErrorBodyLength = 4096;

    /// <summary>The status gate most endpoints share: non-2xx raises HttpRequestException
    /// carrying the status, the URL and up to <see cref="MaxErrorBodyLength"/> characters
    /// of the body.</summary>
    public static void EnsureSuccess(HttpRequestMessage request, HttpResponseMessage response, byte[] body)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string excerpt;
        using (var reader = new StreamReader(new MemoryStream(body)))
        {
            var buffer = new char[MaxErrorBodyLength + 1];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            excerpt = new string(buffer, 0, Math.Min(read, MaxErrorBodyLength));
        }

        throw new HttpRequestException(
            $"HTTP status {(int)response.StatusCode} for {request.RequestUri}\n{excerpt}",
            inner: null,
            statusCode: response.StatusCode);
    }

    /// <summary>Sends the operation through the executor it was created from. Transport,
    /// gate and decode failures surface as the executor's wrapped failure; cancellation
    /// propagates untouched.</summary>
    public static async Task<TResult> SendTo<TResult>(this IApiOperation<TResult> operation,
        CancellationToken ct = default) where TResult : class
    {
        try
        {
            return await SendTo(operation, operation.Executor, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw operation.Executor.WrapFailure(e);
        }
    }

    /// <summary>The raw pipeline: send through the given executor, buffer the body, hand the
    /// triple to the operation's reader. No failure shaping happens here.</summary>
    public static async Task<TResult> SendTo<TResult>(this IApiOperation<TResult> operation,
        IApiExecutor executor, CancellationToken ct) where TResult : class
    {
        using var request = operation.CreateRequest();
        using var response = await executor.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return await operation.ReadResponseAsync(request, response, body, ct).ConfigureAwait(false);
    }
}
