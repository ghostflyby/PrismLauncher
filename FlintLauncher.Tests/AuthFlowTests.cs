// SPDX-License-Identifier: GPL-3.0-only
// Auth flow tests against a local stub server: full device-code MSA run (all 7 steps), the XSTS
// XErr child-account path, profile 404/500 handling, the device-code expired_token path, the
// offline no-op flow, the interactive loopback listener exchange and the silent refresh flow.
// All endpoint URLs are redirected through a stub-pointed AuthEndpointsOptions passed into the
// flow, so no real network is involved.
using System.Text;
using System.Text.Json.Nodes;
using FlintLauncher.Core.Auth;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Tasks;
using Xunit;

namespace Flint.Tests;

public class AuthFlowTests : IDisposable
{
    private readonly AuthStubServer _server = new();
    private readonly AuthEndpointsOptions _auth = new();

    public AuthFlowTests()
    {
        _auth.MsaDeviceCode = _server.BaseUrl + "/devicecode";
        _auth.MsaToken = _server.BaseUrl + "/token";
        _auth.MsaAuthorize = _server.BaseUrl + "/authorize";
        _auth.XboxUserAuth = _server.BaseUrl + "/user/authenticate";
        _auth.XboxXstsAuthorize = _server.BaseUrl + "/xsts/authorize";
        _auth.MinecraftServices = _server.BaseUrl;
        _auth.MsaClientId = "c36a9fb6-4f2a-41ff-90bd-ae7cc92031eb";
    }

    public void Dispose()
    {
        _server.Dispose();
    }

