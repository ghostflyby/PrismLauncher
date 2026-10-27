// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

namespace FlintLauncher.ExternalApi;

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
    protected internal HttpRequestMessage CreateRequest();

    /// <summary>The failure decision of this builder: wraps a transport, gate or read
    /// failure into the exception the API wears — uniform across every operation of a root.
    /// The pipeline throws the result; cancellation never reaches this.</summary>
    protected internal Exception WrapFailure(Exception failure);

    /// <summary>Reads the response into the result type, owning the endpoint's status
    /// semantics and body shape: gate through <see cref="ApiOperation.EnsureSuccessAsync"/>
    /// (or the endpoint's own rule), then take the body from the response message and decode
    /// it in its natural shape (JSON through the serializer's stream API, text through a
    /// reader, files straight to disk). The response stays owned by the pipeline and must
    /// not be disposed here.</summary>
    protected internal Task<TResult> ReadResponseAsync(HttpResponseMessage response,
        CancellationToken ct);
}

/// <summary>An endpoint operation value with execution-time arguments: parameters that
/// cannot be bound on a replayable value — one-shot content, the per-execution targets of
/// the read — arrive through <typeparamref name="TArgs"/> at SendTo time and are exclusively
/// owned by the send, released with the request message. Whatever is still replayable stays
/// bound at construction under the <see cref="IApiOperation{TResult}"/> rules.</summary>
/// <typeparam name="TArgs">The per-send arguments.</typeparam>
/// <typeparam name="TResult">The read result.</typeparam>
public interface IApiOperation<in TArgs, TResult>
{
    /// <summary>Builds a fresh request message for this send; the content must be replayable
    /// (buffered or empty).</summary>
    HttpRequestMessage CreateRequest(TArgs args);

    /// <summary>The failure decision of this builder (see
    /// <see cref="IApiOperation{TResult}.WrapFailure"/>).</summary>
    Exception WrapFailure(Exception failure);

    /// <summary>Reads the response into the result type for this send, owning the endpoint's
    /// status semantics and body shape (see
    /// <see cref="IApiOperation{TResult}.ReadResponseAsync"/>). The response stays owned by
    /// the pipeline and must not be disposed here.</summary>
    Task<TResult> ReadResponseAsync(TArgs args, HttpResponseMessage response,
        CancellationToken ct);
}

/// <summary>The shared execution pipeline for all typed API operations: send through the
/// handed executor and give the response to the operation's own reader; a failed send, gate
/// or read surfaces as the operation's wrapped failure, while cancellation propagates
/// untouched. Owns the disposal of the request and the response.</summary>
public static class ApiOperation
{
    /// <summary>The body excerpt cap of the status-gate message.</summary>
    public const int MaxErrorBodyLength = 4096;

    /// <summary>The status gate most endpoints share: non-2xx raises HttpRequestException
    /// carrying the status, the request URL and up to <see cref="MaxErrorBodyLength"/>
    /// characters of the response body.</summary>
    public static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
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
            $"HTTP status {(int)response.StatusCode} for {response.RequestMessage?.RequestUri}\n{excerpt}",
            inner: null,
            statusCode: response.StatusCode);
    }

    /// <summary>Sends the operation through the given executor. Transport, gate and read
    /// failures surface as the operation's wrapped failure; cancellation propagates
    /// untouched.</summary>
    public static async Task<TResult> SendTo<TResult>(this IApiOperation<TResult> operation,
        HttpMessageInvoker invoker, CancellationToken ct = default)
    {
        try
        {
            using var request = operation.CreateRequest();
            using var response = await invoker.SendAsync(request, ct).ConfigureAwait(false);
            return await operation.ReadResponseAsync(response, ct).ConfigureAwait(false);
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

    /// <summary>Sends the operation through an HttpClient with the read option of this send
    /// — ResponseHeadersRead streams the body to the reader, ResponseContentRead buffers it
    /// first. Failure shaping matches the invoker overload.</summary>
    public static async Task<TResult> SendTo<TResult>(this IApiOperation<TResult> operation,
        HttpClient http, HttpCompletionOption readOption, CancellationToken ct = default)
    {
        try
        {
            using var request = operation.CreateRequest();
            using var response = await http.SendAsync(request, readOption, ct).ConfigureAwait(false);
            return await operation.ReadResponseAsync(response, ct).ConfigureAwait(false);
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
    /// arguments are exclusively owned by this send. Failure shaping matches the no-argument
    /// overload.</summary>
    public static async Task<TResult> SendTo<TArgs, TResult>(this IApiOperation<TArgs, TResult> operation,
        HttpMessageInvoker invoker, TArgs args, CancellationToken ct = default)
    {
        try
        {
            using var request = operation.CreateRequest(args);
            using var response = await invoker.SendAsync(request, ct).ConfigureAwait(false);
            return await operation.ReadResponseAsync(args, response, ct).ConfigureAwait(false);
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

    /// <summary>Sends the operation with per-send arguments through an HttpClient with the
    /// read option of this send — the streaming form of the parameterized download. Failure
    /// shaping matches the no-argument overload.</summary>
    public static async Task<TResult> SendTo<TArgs, TResult>(this IApiOperation<TArgs, TResult> operation,
        HttpClient http, HttpCompletionOption readOption, TArgs args, CancellationToken ct = default)
    {
        try
        {
            using var request = operation.CreateRequest(args);
            using var response = await http.SendAsync(request, readOption, ct).ConfigureAwait(false);
            return await operation.ReadResponseAsync(args, response, ct).ConfigureAwait(false);
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
