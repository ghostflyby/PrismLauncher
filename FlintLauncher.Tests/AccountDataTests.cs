// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Tests for the AccountData port: v3 wire resume/save round-trips on the exact upstream key
// layout, the legacy "offline" ygg token migration, the entitlement fallback from profile
// validity, the http->https textures rewrite and unknown-type rejection. The wire is the
// typed AccountWire record set; the former upstream byte gate is retired (the accounts.json
// write shape has no external consumer — docs/JSON-MIGRATION.md), value assertions remain.

using System.Text;
using System.Text.Json;
using FlintLauncher.Core.Auth;
using Xunit;

namespace FlintLauncher.Tests;

public class AccountDataTests
{
    // Synthetic MSA account in the exact layout upstream's AccountData::saveState() produces
    // (keys are shown sorted, like QJsonObject iterates them).
    private const string MsaAccountJson = """
                                          {
                                              "entitlement": {
                                                  "canPlayMinecraft": true,
                                                  "ownsMinecraft": true
                                              },
                                              "msa": {
                                                  "exp": 1638990728,
                                                  "extra": {
                                                      "access_token": "MSA_TOKEN",
                                                      "expires_in": 86400,
                                                      "token_type": "Bearer"
                                                  },
                                                  "iat": 1638904328,
                                                  "refresh_token": "MSA_REFRESH",
                                                  "token": "MSA_TOKEN"
                                              },
                                              "msa-client-id": "c36a9fb6-4f2a-41ff-90bd-ae7cc92031eb",
                                              "profile": {
                                                  "capes": [
                                                      {
                                                          "alias": "Migrator",
                                                          "id": "cape-id",
                                                          "url": "http://textures.minecraft.net/texture/capehash"
                                                      }
                                                  ],
                                                  "cape": "cape-id",
                                                  "id": "b50ad385829d3141a2167e7d7539ba7f",
                                                  "name": "TestPlayer",
                                                  "skin": {
                                                      "data": "aGVsbG8gc2tpbg==",
                                                      "id": "skin-id",
                                                      "url": "http://textures.minecraft.net/texture/skinhash",
                                                      "variant": "CLASSIC"
                                                  }
                                              },
                                              "type": "MSA",
                                              "utoken": {
                                                  "extra": {
                                                      "uhs": "UHS123"
                                                  },
                                                  "token": "USER_TOKEN"
                                              },
                                              "xrp-mc": {
                                                  "extra": {
                                                      "uhs": "UHS123"
                                                  },
                                                  "token": "XSTS_TOKEN"
                                              },
                                              "ygg": {
                                                  "exp": 1638907928,
                                                  "iat": 1638904328,
                                                  "token": "YGG_TOKEN"
                                              }
                                          }
                                          """;

    private static AccountJson Wire(string json)
    {
        var account = JsonSerializer.Deserialize(json, AuthJsonContext.Default.AccountJson);
        Assert.NotNull(account);
        return account;
    }

    private static AccountData ResumedMsaAccount()
    {
        var data = new AccountData();
        Assert.True(data.ResumeStateFromV3(Wire(MsaAccountJson)));
        return data;
    }

    [Fact]
    public void Resume_MSA_Account_Key_Layout()
    {
        var data = ResumedMsaAccount();

        Assert.Equal(AccountType.Msa, data.Type);
        Assert.Equal("c36a9fb6-4f2a-41ff-90bd-ae7cc92031eb", data.MsaClientId);

        Assert.Equal("MSA_TOKEN", data.MsaToken.Value);
        Assert.Equal("MSA_REFRESH", data.MsaToken.RefreshToken);
        Assert.Equal(Validity.Assumed, data.MsaToken.Validity);
        Assert.NotNull(data.MsaToken.IssueInstant);
        Assert.Equal(1638904328, new DateTimeOffset(data.MsaToken.IssueInstant.Value).ToUnixTimeSeconds());
        Assert.NotNull(data.MsaToken.NotAfter);
        Assert.Equal(1638990728, new DateTimeOffset(data.MsaToken.NotAfter.Value).ToUnixTimeSeconds());
        // the numeric extra value must survive as a number
        Assert.True(data.MsaToken.Extra.TryGetValue("expires_in", out var expiresIn));
        Assert.Equal("86400", expiresIn.GetRawText());

        Assert.Equal("USER_TOKEN", data.UserToken.Value);
        Assert.Equal("UHS123", AccountData.GetString(data.UserToken.Extra["uhs"]));
        Assert.Equal("XSTS_TOKEN", data.MojangServicesToken.Value);

        Assert.Equal("YGG_TOKEN", data.AccessToken());
        Assert.Equal(Validity.Assumed, data.YggdrasilToken.Validity);

        Assert.Equal("TestPlayer", data.ProfileName());
        Assert.Equal("b50ad385829d3141a2167e7d7539ba7f", data.ProfileId());
        Assert.Equal(Validity.Assumed, data.MinecraftProfile.Validity);
        Assert.Equal(Validity.Assumed, data.MinecraftEntitlement.Validity);
        Assert.True(data.MinecraftEntitlement.OwnsMinecraft);
        Assert.True(data.MinecraftEntitlement.CanPlayMinecraft);
        Assert.Equal(Validity.Assumed, data.Validity);
    }

