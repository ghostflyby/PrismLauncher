// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2024 Trial97 <alexandru.tripon97@gmail.com>
//
// This file incorporates work covered by the following copyright and
// permission notice:
//
//     Copyright 2013-2021 MultiMC Contributors
//
//     Licensed under the Apache License, Version 2.0 (the "License");
//     you may not use this file except in compliance with the License.
//     You may obtain a copy of the License at
//
//         http://www.apache.org/licenses/LICENSE-2.0
//
//     Unless required by applicable law or agreed to in writing, software
//     distributed under the License is distributed on an "AS IS" BASIS,
//     WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//     See the License for the specific language governing permissions and
//     limitations under the License.
//
// Port of launcher/minecraft/auth/steps/MSADeviceCodeStep.{h,cpp} (RFC 8628 device code flow).
//
// The Qt pool/expiration timers become an await/Task.Delay polling loop with the exact upstream
// state machine: authorization_pending keeps polling, slow_down adds 5 seconds to the interval,
// a connection timeout doubles it (rfc8628 section 3.5), transport failures retry without
// touching the interval, and when the device code is about to expire the whole device
// authorization restarts. Upstream quirk kept: the local expiry abort (step abort with no
// Finished report) leaves the flow hanging, exactly like the C++ MSADeviceCodeStep::abort() —
// in practice the server's expired_token error is what surfaces to the user.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FlintLauncher.Core.Auth;

public sealed class MsaDeviceCodeStep(AccountData data, AuthEndpointsOptions? auth = null) : AuthStep(data)
{
    private readonly AuthEndpointsOptions _auth = auth ?? new AuthEndpointsOptions();

    // https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-device-code

    public override string Describe() => "Logging in with Microsoft account(device code).";

    /// <summary>Port of the authorizeWithBrowser(QString url, QString code, int expiresIn) signal.</summary>
    public event Action<string, string, int>? AuthorizeWithBrowser;

    private string _clientId = "";
    private string _deviceCode = "";
    private bool _isAborted;
    private int _interval = 5;
    private DateTime? _expirationUtc; // stands in for m_expiration_timer

    public override void Abort()
    {
        _expirationUtc = null; // m_expiration_timer.stop(); m_pool_timer.stop();
        _isAborted = true;
    }

