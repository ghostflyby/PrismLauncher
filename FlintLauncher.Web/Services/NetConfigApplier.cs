// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// The port of the C++ network-settings application paths — Application::updateProxySettings
// (the Proxy* five), NumberOfManualRetries, RequestTimeout and the UserAgentOverride — written
// onto the persistent client's handlers (the primary handler's user agent and proxy tuple, the
// retry handler's manual retry count and inactivity window). Called by the host at startup
// and on every committed change of the user layer (the host subscribes it to the config
// monitor); the persistent client picks a proxy change up without a restart (the transport
// swaps its inner handler on the proxy version, in-flight transfers included — nothing is
// rebuilt or aborted).
//
// Secret rerouting: the config world carries no secret fields, so the proxy password
// reads from the ISecretStore by name (the write path was rerouted in PUT /settings), and the
// secret-consuming API client options re-derive from the store here: the Modrinth PAT, the
// Flame (CurseForge) key provider and — from the config side (kept public) — the MSA
// client-id override for the auth options. A secret-only write changes no config snapshot,
// so PUT /settings still applies explicitly after one.

using FlintLauncher.Core;
using FlintLauncher.Core.Auth;
using FlintLauncher.Core.Config;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Secrets;
using FlintLauncher.ExternalApi.Flame;
using FlintLauncher.ExternalApi.Modrinth;
using FlintLauncher.ExternalApi.Mojang;
using Microsoft.Extensions.Options;

namespace FlintLauncher.Web.Services;

public sealed class NetConfigApplier(
    IOptionsMonitor<FlintConfigFile> config,
    ISecretStore secrets,
    NetPrimaryHandler primary,
    RetryHandler retry,
    ModrinthClient modrinth,
    FlameClient flame,
    AuthEndpointsOptions auth)
{
    public const string ProxyPassSecret = "ProxyPass";
    public const string ModrinthTokenSecret = "ModrinthToken";
    public const string FlameKeySecret = "FlameKeyOverride";

    public void Apply()
    {
        // the typed user layer (the SettingsObject adapter view retired); the clamping of the
        // proxy fallback, retries and timeout lives in the handlers' own setters
        var snapshot = config.CurrentValue;
        var userAgent = snapshot.Api.UserAgentOverride ?? "";
        primary.UserAgent = userAgent.Length > 0 ? userAgent : FlintConfig.Default.UserAgent;
        primary.SetProxy(
            snapshot.Proxy.ProxyType ?? "None",
            snapshot.Proxy.ProxyAddr ?? "127.0.0.1",
            snapshot.Proxy.ProxyPort ?? 8080,
            snapshot.Proxy.ProxyUser ?? "",
            secrets.Get(ProxyPassSecret) ?? "");
        retry.MaxManualRetries = snapshot.Launcher.NumberOfManualRetries ?? 1;
        retry.RequestTimeout = TimeSpan.FromSeconds(snapshot.Launcher.RequestTimeout ?? 60);

        ApplyApiCredentials();
    }

    /// <summary>The secret-consuming client options read the store. The Modrinth PAT and
    /// the Flame key are secrets (store); the MSA client id is a public override and reads
    /// the typed config (Api domain — explicitly kept out of the secret store).</summary>
    private void ApplyApiCredentials()
    {
        var modrinthToken = secrets.Get(ModrinthTokenSecret) ?? "";
        if (modrinth.Options.ApiToken != modrinthToken)
        {
            modrinth.Options.ApiToken = modrinthToken;
        }

        var flameKey = secrets.Get(FlameKeySecret);
        flame.Options.ApiKey =
            !string.IsNullOrEmpty(flameKey) ? flameKey : FlintConfig.Default.FlameApiKey;

        var msaOverride = config.CurrentValue.Api.MsaClientIdOverride ?? "";
        auth.Mojang.Options.MsaClientId = msaOverride.Length > 0
            ? msaOverride
            : MojangClientOptions.DefaultMsaClientId;
    }
}
