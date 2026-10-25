// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
//
// Port of launcher/minecraft/auth/steps/XboxAuthorizationStep.{h,cpp} (XSTS authorize), including
// the full XErr error-code ladder of processSTSError().

using System.Text.Json;
using System.Text.Json.Nodes;

namespace FlintLauncher.Core.Auth;

public sealed class XboxAuthorizationStep(
    AccountData data,
    Action<Token> assignToken,
    string relyingParty,
    string authorizationKind,
    AuthEndpointsOptions? auth = null)
    : AuthStep(data)
{
    private readonly AuthEndpointsOptions _auth = auth ?? new AuthEndpointsOptions();

    public override string Describe() => $"Getting authorization to access {authorizationKind} services.";

    private static readonly KeyValuePair<string, string>[] Headers =
    [
        new("Content-Type", "application/json"),
        new("Accept", "application/json"),
        new("x-xbl-contract-version", "1"),
    ];

    public override async Task PerformAsync(CancellationToken cancellationToken)
    {
        var xboxAuthTemplate = "\n" + """
                                      {
                                          "Properties": {
                                              "SandboxId": "RETAIL",
                                              "UserTokens": [
                                                  "%1"
                                              ]
                                          },
                                          "RelyingParty": "%2",
                                          "TokenType": "JWT"
                                      }
                                      """ + "\n";
        var xboxAuthData = xboxAuthTemplate.Replace("%1", Data.UserToken.Value).Replace("%2", relyingParty);

        var response = await AuthStepHttp.SendAsync(new AuthStepRequest
        {
            Url = new Uri(_auth.XboxXstsAuthorize),
            Body = xboxAuthData,
            Headers = Headers,
            AutoRetry = true,
        }, _auth.Http, _auth.Settings, cancellationToken).ConfigureAwait(false);

        if (!response.Success)
        {
            if (AuthNetUtils.IsApplicationError(response.Error) && !AuthNetUtils.IsServerError(response.Error))
            {
                if (ProcessStsError(response))
                {
                    return;
                }

                EmitFinished(AccountTaskState.FailedSoft,
                    $"Unknown STS error for {authorizationKind} services: {response.ErrorString}");
            }
            else
            {
                Data.NetworkError = response.Error;
                EmitFinished(AccountTaskState.Offline,
                    $"Failed to get authorization for {authorizationKind} services: {response.ErrorString}");
            }

            return;
        }

        var temp = new Token();
        if (!Parsers.ParseXTokenResponse(response.Body, temp, authorizationKind))
        {
            EmitFinished(AccountTaskState.FailedSoft,
                $"Could not parse authorization response for access to {authorizationKind} services.");
            return;
        }

        if (AccountData.GetString(ExtraValue(temp.Extra, "uhs")) !=
            AccountData.GetString(ExtraValue(Data.UserToken.Extra, "uhs")))
        {
            EmitFinished(AccountTaskState.FailedSoft,
                $"Server has changed {authorizationKind} authorization user hash in the reply. Something is wrong.");
            return;
        }

        assignToken(temp);

        EmitFinished(AccountTaskState.Working, $"Got authorization to access {relyingParty}");
    }

    private static JsonElement ExtraValue(Dictionary<string, JsonElement> extra, string key)
    {
        return extra.GetValueOrDefault(key);
    }

    /// <summary>Port of processSTSError: true when the XSTS error object was handled.</summary>
    private bool ProcessStsError(AuthStepResponse response)
    {
        if (response.Error != (int)QtNetworkError.AuthenticationRequiredError)
        {
            return false;
        }

        JsonObject? obj;
        try
        {
            obj = JsonNode.Parse(response.Body) as JsonObject;
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            obj = null;
        }

        if (obj is null)
        {
            EmitFinished(AccountTaskState.FailedSoft,
                $"Cannot parse {authorizationKind} authorization error response as JSON.");
            return true;
        }

        if (!Parsers.GetNumber(AccountData.Get(obj, "XErr"), out long errorCode))
        {
            EmitFinished(AccountTaskState.FailedSoft,
                $"XErr element is missing from {authorizationKind} authorization error response.");
            return true;
        }

        switch (errorCode)
        {
            case 2148916233:
                EmitFinished(AccountTaskState.FailedSoft,
                    "This Microsoft account does not have an Xbox Live profile. Buy the game on " +
                    "<a href=\"https://www.minecraft.net/en-us/store/minecraft-java-edition\">minecraft.net</a> first.");
                return true;
            case 2148916235:
                // NOTE: this is the Grulovia error
                EmitFinished(AccountTaskState.FailedSoft, "Xbox Live is not available in your country. You've been blocked.");
                return true;
            case 2148916238:
                EmitFinished(AccountTaskState.FailedSoft,
                    "This Microsoft account is underaged and is not linked to a family.\n\n" +
                    "Please set up your account according to " +
                    "<a href=\"https://help.minecraft.net/hc/en-us/articles/4408968616077\">help.minecraft.net</a>.");
                return true;
            // the following codes where copied from: https://github.com/PrismarineJS/prismarine-auth/pull/44
            case 2148916236:
                EmitFinished(AccountTaskState.FailedSoft,
                    "This Microsoft account requires proof of age to play. Please login to " +
                    "<a href=\"https://login.live.com/login.srf\">login.live.com</a> to provide proof of age.");
                return true;
            case 2148916237:
                EmitFinished(AccountTaskState.FailedSoft,
                    "This Microsoft account has reached its limit for playtime. This Microsoft account has been blocked from logging in.");
                return true;
            case 2148916227:
                EmitFinished(AccountTaskState.FailedSoft,
                    "This Microsoft account was banned by Xbox for violating one or more Community Standards for Xbox and is unable to be used.");
                return true;
            case 2148916229:
                EmitFinished(AccountTaskState.FailedSoft,
                    "This Microsoft account is currently restricted and your guardian has not given you permission to play " +
                    "online. Login to <a href=\"https://account.microsoft.com/family/\">account.microsoft.com</a> and have " +
                    "your guardian change your permissions.");
                return true;
            case 2148916234:
                EmitFinished(AccountTaskState.FailedSoft,
                    "This Microsoft account has not accepted Xbox's Terms of Service. Please login and accept them.");
                return true;
            default:
                EmitFinished(AccountTaskState.FailedSoft,
                    $"XSTS authentication ended with unrecognized error(s):\n\n{errorCode}");
                return true;
        }
    }
}