    public override async Task PerformAsync(CancellationToken cancellationToken)
    {
        _clientId = _auth.MsaClientId;
        await RequestDeviceAuthorization(cancellationToken).ConfigureAwait(false);
        while (!_isAborted && _expirationUtc is { } expiration)
        {
            var remaining = expiration - DateTime.UtcNow;
            if (remaining < TimeSpan.FromSeconds(_interval))
            {
                // the device code is about to expire: start the flow over with a fresh code
                // (upstream startPoolTimer() -> perform())
                await RequestDeviceAuthorization(cancellationToken).ConfigureAwait(false);
                continue;
            }

            await Task.Delay(TimeSpan.FromSeconds(_interval), cancellationToken).ConfigureAwait(false);
            if (await PollForToken(cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    private async Task RequestDeviceAuthorization(CancellationToken cancellationToken)
    {
        var payload = FormEncode(("client_id", _clientId), ("scope", "XboxLive.SignIn XboxLive.offline_access"));
        var response = await AuthStepHttp.SendAsync(new AuthStepRequest
        {
            Url = new Uri(_auth.MsaDeviceCode),
            Body = payload,
            Headers = FormHeaders,
            AutoRetry = true,
        }, cancellationToken).ConfigureAwait(false);

        if (!response.Success)
        {
            EmitFinished(AccountTaskState.FailedHard, $"Device authorization failed: {response.ErrorString}");
            return;
        }

        var rsp = ParseDeviceAuthorizationResponse(response.Body);
        if (rsp.Error.Length != 0 || rsp.ErrorDescription.Length != 0)
        {
            EmitFinished(AccountTaskState.FailedHard,
                $"Device authorization failed: {(rsp.ErrorDescription.Length != 0 ? rsp.ErrorDescription : rsp.Error)}");
            return;
        }

        if (rsp.DeviceCode.Length == 0 || rsp.UserCode.Length == 0 || rsp.VerificationUri.Length == 0 || rsp.ExpiresIn == 0)
        {
            EmitFinished(AccountTaskState.FailedHard, "Device authorization failed: required fields missing");
            return;
        }

        if (rsp.Interval != 0)
        {
            _interval = rsp.Interval;
        }

        _deviceCode = rsp.DeviceCode;
        AuthorizeWithBrowser?.Invoke(rsp.VerificationUri, rsp.UserCode, rsp.ExpiresIn);
        _expirationUtc = DateTime.UtcNow.AddSeconds(rsp.ExpiresIn);
    }

    private async Task<bool> PollForToken(CancellationToken cancellationToken)
    {
        var payload = FormEncode(
            ("client_id", _clientId),
            ("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
            ("device_code", _deviceCode));
        var response = await AuthStepHttp.SendAsync(new AuthStepRequest
        {
            Url = new Uri(_auth.MsaToken),
            Body = payload,
            Headers = FormHeaders,
        }, cancellationToken).ConfigureAwait(false);

        if (response.Error == (int)QtNetworkError.TimeoutError)
        {
            // rfc8628#section-3.5
            // "On encountering a connection timeout, clients MUST unilaterally
            // reduce their polling frequency before retrying.  The use of an
            // exponential backoff algorithm to achieve this, such as doubling the
            // polling interval on each such connection timeout, is RECOMMENDED."
            _interval *= 2;
            return false;
        }

        var rsp = ParseAuthenticationResponse(response.Body);
        if (rsp.Error == "slow_down")
        {
            // rfc8628#section-3.5
            // "A variant of 'authorization_pending', the authorization request is
            // still pending and polling should continue, but the interval MUST
            // be increased by 5 seconds for this and all subsequent requests."
            _interval += 5;
            return false;
        }

        if (rsp.Error == "authorization_pending")
        {
            // keep trying - rfc8628#section-3.5
            return false;
        }

        if (rsp.Error.Length != 0 || rsp.ErrorDescription.Length != 0)
        {
            EmitFinished(AccountTaskState.FailedHard,
                $"Device Access failed: {(rsp.ErrorDescription.Length != 0 ? rsp.ErrorDescription : rsp.Error)}");
            return true;
        }

        if (!response.Success)
        {
            return false; // it failed so just try again without increasing the interval
        }

        _expirationUtc = null;
        Data.MsaClientId = _clientId;
        Data.MsaToken.IssueInstant = DateTime.UtcNow;
        // upstream writes QDateTime::currentDateTime() (local time) here, but the consumers
        // (MinecraftAccount refresh check, MsaInteractiveStep) compare against UtcNow — a
        // local-kind DateTime silently shifts the expiry by the zone offset, so store UTC
        Data.MsaToken.NotAfter = DateTime.UtcNow.AddSeconds(rsp.ExpiresIn);
        Data.MsaToken.Extra = rsp.Extra;
        Data.MsaToken.RefreshToken = rsp.RefreshToken;
        Data.MsaToken.Value = rsp.AccessToken;
        EmitFinished(AccountTaskState.Working, "Got MSA token");
        return true;
    }

    private static readonly KeyValuePair<string, string>[] FormHeaders =
    [
        new("Content-Type", "application/x-www-form-urlencoded"),
        new("Accept", "application/json"),
    ];

    private static string FormEncode(params (string key, string value)[] pairs)
    {
        var sb = new StringBuilder();
        foreach (var (key, value) in pairs)
        {
            if (sb.Length > 0)
            {
                sb.Append('&');
            }

            sb.Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
        }

        return sb.ToString();
    }

    private sealed record DeviceAuthorizationResponse(
        string DeviceCode,
        string UserCode,
        string VerificationUri,
        int ExpiresIn,
        int Interval,
        string Error,
        string ErrorDescription);

    private static DeviceAuthorizationResponse ParseDeviceAuthorizationResponse(byte[] data)
    {
        if (TryParseObject(data) is not { } obj)
        {
            return new DeviceAuthorizationResponse("", "", "", 0, 0, "", "");
        }

        return new DeviceAuthorizationResponse(
            AccountData.GetString(AccountData.Get(obj, "device_code")),
            AccountData.GetString(AccountData.Get(obj, "user_code")),
            AccountData.GetString(AccountData.Get(obj, "verification_uri")),
            AccountData.EnsureInteger(obj, "expires_in"),
            AccountData.EnsureInteger(obj, "interval"),
            AccountData.GetString(AccountData.Get(obj, "error")),
            AccountData.GetString(AccountData.Get(obj, "error_description")));
    }

    private sealed record AuthenticationResponse(
        string AccessToken,
        string RefreshToken,
        int ExpiresIn,
        string Error,
        string ErrorDescription,
        Dictionary<string, JsonElement> Extra);

    private static AuthenticationResponse ParseAuthenticationResponse(byte[] data)
    {
        if (TryParseObject(data) is not { } obj)
        {
            return new AuthenticationResponse("", "", 0, "", "", new Dictionary<string, JsonElement>());
        }

        var extra = new Dictionary<string, JsonElement>();
        foreach (var (key, value) in obj)
        {
            if (value is not null)
            {
                extra[key] = AccountData.ToElement(value);
            }
        }

        return new AuthenticationResponse(
            AccountData.GetString(AccountData.Get(obj, "access_token")),
            AccountData.GetString(AccountData.Get(obj, "refresh_token")),
            AccountData.EnsureInteger(obj, "expires_in"),
            AccountData.GetString(AccountData.Get(obj, "error")),
            AccountData.GetString(AccountData.Get(obj, "error_description")),
            extra);
    }

    /// <summary>QJsonDocument::fromJson equivalent: null object on any parse failure (upstream {} struct).</summary>
    private static JsonObject? TryParseObject(byte[] data)
    {
        try
        {
            return JsonNode.Parse(data) as JsonObject;
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            return null;
        }
    }
}
