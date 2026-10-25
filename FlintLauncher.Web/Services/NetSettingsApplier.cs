// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
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
using FlintLauncher.Core.Config;
using FlintLauncher.Core.ModPlatform.Flame;
using FlintLauncher.Core.ModPlatform.Modrinth;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Secrets;

namespace FlintLauncher.Web.Services;

public sealed class NetSettingsApplier(
    FlintConfigStore configStore,
    ISecretStore secrets,
    NetStack net,
    ModrinthApi modrinth,
    FlameApi flame,
    AuthEndpointsOptions auth)
{
    public const string ProxyPassSecret = "ProxyPass";
    public const string ModrinthTokenSecret = "ModrinthToken";
    public const string FlameKeySecret = "FlameKeyOverride";

    public void Apply()
    {
        // the typed user layer (the SettingsObject adapter view retired)
        var config = configStore.Current;
        var userAgent = config.Api.UserAgentOverride ?? "";
        net.Configure(
            userAgent.Length > 0 ? userAgent : FlintConfig.Default.UserAgent,
            config.Proxy.ProxyType ?? "None",
            config.Proxy.ProxyAddr ?? "127.0.0.1",
            config.Proxy.ProxyPort ?? 8080,
            config.Proxy.ProxyUser ?? "",
            secrets.Get(ProxyPassSecret) ?? "");

        var retries = config.Launcher.NumberOfManualRetries ?? 1;
        net.ManualRetries = retries < 0 ? 0 : retries;

        // C++ RequestTimeout is in seconds; 0/negative would cancel instantly, so clamp to the default
        var timeoutSeconds = config.Launcher.RequestTimeout ?? 60;
        net.RequestTimeout = TimeSpan.FromSeconds(timeoutSeconds < 1 ? 60 : timeoutSeconds);

        ApplyApiCredentials();
    }

    /// <summary>The secret-consuming client options read the store. The Modrinth PAT and
    /// the Flame key are secrets (store); the MSA client id is a public override and reads
    /// the typed config (Api domain — explicitly kept out of the secret store).</summary>
    private void ApplyApiCredentials()
    {
        var modrinthToken = secrets.Get(ModrinthTokenSecret) ?? "";
        if (modrinth.ApiToken != modrinthToken)
        {
            modrinth.ApiToken = modrinthToken;
        }

        var flameKey = secrets.Get(FlameKeySecret);
        flame.ApiKeyProvider = () =>
            !string.IsNullOrEmpty(flameKey) ? flameKey : FlintConfig.Default.FlameApiKey;

        var msaOverride = configStore.Current.Api.MsaClientIdOverride ?? "";
        auth.MsaClientId = msaOverride.Length > 0
            ? msaOverride
            : AuthEndpointsOptions.DefaultMsaClientId;
    }
}
