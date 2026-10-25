// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Endpoint-injection options (Flint deviation, no upstream counterpart): the C++ auth steps
// hardcode the Microsoft/Xbox/Minecraft service URLs (login.microsoftonline.com,
// user.auth.xboxlive.com, xsts.auth.xboxlive.com, api.minecraftservices.com). The defaults
// below are byte-identical to the upstream URLs, but every step resolves its endpoint through
// an injected AuthEndpointsOptions instance so tests can point the whole flow at a local stub
// server (pass the options through AuthFlow / the step constructors / LauncherRuntime.Auth).

using FlintLauncher.Core.Net;

namespace FlintLauncher.Core.Auth;

public sealed class AuthEndpointsOptions
{
    public const string DefaultMsaDeviceCode = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
    public const string DefaultMsaToken = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
    public const string DefaultMsaAuthorize = "https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize";
    public const string DefaultXboxUserAuth = "https://user.auth.xboxlive.com/user/authenticate";
    public const string DefaultXboxXstsAuthorize = "https://xsts.auth.xboxlive.com/xsts/authorize";
    public const string DefaultMinecraftServices = "https://api.minecraftservices.com";

    /// <summary>BuildConfig.MSA_CLIENT_ID (CMake Launcher_MSA_CLIENT_ID default).</summary>
    public const string DefaultMsaClientId = "c36a9fb6-4f2a-41ff-90bd-ae7cc92031eb";

    /// <summary>Application::getMSAClientID() (BuildConfig value; the settings override is applied by the app shell).</summary>
    public string MsaClientId { get; set; } = DefaultMsaClientId;

    /// <summary>POST device_code start (MSADeviceCodeStep::perform).</summary>
    public string MsaDeviceCode { get; set; } = DefaultMsaDeviceCode;

    /// <summary>POST device_code polling + interactive token exchange (MSADeviceCodeStep/MSAStep).</summary>
    public string MsaToken { get; set; } = DefaultMsaToken;

    /// <summary>Interactive authorization-code flow (MSAStep::perform).</summary>
    public string MsaAuthorize { get; set; } = DefaultMsaAuthorize;

    /// <summary>XboxUserStep.</summary>
    public string XboxUserAuth { get; set; } = DefaultXboxUserAuth;

    /// <summary>XboxAuthorizationStep.</summary>
    public string XboxXstsAuthorize { get; set; } = DefaultXboxXstsAuthorize;

    /// <summary>Base of launcher/login, entitlements, minecraft/profile, skins and capes endpoints.</summary>
    public string MinecraftServices { get; set; } = DefaultMinecraftServices;

    /// <summary>The network stack the auth steps send through — the host wires its shared,
    /// settings-configured stack (proxy, user agent, timeout) so the auth traffic follows the
    /// same configuration as every other request. Unset falls back to a private default
    /// stack, which keeps a bare fixture working without process-global state.</summary>
    public NetStack Net
    {
        get => _net ??= new NetStack();
        init => _net = value;
    }

    private NetStack? _net;
}
