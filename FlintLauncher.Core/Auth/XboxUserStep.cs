// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
//
// Port of launcher/minecraft/auth/steps/XboxUserStep.{h,cpp}.

using System.Text.Json;
using FlintLauncher.ExternalApi.Mojang;

namespace FlintLauncher.Core.Auth;

public sealed class XboxUserStep(AccountData data, AuthEndpointsOptions? auth = null) : AuthStep(data)
{
    private readonly AuthEndpointsOptions _auth = auth ?? new AuthEndpointsOptions();

    public override string Describe() => "Logging in as an Xbox user.";

    public override async Task PerformAsync(CancellationToken cancellationToken)
    {
        try
        {
            Data.UserToken = (await AuthStepHttp.SendAsync(
                    _auth.Mojang.Xbox.UserAuthenticate(Data.MsaToken.Value),
                    _auth.Http, autoRetry: true, cancellationToken).ConfigureAwait(false))
                .ToToken();
        }
        catch (MojangApiException e) when (e.InnerException is JsonException)
        {
            EmitFinished(AccountTaskState.FailedSoft, "Xbox user authentication response could not be understood.");
            return;
        }
        catch (MojangApiException e)
        {
            if (e.IsClientError)
            {
                EmitFinished(AccountTaskState.FailedSoft, $"Xbox user authentication failed: {e.Message}");
            }
            else
            {
                Data.NetworkError = e.StatusCode;
                EmitFinished(AccountTaskState.Offline, $"Xbox user authentication failed: {e.Message}");
            }

            return;
        }

        EmitFinished(AccountTaskState.Working, "Got Xbox user token");
    }
}
