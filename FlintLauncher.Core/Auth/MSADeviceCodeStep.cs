// SPDX-License-Identifier: GPL-3.0-only
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

public sealed class MsaDeviceCodeStep : AuthStep
{
    private readonly AuthEndpointsOptions _auth;

    // https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-device-code
    public MsaDeviceCodeStep(AccountData data, AuthEndpointsOptions? auth = null) : base(data)
    {
        _auth = auth ?? new AuthEndpointsOptions();
    }

    public override string Describe() => "Logging in with Microsoft account(device code).";

    /// <summary>Port of the authorizeWithBrowser(QString url, QString code, int expiresIn) signal.</summary>
    public event Action<string, string, int>? AuthorizeWithBrowser;

    private string clientId = "";
    private string deviceCode = "";
    private bool isAborted;
    private int interval = 5;
    private DateTime? expirationUtc; // stands in for m_expiration_timer

    public override void Abort()
    {
        expirationUtc = null; // m_expiration_timer.stop(); m_pool_timer.stop();
        isAborted = true;
    }

    public override async Task PerformAsync(CancellationToken cancellationToken)
    {
        clientId = _auth.MsaClientId;
        await RequestDeviceAuthorization(cancellationToken).ConfigureAwait(false);
        while (!isAborted && expirationUtc is { } expiration)
        {
            var remaining = expiration - DateTime.UtcNow;
            if (remaining < TimeSpan.FromSeconds(interval))
            {
                // the device code is about to expire: start the flow over with a fresh code
                // (upstream startPoolTimer() -> perform())
                await RequestDeviceAuthorization(cancellationToken).ConfigureAwait(false);
                continue;
            }
            await Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken).ConfigureAwait(false);
            if (await PollForToken(cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    private async Task RequestDeviceAuthorization(CancellationToken cancellationToken)
    {
        var payload = FormEncode(("client_id", clientId), ("scope", "XboxLive.SignIn XboxLive.offline_access"));
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
            interval = rsp.Interval;
        }
        deviceCode = rsp.DeviceCode;
        AuthorizeWithBrowser?.Invoke(rsp.VerificationUri, rsp.UserCode, rsp.ExpiresIn);
        expirationUtc = DateTime.UtcNow.AddSeconds(rsp.ExpiresIn);
    }

    private async Task<bool> PollForToken(CancellationToken cancellationToken)
    {
        var payload = FormEncode(
            ("client_id", clientId),
            ("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
            ("device_code", deviceCode));
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
            interval *= 2;
            return false;
        }
        var rsp = ParseAuthenticationResponse(response.Body);
        if (rsp.Error == "slow_down")
        {
            // rfc8628#section-3.5
            // "A variant of 'authorization_pending', the authorization request is
            // still pending and polling should continue, but the interval MUST
            // be increased by 5 seconds for this and all subsequent requests."
            interval += 5;
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

        expirationUtc = null;
        Data.MsaClientId = clientId;
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
        string TokenType,
        string RefreshToken,
        int ExpiresIn,
        string Error,
        string ErrorDescription,
        Dictionary<string, JsonNode> Extra);

    private static AuthenticationResponse ParseAuthenticationResponse(byte[] data)
    {
        if (TryParseObject(data) is not { } obj)
        {
            return new AuthenticationResponse("", "", "", 0, "", "", new Dictionary<string, JsonNode>());
        }
        var extra = new Dictionary<string, JsonNode>();
        foreach (var (key, value) in obj)
        {
            if (value is not null)
            {
                extra[key] = value.DeepClone();
            }
        }
        return new AuthenticationResponse(
            AccountData.GetString(AccountData.Get(obj, "access_token")),
            AccountData.GetString(AccountData.Get(obj, "token_type")),
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
