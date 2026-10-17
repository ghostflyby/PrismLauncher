// SPDX-License-Identifier: GPL-3.0-only
// Tests for the AccountData port: v3 JSON resume/save round-trips on the exact upstream key
// layout, the legacy "offline" ygg token migration, the entitlement fallback from profile
// validity, the http->https textures rewrite and unknown-type rejection.

using System.Text;
using System.Text.Json.Nodes;
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

    private static AccountData ResumedMsaAccount()
    {
        var data = new AccountData();
        Assert.True(data.ResumeStateFromV3(JsonNode.Parse(MsaAccountJson)!.AsObject()));
        return data;
    }

    private static void AssertKeySet(JsonObject obj, params string[] keys)
    {
        Assert.Equal(new HashSet<string>(keys), new HashSet<string>(obj.Select(p => p.Key)));
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
        Assert.Equal(1638904328, new DateTimeOffset(data.MsaToken.IssueInstant!.Value).ToUnixTimeSeconds());
        Assert.Equal(1638990728, new DateTimeOffset(data.MsaToken.NotAfter!.Value).ToUnixTimeSeconds());
        // the numeric extra value must survive as a number
        Assert.True(data.MsaToken.Extra.TryGetValue("expires_in", out var expiresIn));
        Assert.Equal("86400", expiresIn.ToJsonString());

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
    public void Save_Restores_The_Upstream_Key_Layout()
    {
        var data = ResumedMsaAccount();
        var saved = data.SaveState();

        AssertKeySet(saved, "type", "msa-client-id", "msa", "utoken", "xrp-mc", "ygg", "profile", "entitlement");
        AssertKeySet(saved["msa"]!.AsObject(), "iat", "exp", "token", "refresh_token", "extra");
        AssertKeySet(saved["profile"]!.AsObject(), "id", "name", "cape", "skin", "capes");
        AssertKeySet(saved["profile"]!["skin"]!.AsObject(), "id", "url", "variant", "data");
        AssertKeySet(saved["entitlement"]!.AsObject(), "ownsMinecraft", "canPlayMinecraft");
    }

    [Fact]
    public void Resume_Save_Round_Trip_Preserves_The_State()
    {
        var data = ResumedMsaAccount();
        var saved = data.SaveState();

        var second = new AccountData();
        Assert.True(second.ResumeStateFromV3(saved));
        var saved2 = second.SaveState();

        // byte-shape: keys and values round-trip (QJsonObject key order is sorted, so compare
        // the sorted serializations)
        var a = saved.ToJsonString();
        var b = saved2.ToJsonString();
        Assert.Equal(a, b);
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
        var json = JsonNode.Parse("""
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
                                  """)!.AsObject();
        var data = new AccountData();
        Assert.True(data.ResumeStateFromV3(json));
        Assert.Equal("", data.MinecraftProfile.CurrentCape);
    }

    [Fact]
    public void Legacy_Offline_Ygg_Token_Migrates_To_Zero()
    {
        var json = JsonNode.Parse("""
                                  {
                                      "type": "Offline",
                                      "ygg": { "token": "offline" }
                                  }
                                  """)!.AsObject();
        var data = new AccountData();
        Assert.True(data.ResumeStateFromV3(json));
        Assert.Equal("0", data.AccessToken());
        // and it saves the migrated value
        Assert.Equal("0", data.SaveState()["ygg"]!["token"]!.GetValue<string>());
    }

    [Fact]
    public void Entitlement_Fallback_From_Profile_Validity()
    {
        // no entitlement object, but a valid profile -> assumed ownership
        var json = JsonNode.Parse("""
                                  {
                                      "type": "MSA",
                                      "profile": {
                                          "id": "abc",
                                          "name": "Name",
                                          "skin": { "id": "s", "url": "https://x/y", "variant": "CLASSIC" },
                                          "capes": []
                                      }
                                  }
                                  """)!.AsObject();
        var data = new AccountData();
        Assert.True(data.ResumeStateFromV3(json));
        Assert.True(data.MinecraftEntitlement.OwnsMinecraft);
        Assert.True(data.MinecraftEntitlement.CanPlayMinecraft);
        Assert.Equal(Validity.Assumed, data.MinecraftEntitlement.Validity);
    }

    [Fact]
    public void Broken_Profile_Does_Not_Grant_Entitlement_Fallback()
    {
        // profile present but missing the mandatory skin attributes -> profile invalid -> no fallback
        var json = JsonNode.Parse("""
                                  {
                                      "type": "MSA",
                                      "profile": {
                                          "id": "abc",
                                          "name": "Name",
                                          "capes": []
                                      }
                                  }
                                  """)!.AsObject();
        var data = new AccountData();
        Assert.True(data.ResumeStateFromV3(json));
        Assert.Equal(Validity.None, data.MinecraftEntitlement.Validity);
        Assert.False(data.MinecraftEntitlement.OwnsMinecraft);
    }

    [Fact]
    public void Unknown_Type_Is_Rejected()
    {
        var data = new AccountData();
        Assert.False(data.ResumeStateFromV3(JsonNode.Parse("""{"type": "Elsewhere"}""")!.AsObject()));
        Assert.False(data.ResumeStateFromV3(JsonNode.Parse("""{"noTypeHere": true}""")!.AsObject()));
    }

    [Fact]
    public void Non_Persistent_Tokens_Are_Not_Saved()
    {
        var data = ResumedMsaAccount();
        data.MsaToken.Persistent = false;
        var saved = data.SaveState();
        Assert.False(saved.ContainsKey("msa"));
        Assert.True(saved.ContainsKey("ygg"));
    }

    [Fact]
    public void Offline_Account_Saves_Only_Type_And_Ygg()
    {
        var account = MinecraftAccount.CreateOffline("Notch");
        var saved = account.SaveToJson();
        AssertKeySet(saved, "type", "ygg", "profile");
        Assert.Equal("Offline", saved["type"]!.GetValue<string>());
        Assert.Equal("0", saved["ygg"]!["token"]!.GetValue<string>());
        Assert.Equal("Notch", saved["profile"]!["name"]!.GetValue<string>());
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
