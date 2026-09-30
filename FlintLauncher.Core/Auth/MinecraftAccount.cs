// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/auth/MinecraftAccount.{h,cpp}.
//
// Deviations:
//  - Usable (increment/decrementUses in-use tracking) and the getFace() QPixmap skin cropping
//    are not ported (Qt UI / launch-time reference counting, the latter P5); shouldRefresh()
//    therefore never sees isInUse() == true.
//  - The signal/slot pair changed()/activityChanged(bool) becomes the Changed/ActivityChanged
//    events; connect() bookkeeping inside AccountList is dropped accordingly.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FlintLauncher.Core.Instances;

namespace FlintLauncher.Core.Auth;

/// <summary>Object that stores information about a certain Mojang account.</summary>
public sealed class MinecraftAccount
{
    public MinecraftAccount()
    {
        // QUuid::createUuid().toString(QUuid::Id128): 32 hex digits, no dashes
        Data.InternalId = Guid.NewGuid().ToString("N");
    }

    public static MinecraftAccount? LoadFromJsonV3(JsonObject json)
    {
        var account = new MinecraftAccount();
        if (account.Data.ResumeStateFromV3(json))
        {
            return account;
        }
        return null;
    }

    public static MinecraftAccount CreateBlankMSA()
    {
        var account = new MinecraftAccount
        {
            Data =
            {
                Type = AccountType.MSA,
            },
        };
        return account;
    }

    public static MinecraftAccount CreateOffline(string username)
    {
        var account = new MinecraftAccount();
        account.Data.Type = AccountType.Offline;
        account.Data.YggdrasilToken.Token_ = "0";
        account.Data.YggdrasilToken.Validity = Validity.Certain;
        account.Data.YggdrasilToken.IssueInstant = DateTime.UtcNow;
        account.Data.YggdrasilToken.Extra["userName"] = System.Text.Json.Nodes.JsonValue.Create(username);
        account.Data.YggdrasilToken.Extra["clientToken"] = System.Text.Json.Nodes.JsonValue.Create(Guid.NewGuid().ToString("N"));
        account.Data.MinecraftProfile.Id = UuidFromUsername(username).ToString("N");
        account.Data.MinecraftProfile.Name = username;
        account.Data.MinecraftProfile.Validity = Validity.Certain;
        return account;
    }

    /// <summary>
    /// Port of uuidFromUsername: Java's UUID#nameUUIDFromBytes over "OfflinePlayer:&lt;name&gt;"
    /// (MD5, version 3, IETF variant). QUuid::fromRfc4122 maps to the RFC 4122 byte order.
    /// </summary>
    public static Guid UuidFromUsername(string username)
    {
        var input = Encoding.UTF8.GetBytes("OfflinePlayer:" + username);
        var digest = MD5.HashData(input);

        digest[6] = (byte)((digest[6] & 0x0f) | 0x30); // clear version, set to version 3
        digest[8] = (byte)((digest[8] & 0x3f) | 0x80); // clear variant, set to IETF variant

        return FromRfc4122(digest);
    }

    private static Guid FromRfc4122(ReadOnlySpan<byte> rfcBytes)
    {
        // QUuid::fromRfc4122 expects RFC 4122 (big-endian) byte order; System.Guid's byte[]
        // constructor uses the mixed-endian .NET layout, so reshuffle on little-endian hosts.
        if (BitConverter.IsLittleEndian)
        {
            Span<byte> guid = stackalloc byte[16];
            guid[0] = rfcBytes[3];
            guid[1] = rfcBytes[2];
            guid[2] = rfcBytes[1];
            guid[3] = rfcBytes[0];
            guid[4] = rfcBytes[5];
            guid[5] = rfcBytes[4];
            guid[6] = rfcBytes[7];
            guid[7] = rfcBytes[6];
            rfcBytes[8..].CopyTo(guid[8..]);
            return new Guid(guid);
        }
        return new Guid(rfcBytes.ToArray());
    }

    /// <summary>Saves a MinecraftAccount to a JSON object and returns it.</summary>
    public JsonObject SaveToJson() => Data.SaveState();

    public AuthFlow? Login(bool useDeviceCode = false, AuthEndpointsOptions? auth = null)
    {
        return Attach(new AuthFlow(Data, useDeviceCode ? AuthFlowAction.DeviceCode : AuthFlowAction.Login, auth));
    }

    public AuthFlow? Refresh(AuthEndpointsOptions? auth = null)
    {
        if (_currentTask is not null)
        {
            return _currentTask;
        }

        return Attach(new AuthFlow(Data, AuthFlowAction.Refresh, auth));
    }

    public AuthFlow? CurrentTask => _currentTask;

    private AuthFlow Attach(AuthFlow flow)
    {
        _currentTask = flow;
        flow.Succeeded += _ => AuthSucceeded();
        flow.Failed += (_, reason) => AuthFailed(reason);
        flow.Aborted += _ => AuthFailed("Aborted");
        ActivityChanged?.Invoke(true);
        return flow;
    }

