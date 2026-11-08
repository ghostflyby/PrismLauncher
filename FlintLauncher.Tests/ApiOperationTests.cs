// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Tests for the SendTo prepare seam: the hook sets the request options of this send before
// it goes out, and a throwing hook surfaces as the operation's wrapped failure like a
// transport error; the shared EnsureSuccessAsync pattern's best-effort error body; and the
// independence of the per-category failure exceptions.

using System.Net;
using FlintLauncher.ExternalApi;
using Xunit;

namespace FlintLauncher.Tests;

public class ApiOperationTests
{
    [Fact]
    public async Task SendTo_Prepare_Sets_The_Request_Options_The_Handler_Chain_Reads()
    {
        var policy = new HttpRequestOptionsKey<string>("Flint.Test.Policy");
        string? seen = null;
        using var stub = new StubInvoker(request =>
        {
            seen = request.Options.TryGetValue(policy, out var value) ? value : null;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
        });

        var result = await new StubQuery().SendTo(stub, prepare: request => request.Options.Set(policy, "armed"));

        Assert.Equal("ok", result);
        Assert.Equal("armed", seen);
    }

    [Fact]
    public async Task SendTo_Prepare_Throwing_Wraps_As_The_Operation_Failure()
    {
        using var stub = new StubInvoker(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });

        var failure = await Assert.ThrowsAsync<StubApiException>(() => new StubQuery().SendTo(stub,
            prepare: _ => throw new InvalidOperationException("prepare blew up")));

        Assert.IsType<InvalidOperationException>(failure.InnerException);
    }

    [Fact]
    public async Task SendTo_Without_Prepare_Still_Sends()
    {
        using var stub = new StubInvoker(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });

        Assert.Equal("ok", await new StubQuery().SendTo(stub));
    }

    [Fact]
    public async Task EnsureSuccess_ErrorBody_Read_Failure_Still_Surfaces_The_Status()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new ThrowingContent(),
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://stub/missing"),
        };

        var status = await Assert.ThrowsAsync<HttpRequestException>(() =>
            response.EnsureSuccessAsync(CancellationToken.None));

        Assert.Equal(HttpStatusCode.NotFound, status.StatusCode);
        Assert.StartsWith("HTTP status 404 for http://stub/missing", status.Message);
    }

    private sealed class ThrowingContent : HttpContent
    {
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            Task.FromException<Stream>(new IOException("connection reset mid-body"));

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.FromException(new IOException("connection reset mid-body"));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    [Fact]
    public void Every_Api_Category_Owns_An_Independent_Failure_Exception()
    {
        // one root exception per provider namespace, each directly an Exception, and every
        // refinement in the namespace derives from its own root: a consumer catching one
        // category can never catch another's failures by accident
        var exported = typeof(ApiOperation).Assembly.GetExportedTypes().ToList();
        var categories = exported
            .Select(type => type.Namespace)
            .Where(ns => ns != typeof(ApiOperation).Namespace)
            .Distinct()
            .ToList();
        Assert.NotEmpty(categories);
        Assert.DoesNotContain(exported, type =>
            type.Namespace == typeof(ApiOperation).Namespace && typeof(Exception).IsAssignableFrom(type));

        foreach (var category in categories)
        {
            var failures = exported
                .Where(type => type.Namespace == category && typeof(Exception).IsAssignableFrom(type))
                .ToList();
            var roots = failures.Where(type => type.BaseType == typeof(Exception)).ToList();
            var root = Assert.Single(roots);
            Assert.All(failures, failure => Assert.True(failure == root ||
                IsDerivedFrom(failure, root)));
        }
    }

    private static bool IsDerivedFrom(Type type, Type root)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current == root)
            {
                return true;
            }
        }

        return false;
    }

    private sealed class StubQuery : IApiOperation<string>
    {
        HttpRequestMessage IApiOperation<string>.CreateRequest() =>
            new(HttpMethod.Get, "http://stub/api-operation");

        Exception IApiOperation<string>.WrapFailure(Exception failure) =>
            new StubApiException(failure.Message, failure);

        async Task<string> IApiOperation<string>.ReadResponseAsync(HttpResponseMessage response,
            CancellationToken ct) => await response.Content.ReadAsStringAsync(ct);
    }

    private sealed class StubApiException(string message, Exception innerException)
        : Exception(message, innerException);

    private sealed class StubInvoker(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageInvoker(new StubHandler(respond))
    {
        private sealed class StubHandler(
            Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken) => Task.FromResult(respond(request));
        }
    }
}
