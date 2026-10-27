// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

namespace FlintLauncher.ExternalApi;

/// <summary>An endpoint operation: the request is built fresh per send and the response is
/// read into the result type. The request content must be replayable (buffered or empty),
/// so one value can be sent repeatedly; operations with per-send arguments use
/// <see cref="IApiOperation{TArgs,TResult}"/>.</summary>
/// <typeparam name="TResult">The read result.</typeparam>
public interface IApiOperation<TResult>
{
    /// <summary>Builds a fresh request message with the operation's method, URL, headers and
    /// body; the content must be replayable (buffered or empty).</summary>
    protected internal HttpRequestMessage CreateRequest();

    /// <summary>Wraps a transport, gate or read failure into the operation's failure
    /// exception; the pipeline throws the result. Cancellation never reaches this.</summary>
    protected internal Exception WrapFailure(Exception failure);

    /// <summary>Reads the response into the result type: gate through
    /// <see cref="ApiOperation.EnsureSuccessAsync"/> (or the endpoint's own rule), then take
    /// the body from the response message and decode it (JSON through the serializer's
    /// stream API, text through a reader, files straight to disk). The response stays owned
    /// by the pipeline and must not be disposed here.</summary>
    protected internal Task<TResult> ReadResponseAsync(HttpResponseMessage response,
        CancellationToken ct);
}

/// <summary>An endpoint operation with per-send arguments: the arguments arrive at SendTo
/// and are exclusively owned by that send, released with the request message. Everything
/// replayable stays bound under the <see cref="IApiOperation{TResult}"/> rules.</summary>
/// <typeparam name="TArgs">The per-send arguments.</typeparam>
/// <typeparam name="TResult">The read result.</typeparam>
public interface IApiOperation<in TArgs, TResult>
{
    /// <summary>Builds a fresh request message for this send; the content must be replayable
    /// (buffered or empty).</summary>
    HttpRequestMessage CreateRequest(TArgs args);

    /// <summary>Wraps a failure into the operation's exception (see
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
