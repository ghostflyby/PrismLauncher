// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
//
// Port of launcher/minecraft/auth/steps/XboxUserStep.{h,cpp}.

namespace FlintLauncher.Core.Auth;

public sealed class XboxUserStep(AccountData data, AuthEndpointsOptions? auth = null) : AuthStep(data)
{
    private readonly AuthEndpointsOptions _auth = auth ?? new AuthEndpointsOptions();

    public override string Describe() => "Logging in as an Xbox user.";

    private static readonly KeyValuePair<string, string>[] Headers =
    [
        new("Content-Type", "application/json"),
        new("Accept", "application/json"),
        // set contract-version header (prevent err 400 bad-request?)
        // https://learn.microsoft.com/en-us/gaming/gdk/_content/gc/reference/live/rest/additional/httpstandardheaders
        new("x-xbl-contract-version", "1"),
    ];

    public override async Task PerformAsync(CancellationToken cancellationToken)
    {
        // NOTE: the raw-string template (with its leading/trailing newline and the "d=" RpsTicket
        // prefix on the plain MSA token) is reproduced byte for byte.
        var xboxAuthTemplate = "\n" + """
                                      {
                                          "Properties": {
                                              "AuthMethod": "RPS",
                                              "SiteName": "user.auth.xboxlive.com",
                                              "RpsTicket": "d=%1"
                                          },
                                          "RelyingParty": "http://auth.xboxlive.com",
                                          "TokenType": "JWT"
                                      }
                                      """ + "\n";
        var xboxAuthData = xboxAuthTemplate.Replace("%1", Data.MsaToken.Value);

        var response = await AuthStepHttp.SendAsync(new AuthStepRequest
        {
            Url = new Uri(_auth.XboxUserAuth),
            Body = xboxAuthData,
            Headers = Headers,
            AutoRetry = true,
        }, _auth.Http, _auth.Settings, cancellationToken).ConfigureAwait(false);

        if (!response.Success)
        {
            if (AuthNetUtils.IsApplicationError(response.Error) && !AuthNetUtils.IsServerError(response.Error))
            {
                EmitFinished(AccountTaskState.FailedSoft, $"Xbox user authentication failed: {response.ErrorString}");
            }
            else
            {
                Data.NetworkError = response.Error;
                EmitFinished(AccountTaskState.Offline, $"Xbox user authentication failed: {response.ErrorString}");
            }

            return;
        }

        var temp = new Token();
        if (!Parsers.ParseXTokenResponse(response.Body, temp, "UToken"))
        {
            EmitFinished(AccountTaskState.FailedSoft, "Xbox user authentication response could not be understood.");
            return;
        }

        Data.UserToken = temp;
        EmitFinished(AccountTaskState.Working, "Got Xbox user token");
    }
}
