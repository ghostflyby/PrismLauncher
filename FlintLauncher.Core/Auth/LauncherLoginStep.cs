// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
//
// Port of launcher/minecraft/auth/steps/LauncherLoginStep.{h,cpp}.

using System.Text.Json;
using FlintLauncher.ExternalApi.Mojang;

namespace FlintLauncher.Core.Auth;

public sealed class LauncherLoginStep(AccountData data, AuthEndpointsOptions? auth = null) : AuthStep(data)
{
    private readonly AuthEndpointsOptions _auth = auth ?? new AuthEndpointsOptions();

    public override string Describe() => "Fetching Minecraft access token";

    public override async Task PerformAsync(CancellationToken cancellationToken)
    {
        var uhs = AccountData.GetString(ExtraValue("uhs"));
        var xToken = Data.MojangServicesToken.Value;

        try
        {
            (await AuthStepHttp.SendAsync(_auth.Mojang.MinecraftServices.LauncherLogin(uhs, xToken),
                _auth.Http, autoRetry: true, cancellationToken).ConfigureAwait(false))
                .ApplyTo(Data.YggdrasilToken);
        }
        catch (MojangApiException e) when (e.InnerException is JsonException)
        {
            EmitFinished(AccountTaskState.FailedSoft, "Failed to parse the Minecraft access token response.");
            return;
        }
        catch (MojangApiException e)
        {
            if (e.IsClientError)
            {
                EmitFinished(AccountTaskState.FailedSoft, $"Failed to get Minecraft access token: {e.Message}");
            }
            else
            {
                Data.NetworkError = e.StatusCode;
                EmitFinished(AccountTaskState.Offline, $"Failed to get Minecraft access token: {e.Message}");
            }

            return;
        }

        EmitFinished(AccountTaskState.Working, "Got Minecraft access token");
    }

    private JsonElement ExtraValue(string key)
    {
        return Data.MojangServicesToken.Extra.GetValueOrDefault(key);
    }
}
