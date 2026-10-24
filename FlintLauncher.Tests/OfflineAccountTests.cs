// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Tests for MinecraftAccount offline creation: the deterministic offline UUID
// (Java UUID#nameUUIDFromBytes over "OfflinePlayer:<name>", ported via MD5 + QUuid byte order),
// the format of the random pieces and the saved JSON.

using System.Text.Json;
using FlintLauncher.Core.Auth;
using Xunit;

namespace FlintLauncher.Tests;

public class OfflineAccountTests
{
    // Reference value: MD5("OfflinePlayer:Notch") with version 3 / IETF variant, computed
    // independently (uuid.UUID(bytes=...) in Python) — the C++ uses the identical algorithm.
    private static readonly Guid NotchOfflineUuid = new("b50ad385-829d-3141-a216-7e7d7539ba7f");

    [Fact]
    public void UuidFromUsername_Matches_The_Java_Algorithm()
    {
        Assert.Equal(NotchOfflineUuid, MinecraftAccount.UuidFromUsername("Notch"));
        // version 3 bits are set by the algorithm
        Assert.Equal(3, NotchOfflineUuid.Version);

        // another known input, dash-free round trip through the Id128 form
        var id = MinecraftAccount.UuidFromUsername("OfflinePlayer").ToString("N");
        Assert.Equal("cb7ecbf7173e39428f9fb92995eab3d0", id);
    }

    [Fact]
    public void CreateOffline_Is_Deterministic_In_The_Profile_Id()
    {
        var a = MinecraftAccount.CreateOffline("Notch");
        var b = MinecraftAccount.CreateOffline("Notch");

        // upstream uses uuidFromUsername(name) for the profile id — deterministic
        Assert.Equal(NotchOfflineUuid.ToString("N"), a.AccountData.ProfileId());
        Assert.Equal(a.ProfileId(), b.ProfileId());
        Assert.Equal(32, a.ProfileId().Length); // QUuid::Id128: no dashes
        Assert.Equal("Notch", a.ProfileName());

        var other = MinecraftAccount.CreateOffline("jomo");
        Assert.NotEqual(a.ProfileId(), other.ProfileId());
    }

    [Fact]
    public void CreateOffline_Random_Pieces_Are_Id128_Unique()
    {
        var a = MinecraftAccount.CreateOffline("Notch");
        var b = MinecraftAccount.CreateOffline("Notch");

        // internalId and the clientToken extra are QUuid::createUuid() based: random but Id128
        Assert.NotEqual(a.InternalId, b.InternalId);
        Assert.Matches("^[0-9a-f]{32}$", a.InternalId);
        Assert.Matches("^[0-9a-f]{32}$", b.InternalId);
        Assert.NotEqual(
            AccountData.GetString(a.AccountData.YggdrasilToken.Extra["clientToken"]),
            AccountData.GetString(b.AccountData.YggdrasilToken.Extra["clientToken"]));
        Assert.Equal("Notch", AccountData.GetString(a.AccountData.YggdrasilToken.Extra["userName"]));
    }

    [Fact]
    public void CreateOffline_Token_And_Validity()
    {
        var account = MinecraftAccount.CreateOffline("Notch");

        Assert.Equal(AccountType.Offline, account.AccountType);
        Assert.Equal("0", account.AccessToken());
        Assert.Equal(Validity.Certain, account.AccountData.YggdrasilToken.Validity);
        Assert.NotNull(account.AccountData.YggdrasilToken.IssueInstant);
        Assert.Equal(Validity.Certain, account.AccountData.MinecraftProfile.Validity);
        Assert.True(account.HasProfile());
        Assert.False(account.OwnsMinecraft()); // offline accounts never own the game
        Assert.Equal("offline", account.TypeString());
        Assert.NotEqual("⚠ " + account.ProfileName(), account.DisplayName()); // sane state
    }

    [Fact]
    public void CreateOffline_Saves_And_Reloads()
    {
        var account = MinecraftAccount.CreateOffline("Notch");
        var saved = account.SaveToJson();

        var reloaded = MinecraftAccount.LoadFromJsonV3(saved);
        Assert.NotNull(reloaded);
        Assert.Equal(AccountType.Offline, reloaded.AccountType);
        Assert.Equal("Notch", reloaded.ProfileName());
        Assert.Equal(account.ProfileId(), reloaded.ProfileId());
        Assert.Equal("0", reloaded.AccessToken());
        Assert.Equal("Notch", AccountData.GetString(reloaded.AccountData.YggdrasilToken.Extra["userName"]));
        // internalId is runtime-only and NOT saved: the reload generated a fresh one
        Assert.NotEqual(account.InternalId, reloaded.InternalId);
        Assert.Matches("^[0-9a-f]{32}$", reloaded.InternalId);
    }

    [Fact]
    public void LoadFromJsonV3_Rejects_Garbage()
    {
        Assert.Null(MinecraftAccount.LoadFromJsonV3(new AccountJson()));
        var garbage = JsonSerializer.Deserialize("""{"type": "Nonsense"}""", AuthJsonContext.Default.AccountJson);
        Assert.NotNull(garbage);
        Assert.Null(MinecraftAccount.LoadFromJsonV3(garbage));
    }
}
