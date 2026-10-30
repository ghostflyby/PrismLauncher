// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// The innermost transport handler: it holds the user agent and the proxy configuration on
// itself and is never rebuilt. Proxy changes are picked up through the internal version
// counter by swapping the inner SocketsHttpHandler; the retired handler is deliberately NOT
// disposed — its pooled connections close on the idle timeout, so transfers already in flight
// run to completion (disposing would abort them). The user agent is stamped per request, so
// a UserAgent override applies without rebuilding anything.

using System.Net;

namespace FlintLauncher.Core.Net;

public sealed class NetPrimaryHandler : HttpMessageHandler
{
    /// <summary>How long a pooled connection may be reused before the pool re-establishes it
    /// (the DNS-refresh channel; established transfers are never interrupted by the
    /// rotation).</summary>
    private static readonly TimeSpan PooledConnectionLifetime = TimeSpan.FromMinutes(5);

    /// <summary>The proxy tuple plus the version counter in one immutable value: readers
    /// take one consistent snapshot, <see cref="SetProxy"/> replaces the box.</summary>
    private readonly record struct ProxyState(
        string Type,
        string Host,
        int Port,
        string User,
        string Pass,
        long Version);

    /// <summary>The reference wrapper makes the volatile swap possible (a struct field
    /// cannot be volatile): readers take one snapshot, writers replace the box.</summary>
    private sealed class ProxyBox(ProxyState state)
    {
        public readonly ProxyState State = state;
    }

    private static readonly ProxyBox Defaults = new(new ProxyState(
        Type: "None", Host: "", Port: 0, User: "", Pass: "", Version: 0));

    private sealed class Generation(long proxyVersion, SocketsHttpHandler handler)
    {
        public readonly long ProxyVersion = proxyVersion;

        /// <summary>The public invoker over the handler — <c>HttpMessageHandler.SendAsync</c>
        /// is protected, so a plain handler cannot dispatch into another instance.</summary>
        public readonly HttpMessageInvoker Invoker = new(handler, disposeHandler: false);
    }

    private volatile ProxyBox _proxy = Defaults;
    private volatile string _userAgent = FlintConfig.Default.UserAgent;
    private volatile Generation _current;
    private readonly object _swapLock = new();

    public NetPrimaryHandler()
    {
        var proxy = _proxy.State;
        _current = new Generation(proxy.Version, BuildHandler(proxy));
    }

    /// <summary>The product user agent stamped onto requests that name none; a per-request
    /// User-Agent header overrides it.</summary>
    public string UserAgent
    {
        get => _userAgent;
        set => _userAgent = value;
    }

    /// <summary>The applied proxy tuple (the set values, after the no-host fallback).</summary>
    public (string Type, string Host, int Port, string User, string Pass) Proxy
    {
        get
        {
            var proxy = _proxy.State;
            return (proxy.Type, proxy.Host, proxy.Port, proxy.User, proxy.Pass);
        }
    }

    /// <summary>Port of Application::updateProxySettings. Proxy semantics match the C++
    /// ProxyPage combo: "SOCKS5" and "HTTP" name an explicit proxy, "None" disables proxying
    /// and anything else uses the system configuration; a named proxy without a host falls
    /// back to "no proxy". The version advances only when the proxy tuple actually changed,
    /// so re-applying identical settings is a no-op for the transport.</summary>
    public void SetProxy(string proxyType, string host, int port, string user, string pass)
    {
        var type = string.IsNullOrWhiteSpace(proxyType) ? "None" : proxyType.Trim();
        // an HTTP/SOCKS proxy without a host cannot be used — fall back to "no proxy"
        if (type is "HTTP" or "SOCKS5" && host.Length == 0)
        {
            type = "None";
        }

        lock (_swapLock)
        {
            var previous = _proxy.State;
            var changed = previous.Type != type || previous.Host != host || previous.Port != port
                          || previous.User != user || previous.Pass != pass;
            if (!changed)
            {
                return;
            }

            _proxy = new ProxyBox(new ProxyState(type, host, port, user, pass, previous.Version + 1));
        }
    }

    internal long ProxyVersion => _proxy.State.Version;

    /// <summary>The persistent client over this handler chain: the retry gate rides above it,
    /// and there is no total-duration timeout — the request executors carry the RequestTimeout
    /// inactivity timeout instead (the C++ setTransferTimeout port), so a large steady
    /// download is never cut mid-transfer. The handlers hold the configuration (user agent,
    /// proxy, retry count, inactivity window); updating them re-arms the live stack without
    /// rebuilding anything.</summary>
    public static HttpClient CreateClient() =>
        new(new RetryHandler { InnerHandler = new NetPrimaryHandler() }, disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var proxy = _proxy.State;
        var generation = _current;
        if (generation.ProxyVersion < proxy.Version)
        {
            generation = Swap(proxy);
        }

        if (request.Headers.UserAgent.Count == 0 && _userAgent.Length > 0)
        {
            // user-supplied user-agent overrides may not be valid product tokens; add raw
            request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        }

        return await generation.Invoker.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private Generation Swap(ProxyState proxy)
    {
        lock (_swapLock)
        {
            if (_current.ProxyVersion >= proxy.Version)
            {
                // another thread already installed this version or a newer one — never
                // regress to a stale snapshot read before that swap
                return _current;
            }

            // the retired generation keeps servicing its in-flight transfers and its pooled
            // connections idle out on their own; see the file header
            _current = new Generation(proxy.Version, BuildHandler(proxy));
            return _current;
        }
    }

    private static SocketsHttpHandler BuildHandler(ProxyState proxy) => new()
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = PooledConnectionLifetime,
        Proxy = BuildProxy(proxy),
    };

    private static IWebProxy? BuildProxy(ProxyState proxy) => proxy.Type switch
    {
        "HTTP" => NamedProxy(new Uri($"http://{proxy.Host}:{proxy.Port}"), proxy),
        "SOCKS5" => NamedProxy(new Uri($"socks5://{proxy.Host}:{proxy.Port}"), proxy),
        // explicit "no proxy"
        "None" => null,
        // C++ "DefaultProxy": the system proxy configuration
        _ => HttpClient.DefaultProxy,
    };

    private static WebProxy NamedProxy(Uri uri, ProxyState proxy)
    {
        var webProxy = new WebProxy(uri);
        if (proxy.User.Length != 0)
        {
            webProxy.Credentials = new NetworkCredential(proxy.User, proxy.Pass);
        }

        return webProxy;
    }
}
