// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
//
// Port of launcher/minecraft/auth/steps/XboxAuthorizationStep.{h,cpp} (XSTS authorize), including
// the full XErr error-code ladder of processSTSError().

using System.Text.Json;
using FlintLauncher.ExternalApi.Mojang;

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

    public override async Task PerformAsync(CancellationToken cancellationToken)
    {
        try
        {
            var granted = (await AuthStepHttp.SendAsync(
                    _auth.Mojang.Xbox.XstsAuthorize(Data.UserToken.Value, relyingParty),
                    _auth.Http, autoRetry: true, cancellationToken).ConfigureAwait(false))
                .ToToken();

            if (AccountData.GetString(ExtraValue(granted.Extra, "uhs")) !=
                AccountData.GetString(ExtraValue(Data.UserToken.Extra, "uhs")))
            {
                EmitFinished(AccountTaskState.FailedSoft,
                    $"Server has changed {authorizationKind} authorization user hash in the reply. Something is wrong.");
                return;
            }

            assignToken(granted);
        }
        catch (MojangXstsDeniedException e)
        {
            ProcessStsError(e);
            return;
        }
        catch (MojangApiException e) when (e.InnerException is JsonException)
        {
            EmitFinished(AccountTaskState.FailedSoft,
                $"Could not parse authorization response for access to {authorizationKind} services.");
            return;
        }
        catch (MojangApiException e)
        {
            if (e.IsClientError)
            {
                EmitFinished(AccountTaskState.FailedSoft,
                    $"Unknown STS error for {authorizationKind} services: {e.Message}");
            }
            else
            {
                Data.NetworkError = e.StatusCode;
                EmitFinished(AccountTaskState.Offline,
                    $"Failed to get authorization for {authorizationKind} services: {e.Message}");
            }

            return;
        }

        EmitFinished(AccountTaskState.Working, $"Got authorization to access {relyingParty}");
    }

    private static JsonElement ExtraValue(Dictionary<string, JsonElement> extra, string key)
    {
        return extra.GetValueOrDefault(key);
    }

    /// <summary>Port of processSTSError: the failure ladder of a 401 refusal.</summary>
    private void ProcessStsError(MojangXstsDeniedException failure)
    {
        var message = failure.Denial switch
        {
            MojangXstsDenied denial => DenialMessage(denial.XErr),
            MojangXstsDenialWithoutCode =>
                $"XErr element is missing from {authorizationKind} authorization error response.",
            _ => $"Cannot parse {authorizationKind} authorization error response as JSON.",
        };
        EmitFinished(AccountTaskState.FailedSoft, message);
    }

    private string DenialMessage(long errorCode)
    {
        return errorCode switch
        {
            2148916233 =>
                "This Microsoft account does not have an Xbox Live profile. Buy the game on " +
                "<a href=\"https://www.minecraft.net/en-us/store/minecraft-java-edition\">minecraft.net</a> first.",
            // NOTE: this is the Grulovia error
            2148916235 => "Xbox Live is not available in your country. You've been blocked.",
            2148916238 =>
                "This Microsoft account is underaged and is not linked to a family.\n\n" +
                "Please set up your account according to " +
                "<a href=\"https://help.minecraft.net/hc/en-us/articles/4408968616077\">help.minecraft.net</a>.",
            // the following codes where copied from: https://github.com/PrismarineJS/prismarine-auth/pull/44
            2148916236 =>
                "This Microsoft account requires proof of age to play. Please login to " +
                "<a href=\"https://login.live.com/login.srf\">login.live.com</a> to provide proof of age.",
            2148916237 =>
                "This Microsoft account has reached its limit for playtime. This Microsoft account has been blocked from logging in.",
            2148916227 =>
                "This Microsoft account was banned by Xbox for violating one or more Community Standards for Xbox and is unable to be used.",
            2148916229 =>
                "This Microsoft account is currently restricted and your guardian has not given you permission to play " +
                "online. Login to <a href=\"https://account.microsoft.com/family/\">account.microsoft.com</a> and have " +
                "your guardian change your permissions.",
            2148916234 => "This Microsoft account has not accepted Xbox's Terms of Service. Please login and accept them.",
            _ => $"XSTS authentication ended with unrecognized error(s):\n\n{errorCode}",
        };
    }
}
