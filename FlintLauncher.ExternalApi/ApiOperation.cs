// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// The base contract for typed endpoint operations: an operation value describes one request
// (CreateRequest, replayable — fresh message and buffered content per call) and owns how its
// response is read into the result type — the endpoint handles the response message itself,
// status semantics and body shape included — and it decides the exception its failures wear,
// uniform across everything built from one root. The value holds no executor: sending is
// always through an executor handed to SendTo. Parameters that cannot be bound on a
// replayable value — one-shot content, the per-execution targets of the read — are not
// fields of the value: they are parameterized through
// <see cref="IApiOperation{TArgs,TResult}"/> and arrive at SendTo time. The shared pipeline
// sends and hands the (request, response) pair to the operation's reader; everything
// transport-shaped — proxy, user agent, timeouts, retries, resilience — belongs to the
// executor's handler chain, not here. Cancellation is never shaped into a failure.

namespace FlintLauncher.ExternalApi;

/// <summary>The execution face of an API navigation root: pure transport — it sends prepared
/// requests; the configuration the root owns (hosts, tokens) is read per send. Failure
/// shaping is not transport and lives with the operations.</summary>
public interface IApiExecutor
{
    /// <summary>Sends one request; the message is owned and disposed by the caller.</summary>
    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct);
}

/// <summary>An endpoint operation value without execution-time arguments: everything needed
/// to describe the request and its read is bound at construction, the request content is
/// replayable (buffered or empty), and the value carries no executor — it can be sent
/// repeatedly through whatever executor of its API a caller hands to SendTo. Parameters
/// that cannot carry that replayability (one-shot content, per-execution targets) belong in
/// <see cref="IApiOperation{TArgs,TResult}"/>, not here.</summary>
/// <typeparam name="TResult">The read result.</typeparam>
public interface IApiOperation<TResult>
{
    /// <summary>Builds a fresh request message with the operation's method, URL, headers and
    /// body; the content must be replayable (buffered or empty).</summary>
    HttpRequestMessage CreateRequest();

    /// <summary>The failure decision of this builder: wraps a transport, gate or read
    /// failure into the exception the API wears — uniform across every operation of a root.
    /// The pipeline throws the result; cancellation never reaches this.</summary>
    Exception WrapFailure(Exception failure);

    /// <summary>Reads the response into the result type, owning the endpoint's status
    /// semantics and body shape: gate through <see cref="ApiOperation.EnsureSuccessAsync"/>
    /// (or the endpoint's own rule), then take the body from the response message and decode
    /// it in its natural shape (JSON through the serializer's stream API, text through a
    /// reader, files straight to disk). The message and the response stay owned by the
    /// pipeline and must not be disposed here.</summary>
    Task<TResult> ReadResponseAsync(HttpRequestMessage request, HttpResponseMessage response,
        CancellationToken ct);
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
    /// <summary>Builds a fresh request message for this send; the content must be replayable
    /// (buffered or empty).</summary>
    HttpRequestMessage CreateRequest(TArgs args);

    /// <summary>The failure decision of this builder (see
    /// <see cref="IApiOperation{TResult}.WrapFailure"/>).</summary>
    Exception WrapFailure(Exception failure);

    /// <summary>Reads the response into the result type for this send, owning the endpoint's
    /// status semantics and body shape (see
    /// <see cref="IApiOperation{TResult}.ReadResponseAsync"/>). The message and the response
    /// stay owned by the pipeline and must not be disposed here.</summary>
    Task<TResult> ReadResponseAsync(TArgs args, HttpRequestMessage request, HttpResponseMessage response,
        CancellationToken ct);
}

/// <summary>The shared execution pipeline for all typed API operations: send through the
/// handed executor and give the (request, response) pair to the operation's own reader;
/// a failed send, gate or read surfaces as the operation's wrapped failure, while
/// cancellation propagates untouched. Owns the disposal of the request and the
/// response.</summary>
public static class ApiOperation
{
    /// <summary>The body excerpt cap of the status-gate message.</summary>
    public const int MaxErrorBodyLength = 4096;

    /// <summary>The status gate most endpoints share: non-2xx raises HttpRequestException
    /// carrying the status, the URL and up to <see cref="MaxErrorBodyLength"/> characters
    /// of the response body.</summary>
    public static async Task EnsureSuccessAsync(HttpRequestMessage request, HttpResponseMessage response,
        CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(body);
        var buffer = new char[MaxErrorBodyLength + 1];
        var read = await reader.ReadBlockAsync(buffer, ct).ConfigureAwait(false);
        var excerpt = new string(buffer, 0, Math.Min(read, MaxErrorBodyLength));
        throw new HttpRequestException(
            $"HTTP status {(int)response.StatusCode} for {request.RequestUri}\n{excerpt}",
            inner: null,
            statusCode: response.StatusCode);
    }

    /// <summary>Sends the operation through the given executor. Transport, gate and read
    /// failures surface as the operation's wrapped failure; cancellation propagates
    /// untouched.</summary>
    public static async Task<TResult> SendTo<TResult>(this IApiOperation<TResult> operation,
        IApiExecutor executor, CancellationToken ct = default)
    {
        try
        {
            using var request = operation.CreateRequest();
            using var response = await executor.SendAsync(request, ct).ConfigureAwait(false);
            return await operation.ReadResponseAsync(request, response, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw operation.WrapFailure(e);
        }
    }

    /// <summary>Sends the operation with per-send arguments through the given executor; the
    /// arguments are exclusively owned by this send. Transport, gate and read failures
    /// surface as the operation's wrapped failure; cancellation propagates untouched.</summary>
    public static async Task<TResult> SendTo<TArgs, TResult>(this IApiOperation<TArgs, TResult> operation,
        TArgs args, IApiExecutor executor, CancellationToken ct = default)
    {
        try
        {
            using var request = operation.CreateRequest(args);
            using var response = await executor.SendAsync(request, ct).ConfigureAwait(false);
            return await operation.ReadResponseAsync(args, request, response, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw operation.WrapFailure(e);
        }
    }
}