    [Fact]
    public void Save_Restores_The_Upstream_Key_Values()
    {
        // the saved wire carries the same member set the upstream saveState wrote (now as
        // record members — the write shape is ours, the values are the gate)
        var saved = ResumedMsaAccount().SaveState();

        Assert.Equal("MSA", saved.Type);
        Assert.Equal("c36a9fb6-4f2a-41ff-90bd-ae7cc92031eb", saved.MsaClientId);

        var msa = saved.Msa;
        Assert.NotNull(msa);
        Assert.Equal(1638904328, msa.Iat);
        Assert.Equal(1638990728, msa.Exp);
        Assert.Equal("MSA_TOKEN", msa.Token);
        Assert.Equal("MSA_REFRESH", msa.RefreshToken);
        var extra = msa.Extra;
        Assert.NotNull(extra);
        Assert.Equal("Bearer", AccountData.GetString(extra["token_type"]));
        Assert.Equal("86400", extra["expires_in"].GetRawText());

        var utoken = saved.Utoken;
        Assert.NotNull(utoken);
        Assert.Equal("USER_TOKEN", utoken.Token);
        var xrpMc = saved.XrpMc;
        Assert.NotNull(xrpMc);
        Assert.Equal("XSTS_TOKEN", xrpMc.Token);

        var ygg = saved.Ygg;
        Assert.NotNull(ygg);
        Assert.Equal("YGG_TOKEN", ygg.Token);
        Assert.Equal(1638904328, ygg.Iat);
        Assert.Equal(1638907928, ygg.Exp);

        var profile = saved.Profile;
        Assert.NotNull(profile);
        Assert.Equal("b50ad385829d3141a2167e7d7539ba7f", profile.Id);
        Assert.Equal("TestPlayer", profile.Name);
        Assert.Equal("cape-id", profile.Cape);
        var skin = profile.Skin;
        Assert.NotNull(skin);
        Assert.Equal("skin-id", skin.Id);
        Assert.Equal("aGVsbG8gc2tpbg==", skin.Data);
        var capes = profile.Capes;
        Assert.NotNull(capes);
        var cape = Assert.Single(capes);
        Assert.Equal("cape-id", cape.Id);
        Assert.Equal("Migrator", cape.Alias);

        var entitlement = saved.Entitlement;
        Assert.NotNull(entitlement);
        Assert.True(entitlement.OwnsMinecraft);
        Assert.True(entitlement.CanPlayMinecraft);
    }

    [Fact]
    public void SaveState_Carries_The_Rewritten_Texture_Urls()
    {
        // the former upstream BYTE gate is retired (the write shape has no external
        // consumer); the one behavior it pinned beyond the value assertions above — the
        // textures http->https rewrite landing in the SAVED document — stays as a value gate
        var saved = ResumedMsaAccount().SaveState();

        var profile = saved.Profile;
        Assert.NotNull(profile);
        var skin = profile.Skin;
        Assert.NotNull(skin);
        Assert.Equal("https://textures.minecraft.net/texture/skinhash", skin.Url);
        var capes = profile.Capes;
        Assert.NotNull(capes);
        Assert.Equal("https://textures.minecraft.net/texture/capehash", Assert.Single(capes).Url);
    }

    [Fact]
    public void Resume_Save_Round_Trip_Preserves_The_State()
    {
        var data = ResumedMsaAccount();
        var saved = data.SaveState();

        var second = new AccountData();
        Assert.True(second.ResumeStateFromV3(saved));
        var saved2 = second.SaveState();

        // self-stability of the typed wire: both saves of the same state serialize alike
        Assert.Equal(
            JsonSerializer.SerializeToUtf8Bytes(saved, AuthJsonContext.Default.AccountJson),
            JsonSerializer.SerializeToUtf8Bytes(saved2, AuthJsonContext.Default.AccountJson));
    }

