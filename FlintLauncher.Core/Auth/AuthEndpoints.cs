// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// The auth context the account steps send through (Flint deviation, no upstream
// counterpart): the C++ steps hardcode the Microsoft/Xbox/Minecraft service URLs. Here the
// service endpoints and the MSA client id are the Mojang client's options (defaults
// byte-identical to the upstream URLs), so tests can point the whole flow at a local stub
// server (pass the context through AuthFlow / the step constructors / the composition
// root's AuthEndpointsOptions registration).

using FlintLauncher.Core.Net;
using FlintLauncher.ExternalApi.Mojang;

namespace FlintLauncher.Core.Auth;

public sealed class AuthEndpointsOptions
{
    /// <summary>The account service client: the endpoints and the MSA client id
    /// (Application::getMSAClientID() — the settings override is applied by the host).</summary>
    public MojangClient Mojang { get; init; } = new();

    /// <summary>The persistent client the auth steps send through — the host wires its
    /// shared client (proxy, user agent, timeout) so the auth traffic follows the same
    /// configuration as every other request. Unset falls back to a private default client,
    /// which keeps a bare fixture working without process-global state.</summary>
    public HttpClient Http
    {
        get => _http ??= NetPrimaryHandler.CreateClient();
        init => _http = value;
    }

    private HttpClient? _http;
}