    [Fact]
    public async Task Device_Code_MSA_Flow_Succeeds_End_To_End()
    {
        var account = MinecraftAccount.CreateBlankMSA();
        var flow = new AuthFlow(account.AccountData, AuthFlowAction.DeviceCode, _auth);
        var browserEvents = new List<(string url, string code, int expiresIn)>();
        flow.AuthorizeWithBrowserWithExtra += (url, code, expiresIn) => browserEvents.Add((url, code, expiresIn));

        await flow.StartAsync();

        Assert.True(flow.State == TaskState.Succeeded,
            "state=" + flow.State + " taskState=" + flow.TaskState_ + " reason=" + flow.FailReason + " status=" + flow.Status);
        Assert.Equal(AccountTaskState.Succeeded, flow.TaskState_);

        var data = account.AccountData;
        // MSA token step (after two simulated authorization_pending polls)
        Assert.Equal("MSA_ACCESS", data.MsaToken.Token_);
        Assert.Equal("MSA_REFRESH", data.MsaToken.RefreshToken);
        Assert.Equal("c36a9fb6-4f2a-41ff-90bd-ae7cc92031eb", data.MsaClientID);
        Assert.NotNull(data.MsaToken.IssueInstant);
        Assert.NotNull(data.MsaToken.NotAfter);
        // Xbox user token step
        Assert.Equal("USER_TOKEN_STUB", data.UserToken.Token_);
        Assert.Equal(Validity.Certain, data.UserToken.Validity);
        Assert.Equal("UHS_STUB", AccountData.GetString(data.UserToken.Extra["uhs"]));
        // XSTS step (rp://api.minecraftservices.com/)
        Assert.Equal("XSTS_TOKEN_STUB", data.MojangServicesToken.Token_);
        // launcher login step -> yggdrasil token
        Assert.Equal("MC_ACCESS_TOKEN_STUB", data.AccessToken());
        Assert.Equal(Validity.Certain, data.YggdrasilToken.Validity);
        Assert.NotNull(data.YggdrasilToken.NotAfter);
        // entitlements step
        Assert.True(data.MinecraftEntitlement.OwnsMinecraft);
        Assert.True(data.MinecraftEntitlement.CanPlayMinecraft);
        Assert.Equal(Validity.Certain, data.MinecraftEntitlement.Validity);
        // profile step
        Assert.Equal("b50ad385829d3141a2167e7d7539ba7f", data.ProfileId());
        Assert.Equal("TestPlayer", data.ProfileName());
        Assert.Equal(_server.BaseUrl + "/skin.png", data.MinecraftProfile.SkinField.Url);
        Assert.Equal("cape-id", data.MinecraftProfile.CurrentCape);
        // skin step downloaded the (rewritten) skin url
        Assert.Equal("fake-png-bytes-for-testing", Encoding.UTF8.GetString(data.MinecraftProfile.SkinField.Data));
        // overall account state
        Assert.Equal(AccountState.Online, data.AccountState_);
        Assert.Equal(Validity.Certain, data.Validity_);
        Assert.True(account.HasProfile());

        // the browser event carries the stub verification uri/user code
        var browser = Assert.Single(browserEvents);
        Assert.Equal("https://microsoft.com/link", browser.url);
        Assert.Equal("FLINT-CODE", browser.code);
        Assert.Equal(900, browser.expiresIn);

        // ---- request shape assertions ----
        var device = _server.Request("POST", "/devicecode")!;
        Assert.Equal("application/x-www-form-urlencoded", device.Headers["Content-Type"]);
        Assert.Equal("application/json", device.Headers["Accept"]);
        Assert.Contains("client_id=c36a9fb6-4f2a-41ff-90bd-ae7cc92031eb", device.Body);
        Assert.Contains("scope=XboxLive.SignIn%20XboxLive.offline_access", device.Body);

        var poll = _server.Request("POST", "/token")!;
        Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code", poll.Body);
        Assert.Contains("device_code=DEV_CODE_STUB", poll.Body);
        Assert.Contains("client_id=c36a9fb6-4f2a-41ff-90bd-ae7cc92031eb", poll.Body);

        var xbox = _server.Request("POST", "/user/authenticate")!;
        Assert.Equal("application/json", xbox.Headers["Content-Type"]);
        Assert.Equal("application/json", xbox.Headers["Accept"]);
        Assert.Equal("1", xbox.Headers["x-xbl-contract-version"]);
        var xboxBody = JsonNode.Parse(xbox.Body)!.AsObject();
        Assert.Equal("RPS", xboxBody["Properties"]!["AuthMethod"]!.GetValue<string>());
        Assert.Equal("user.auth.xboxlive.com", xboxBody["Properties"]!["SiteName"]!.GetValue<string>());
        Assert.Equal("d=MSA_ACCESS", xboxBody["Properties"]!["RpsTicket"]!.GetValue<string>()); // "d=" prefix
        Assert.Equal("http://auth.xboxlive.com", xboxBody["RelyingParty"]!.GetValue<string>());
        Assert.Equal("JWT", xboxBody["TokenType"]!.GetValue<string>());

        var xsts = _server.Request("POST", "/xsts/authorize")!;
        var xstsBody = JsonNode.Parse(xsts.Body)!.AsObject();
        Assert.Equal("RETAIL", xstsBody["Properties"]!["SandboxId"]!.GetValue<string>());
        Assert.Equal("USER_TOKEN_STUB", xstsBody["Properties"]!["UserTokens"]![0]!.GetValue<string>());
        Assert.Equal("rp://api.minecraftservices.com/", xstsBody["RelyingParty"]!.GetValue<string>());
        Assert.Equal("JWT", xstsBody["TokenType"]!.GetValue<string>());

        var login = _server.Request("POST", "/launcher/login")!;
        var loginBody = JsonNode.Parse(login.Body)!.AsObject();
        Assert.Equal("XBL3.0 x=UHS_STUB;XSTS_TOKEN_STUB", loginBody["xtoken"]!.GetValue<string>());
        Assert.Equal("PC_LAUNCHER", loginBody["platform"]!.GetValue<string>());

        var entitlements = _server.Request("GET", "/entitlements/license")!;
        Assert.Equal("Bearer MC_ACCESS_TOKEN_STUB", entitlements.Headers["Authorization"]);
        var query = entitlements.Query.TrimStart('?').Split('=');
        Assert.Equal("requestId", query[0]);
        Assert.True(Guid.TryParseExact(query[1], "D", out _), "requestId must be a uuid without braces");

        var profile = _server.Request("GET", "/minecraft/profile")!;
        Assert.Equal("Bearer MC_ACCESS_TOKEN_STUB", profile.Headers["Authorization"]);

        Assert.NotNull(_server.Request("GET", "/skin.png"));
    }

