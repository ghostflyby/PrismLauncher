// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Instance-owned shared HttpClient for Flint network activity (the DI successor of the
// former `Http` static class, which replaced QNetworkAccessManager). A NetStack belongs to a
// runtime/composition root: its configuration (user agent, manual retries, transfer timeout,
// proxy) lives on the instance, and re-applying a changed configuration rebuilds the
// HttpClient — the "hot settings" semantics of the C++ Application::updateProxySettings,
// with process-global state replaced by a reference the owner hands out.

using System.Net;

namespace FlintLauncher.Core.Net;

public sealed class NetStack
{
    /// <summary>The canonical process-wide stack: the host runtime hands this instance out,
    /// request plumbing falls back to it when no explicit stack was injected. Tests that need
    /// isolation construct their own and pass it down the service constructors.</summary>
    public static NetStack Default { get; } = CreateDefault();

    private readonly Lock _lock = new();
    private (string Type, string Host, int Port, string User, string Pass) _proxy = ("None", "", 0, "", "");
    private (string Ua, string Type, string Host, int Port, string User, string Pass)? _applied;
    private volatile HttpClient _client;

    public NetStack() : this(FlintConfig.Default.UserAgent, manualRetries: 1, TimeSpan.FromSeconds(60)) { }

    public NetStack(string userAgent, int manualRetries, TimeSpan requestTimeout)
    {
        UserAgent = userAgent;
        ManualRetries = manualRetries;
        RequestTimeout = requestTimeout;
        _client = CreateClient();
    }

    /// <summary>Convenience factory with the C++ defaults (product user agent, 1 manual
    /// retry, 60s inactivity timeout).</summary>
    public static NetStack CreateDefault() => new();

    public string UserAgent { get; set; }

    /// <summary>Port of the NumberOfManualRetries setting: how often a request that failed on
    /// the network level is retried (NetJob::executeNextStylesheet gate). Consumed by
    /// <see cref="NetRequest"/>.</summary>
    public int ManualRetries { get; set; }

    /// <summary>Port of the RequestTimeout setting (QNetworkReply::setTransferTimeout): the
    /// transfer is aborted when no data flows for this long (an inactivity timeout, not a
    /// total-duration cap, so large steady downloads keep working).</summary>
    public TimeSpan RequestTimeout { get; set; }

    /// <summary>The proxy configuration currently baked into <see cref="Client"/>
    /// (Application::updateProxySettings state; diagnostics/tests).</summary>
    public (string Type, string Host, int Port, string User, string Pass) ProxyConfig => _proxy;

    public HttpClient Client => _client;

    /// <summary>Port of Application::updateProxySettings (+ the UserAgent the client is built
    /// with): rebuild the shared client so the proxy/user-agent take effect. Proxy semantics
    /// match the C++ ProxyPage combo: "SOCKS5" and "HTTP" build an explicit proxy, "None"
    /// disables proxying and anything else uses the system configuration. Idempotent: an
    /// unchanged configuration does not rebuild the client.</summary>
    public void Configure(string userAgent, string proxyType, string addr, int port,
        string user, string pass)
    {
        var ua = userAgent;
        var type = string.IsNullOrWhiteSpace(proxyType) ? "None" : proxyType.Trim();
        var host = addr;
        // an HTTP/SOCKS proxy without a host cannot be used — fall back to "no proxy"
        if (type is "HTTP" or "SOCKS5" && host.Length == 0)
        {
            type = "None";
        }

        var next = (ua, type, host, port, user, pass);
        if (this._applied is { } previous && previous.Equals(next))
        {
            return;
        }

        lock (_lock)
        {
            if (this._applied is { } appliedAgain && appliedAgain.Equals(next))
            {
                return;
            }

            this._applied = next;
            UserAgent = ua;
            _proxy = (type, host, port, user, pass);
            _client = CreateClient();
        }
    }

    private HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            Proxy = BuildProxy(),
        };
        var httpClient = new HttpClient(handler, disposeHandler: true);
        // user-supplied user-agent overrides may not be valid product tokens; ignore those
        _ = httpClient.DefaultRequestHeaders.UserAgent.TryParseAdd(UserAgent);
        httpClient.Timeout = TimeSpan.FromMinutes(10);
        return httpClient;
    }

    private IWebProxy? BuildProxy() => _proxy.Type switch
    {
        "HTTP" => NamedProxy(new Uri($"http://{_proxy.Host}:{_proxy.Port}")),
        "SOCKS5" => NamedProxy(new Uri($"socks5://{_proxy.Host}:{_proxy.Port}")),
        // explicit "no proxy"
        "None" => null,
        // C++ "DefaultProxy": the system proxy configuration
        _ => HttpClient.DefaultProxy,
    };

    private WebProxy NamedProxy(Uri uri)
    {
        var webProxy = new WebProxy(uri);
        if (this._proxy.User.Length != 0)
        {
            webProxy.Credentials = new NetworkCredential(this._proxy.User, this._proxy.Pass);
        }

        return webProxy;
    }
}
