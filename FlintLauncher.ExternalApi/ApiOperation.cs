// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// The base contract for typed endpoint operations: an operation value describes one request
// (CreateRequest, replayable — fresh message and buffered content per call) and owns how its
// response is read into the result type, status semantics included. Parameters that cannot
// be bound on a replayable value — one-shot content, the per-execution targets of the read —
// are not fields of the value: they are parameterized through
// <see cref="IApiOperation{TArgs,TResult}"/> and arrive at SendTo time. The shared pipeline
// sends the request through the executor the operation was created from and hands the
// (request, response, body stream) triple to the operation's reader; everything
// transport-shaped — proxy, user agent, timeouts, retries, resilience — belongs to the
// executor's handler chain, not here. A send, gate or read failure is wrapped by the
// executor into the API's own failure type; cancellation is never wrapped.

namespace FlintLauncher.ExternalApi;

/// <summary>The execution face of an API navigation root: it sends prepared requests and
/// shapes their failures into the API's own exception type. The configuration the root owns
/// (hosts, tokens) is read per send, by the operations it builds and by the sender.</summary>
public interface IApiExecutor
{
    /// <summary>Sends one request; the message is owned and disposed by the caller.</summary>
    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct);

    /// <summary>Wraps a transport, status-gate or read failure into the API's failure
    /// type; the pipeline throws the result. Cancellation never reaches this.</summary>
    Exception WrapFailure(Exception failure);
}

/// <summary>An endpoint operation value without execution-time arguments: everything needed
/// to describe the request and its read is bound at construction, the request content is
/// replayable (buffered or empty), and the value knows the executor it belongs to — so it
/// can be sent repeatedly, independently of whoever built it. Parameters that cannot carry
/// that replayability (one-shot content, per-execution targets) belong in
/// <see cref="IApiOperation{TArgs,TResult}"/>, not here.</summary>
/// <typeparam name="TResult">The read result.</typeparam>
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
    /// semantics and the body's shape: gate through
    /// <see cref="ApiOperation.EnsureSuccessAsync"/> (or the endpoint's own rule), then
    /// decode — the body streams (JSON through the serializer's stream API, text through a
    /// reader, files straight to disk). The message, the response and the stream stay owned
    /// by the pipeline and must not be disposed here.</summary>
    Task<TResult> ReadResponseAsync(HttpRequestMessage request, HttpResponseMessage response,
        Stream body, CancellationToken ct);
}

/// <summary>An endpoint operation value with execution-time arguments: parameters that
/// cannot be bound on a replayable value — one-shot content, the per-execution targets of
/// the read — arrive through <typeparamref name="TArgs"/> at SendTo time and are exclusively
/// owned by the send, released with the request message. Whatever is still replayable stays
/// bound at construction under the <see cref="IApiOperation{TResult}"/> rules.</summary>
/// <typeparam name="TArgs">The per-send arguments.</typeparam>
/// <typeparam name="TResult">The read result.</typeparam>
public interface IApiOperation<TArgs, TResult>
{
    /// <summary>The executor this operation was created from — the send target of
    /// <see cref="ApiOperation.SendTo{TArgs,TResult}(IApiOperation{TArgs,TResult},TArgs,CancellationToken)"/>.
    /// </summary>
    IApiExecutor Executor { get; }

    /// <summary>Builds a fresh request message for this send; the content must be replayable
    /// (buffered or empty).</summary>
    HttpRequestMessage CreateRequest(TArgs args);

    /// <summary>Reads the response into the result type for this send, owning the endpoint's
    /// status semantics and the body's shape (see
    /// <see cref="IApiOperation{TResult}.ReadResponseAsync"/>). The message, the response and
    /// the stream stay owned by the pipeline and must not be disposed here.</summary>
    Task<TResult> ReadResponseAsync(TArgs args, HttpRequestMessage request, HttpResponseMessage response,
        Stream body, CancellationToken ct);
}

/// <summary>The shared execution pipeline for all typed API operations: send through the
/// executor and hand the (request, response, body stream) triple to the operation's own
/// reader. Owns the disposal of the request and the response.</summary>
public static class ApiOperation
{
    /// <summary>The body excerpt cap of the status-gate message.</summary>
    public const int MaxErrorBodyLength = 4096;

    /// <summary>The status gate most endpoints share: non-2xx raises HttpRequestException
    /// carrying the status, the URL and up to <see cref="MaxErrorBodyLength"/> characters
    /// of the body.</summary>
    public static async Task EnsureSuccessAsync(HttpRequestMessage request, HttpResponseMessage response,
        Stream body, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        using var reader = new StreamReader(body);
        var buffer = new char[MaxErrorBodyLength + 1];
        var read = await reader.ReadBlockAsync(buffer, ct).ConfigureAwait(false);
        var excerpt = new string(buffer, 0, Math.Min(read, MaxErrorBodyLength));
        throw new HttpRequestException(
            $"HTTP status {(int)response.StatusCode} for {request.RequestUri}\n{excerpt}",
            inner: null,
            statusCode: response.StatusCode);
    }

    /// <summary>Sends the operation through the executor it was created from. Transport,
    /// gate and read failures surface as the executor's wrapped failure; cancellation
    /// propagates untouched.</summary>
    public static async Task<TResult> SendTo<TResult>(this IApiOperation<TResult> operation,
        CancellationToken ct = default)
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

    /// <summary>The raw pipeline: send through the given executor and hand the
    /// (request, response, body stream) triple to the operation's reader. No failure shaping
    /// happens here.</summary>
    public static async Task<TResult> SendTo<TResult>(this IApiOperation<TResult> operation,
        IApiExecutor executor, CancellationToken ct)
    {
        using var request = operation.CreateRequest();
        using var response = await executor.SendAsync(request, ct).ConfigureAwait(false);
        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await operation.ReadResponseAsync(request, response, body, ct).ConfigureAwait(false);
    }

    /// <summary>Sends the operation with per-send arguments through the executor it was
    /// created from; the arguments are exclusively owned by this send. Transport, gate and
    /// read failures surface as the executor's wrapped failure; cancellation propagates
    /// untouched.</summary>
    public static async Task<TResult> SendTo<TArgs, TResult>(this IApiOperation<TArgs, TResult> operation,
        TArgs args, CancellationToken ct = default)
    {
        try
        {
            return await SendTo(operation, args, operation.Executor, ct).ConfigureAwait(false);
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

    /// <summary>The raw pipeline for argument-carrying operations: send through the given
    /// executor and hand the (args, request, response, body stream) tuple to the operation's
    /// reader. No failure shaping happens here.</summary>
    public static async Task<TResult> SendTo<TArgs, TResult>(this IApiOperation<TArgs, TResult> operation,
        TArgs args, IApiExecutor executor, CancellationToken ct)
    {
        using var request = operation.CreateRequest(args);
        using var response = await executor.SendAsync(request, ct).ConfigureAwait(false);
        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await operation.ReadResponseAsync(args, request, response, body, ct).ConfigureAwait(false);
    }
}