    [Fact]
    public void Texture_Urls_Are_Rewritten_To_Https()
    {
        var data = ResumedMsaAccount();
        Assert.Equal("https://textures.minecraft.net/texture/skinhash", data.MinecraftProfile.SkinField.Url);
        Assert.Equal("https://textures.minecraft.net/texture/capehash", data.MinecraftProfile.Capes["cape-id"].Url);
        // base64 skin data survives
        Assert.Equal("hello skin", Encoding.UTF8.GetString(data.MinecraftProfile.SkinField.Data));
        // currentCape only sticks when the cape exists in the cape map
        Assert.Equal("cape-id", data.MinecraftProfile.CurrentCape);
    }

    [Fact]
    public void CurrentCape_Ignored_When_Not_In_Capes()
    {
        var data = new AccountData();
        Assert.True(data.ResumeStateFromV3(Wire("""
                                                {
                                                    "type": "MSA",
                                                    "profile": {
                                                        "id": "abc",
                                                        "name": "Name",
                                                        "cape": "missing-cape",
                                                        "skin": { "id": "s", "url": "https://x/y", "variant": "CLASSIC" },
                                                        "capes": []
                                                    }
                                                }
                                                """)));
        Assert.Equal("", data.MinecraftProfile.CurrentCape);
    }

    [Fact]
    public void Legacy_Offline_Ygg_Token_Migrates_To_Zero()
    {
        var data = new AccountData();
        Assert.True(data.ResumeStateFromV3(Wire("""
                                                {
                                                    "type": "Offline",
                                                    "ygg": { "token": "offline" }
                                                }
                                                """)));
        Assert.Equal("0", data.AccessToken());
        // and it saves the migrated value
        var ygg = data.SaveState().Ygg;
        Assert.NotNull(ygg);
        Assert.Equal("0", ygg.Token);
    }

    [Fact]
    public void Entitlement_Fallback_From_Profile_Validity()
    {
        // no entitlement object, but a valid profile -> assumed ownership
        var data = new AccountData();
        Assert.True(data.ResumeStateFromV3(Wire("""
                                                {
                                                    "type": "MSA",
                                                    "profile": {
                                                        "id": "abc",
                                                        "name": "Name",
                                                        "skin": { "id": "s", "url": "https://x/y", "variant": "CLASSIC" },
                                                        "capes": []
                                                    }
                                                }
                                                """)));
        Assert.True(data.MinecraftEntitlement.OwnsMinecraft);
        Assert.True(data.MinecraftEntitlement.CanPlayMinecraft);
        Assert.Equal(Validity.Assumed, data.MinecraftEntitlement.Validity);
    }

    [Fact]
    public void Broken_Profile_Does_Not_Grant_Entitlement_Fallback()
    {
        // profile present but missing the mandatory skin attributes -> profile invalid -> no fallback
        var data = new AccountData();
        Assert.True(data.ResumeStateFromV3(Wire("""
                                                {
                                                    "type": "MSA",
                                                    "profile": {
                                                        "id": "abc",
                                                        "name": "Name",
                                                        "capes": []
                                                    }
                                                }
                                                """)));
        Assert.Equal(Validity.None, data.MinecraftEntitlement.Validity);
        Assert.False(data.MinecraftEntitlement.OwnsMinecraft);
    }

    [Fact]
    public void Unknown_Type_Is_Rejected()
    {
        var data = new AccountData();
        Assert.False(data.ResumeStateFromV3(Wire("""{"type": "Elsewhere"}""")));
        Assert.False(data.ResumeStateFromV3(Wire("""{"noTypeHere": true}""")));
    }

    [Fact]
    public void Non_Persistent_Tokens_Are_Not_Saved()
    {
        var data = ResumedMsaAccount();
        data.MsaToken.Persistent = false;
        var saved = data.SaveState();
        Assert.Null(saved.Msa);
        Assert.NotNull(saved.Ygg);
    }

    [Fact]
    public void Offline_Account_Saves_Only_Type_And_Ygg()
    {
        var account = MinecraftAccount.CreateOffline("Notch");
        var saved = account.SaveToJson();
        Assert.Equal("Offline", saved.Type);
        Assert.Null(saved.Msa);
        Assert.Null(saved.Utoken);
        Assert.Null(saved.XrpMc);
        Assert.Null(saved.Entitlement); // the offline entitlement never parsed -> Validity None
        var ygg = saved.Ygg;
        Assert.NotNull(ygg);
        Assert.Equal("0", ygg.Token);
        var profile = saved.Profile;
        Assert.NotNull(profile);
        Assert.Equal("Notch", profile.Name);
    }

    [Fact]
    public void Accessors_On_Empty_Data()
    {
        var data = new AccountData();
        Assert.Equal("", data.AccessToken());
        Assert.Equal("", data.ProfileId());
        Assert.Equal("No Minecraft profile", data.ProfileName());
        Assert.Equal("", data.LastError());
    }
}
