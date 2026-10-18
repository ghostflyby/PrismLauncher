// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/auth/steps/LauncherLoginStep.{h,cpp}.

using System.Text.Json.Nodes;

namespace FlintLauncher.Core.Auth;

public sealed class LauncherLoginStep(AccountData data, AuthEndpointsOptions? auth = null) : AuthStep(data)
{
    private readonly AuthEndpointsOptions _auth = auth ?? new AuthEndpointsOptions();

    public override string Describe() => "Fetching Minecraft access token";

    private static readonly KeyValuePair<string, string>[] Headers =
    [
        new("Content-Type", "application/json"),
        new("Accept", "application/json"),
    ];

    public override async Task PerformAsync(CancellationToken cancellationToken)
    {
        var url = _auth.MinecraftServices.TrimEnd('/') + "/launcher/login";
        var uhs = AccountData.GetString(ExtraValue("uhs"));
        var xToken = Data.MojangServicesToken.Value;

        var mcAuthTemplate = "\n" + """
                                    {
                                        "xtoken": "XBL3.0 x=%1;%2",
                                        "platform": "PC_LAUNCHER"
                                    }
                                    """ + "\n";
        var requestBody = mcAuthTemplate.Replace("%1", uhs).Replace("%2", xToken);

        var response = await AuthStepHttp.SendAsync(new AuthStepRequest
        {
            Url = new Uri(url),
            Body = requestBody,
            Headers = Headers,
            AutoRetry = true,
        }, cancellationToken).ConfigureAwait(false);

        if (!response.Success)
        {
            if (AuthNetUtils.IsApplicationError(response.Error) && !AuthNetUtils.IsServerError(response.Error))
            {
                EmitFinished(AccountTaskState.FailedSoft, $"Failed to get Minecraft access token: {response.ErrorString}");
            }
            else
            {
                Data.NetworkError = response.Error;
                EmitFinished(AccountTaskState.Offline, $"Failed to get Minecraft access token: {response.ErrorString}");
            }

            return;
        }

        if (!Parsers.ParseMojangResponse(response.Body, Data.YggdrasilToken))
        {
            EmitFinished(AccountTaskState.FailedSoft, "Failed to parse the Minecraft access token response.");
            return;
        }

        EmitFinished(AccountTaskState.Working, "Got Minecraft access token");
    }

    private JsonNode? ExtraValue(string key)
    {
        return Data.MojangServicesToken.Extra.GetValueOrDefault(key);
    }
}