    [Fact]
    public async Task Xsts_Child_Account_Error_Surfaces_Upstream_Message()
    {
        _server.XErr = 2148916238;

        var account = MinecraftAccount.CreateBlankMSA();
        var flow = new AuthFlow(account.AccountData, AuthFlowAction.DeviceCode, _auth);
        await flow.StartAsync();

        Assert.Equal(TaskState.Failed, flow.State);
        Assert.Equal(AccountTaskState.FailedSoft, flow.TaskState_);
        // 2148916238: underaged account not linked to a family (upstream message, verbatim)
        Assert.Equal("This Microsoft account is underaged and is not linked to a family.\n\n" +
            "Please set up your account according to <a href=\"https://help.minecraft.net/hc/en-us/articles/4408968616077\">help.minecraft.net</a>.",
            flow.FailReason);
        Assert.Equal(AccountState.Errored, account.AccountData.AccountState_);
        Assert.Contains("underaged", account.AccountData.LastError());
    }

    [Fact]
    public async Task Xsts_Missing_Xbox_Profile_Error_Message()
    {
        _server.XErr = 2148916233;

        var account = MinecraftAccount.CreateBlankMSA();
        var flow = new AuthFlow(account.AccountData, AuthFlowAction.DeviceCode, _auth);
        await flow.StartAsync();

        Assert.Equal(TaskState.Failed, flow.State);
        Assert.Contains("This Microsoft account does not have an Xbox Live profile. Buy the game on " +
            "<a href=\"https://www.minecraft.net/en-us/store/minecraft-java-edition\">minecraft.net</a> first.",
            flow.FailReason);
    }

    [Fact]
    public async Task Xsts_Unrecognized_Error_Message()
    {
        _server.XErr = 2148916999;

        var account = MinecraftAccount.CreateBlankMSA();
        var flow = new AuthFlow(account.AccountData, AuthFlowAction.DeviceCode, _auth);
        await flow.StartAsync();

        Assert.Equal(TaskState.Failed, flow.State);
        Assert.Equal("XSTS authentication ended with unrecognized error(s):\n\n2148916999", flow.FailReason);
    }

    [Fact]
    public async Task Profile_404_Is_A_Valid_Account_State()
    {
        _server.ProfileStatus = 404;

        var account = MinecraftAccount.CreateBlankMSA();
        var flow = new AuthFlow(account.AccountData, AuthFlowAction.DeviceCode, _auth);
        await flow.StartAsync();

        // NOTE (upstream): Succeed even if we do not have a profile. This is a valid account state.
        Assert.Equal(TaskState.Succeeded, flow.State);
        Assert.Equal("", account.AccountData.ProfileId());
        Assert.Equal("No Minecraft profile", account.AccountData.ProfileName());
        Assert.Equal(AccountState.Online, account.AccountData.AccountState_);
        // entitlements were still fetched
        Assert.True(account.AccountData.MinecraftEntitlement.CanPlayMinecraft);
    }

    [Fact]
    public async Task Profile_Server_Error_Maps_To_Offline_State()
    {
        _server.ProfileStatus = 500;

        var account = MinecraftAccount.CreateBlankMSA();
        var flow = new AuthFlow(account.AccountData, AuthFlowAction.DeviceCode, _auth);
        await flow.StartAsync();

        Assert.Equal(TaskState.Failed, flow.State);
        Assert.Equal(AccountTaskState.Offline, flow.TaskState_);
        Assert.Contains("Minecraft Java profile acquisition failed", flow.FailReason);
        Assert.Equal(AccountState.Offline, account.AccountData.AccountState_);
    }

    [Fact]
    public async Task Device_Code_Expired_Surfaces_Upstream_Message()
    {
        _server.TokenMode = AuthStubServer.DeviceCodeTokenMode.AlwaysExpired;

        var account = MinecraftAccount.CreateBlankMSA();
        var flow = new AuthFlow(account.AccountData, AuthFlowAction.DeviceCode, _auth);
        await flow.StartAsync();

        Assert.Equal(TaskState.Failed, flow.State);
        Assert.Equal(AccountTaskState.FailedHard, flow.TaskState_);
        Assert.Equal("Device Access failed: Device token has expired.", flow.FailReason);
        // the token stored on the account stays untouched (empty on a fresh MSA account)
        Assert.Equal("", account.AccountData.MsaToken.Token_);
        // hard failure clears the MSA token via MinecraftAccount::authFailed semantics — but the
        // flow itself only reports; the account layer reacts through events (see OfflineAccountTests).
    }

