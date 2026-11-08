// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
//
// Port of launcher/minecraft/auth/steps/EntitlementsStep.{h,cpp}.
//
// Upstream ignores the parseMinecraftEntitlements() return value and always reports success
// ("Got entitlements") — ported as-is.

using FlintLauncher.ExternalApi.Mojang;

namespace FlintLauncher.Core.Auth;

public sealed class EntitlementsStep(AccountData data, AuthEndpointsOptions? auth = null) : AuthStep(data)
{
    private readonly AuthEndpointsOptions _auth = auth ?? new AuthEndpointsOptions();

    public override string Describe() => "Determining game ownership.";

    public override async Task PerformAsync(CancellationToken cancellationToken)
    {
        // QUuid::createUuid().toString(QUuid::WithoutBraces)
        var entitlementsRequestId = Guid.NewGuid().ToString("D");

        // TODO: check presence of same entitlementsRequestId?
        // TODO: validate JWTs?
        // upstream never fails this step — a missing answer reads as no entitlements
        try
        {
            var entitlements = await AuthStepHttp.SendAsync(
                _auth.Mojang.MinecraftServices.Entitlements(Data.YggdrasilToken.Value, entitlementsRequestId),
                _auth.Http, autoRetry: true, cancellationToken).ConfigureAwait(false);
            entitlements?.ApplyTo(Data.MinecraftEntitlement);
        }
        catch (MojangApiException)
        {
        }

        EmitFinished(AccountTaskState.Working, "Got entitlements");
    }
}
