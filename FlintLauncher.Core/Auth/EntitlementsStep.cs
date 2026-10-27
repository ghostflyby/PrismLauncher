// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
//
// Port of launcher/minecraft/auth/steps/EntitlementsStep.{h,cpp}.
//
// Upstream ignores the parseMinecraftEntitlements() return value and always reports success
// ("Got entitlements") — ported as-is.

namespace FlintLauncher.Core.Auth;

public sealed class EntitlementsStep(AccountData data, AuthEndpointsOptions? auth = null) : AuthStep(data)
{
    private readonly AuthEndpointsOptions _auth = auth ?? new AuthEndpointsOptions();

    public override string Describe() => "Determining game ownership.";

    private static readonly KeyValuePair<string, string>[] Headers =
    [
        new("Content-Type", "application/json"),
        new("Accept", "application/json"),
    ];

    public override async Task PerformAsync(CancellationToken cancellationToken)
    {
        // QUuid::createUuid().toString(QUuid::WithoutBraces)
        var entitlementsRequestId = Guid.NewGuid().ToString("D");

        var url = _auth.MinecraftServices.TrimEnd('/') + "/entitlements/license?requestId=" + entitlementsRequestId;
        var headers = new List<KeyValuePair<string, string>>(Headers)
        {
            new("Authorization", $"Bearer {Data.YggdrasilToken.Value}"),
        };

        var response = await AuthStepHttp.SendAsync(new AuthStepRequest
        {
            Url = new Uri(url),
            Headers = headers,
            AutoRetry = true,
        }, _auth.Http, cancellationToken).ConfigureAwait(false);

        // TODO: check presence of same entitlementsRequestId?
        // TODO: validate JWTs?
        _ = Parsers.ParseMinecraftEntitlements(response.Body, Data.MinecraftEntitlement);

        EmitFinished(AccountTaskState.Working, "Got entitlements");
    }
}
