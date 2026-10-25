// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// The live network settings (the NumberOfManualRetries / RequestTimeout / Proxy* five /
// UserAgentOverride fold) as a snapshot holder. The transport reads the current snapshot per
// request or per connection instead of holding a rebuilt client: a proxy change is picked up
// by <see cref="NetPrimaryHandler"/> through the <c>ProxyVersion</c> counter alone, and the
// user agent is stamped per request, so nothing here ever rebuilds an HttpClient.

namespace FlintLauncher.Core.Net;

/// <summary>An immutable view of the network settings; <see cref="ProxyVersion"/> counts only
/// proxy-tuple changes (the user agent, retry count and timeout apply live without touching
/// the transport).</summary>
public readonly record struct NetSettingsSnapshot(
    string UserAgent,
    string ProxyType,
    string Host,
    int Port,
    string User,
    string Pass,
    int ManualRetries,
    TimeSpan RequestTimeout,
    long ProxyVersion)
{
    public (string Type, string Host, int Port, string User, string Pass) Proxy =>
        (ProxyType, Host, Port, User, Pass);
}

public sealed class NetSettings
{
    private static readonly NetSettingsSnapshot Defaults = new(
        UserAgent: FlintConfig.Default.UserAgent,
        ProxyType: "None",
        Host: "",
        Port: 0,
        User: "",
        Pass: "",
        ManualRetries: 1,
        RequestTimeout: TimeSpan.FromSeconds(60),
        ProxyVersion: 0);

    /// <summary>The reference wrapper makes the volatile swap possible (a struct field
    /// cannot be volatile): readers take one snapshot, writers replace the box.</summary>
    private sealed class Box(NetSettingsSnapshot snapshot)
    {
        public readonly NetSettingsSnapshot Value = snapshot;
    }

    private volatile Box _current = new(Defaults);
    private readonly object _updateLock = new();

    /// <summary>The settings requests and the transport read; a stable reference until the
    /// next <see cref="Update"/>.</summary>
    public NetSettingsSnapshot Current => _current.Value;

    /// <summary>The settings a bare fixture starts from (the C++ defaults: product user
    /// agent, one manual retry, 60s inactivity timeout, no proxy).</summary>
    public static NetSettings CreateDefault() => new();

    /// <summary>Port of Application::updateProxySettings plus the request-level settings.
    /// Proxy semantics match the C++ ProxyPage combo: "SOCKS5" and "HTTP" name an explicit
    /// proxy, "None" disables proxying and anything else uses the system configuration; a
    /// named proxy without a host falls back to "no proxy". The <c>ProxyVersion</c> advances
    /// only when the proxy tuple actually changed, so re-applying identical settings is a
    /// no-op for the transport.</summary>
    public void Update(string userAgent, string proxyType, string addr, int port,
        string user, string pass, int manualRetries, TimeSpan requestTimeout)
    {
        var type = string.IsNullOrWhiteSpace(proxyType) ? "None" : proxyType.Trim();
        var host = addr;
        // an HTTP/SOCKS proxy without a host cannot be used — fall back to "no proxy"
        if (type is "HTTP" or "SOCKS5" && host.Length == 0)
        {
            type = "None";
        }

        // NumberOfManualRetries: a negative retry count retries nothing
        if (manualRetries < 0)
        {
            manualRetries = 0;
        }

        // C++ RequestTimeout is in seconds; 0/negative would cancel instantly, so clamp to
        // the default
        if (requestTimeout < TimeSpan.FromSeconds(1))
        {
            requestTimeout = TimeSpan.FromSeconds(60);
        }

        lock (_updateLock)
        {
            var previous = _current.Value;
            var proxyChanged = previous.Proxy != (type, host, port, user, pass);
            _current = new Box(new NetSettingsSnapshot(
                UserAgent: userAgent,
                ProxyType: type,
                Host: host,
                Port: port,
                User: user,
                Pass: pass,
                ManualRetries: manualRetries,
                RequestTimeout: requestTimeout,
                ProxyVersion: proxyChanged ? previous.ProxyVersion + 1 : previous.ProxyVersion));
        }
    }
}
