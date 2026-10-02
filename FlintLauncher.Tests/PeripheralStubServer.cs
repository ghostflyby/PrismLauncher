// SPDX-License-Identifier: GPL-3.0-only
// In-process HttpListener stub for the peripheral-feature tests (Imgur upload/album, news feed,
// paste uploads) — the same free-port pattern as AuthStubServer/NetStackTests' TestHttpServer.
// Every request is recorded with its raw body bytes (multipart bodies are binary) and the
// response is driven by the Responder delegate.
using System.Net;
using System.Text;
using Xunit;

namespace Flint.Tests;

internal sealed class PeripheralStubServer : IDisposable
{
    private readonly HttpListener _listener = new();

    public PeripheralStubServer()
    {
        // TestPorts owns the port lease AND the rebind-retry (HttpListener cannot adopt the
        // lease socket, so losing the takeover window retries on a fresh port)
        (_listener, BaseUrl) = PortAllocator.StartHttpListener();
        _ = Task.Run(ListenLoop);
    }

    public string BaseUrl { get; }

    public List<CapturedRequest> Requests { get; } = [];

    /// <summary>Response factory: status code, body text, reason phrase. Default 200 "{}".</summary>
    public Func<CapturedRequest, (int Status, string Body, string Reason)>? Responder { get; set; }

    /// <summary>The last recorded request for a path, or null.</summary>
    public CapturedRequest? Request(string path)
    {
        lock (Requests)
        {
            return Requests.LastOrDefault(r => r.Path == path);
        }
    }

    public void Dispose()
    {
        try
        {
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }
        GC.SuppressFinalize(this);
    }

    private async Task ListenLoop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                break;
            }
            try
            {
                Handle(context);
            }
            catch (Exception)
            {
                // never let a stub bug crash the listener loop
            }
            finally
            {
                try
                {
                    context.Response.Close();
                }
                catch (Exception)
                {
                }
            }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        using var bodyStream = new MemoryStream();
        ctx.Request.InputStream.CopyTo(bodyStream);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in ctx.Request.Headers.AllKeys)
        {
            if (key is not null)
            {
                headers[key] = ctx.Request.Headers[key] ?? "";
            }
        }

        var recorded = new CapturedRequest(ctx.Request.HttpMethod, ctx.Request.Url!.AbsolutePath, headers,
            bodyStream.ToArray());
        lock (Requests)
        {
            Requests.Add(recorded);
        }

        var (status, body, reason) = Responder?.Invoke(recorded) ?? (200, "{}", "OK");
        ctx.Response.StatusCode = status;
        ctx.Response.StatusDescription = reason;
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes);
    }
}

internal sealed record CapturedRequest(
    string Method,
    string Path,
    Dictionary<string, string> Headers,
    byte[] Body)
{
    public string BodyAsString => Encoding.UTF8.GetString(Body);
}

/// <summary>Multipart inspection helpers for the byte-exact body assertions.</summary>
internal static class MultipartAssertions
{
    public static string BoundaryFromContentType(string contentType)
    {
        var marker = "boundary=";
        var index = contentType.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, "no boundary in Content-Type: " + contentType);
        return contentType[(index + marker.Length)..];
    }

    /// <summary>Splits a multipart body into (headers, payload) parts between the boundary markers.</summary>
    public static List<(string[] Headers, byte[] Payload)> SplitParts(byte[] body, string boundary)
    {
        var delimiter = Encoding.Latin1.GetBytes("--" + boundary);
        var parts = new List<(string[], byte[])>();
        var index = IndexOf(body, delimiter, 0);
        Assert.True(index >= 0, "boundary not found in body");
        while (true)
        {
            var lineEnd = IndexOf(body, "\r\n"u8.ToArray(), index);
            Assert.True(lineEnd >= 0, "malformed boundary line");
            var boundaryLine = Encoding.Latin1.GetString(body[index..lineEnd]);
            if (boundaryLine == "--" + boundary + "--")
            {
                break;
            }
            // headers until an empty line
            var payloadStart = lineEnd + 2;
            var headerLines = new List<string>();
            while (true)
            {
                var eol = IndexOf(body, "\r\n"u8.ToArray(), payloadStart);
                Assert.True(eol >= 0, "unterminated headers");
                var line = Encoding.Latin1.GetString(body[payloadStart..eol]);
                payloadStart = eol + 2;
                if (line.Length == 0)
                {
                    break;
                }
                headerLines.Add(line);
            }
            var nextBoundary = IndexOf(body, delimiter, payloadStart);
            Assert.True(nextBoundary >= 0, "missing closing boundary");
            var payload = body[payloadStart..(nextBoundary - 2)]; // strip the trailing CRLF
            parts.Add((headerLines.ToArray(), payload));
            index = nextBoundary;
        }
        return parts;
    }

    public static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        for (var i = start; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
            {
                return i;
            }
        }
        return -1;
    }
}
