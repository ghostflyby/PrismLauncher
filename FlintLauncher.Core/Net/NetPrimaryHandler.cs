// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// The persistent transport: one HttpClient per settings pair lives for the lifetime of its
// owner and is never rebuilt. Proxy changes are picked up through the settings' ProxyVersion
// counter by swapping the inner SocketsHttpHandler; the retired handler is deliberately NOT
// disposed — its pooled connections close on the idle timeout, so transfers already in flight
// run to completion (disposing would abort them). The user agent is stamped per request from
// the live settings, so a UserAgentOverride applies without rebuilding anything.

using System.Net;

namespace FlintLauncher.Core.Net;

public sealed class NetPrimaryHandler : HttpMessageHandler
{
    /// <summary>How long a pooled connection may be reused before the pool re-establishes it
    /// (the DNS-refresh channel; established transfers are never interrupted by the
    /// rotation).</summary>
    private static readonly TimeSpan PooledConnectionLifetime = TimeSpan.FromMinutes(5);

    private sealed class Generation(long proxyVersion, SocketsHttpHandler handler)
    {
        public readonly long ProxyVersion = proxyVersion;

        /// <summary>The public invoker over the handler — <c>HttpMessageHandler.SendAsync</c>
        /// is protected, so a plain handler cannot dispatch into another instance.</summary>
        public readonly HttpMessageInvoker Invoker = new(handler, disposeHandler: false);
    }

    private volatile Generation _current;

    public NetPrimaryHandler(NetSettings settings)
    {
        Settings = settings;
        var snapshot = settings.Current;
        _current = new Generation(snapshot.ProxyVersion, BuildHandler(snapshot));
    }

    public NetSettings Settings { get; }

    /// <summary>The persistent client over this handler: no total-duration timeout — the
    /// request executors carry the RequestTimeout inactivity timeout instead (the C++
    /// setTransferTimeout port), so a large steady download is never cut mid-transfer.</summary>
    public static HttpClient CreateClient(NetSettings settings) =>
        new(new NetPrimaryHandler(settings), disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var snapshot = Settings.Current;
        var generation = _current;
        if (generation.ProxyVersion != snapshot.ProxyVersion)
        {
            generation = Swap(snapshot);
        }

        if (request.Headers.UserAgent.Count == 0 && snapshot.UserAgent.Length > 0)
        {
            // user-supplied user-agent overrides may not be valid product tokens; add raw
            request.Headers.TryAddWithoutValidation("User-Agent", snapshot.UserAgent);
        }

        return await generation.Invoker.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private Generation Swap(NetSettingsSnapshot snapshot)
    {
        lock (this)
        {
            if (_current.ProxyVersion == snapshot.ProxyVersion)
            {
                return _current;
            }

            // the retired generation keeps servicing its in-flight transfers and its pooled
            // connections idle out on their own; see the file header
            _current = new Generation(snapshot.ProxyVersion, BuildHandler(snapshot));
            return _current;
        }
    }

    private static SocketsHttpHandler BuildHandler(NetSettingsSnapshot snapshot) => new()
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = PooledConnectionLifetime,
        Proxy = BuildProxy(snapshot),
    };

    private static IWebProxy? BuildProxy(NetSettingsSnapshot snapshot) => snapshot.ProxyType switch
    {
        "HTTP" => NamedProxy(new Uri($"http://{snapshot.Host}:{snapshot.Port}"), snapshot),
        "SOCKS5" => NamedProxy(new Uri($"socks5://{snapshot.Host}:{snapshot.Port}"), snapshot),
        // explicit "no proxy"
        "None" => null,
        // C++ "DefaultProxy": the system proxy configuration
        _ => HttpClient.DefaultProxy,
    };

    private static WebProxy NamedProxy(Uri uri, NetSettingsSnapshot snapshot)
    {
        var webProxy = new WebProxy(uri);
        if (snapshot.User.Length != 0)
        {
            webProxy.Credentials = new NetworkCredential(snapshot.User, snapshot.Pass);
        }

        return webProxy;
    }
}
