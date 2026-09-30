// SPDX-License-Identifier: GPL-3.0-only
// fix-3: the port of the C++ network-settings application paths — Application::updateProxySettings
// (the Proxy* five), NumberOfManualRetries, RequestTimeout and the UserAgentOverride — applied
// to the runtime's NetStack. Called by the host at startup and after every successful
// PUT /settings write, so proxy changes take effect without a restart (the stack's HttpClient
// is rebuilt only when the applied configuration actually changed).
using FlintLauncher.Core;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Settings;

namespace FlintLauncher.Web.Services;

public static class RuntimeNetConfig
{
    public static void Apply(LauncherRuntime runtime)
    {
        var settings = runtime.GlobalSettings;
        var stack = runtime.Net;
        var userAgent = Variant.AsString(settings.Get("UserAgentOverride"));
        stack.Configure(
            userAgent.Length > 0 ? userAgent : FlintConfig.Default.UserAgent,
            Variant.AsString(settings.Get("ProxyType")),
            Variant.AsString(settings.Get("ProxyAddr")),
            Variant.AsInt(settings.Get("ProxyPort")),
            Variant.AsString(settings.Get("ProxyUser")),
            Variant.AsString(settings.Get("ProxyPass")));

        var retries = Variant.AsInt(settings.Get("NumberOfManualRetries"));
        stack.ManualRetries = retries < 0 ? 0 : retries;

        // C++ RequestTimeout is in seconds; 0/negative would cancel instantly, so clamp to the default
        var timeoutSeconds = Variant.AsInt(settings.Get("RequestTimeout"));
        stack.RequestTimeout = TimeSpan.FromSeconds(timeoutSeconds < 1 ? 60 : timeoutSeconds);
    }
}
