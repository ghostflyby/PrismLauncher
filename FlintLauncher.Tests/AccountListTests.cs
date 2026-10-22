// SPDX-License-Identifier: GPL-3.0-only
// Tests for the AccountList port: accounts.json round-trip in the exact upstream layout
// ({"accounts": [...], "formatVersion": 3} with per-account "active" flags), default-account
// activation and add/remove/replace semantics.

using System.Text.Json;
using FlintLauncher.Core.Auth;
using TruePath;
using Xunit;

namespace FlintLauncher.Tests;

public class AccountListTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"flint-accounts-{Guid.NewGuid():N}");

    public AccountListTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException) { }
    }

    private string TempPath(string name) => Path.Combine(_dir, name);

    // Synthetic accounts.json in the EXACT upstream layout: root {"accounts": [...],
    // "formatVersion": 3}, the default account carries "active": true, and every account object
    // uses the AccountData::saveState key set.
    private const string UpstreamAccountsJson = """
                                                {
                                                    "accounts": [
                                                        {
                                                            "active": true,
                                                            "entitlement": {
                                                                "canPlayMinecraft": true,
                                                                "ownsMinecraft": true
                                                            },
                                                            "msa": {
                                                                "exp": 1638990728,
                                                                "iat": 1638904328,
                                                                "refresh_token": "MSA_REFRESH",
                                                                "token": "MSA_TOKEN"
                                                            },
                                                            "msa-client-id": "c36a9fb6-4f2a-41ff-90bd-ae7cc92031eb",
                                                            "profile": {
                                                                "capes": [],
                                                                "id": "b50ad385829d3141a2167e7d7539ba7f",
                                                                "name": "PlayerOne",
                                                                "skin": {
                                                                    "id": "skin-id",
                                                                    "url": "https://textures.minecraft.net/texture/skinhash",
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
                                                        },
                                                        {
                                                            "profile": {
                                                                "capes": [],
                                                                "id": "cb7ecbf7173e39428f9fb92995eab3d0",
                                                                "name": "OfflinePlayer",
                                                                "skin": {
                                                                    "id": "",
                                                                    "url": "",
                                                                    "variant": ""
                                                                }
                                                            },
                                                            "type": "Offline",
                                                            "ygg": {
                                                                "token": "0"
                                                            }
                                                        }
                                                    ],
                                                    "formatVersion": 3
                                                }
                                                """;

    private AccountList LoadedList(string fileName = "accounts.json")
    {
        var path = TempPath(fileName);
        File.WriteAllText(path, UpstreamAccountsJson);
        var list = new AccountList();
        list.SetListFilePath(new AbsolutePath(path));
        Assert.True(list.LoadList());
        return list;
    }

    [Fact]
    public void Load_Upstream_Layout()
    {
        var list = LoadedList();

        Assert.Equal(2, list.Count);
        var defaultAccount = list.DefaultAccount;
        Assert.NotNull(defaultAccount);
        Assert.Equal("PlayerOne", defaultAccount.ProfileName());
        Assert.Equal("b50ad385829d3141a2167e7d7539ba7f", list.At(0).ProfileId());
        Assert.Equal("cb7ecbf7173e39428f9fb92995eab3d0", list.At(1).ProfileId());

        Assert.Equal("YGG_TOKEN", list.At(0).AccessToken());
        Assert.True(list.At(0).OwnsMinecraft());
        Assert.False(list.At(1).OwnsMinecraft()); // offline accounts never own
        Assert.Equal("offline", list.At(1).TypeString());
    }

    [Fact]
    public void Save_Restores_The_Upstream_Key_Layout()
    {
        var list = LoadedList();
        var savePath = TempPath("saved.json");
        list.SetListFilePath(new AbsolutePath(savePath));
        Assert.True(list.SaveList());

        // the written file is the typed wire: formatVersion 3, both accounts, the default
        // carrying "active" (the write shape is ours; the values are the gate)
        var root = JsonSerializer.Deserialize(
            File.ReadAllText(savePath), AuthJsonContext.Default.AccountFileJson);
        Assert.NotNull(root);
        Assert.Equal(3, root.FormatVersion);
        var accounts = root.Accounts;
        Assert.NotNull(accounts);
        Assert.Equal(2, accounts.Length);

        var first = accounts[0].Deserialize(AuthJsonContext.Default.AccountJson);
        var second = accounts[1].Deserialize(AuthJsonContext.Default.AccountJson);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.True(first.Active);
        Assert.Equal("MSA", first.Type);
        var firstProfile = first.Profile;
        Assert.NotNull(firstProfile);
        Assert.Equal("b50ad385829d3141a2167e7d7539ba7f", firstProfile.Id);
        Assert.Null(second.Active);
        Assert.Equal("Offline", second.Type);
        var secondProfile = second.Profile;
        Assert.NotNull(secondProfile);
        Assert.Equal("cb7ecbf7173e39428f9fb92995eab3d0", secondProfile.Id);
    }

    [Fact]
    public void Round_Trip_Through_Save_And_Reload()
    {
        var list = LoadedList();
        var savePath = TempPath("roundtrip.json");
        list.SetListFilePath(new AbsolutePath(savePath));
        Assert.True(list.SaveList());

        var reloaded = new AccountList();
        reloaded.SetListFilePath(new AbsolutePath(savePath));
        Assert.True(reloaded.LoadList());

        Assert.Equal(list.Count, reloaded.Count);
        Assert.Equal(list.DefaultAccount?.ProfileId(), reloaded.DefaultAccount?.ProfileId());
        for (var i = 0; i < list.Count; i++)
        {
            Assert.Equal(list.At(i).ProfileId(), reloaded.At(i).ProfileId());
            Assert.Equal(list.At(i).AccessToken(), reloaded.At(i).AccessToken());
            Assert.Equal(list.At(i).ProfileName(), reloaded.At(i).ProfileName());
            Assert.Equal(list.At(i).AccountType, reloaded.At(i).AccountType);
        }
    }

    [Fact]
    public void Unknown_Format_Version_Fails_And_Renames_The_File()
    {
        var path = TempPath("accounts.json");
        File.WriteAllText(path, """{"accounts": [], "formatVersion": 2}""");
        var list = new AccountList();
        list.SetListFilePath(new AbsolutePath(path));
        Assert.False(list.LoadList());
        // the old file is renamed out of the way
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(TempPath("accounts-old.json")));
    }

    [Fact]
    public void FormatVersion_Digit_String_Still_Loads()
    {
        // the QVariant::toInt read accepted digit strings; the converter keeps that input
        var path = TempPath("accounts.json");
        File.WriteAllText(path, """{"accounts": [], "formatVersion": "3"}""");
        var list = new AccountList();
        list.SetListFilePath(new AbsolutePath(path));
        Assert.True(list.LoadList());
    }

    [Fact]
    public void Structurally_Broken_Account_Is_Skipped_Good_One_Loads()
    {
        // the deliberate failure-behavior migration: a wrong-kind member fails THAT
        // account's deserialization and the account is skipped, while the rest of the
        // file loads (the old probing half-loaded such accounts with empty members)
        var path = TempPath("accounts.json");
        File.WriteAllText(path, """
                                {
                                    "accounts": [
                                        { "type": "Offline", "profile": "not-an-object" },
                                        { "type": "Offline", "profile": { "id": "ok", "name": "Fine", "skin": { "id": "", "url": "", "variant": "" }, "capes": [] } }
                                    ],
                                    "formatVersion": 3
                                }
                                """);
        var list = new AccountList();
        list.SetListFilePath(new AbsolutePath(path));
        Assert.True(list.LoadList());
        Assert.Equal(1, list.Count);
        Assert.Equal("Fine", list.At(0).ProfileName());
    }

    [Fact]
    public void Non_Boolean_Active_Flag_Does_Not_Skip_The_Account()
    {
        // the flag is read leniently (QJsonValue::toBool): a junk value is no flag, the
        // account itself still loads
        var path = TempPath("accounts.json");
        File.WriteAllText(path, """
                                {
                                    "accounts": [
                                        { "type": "Offline", "active": "yes", "profile": { "id": "a1", "name": "Flagged", "skin": { "id": "", "url": "", "variant": "" }, "capes": [] } }
                                    ],
                                    "formatVersion": 3
                                }
                                """);
        var list = new AccountList();
        list.SetListFilePath(new AbsolutePath(path));
        Assert.True(list.LoadList());
        Assert.Equal(1, list.Count);
        Assert.Null(list.DefaultAccount); // junk flag is simply not the default marker
    }

    [Fact]
    public void Missing_File_Fails()
    {
        var list = new AccountList();
        list.SetListFilePath(new AbsolutePath(TempPath("nope.json")));
        Assert.False(list.LoadList());
    }

    [Fact]
    public void No_File_Path_Fails()
    {
        var list = new AccountList();
        Assert.False(list.LoadList());
        Assert.False(list.SaveList());
    }

    [Fact]
    public void Duplicate_ProfileId_Accounts_Are_Skipped_On_Load()
    {
        var path = TempPath("dupes.json");
        File.WriteAllText(path, """
                                {
                                    "accounts": [
                                        { "type": "Offline", "profile": { "id": "same", "name": "First", "skin": { "id": "", "url": "", "variant": "" }, "capes": [] } },
                                        { "type": "Offline", "profile": { "id": "same", "name": "Second", "skin": { "id": "", "url": "", "variant": "" }, "capes": [] } }
                                    ],
                                    "formatVersion": 3
                                }
                                """);
        var list = new AccountList();
        list.SetListFilePath(new AbsolutePath(path));
        Assert.True(list.LoadList());
        Assert.Equal(1, list.Count);
        Assert.Equal("First", list.At(0).ProfileName());
    }

    [Fact]
    public void AddAccount_Replaces_Account_With_Same_ProfileId()
    {
        var list = LoadedList();
        var replacement = MinecraftAccount.CreateOffline("OfflinePlayer");
        // same profile id (deterministic offline uuid), so it replaces At(1)
        Assert.Equal(list.At(1).ProfileId(), replacement.ProfileId());

        list.AddAccount(replacement);
        Assert.Equal(2, list.Count);
        Assert.Equal("OfflinePlayer", list.At(1).ProfileName());
        Assert.Same(replacement, list.At(1));
        // the default account is untouched
        var defaultAccount = list.DefaultAccount;
        Assert.NotNull(defaultAccount);
        Assert.Equal("PlayerOne", defaultAccount.ProfileName());

        // adding an unknown profile id appends
        var fresh = MinecraftAccount.CreateOffline("SomeoneElse");
        list.AddAccount(fresh);
        Assert.Equal(3, list.Count);
        Assert.Same(fresh, list.At(2));

        // adding the same instance again is a no-op
        list.AddAccount(fresh);
        Assert.Equal(3, list.Count);
    }

    [Fact]
    public void Remove_And_Set_Default()
    {
        var list = LoadedList();

        list.RemoveAccount(0);
        Assert.Equal(1, list.Count);
        Assert.Null(list.DefaultAccount); // removing the default clears it

        list.SetDefaultAccount(list.At(0));
        Assert.Same(list.At(0), list.DefaultAccount);

        // a non-listed account cannot become the default (upstream ignores it)
        var stranger = MinecraftAccount.CreateOffline("Stranger");
        list.SetDefaultAccount(stranger);
        Assert.NotSame(stranger, list.DefaultAccount);

        list.SetDefaultAccount(null);
        Assert.Null(list.DefaultAccount);
    }

    [Fact]
    public void MoveAccount()
    {
        var list = LoadedList();
        var first = list.At(0);
        list.MoveAccount(0, 1);
        Assert.Same(first, list.At(1));

        list.MoveAccount(1, -5); // out of range: no-op
        Assert.Same(first, list.At(1));
    }

    [Fact]
    public void AnyAccountIsValid()
    {
        var list = LoadedList();
        Assert.True(list.AnyAccountIsValid()); // PlayerOne owns minecraft

        list.At(0).AccountData.MinecraftEntitlement.OwnsMinecraft = false;
        Assert.False(list.AnyAccountIsValid());
    }

    [Fact]
    public void Autosave_Writes_On_Mutation()
    {
        var path = TempPath("autosave.json");
        File.WriteAllText(path, UpstreamAccountsJson);
        var list = new AccountList();
        list.SetListFilePath(new AbsolutePath(path), enableAutosave: true);
        Assert.True(list.LoadList());

        var changed = false;
        list.ListChanged += () => changed = true;
        list.AddAccount(MinecraftAccount.CreateOffline("AutoSaved"));
        Assert.True(changed);

        var reloaded = new AccountList();
        reloaded.SetListFilePath(new AbsolutePath(path));
        Assert.True(reloaded.LoadList());
        Assert.Equal(3, reloaded.Count);
    }
}
