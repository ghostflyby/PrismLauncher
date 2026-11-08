// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
//
// Port of launcher/minecraft/auth/steps/MinecraftProfileStep.{h,cpp}.

using System.Text.Json;
using FlintLauncher.ExternalApi.Mojang;

namespace FlintLauncher.Core.Auth;

public sealed class MinecraftProfileStep(AccountData data, AuthEndpointsOptions? auth = null) : AuthStep(data)
{
    private readonly AuthEndpointsOptions _auth = auth ?? new AuthEndpointsOptions();

    public override string Describe() => "Fetching the Minecraft profile.";

    public override async Task PerformAsync(CancellationToken cancellationToken)
    {
        MojangProfile? profile;
        try
        {
            profile = await AuthStepHttp.SendAsync(
                _auth.Mojang.MinecraftServices.Profile(Data.YggdrasilToken.Value),
                _auth.Http, autoRetry: true, cancellationToken).ConfigureAwait(false);
        }
        catch (MojangApiException e) when (e.InnerException is JsonException)
        {
            Data.MinecraftProfile = new MinecraftProfile();
            EmitFinished(AccountTaskState.FailedSoft, "Minecraft Java profile response could not be parsed");
            return;
        }
        catch (MojangApiException e)
        {
            if (e.IsClientError)
            {
                EmitFinished(AccountTaskState.FailedSoft,
                    $"Minecraft Java profile acquisition failed: {e.Message}");
            }
            else
            {
                Data.NetworkError = e.StatusCode;
                EmitFinished(AccountTaskState.Offline,
                    $"Minecraft Java profile acquisition failed: {e.Message}");
            }

            return;
        }

        // NOTE: Succeed even if we do not have a profile. This is a valid account state.
        if (profile is null)
        {
            Data.MinecraftProfile = new MinecraftProfile();
            EmitFinished(AccountTaskState.Working, "Account has no Minecraft profile.");
            return;
        }

        profile.ApplyTo(Data.MinecraftProfile);
        EmitFinished(AccountTaskState.Working, "Got Minecraft profile");
    }
}
