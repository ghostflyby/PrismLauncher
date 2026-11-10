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

using System.Text.Json;
using FlintLauncher.ExternalApi.Mojang;

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
        _clientId = _auth.Mojang.Options.MsaClientId;
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
        try
        {
            var authorization = await AuthStepHttp.SendAsync(
                _auth.Mojang.Microsoft.DeviceAuthorization(_clientId),
                _auth.Http, autoRetry: true, cancellationToken).ConfigureAwait(false);

            // the loader rejects an answer without the codes and a positive expiry; a stated
            // positive interval overrides the client's default
            if (authorization.Interval is int interval and > 0)
            {
                _interval = interval;
            }

            _deviceCode = authorization.DeviceCode!;
            AuthorizeWithBrowser?.Invoke(authorization.VerificationUri!, authorization.UserCode!,
                authorization.ExpiresIn!.Value);
            _expirationUtc = DateTime.UtcNow.AddSeconds(authorization.ExpiresIn!.Value);
        }
        catch (MojangApiException e)
        {
            // the FailedHard report settles the step; leaving the expiration set would keep
            // the loop re-requesting a code that is already expired
            _expirationUtc = null;
            var reason = e.InnerException switch
            {
                InvalidOperationException refusal => refusal.Message,
                JsonException => "required fields missing",
                _ => e.Message,
            };
            EmitFinished(AccountTaskState.FailedHard, $"Device authorization failed: {reason}");
        }
    }

    private async Task<bool> PollForToken(CancellationToken cancellationToken)
    {
        MojangTokenAnswer answer;
        try
        {
            answer = await AuthStepHttp.SendAsync(
                _auth.Mojang.Microsoft.DeviceToken(_clientId, _deviceCode),
                _auth.Http, autoRetry: false, cancellationToken).ConfigureAwait(false);
        }
        catch (MojangApiException e)
        {
            if (e.InnerException is TimeoutException)
            {
                // rfc8628#section-3.5
                // "On encountering a connection timeout, clients MUST unilaterally
                // reduce their polling frequency before retrying.  The use of an
                // exponential backoff algorithm to achieve this, such as doubling the
                // polling interval on each such connection timeout, is RECOMMENDED."
                _interval *= 2;
                return false;
            }

            // a non-2xx answer without an OAuth error object: it failed, so just try again
            // without increasing the interval
            return false;
        }

        if (answer is MojangTokenRefused refusal)
        {
            if (refusal.Error == "slow_down")
            {
                // rfc8628#section-3.5
                // "A variant of 'authorization_pending', the authorization request is
                // still pending and polling should continue, but the interval MUST
                // be increased by 5 seconds for this and all subsequent requests."
                _interval += 5;
                return false;
            }

            if (refusal.Error == "authorization_pending")
            {
                // keep trying - rfc8628#section-3.5
                return false;
            }

            EmitFinished(AccountTaskState.FailedHard,
                $"Device Access failed: {(refusal.ErrorDescription.Length != 0 ? refusal.ErrorDescription : refusal.Error)}");
            return true;
        }

        var grant = (MojangTokenGranted)answer;
        _expirationUtc = null;
        Data.MsaClientId = _clientId;
        var now = DateTimeOffset.UtcNow;
        Data.MsaToken.IssueInstant = now;
        // upstream writes QDateTime::currentDateTime() (local time) here, but the consumers
        // (MinecraftAccount refresh check, MsaInteractiveStep) compare against the current
        // instant — a local-time reading silently shifts the expiry by the zone offset
        Data.MsaToken.NotAfter = now.AddSeconds(grant.ExpiresIn);
        Data.MsaToken.Extra = new Dictionary<string, JsonElement>(grant.Extra);
        Data.MsaToken.RefreshToken = grant.RefreshToken ?? "";
        Data.MsaToken.Value = grant.AccessToken;
        EmitFinished(AccountTaskState.Working, "Got MSA token");
        return true;
    }
}