    private void AuthSucceeded()
    {
        _currentTask = null;
        Changed?.Invoke();
        ActivityChanged?.Invoke(false);
    }

    private void AuthFailed(string reason)
    {
        if (_currentTask is { } task)
        {
            switch (task.TaskState_)
            {
                case AccountTaskState.Offline:
                case AccountTaskState.Disabled:
                    // NOTE: user will need to fix this themselves.
                    // NOTE: this doesn't do much. There was an error of some sort. (STATE_FAILED_SOFT fallthrough)
                    break;
                case AccountTaskState.FailedSoft:
                    break;
                case AccountTaskState.FailedHard:
                    if (AccountType_ == AccountType.MSA)
                    {
                        Data.MsaToken.Token_ = "";
                        Data.MsaToken.RefreshToken = "";
                        Data.MsaToken.Validity = Validity.None;
                        Data.Validity_ = Validity.None;
                    }
                    else
                    {
                        Data.YggdrasilToken.Token_ = "";
                        Data.YggdrasilToken.Validity = Validity.None;
                        Data.Validity_ = Validity.None;
                    }
                    Changed?.Invoke();
                    break;
                case AccountTaskState.FailedGone:
                    Data.Validity_ = Validity.None;
                    Changed?.Invoke();
                    break;
                case AccountTaskState.Working:
                    Data.AccountState_ = AccountState.Unchecked;
                    break;
                case AccountTaskState.Created:
                case AccountTaskState.Succeeded:
                    // Not reachable here, as they are not failures.
                    break;
            }
        }
        _currentTask = null;
        ActivityChanged?.Invoke(false);
    }

    public string DisplayName()
    {
        if (AccountState_ is not (AccountState.Unchecked or AccountState.Working or AccountState.Offline or AccountState.Online))
        {
            return "⚠ " + ProfileName();
        }
        return ProfileName();
    }

    public bool IsActive => _currentTask is not null;

    public AccountType AccountType_ => Data.Type;

    public bool OwnsMinecraft() => Data.Type != AccountType.Offline && Data.MinecraftEntitlement.OwnsMinecraft;

    public bool HasProfile() => Data.ProfileId().Length != 0;

    public string TypeString() => Data.Type switch
    {
        AccountType.MSA => "msa",
        AccountType.Offline => "offline",
        _ => "unknown",
    };

    /// <summary>Returns the current state of the account.</summary>
    public AccountState AccountState_ => Data.AccountState_;

    public bool ShouldRefresh()
    {
        /*
         * Never refresh accounts that are being used by the game, it breaks the game session.
         * Always refresh accounts that have not been refreshed yet during this session.
         * Don't refresh broken accounts.
         * Refresh accounts that would expire in the next 12 hours (fresh token validity is 24 hours).
         */
        switch (Data.Validity_)
        {
            case Validity.Certain:
                break;
            case Validity.None:
                return false;
            case Validity.Assumed:
                return true;
        }
        var now = DateTime.UtcNow;
        var issuedTimestamp = Data.YggdrasilToken.IssueInstant;
        var expiresTimestamp = Data.YggdrasilToken.NotAfter;

        expiresTimestamp ??= (issuedTimestamp ?? now).AddSeconds(24 * 3600);
        if ((expiresTimestamp.Value - now).TotalSeconds < 12 * 3600)
        {
            return true;
        }
        return false;
    }

    /// <summary>Port of MinecraftAccount::fillSession.</summary>
    public void FillSession(AuthSession session)
    {
        // volatile auth token
        session.AccessToken = Data.AccessToken();
        // profile name
        session.PlayerName = Data.ProfileName();
        // profile ID
        session.Uuid = Data.ProfileId();
        if (session.Uuid.Length == 0)
        {
            session.Uuid = UuidFromUsername(session.PlayerName).ToString("N");
        }
        // 'legacy' or 'mojang', depending on account type
        session.UserType = TypeString();
        if (session.AccessToken.Length != 0)
        {
            session.Session = "token:" + Data.AccessToken() + ":" + Data.ProfileId();
        }
        else
        {
            session.Session = "-";
        }
    }

    public string LastError() => Data.LastError();

    // queries
    public string InternalId => Data.InternalId;

    public string AccessToken() => Data.AccessToken();

    public string ProfileId() => Data.ProfileId();

    public string ProfileName() => Data.ProfileName();

    /// <summary>This signal is emitted when the account changes.</summary>
    public event Action? Changed;

    public event Action<bool>? ActivityChanged;

    private AccountData Data { get; } = new();

    /// <summary>Account data access (port of accountData()).</summary>
    public AccountData AccountData => Data;

    // current task we are executing here
    private AuthFlow? _currentTask;
}
