// SPDX-License-Identifier: GPL-3.0-only
// The port of the C++ network-settings application paths — Application::updateProxySettings
// (the Proxy* five), NumberOfManualRetries, RequestTimeout and the UserAgentOverride — applied
// to the runtime's NetStack. Called by the host at startup and after every successful
// PUT /settings write, so proxy changes take effect without a restart (the stack's HttpClient
// is rebuilt only when the applied configuration actually changed).
//
// Secret rerouting: the config world carries no secret fields, so the proxy password
// reads from the ISecretStore by name (the write path was rerouted in PUT /settings), and the
// secret-consuming API client options re-derive from the store here: the Modrinth PAT, the
// Flame (CurseForge) key provider and — from the config side (kept public) — the MSA
// client-id override for the auth options.

using FlintLauncher.Core;
using FlintLauncher.Core.Auth;

namespace FlintLauncher.Web.Services;

public static class RuntimeNetConfig
{
    public const string ProxyPassSecret = "ProxyPass";
    public const string ModrinthTokenSecret = "ModrinthToken";
    public const string FlameKeySecret = "FlameKeyOverride";

    public static void Apply(LauncherRuntime runtime)
    {
        // the typed user layer (the SettingsObject adapter view retired)
        var config = runtime.Config.Current;
        var stack = runtime.Net;
        var userAgent = config.Api.UserAgentOverride ?? "";
        stack.Configure(
            userAgent.Length > 0 ? userAgent : FlintConfig.Default.UserAgent,
            config.Proxy.ProxyType ?? "None",
            config.Proxy.ProxyAddr ?? "127.0.0.1",
            config.Proxy.ProxyPort ?? 8080,
            config.Proxy.ProxyUser ?? "",
            runtime.Secrets.Get(ProxyPassSecret) ?? "");

        var retries = config.Launcher.NumberOfManualRetries ?? 1;
        stack.ManualRetries = retries < 0 ? 0 : retries;

        // C++ RequestTimeout is in seconds; 0/negative would cancel instantly, so clamp to the default
        var timeoutSeconds = config.Launcher.RequestTimeout ?? 60;
        stack.RequestTimeout = TimeSpan.FromSeconds(timeoutSeconds < 1 ? 60 : timeoutSeconds);

        ApplyApiCredentials(runtime);
    }

    /// <summary>The secret-consuming client options read the store. The Modrinth PAT and
    /// the Flame key are secrets (store); the MSA client id is a public override and reads
    /// the typed config (Api domain — explicitly kept out of the secret store).</summary>
    private static void ApplyApiCredentials(LauncherRuntime runtime)
    {
        var modrinthToken = runtime.Secrets.Get(ModrinthTokenSecret) ?? "";
        if (runtime.Modrinth.ApiToken != modrinthToken)
        {
            runtime.Modrinth.ApiToken = modrinthToken;
        }

        var flameKey = runtime.Secrets.Get(FlameKeySecret);
        runtime.Flame.ApiKeyProvider = () =>
            !string.IsNullOrEmpty(flameKey) ? flameKey : FlintConfig.Default.FlameApiKey;

        var msaOverride = runtime.Config.Current.Api.MsaClientIdOverride ?? "";
        runtime.Auth.MsaClientId = msaOverride.Length > 0
            ? msaOverride
            : AuthEndpointsOptions.DefaultMsaClientId;
    }
}
