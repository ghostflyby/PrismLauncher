// SPDX-License-Identifier: GPL-3.0-only
// In-memory correlation store for the MSA device-code endpoints.
//
// Each POST /api/v1/accounts/msa/devicecode creates a real MinecraftAccount MSA login flow
// (MinecraftAccount.Login(useDeviceCode: true) -> AuthFlow with MSADeviceCodeStep); the
// flow's AuthorizeWithBrowserWithExtra event (fired by MSADeviceCodeStep once Microsoft
// issued the code) is captured into the session. The flow keeps running in the background:
// it polls Microsoft for the token and, once authorized, walks the remaining steps (Xbox ->
// XSTS -> minecraftservices login -> entitlements -> profile -> skin) exactly like the
// interactive C++ flow. Poll simply reports the AuthFlow's progress:
//   pending  - the flow is still running (code issued or not yet completed)
//   done     - the flow succeeded; the finished account is added to the AccountList
//   expired  - the flow failed (expired device code, denied user, transport failure...)
// deviceId is an internal correlation id (never sent to Microsoft).

using FlintLauncher.Core.Auth;
using FlintLauncher.Core.Tasks;
using FlintLauncher.Web.Models;

namespace FlintLauncher.Web.Services;

public sealed class DeviceCodeSessionStore
{
    public sealed class Session
    {
        public required string DeviceId { get; init; }
        public required MinecraftAccount Account { get; init; }
        public required AuthFlow Flow { get; init; }
        public string UserCode { get; set; } = "";
        public string VerificationUri { get; set; } = "";
        public int ExpiresInSeconds { get; set; }
        public bool CodeIssued { get; set; }
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    /// <summary>
    /// Start a device-code login: creates the account + flow, registers the code capture and
    /// starts the flow in the background. The deviceId is returned before the code is known
    /// (the HTTP call to Microsoft is in flight); the start endpoint waits briefly for the
    /// code through <see cref="WaitForCodeAsync"/>.
    /// </summary>
    public Session Start(AuthEndpointsOptions? auth = null)
    {
        var account = MinecraftAccount.CreateBlankMsa();
        var flow = account.Login(useDeviceCode: true, auth)
                   ?? throw new InvalidOperationException("could not create the device-code auth flow");

        var session = new Session
        {
            DeviceId = Guid.NewGuid().ToString("N"),
            Account = account,
            Flow = flow,
        };
        flow.AuthorizeWithBrowserWithExtra += (uri, code, expiresIn) =>
        {
            session.UserCode = code;
            session.VerificationUri = uri;
            session.ExpiresInSeconds = expiresIn;
            session.CodeIssued = true;
        };

        lock (_lock)
        {
            _sessions[session.DeviceId] = session;
        }

        _ = flow.StartAsync(); // background: polls Microsoft, then runs the remaining steps
        return session;
    }

    /// <summary>Wait (bounded) until the device code/user code handshake arrived.</summary>
    public async Task<bool> WaitForCodeAsync(Session session, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!session.CodeIssued)
        {
            if (session.Flow.IsFinished || DateTime.UtcNow > deadline)
            {
                return false;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        return true;
    }

    public Session? Get(string deviceId)
    {
        lock (_lock)
        {
            return _sessions.GetValueOrDefault(deviceId);
        }
    }

    /// <summary>Map the session to the poll response; a finished flow is folded into the
    /// account list once (idempotent across repeated polls).</summary>
    public DeviceCodePollResponse Poll(Session session, AccountList accounts)
    {
        var flow = session.Flow;
        if (flow.IsFinished)
        {
            if (flow.State == TaskState.Succeeded)
            {
                if (accounts.FindAccountByProfileId(session.Account.ProfileId()) == -1)
                {
                    accounts.AddAccount(session.Account);
                }

                return new DeviceCodePollResponse("done", ToView(session.Account));
            }

            return new DeviceCodePollResponse("expired", null);
        }

        return new DeviceCodePollResponse("pending", null);
    }

    public static AccountView ToView(MinecraftAccount account) => new(
        account.ProfileId(),
        account.ProfileName(),
        account.TypeString(),
        IsDefault: false);
}