    [Fact]
    public async Task Abort_During_Device_Code_Poll_Routes_To_Aborted()
    {
        _server.PendingTokenResponses = 10; // keep the flow polling

        var account = MinecraftAccount.CreateBlankMSA();
        var flow = new AuthFlow(account.AccountData, AuthFlowAction.DeviceCode, _auth);
        var runTask = flow.StartAsync();

        // wait until the device code was fetched and the flow started polling
        while (_server.Request("POST", "/devicecode") is null)
        {
            await Task.Delay(20);
        }
        await Task.Delay(200);

        flow.Abort();
        await runTask;

        Assert.Equal(TaskState.AbortedByUser, flow.State);
        Assert.Equal("Aborted", flow.FailReason);
    }

    [Fact]
    public async Task Offline_Account_Flow_Succeeds_Without_Steps()
    {
        var account = MinecraftAccount.CreateOffline("Notch");
        var flow = new AuthFlow(account.AccountData, AuthFlowAction.Refresh);
        await flow.StartAsync();

        Assert.Equal(TaskState.Succeeded, flow.State);
        Assert.Equal(Validity.Certain, account.AccountData.Validity_);
        Assert.Equal(AccountState.Online, account.AccountData.AccountState_);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task Interactive_Login_Exchanges_Code_Via_Loopback_Listener()
    {
        _server.PendingTokenResponses = 0;

        var account = MinecraftAccount.CreateBlankMSA();
        var flow = new AuthFlow(account.AccountData, AuthFlowAction.Login, _auth);
        var authorizeUrl = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        flow.AuthorizeWithBrowser += url => authorizeUrl.TrySetResult(url);

        var runTask = flow.StartAsync();
        var url = await authorizeUrl.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // QOAuth2AuthorizationCodeFlow-style authorize URL with the upstream prompt parameter
        Assert.Contains("/authorize?response_type=code", url);
        Assert.Contains("client_id=c36a9fb6-4f2a-41ff-90bd-ae7cc92031eb", url);
        Assert.Contains("scope=XboxLive.SignIn%20XboxLive.offline_access", url);
        Assert.Contains("prompt=select_account", url);
        Assert.Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A", url);

        // act as the browser: Microsoft would redirect to the loopback listener with the code
        var redirectUri = Uri.UnescapeDataString(
            url.Split("redirect_uri=")[1].Split('&')[0]);
        var port = new Uri(redirectUri).Port;
        using var callback = await NetStack.Default.Client
            .GetAsync($"http://127.0.0.1:{port}/?code=INTERACTIVE_CODE")
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(callback.IsSuccessStatusCode);
        var page = await callback.Content.ReadAsStringAsync();
        Assert.Contains("Login Successful", page);

        await runTask;

        Assert.Equal(TaskState.Succeeded, flow.State);
        Assert.Equal("MSA_ACCESS", account.AccountData.MsaToken.Token_);
        Assert.Equal("c36a9fb6-4f2a-41ff-90bd-ae7cc92031eb", account.AccountData.MsaClientID);

        // the code was exchanged at the token endpoint
        var tokenReq = _server.Request("POST", "/token")!;
        Assert.Contains("grant_type=authorization_code", tokenReq.Body);
        Assert.Contains("code=INTERACTIVE_CODE", tokenReq.Body);
        Assert.Contains("redirect_uri=" + Uri.EscapeDataString(redirectUri), tokenReq.Body);
    }

    [Fact]
    public async Task Interactive_Login_Surfaces_OAuth_Error_From_Redirect()
    {
        _server.PendingTokenResponses = 0;

        var account = MinecraftAccount.CreateBlankMSA();
        var flow = new AuthFlow(account.AccountData, AuthFlowAction.Login, _auth);
        var authorizeUrl = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        flow.AuthorizeWithBrowser += url => authorizeUrl.TrySetResult(url);

        var runTask = flow.StartAsync();
        var url = await authorizeUrl.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var redirectUri = Uri.UnescapeDataString(url.Split("redirect_uri=")[1].Split('&')[0]);
        var port = new Uri(redirectUri).Port;
        using var _ = await NetStack.Default.Client
            .GetAsync($"http://127.0.0.1:{port}/?error=access_denied&error_description=User+cancelled")
            .WaitAsync(TimeSpan.FromSeconds(10));

        await runTask;

        Assert.Equal(TaskState.Failed, flow.State);
        Assert.Equal("User cancelled", flow.FailReason);
    }

    [Fact]
    public async Task Silent_Refresh_Exchanges_The_Refresh_Token()
    {
        _server.PendingTokenResponses = 0;

        var account = MinecraftAccount.CreateBlankMSA();
        account.AccountData.MsaClientID = "c36a9fb6-4f2a-41ff-90bd-ae7cc92031eb";
        account.AccountData.MsaToken.RefreshToken = "EXISTING_REFRESH";
        account.AccountData.YggdrasilToken.Token_ = "STALE_YGG";

        var flow = new AuthFlow(account.AccountData, AuthFlowAction.Refresh, _auth);
        await flow.StartAsync();

        Assert.Equal(TaskState.Succeeded, flow.State);
        var tokenReq = _server.Request("POST", "/token")!;
        Assert.Contains("grant_type=refresh_token", tokenReq.Body);
        Assert.Contains("refresh_token=EXISTING_REFRESH", tokenReq.Body);
        Assert.Equal("MSA_ACCESS", account.AccountData.MsaToken.Token_);
        Assert.Equal("MSA_REFRESH", account.AccountData.MsaToken.RefreshToken);

        // the full chain ran after the refresh
        Assert.Equal("MC_ACCESS_TOKEN_STUB", account.AccountData.AccessToken());
    }

    [Fact]
    public async Task Silent_Refresh_With_Changed_Client_Id_Is_Disabled()
    {

        var account = MinecraftAccount.CreateBlankMSA();
        account.AccountData.MsaClientID = "another-client-id";
        account.AccountData.MsaToken.RefreshToken = "EXISTING_REFRESH";

        var flow = new AuthFlow(account.AccountData, AuthFlowAction.Refresh, _auth);
        await flow.StartAsync();

        Assert.Equal(TaskState.Failed, flow.State);
        Assert.Equal(AccountTaskState.Disabled, flow.TaskState_);
        Assert.Equal("Microsoft user authentication failed - client identification has changed.", flow.FailReason);
        Assert.Equal(AccountState.Disabled, account.AccountData.AccountState_);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task Silent_Refresh_With_Empty_Refresh_Token_Is_Disabled()
    {

        var account = MinecraftAccount.CreateBlankMSA();
        account.AccountData.MsaClientID = "c36a9fb6-4f2a-41ff-90bd-ae7cc92031eb";

        var flow = new AuthFlow(account.AccountData, AuthFlowAction.Refresh, _auth);
        await flow.StartAsync();

        Assert.Equal(TaskState.Failed, flow.State);
        Assert.Equal("Microsoft user authentication failed - refresh token is empty.", flow.FailReason);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task MinecraftAccount_Login_Hard_Failure_Clears_Msa_Tokens()
    {
        _server.TokenMode = AuthStubServer.DeviceCodeTokenMode.AlwaysExpired;

        var account = MinecraftAccount.CreateBlankMSA();
        account.AccountData.MsaToken.Token_ = "OLD_TOKEN";
        account.AccountData.MsaToken.RefreshToken = "OLD_REFRESH";
        account.AccountData.Validity_ = Validity.Assumed;

        var flow = account.Login(useDeviceCode: true, _auth);
        await flow!.StartAsync();

        Assert.Equal(TaskState.Failed, flow.State);
        Assert.Equal(AccountTaskState.FailedHard, flow.TaskState_);
        // MinecraftAccount::authFailed(STATE_FAILED_HARD): MSA tokens cleared
        Assert.Equal("", account.AccountData.MsaToken.Token_);
        Assert.Equal("", account.AccountData.MsaToken.RefreshToken);
        Assert.Equal(Validity.None, account.AccountData.Validity_);
    }

    [Fact]
    public async Task MinecraftAccount_Offline_Refresh_Succeeds_And_Fills_Session()
    {

        var account = MinecraftAccount.CreateOffline("Notch");
        // offline accounts have no steps: the flow completes immediately
        var flow = account.Refresh(_auth);
        await flow!.StartAsync();
        Assert.Equal(TaskState.Succeeded, flow.State);
        Assert.Null(account.CurrentTask);
        Assert.False(account.IsActive);

        // fillSession (port of MinecraftAccount::fillSession)
        var session = new FlintLauncher.Core.Instances.AuthSession();
        account.FillSession(session);
        Assert.Equal("0", session.AccessToken);
        Assert.Equal("Notch", session.PlayerName);
        Assert.Equal("offline", session.UserType);
        // token "0" is non-empty, so the session id is built from it
        Assert.Equal("token:0:" + account.ProfileId(), session.Session);
        Assert.Equal(32, session.Uuid.Length);
        Assert.False(session.Uuid.Contains('-'));
    }
}
