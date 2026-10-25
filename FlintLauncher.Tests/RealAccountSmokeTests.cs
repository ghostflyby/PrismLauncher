// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Read-only smoke test against the real PrismLauncher accounts.json (same pattern as
// RealDataSmokeTests): loads the account list through the port, then re-saves to a temp file and
// compares the JSON structure (key sets, never bytes). Never writes to the real file.

using System.Text.Json.Nodes;
using FlintLauncher.Core.Auth;
using TruePath;
using Xunit;

namespace FlintLauncher.Tests;

public class RealAccountSmokeTests : IDisposable
{
    private const string RealAccountsPath = "/Users/ghostflyby/Library/Application Support/PrismLauncher/accounts.json";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"flint-accsmoke-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException) { }
    }

    [Fact]
    public void Real_Accounts_Json_Parses_And_ReSaves_With_The_Same_Structure()
    {
        if (!File.Exists(RealAccountsPath))
        {
            return; // no real accounts file on this machine (CI) — nothing to verify
        }

        var list = new AccountList();
        list.SetListFilePath(new AbsolutePath(RealAccountsPath)); // autosave stays off: read-only access
        Assert.True(list.LoadList(), "the real accounts.json must load");

        Assert.True(list.Count >= 0);
        for (var i = 0; i < list.Count; i++)
        {
            var account = list.At(i);
            // every account gets a runtime Id128 internal id
            Assert.Matches("^[0-9a-f]{32}$", account.InternalId);
            Assert.True(Enum.IsDefined(account.AccountType));
            if (account.AccountType == AccountType.Msa)
            {
                Assert.False(string.IsNullOrEmpty(account.AccountData.MsaClientId));
            }

            if (account.HasProfile())
            {
                Assert.False(string.IsNullOrEmpty(account.ProfileName()));
                // profile ids are Id128 (no dashes)
                Assert.DoesNotContain('-', account.ProfileId());
            }
        }

        // re-save into a temp file and compare the JSON structure
        Directory.CreateDirectory(_dir);
        var savedPath = Path.Combine(_dir, "accounts-copy.json");
        list.SetListFilePath(new AbsolutePath(savedPath));
        Assert.True(list.SaveList());

        var originalNode = JsonNode.Parse(File.ReadAllText(RealAccountsPath));
        var savedNode = JsonNode.Parse(File.ReadAllText(savedPath));
        var originalRoot = originalNode?.AsObject();
        var savedRoot = savedNode?.AsObject();
        Assert.NotNull(originalRoot);
        Assert.NotNull(savedRoot);

        Assert.Equal(
            new HashSet<string>(originalRoot.Select(p => p.Key)),
            new HashSet<string>(savedRoot.Select(p => p.Key)));

        var originalAccounts = originalRoot["accounts"]?.AsArray();
        var savedAccounts = savedRoot["accounts"]?.AsArray();
        Assert.NotNull(originalAccounts);
        Assert.NotNull(savedAccounts);
        Assert.Equal(originalAccounts.Count, savedAccounts.Count);

        for (var i = 0; i < originalAccounts.Count; i++)
        {
            var original = originalAccounts[i]?.AsObject();
            var saved = savedAccounts[i]?.AsObject();
            Assert.NotNull(original);
            Assert.NotNull(saved);

            // key sets must be identical (ordering may differ, contents must round-trip)
            Assert.Equal(
                new HashSet<string>(original.Select(p => p.Key)),
                new HashSet<string>(saved.Select(p => p.Key)));
            // the active flag moves with the default account
            Assert.Equal(original["active"] is not null, saved["active"] is not null);

            // tokens/profiles deserialize into the same values
            Assert.Equal(original["type"]?.GetValue<string>(), saved["type"]?.GetValue<string>());
            Assert.Equal(original["ygg"]?["token"]?.GetValue<string>(), saved["ygg"]?["token"]?.GetValue<string>());
            Assert.Equal(original["profile"]?["id"]?.GetValue<string>(), saved["profile"]?["id"]?.GetValue<string>());
            Assert.Equal(original["profile"]?["name"]?.GetValue<string>(), saved["profile"]?["name"]?.GetValue<string>());
            if (original["msa"]?["refresh_token"] is { } refreshToken)
            {
                Assert.Equal(refreshToken.GetValue<string>(), saved["msa"]?["refresh_token"]?.GetValue<string>());
            }
        }
    }
}
