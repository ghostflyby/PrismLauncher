// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/auth/steps/EntitlementsStep.{h,cpp}.
//
// Upstream ignores the parseMinecraftEntitlements() return value and always reports success
// ("Got entitlements") — ported as-is.
namespace FlintLauncher.Core.Auth;

public sealed class EntitlementsStep : AuthStep
{
    public EntitlementsStep(AccountData data) : base(data)
    {
    }

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

        var url = AuthEndpoints.MinecraftServices.TrimEnd('/') + "/entitlements/license?requestId=" + entitlementsRequestId;
        var headers = new List<KeyValuePair<string, string>>(Headers)
        {
            new("Authorization", $"Bearer {Data.YggdrasilToken.Token_}"),
        };

        var response = await AuthStepHttp.SendAsync(new AuthStepRequest
        {
            Url = new Uri(url),
            Headers = headers,
            AutoRetry = true,
        }, cancellationToken).ConfigureAwait(false);

        // TODO: check presence of same entitlementsRequestId?
        // TODO: validate JWTs?
        _ = Parsers.ParseMinecraftEntitlements(response.Body, Data.MinecraftEntitlement);

        EmitFinished(AccountTaskState.Working, "Got entitlements");
    }
}
