// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Binding tests for the account services' answers: each wire dialect binds through its
// context (required members fail the read, the tolerant endpoints read a malformed body
// as their empty document).

using System.Text;
using System.Text.Json;
using FlintLauncher.ExternalApi.Mojang;
using Xunit;

namespace FlintLauncher.Tests;

public class MojangJsonTests
{
    private static Stream Json(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task XboxToken_Binds_The_Claims()
    {
        var token = await MojangJson.LoadXboxTokenAsync(Json("""
            {"IssueInstant":"2020-12-07T19:52:08.4463796Z","NotAfter":"2020-12-21T19:52:08.4463796Z",
             "Token":"T","DisplayClaims":{"xui":[{"not-uhs":"x"},{"uhs":"UHS"}]}}
            """), CancellationToken.None);

        Assert.Equal("T", token.Token);
        Assert.Equal("UHS", token.Claims["uhs"]);
        Assert.Equal(1607370728, token.IssueInstant.ToUnixTimeSeconds());
        Assert.Equal(1608580328, token.NotAfter.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task TokenDocument_Binds_The_Grant_And_Its_Extra_Members()
    {
        var document = await MojangJson.LoadTokenDocumentAsync(
            Json("""{"access_token":"A","refresh_token":"R","expires_in":100,"token_type":"Bearer"}"""),
            CancellationToken.None);

        Assert.Equal("A", document.AccessToken);
        Assert.Equal("R", document.RefreshToken);
        Assert.Equal(100, document.ExpiresIn);
        Assert.Null(document.Error);
        Assert.Null(document.ErrorDescription);
        // the extension bag keeps the members the typed ones do not carry
        Assert.Equal("Bearer", document.Extra["token_type"].GetString());
    }

    [Fact]
    public async Task DeviceAuthorization_Binds_The_Codes()
    {
        var authorization = await MojangJson.LoadDeviceAuthorizationAsync(
            Json("""{"device_code":"D","user_code":"C","verification_uri":"https://microsoft.com/devicelogin","expires_in":900,"interval":5}"""),
            CancellationToken.None);

        Assert.Equal("D", authorization.DeviceCode);
        Assert.Equal("C", authorization.UserCode);
        Assert.Equal(900, authorization.ExpiresIn);
        Assert.Equal(5, authorization.Interval);
    }

    [Fact]
    public async Task DeviceAuthorization_NonPositive_Expiry_Fails_The_Load()
    {
        await Assert.ThrowsAsync<JsonException>(() => MojangJson.LoadDeviceAuthorizationAsync(
            Json("""{"device_code":"D","user_code":"C","verification_uri":"https://microsoft.com/devicelogin","expires_in":0}"""),
            CancellationToken.None));
    }

    [Fact]
    public async Task LauncherLogin_Binds_The_Token()
    {
        var login = await MojangJson.LoadLauncherLoginAsync(
            Json("""{"username":"u","access_token":"A","expires_in":86400}"""), CancellationToken.None);

        Assert.Equal("A", login.AccessToken);
        Assert.Equal(86400, login.ExpiresIn);
    }

    [Fact]
    public async Task Profile_Binds_The_Tables_And_Selects_The_Active_Skin()
    {
        var profile = await MojangJson.LoadProfileAsync(Json("""
            {"id":"uuid","name":"Steve",
             "skins":[{"id":"s1","state":"INACTIVE","url":"u1","variant":"classic"},
                      {"id":"s2","state":"ACTIVE","url":"u2","variant":"slim"}],
             "capes":[{"id":"c1","state":"ACTIVE","url":"cu1","alias":"cape one"},
                      {"id":"c2","state":"INACTIVE","alias":"junk"}]}
            """), CancellationToken.None);

        Assert.Equal("Steve", profile.Name);
        Assert.Equal("s2", profile.ActiveSkin?.Id);
        Assert.Equal("c1", profile.CapeTable.ActiveCapeId);
        var cape = Assert.Single(profile.CapeTable.Entries);
        Assert.Equal("cape one", cape.Alias);
    }

    [Fact]
    public async Task Required_Members_Fail_The_Read()
    {
        await Assert.ThrowsAsync<JsonException>(() => MojangJson.LoadXboxTokenAsync(
            Json("""{"IssueInstant":"2020-12-07T19:52:08Z","NotAfter":"2020-12-21T19:52:08Z"}"""),
            CancellationToken.None));
        await Assert.ThrowsAsync<JsonException>(() => MojangJson.LoadLauncherLoginAsync(
            Json("""{"username":"u"}"""), CancellationToken.None));
        await Assert.ThrowsAsync<JsonException>(() => MojangJson.LoadProfileAsync(
            Json("""{"id":"uuid"}"""), CancellationToken.None));
    }

    [Fact]
    public async Task Absent_And_Null_Table_Members_Read_As_Missing()
    {
        var withoutTables = await MojangJson.LoadProfileAsync(
            Json("""{"id":"uuid","name":"N"}"""), CancellationToken.None);
        Assert.Null(withoutTables.ActiveSkin);
        Assert.Empty(withoutTables.CapeTable.Entries);

        var nulled = await MojangJson.LoadProfileAsync(
            Json("""{"id":"uuid","name":"N","skins":null,"capes":null}"""), CancellationToken.None);
        Assert.Null(nulled.ActiveSkin);
        Assert.Empty(nulled.CapeTable.Entries);

        var entitlements = await MojangJson.LoadEntitlementsAsync(
            Json("""{"items":null}"""), CancellationToken.None);
        Assert.NotNull(entitlements);
        Assert.Empty(entitlements.ItemNames);
    }

    [Fact]
    public async Task Kind_Mismatched_Table_Members_Fail_The_Read()
    {
        await Assert.ThrowsAsync<JsonException>(() => MojangJson.LoadProfileAsync(
            Json("""{"id":"uuid","name":"N","skins":{}}"""), CancellationToken.None));
        await Assert.ThrowsAsync<JsonException>(() => MojangJson.LoadProfileAsync(
            Json("""{"id":"uuid","name":"N","capes":"x"}"""), CancellationToken.None));
    }

    [Fact]
    public async Task Xsts_Denial_Shapes()
    {
        Assert.IsType<MojangXstsDenied>(await MojangJson.LoadXstsDenialAsync(
            Json("""{"Identity":"0","XErr":2148916238,"Message":""}"""), CancellationToken.None));
        // a non-numeric XErr reads as the "missing" refusal, not a failed read
        Assert.IsType<MojangXstsDenialWithoutCode>(await MojangJson.LoadXstsDenialAsync(
            Json("""{"XErr":"not-a-number"}"""), CancellationToken.None));
        Assert.IsType<MojangXstsDenialUnreadable>(await MojangJson.LoadXstsDenialAsync(
            Json("garbage"), CancellationToken.None));
    }

    [Fact]
    public async Task Kind_Mismatched_Display_Claims_Fail_The_Read()
    {
        await Assert.ThrowsAsync<JsonException>(() => MojangJson.LoadXboxTokenAsync(Json("""
            {"IssueInstant":"2020-12-07T19:52:08Z","NotAfter":"2020-12-21T19:52:08Z","Token":"T",
             "DisplayClaims":"not-an-object"}
            """), CancellationToken.None));
    }

    [Fact]
    public async Task Xbox_Token_Without_A_User_Hash_Entry_Fails_The_Read()
    {
        await Assert.ThrowsAsync<JsonException>(() => MojangJson.LoadXboxTokenAsync(Json("""
            {"IssueInstant":"2020-12-07T19:52:08Z","NotAfter":"2020-12-21T19:52:08Z","Token":"T",
             "DisplayClaims":{"xui":[{"not-uhs":"x"}]}}
            """), CancellationToken.None));
    }
}
