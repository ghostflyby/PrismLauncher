// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

namespace FlintLauncher.ExternalApi.Mojang;

/// <summary>The Minecraft account service endpoints (Microsoft identity, Xbox Live and
/// Minecraft services) and the MSA client id, read per request — apply changes through the
/// instance the client carries. The defaults are the production services.</summary>
public sealed class MojangClientOptions
{
    /// <summary>The MSA client id shared by Prism builds per the Microsoft identity platform
    /// terms of use (Launcher_MSA_CLIENT_ID).</summary>
    public const string DefaultMsaClientId = "c36a9fb6-4f2a-41ff-90bd-ae7cc92031eb";

    /// <summary>The MSA client id a flow starts with (the caller captures it per flow).</summary>
    public string MsaClientId { get; set; } = DefaultMsaClientId;

    /// <summary>The device authorization endpoint (RFC 8628 start).</summary>
    public string MsaDeviceCode { get; set; } = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";

    /// <summary>The token endpoint: device-code polling, refresh and the authorization-code
    /// exchange.</summary>
    public string MsaToken { get; set; } = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";

    /// <summary>The interactive authorization endpoint the user's browser opens.</summary>
    public string MsaAuthorize { get; set; } = "https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize";

    /// <summary>The Xbox Live user authentication endpoint.</summary>
    public string XboxUserAuth { get; set; } = "https://user.auth.xboxlive.com/user/authenticate";

    /// <summary>The Xbox Live security token service (XSTS) authorization endpoint.</summary>
    public string XboxXstsAuthorize { get; set; } = "https://xsts.auth.xboxlive.com/xsts/authorize";

    /// <summary>The base of the launcher login, entitlements, profile, skin and cape
    /// endpoints.</summary>
    public string MinecraftServices { get; set; } = "https://api.minecraftservices.com";
}
