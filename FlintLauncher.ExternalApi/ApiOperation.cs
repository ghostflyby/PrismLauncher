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

    /// <summary>Wraps a transport or read failure into the operation's failure exception; the
    /// pipeline throws the result. Cancellation never reaches this.</summary>
    protected internal Exception WrapFailure(Exception failure);

    /// <summary>Reads the response — any status — into the result type. The endpoint alone
    /// decides what each status means and how the body is taken (JSON through the
    /// serializer's stream API, text through a reader, files straight to disk); shared
    /// patterns such as <see cref="ApiResponses.EnsureSuccessAsync"/> apply only where the
    /// endpoint calls them. The response stays owned by the pipeline and must not be disposed
    /// here.</summary>
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
/// handed executor and give the response to the operation's own reader; a failed send or
/// read surfaces as the operation's wrapped failure, while cancellation propagates
/// untouched. Owns the disposal of the request and the response.</summary>
public static class ApiOperation
{
    /// <summary>Wraps a failure the caller detected around a send of the operation — an
    /// exchange its own transport policy cut short — into the operation's failure exception,
    /// the same wrapper its transport and read failures take.</summary>
    public static Exception WrapFailure<TResult>(IApiOperation<TResult> operation, Exception failure) =>
        operation.WrapFailure(failure);

    /// <summary>Sends the operation through the given executor. Transport and read failures
    /// surface as the operation's wrapped failure; cancellation propagates
    /// untouched.</summary>
    /// <param name="prepare">Runs on the freshly built request before the send — the
    /// caller's transport-policy seam: the request options an invoker's handler chain reads
    /// its behavior from.</param>
    public static async Task<TResult> SendTo<TResult>(this IApiOperation<TResult> operation,
        HttpMessageInvoker invoker, CancellationToken ct = default, Action<HttpRequestMessage>? prepare = null)
    {
        try
        {
            using var request = operation.CreateRequest();
            prepare?.Invoke(request);
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
    /// first. Failure shaping and the prepare seam match the invoker overload.</summary>
    public static async Task<TResult> SendTo<TResult>(this IApiOperation<TResult> operation,
        HttpClient http, HttpCompletionOption readOption, CancellationToken ct = default,
        Action<HttpRequestMessage>? prepare = null)
    {
        try
        {
            using var request = operation.CreateRequest();
            prepare?.Invoke(request);
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
    /// arguments are exclusively owned by this send. Failure shaping and the prepare seam
    /// match the no-argument overload.</summary>
    public static async Task<TResult> SendTo<TArgs, TResult>(this IApiOperation<TArgs, TResult> operation,
        HttpMessageInvoker invoker, TArgs args, CancellationToken ct = default,
        Action<HttpRequestMessage>? prepare = null)
    {
        try
        {
            using var request = operation.CreateRequest(args);
            prepare?.Invoke(request);
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
    /// shaping and the prepare seam match the no-argument overload.</summary>
    public static async Task<TResult> SendTo<TArgs, TResult>(this IApiOperation<TArgs, TResult> operation,
        HttpClient http, HttpCompletionOption readOption, TArgs args, CancellationToken ct = default,
        Action<HttpRequestMessage>? prepare = null)
    {
        try
        {
            using var request = operation.CreateRequest(args);
            prepare?.Invoke(request);
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
