// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/auth/steps/MinecraftProfileStep.{h,cpp}.

namespace FlintLauncher.Core.Auth;

public sealed class MinecraftProfileStep(AccountData data, AuthEndpointsOptions? auth = null) : AuthStep(data)
{
    private readonly AuthEndpointsOptions _auth = auth ?? new AuthEndpointsOptions();

    public override string Describe() => "Fetching the Minecraft profile.";

    private static readonly KeyValuePair<string, string>[] Headers =
    [
        new("Content-Type", "application/json"),
        new("Accept", "application/json"),
    ];

    public override async Task PerformAsync(CancellationToken cancellationToken)
    {
        var url = _auth.MinecraftServices.TrimEnd('/') + "/minecraft/profile";
        var headers = new List<KeyValuePair<string, string>>(Headers)
        {
            new("Authorization", $"Bearer {Data.YggdrasilToken.Value}"),
        };

        var response = await AuthStepHttp.SendAsync(new AuthStepRequest
        {
            Url = new Uri(url),
            Headers = headers,
            AutoRetry = true,
        }, cancellationToken).ConfigureAwait(false);

        if (response.Error == (int)QtNetworkError.ContentNotFoundError)
        {
            // NOTE: Succeed even if we do not have a profile. This is a valid account state.
            Data.MinecraftProfile = new MinecraftProfile();
            EmitFinished(AccountTaskState.Working, "Account has no Minecraft profile.");
            return;
        }

        if (!response.Success)
        {
            if (AuthNetUtils.IsApplicationError(response.Error) && !AuthNetUtils.IsServerError(response.Error))
            {
                EmitFinished(AccountTaskState.FailedSoft,
                    $"Minecraft Java profile acquisition failed: {response.ErrorString}");
            }
            else
            {
                Data.NetworkError = response.Error;
                EmitFinished(AccountTaskState.Offline,
                    $"Minecraft Java profile acquisition failed: {response.ErrorString}");
            }

            return;
        }

        if (!Parsers.ParseMinecraftProfile(response.Body, Data.MinecraftProfile))
        {
            Data.MinecraftProfile = new MinecraftProfile();
            EmitFinished(AccountTaskState.FailedSoft, "Minecraft Java profile response could not be parsed");
            return;
        }

        EmitFinished(AccountTaskState.Working, "Got Minecraft profile");
    }
}
