// SPDX-License-Identifier: GPL-3.0-only
// fix-3: the port of the C++ network-settings application paths — Application::updateProxySettings
// (the Proxy* five), plus the Net::Request settings lookups the port keeps as static hooks
// (Http.ManualRetries = NumberOfManualRetries, Http.RequestTimeout = RequestTimeout) and the
// UserAgentOverride. Called by the host at startup and after every successful PUT /settings
// write, so proxy changes take effect without a restart (the shared Http.Client is rebuilt
// only when the applied configuration actually changed).
using FlintLauncher.Core;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Settings;

namespace FlintLauncher.Web.Services;

public static class RuntimeNetConfig
{
    public static void Apply(LauncherRuntime runtime)
    {
        var settings = runtime.GlobalSettings;
        var userAgent = Variant.AsString(settings.Get("UserAgentOverride"));
        Http.Configure(
            userAgent.Length > 0 ? userAgent : FlintConfig.Default.UserAgent,
            Variant.AsString(settings.Get("ProxyType")),
            Variant.AsString(settings.Get("ProxyAddr")),
            Variant.AsInt(settings.Get("ProxyPort")),
            Variant.AsString(settings.Get("ProxyUser")),
            Variant.AsString(settings.Get("ProxyPass")));

        var retries = Variant.AsInt(settings.Get("NumberOfManualRetries"));
        Http.ManualRetries = retries < 0 ? 0 : retries;

        // C++ RequestTimeout is in seconds; 0/negative would cancel instantly, so clamp to the default
        var timeoutSeconds = Variant.AsInt(settings.Get("RequestTimeout"));
        Http.RequestTimeout = TimeSpan.FromSeconds(timeoutSeconds < 1 ? 60 : timeoutSeconds);
    }
}
