// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Account endpoints: list, offline add, remove, default-selection and the MSA device-code
// login (start + poll; see DeviceCodeSessionStore for the flow wiring).

using FlintLauncher.Core.Auth;
using FlintLauncher.Web.Models;
using FlintLauncher.Web.Services;

namespace FlintLauncher.Web.Endpoints;

public static class AccountEndpoints
{
    public static void MapAccounts(this RouteGroupBuilder api)
    {
        api.WithTags("Accounts");
        // ---------------------------------------------------- accounts/{id}/skin + cape
        // the AccountPage/SkinManageDialog skin surface (read/upload/delete + cape
        // equip/remove; see SkinEndpoints for the mapping)
        api.MapSkins();

        api.MapGet("/accounts", (AccountList accounts) =>
                Results.Ok(Enumerable.Range(0, accounts.Count)
                    .Select(i => View(accounts[i], accounts))
                    .ToArray()))
            .Produces<AccountView[]>();

        api.MapPost("/accounts/offline", (OfflineAccountRequest request, AccountList accounts) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return ApiJson.Error("invalid_body", "'name' is required", 400);
            }

            var account = MinecraftAccount.CreateOffline(request.Name);
            accounts.AddAccount(account);
            return Results.Created($"/api/v1/accounts/{account.ProfileId()}",
                View(account, accounts));
        }).Produces<AccountView>(201);

        api.MapDelete("/accounts/{profileId}", (string profileId, AccountList accounts) =>
        {
            var index = accounts.FindAccountByProfileId(profileId);
            if (index == -1)
            {
                return ApiJson.NotFound("account_not_found", $"no account with profile id '{profileId}'");
            }

            accounts.RemoveAccount(index);
            return Results.NoContent();
        }).Produces(204);

        api.MapPut("/accounts/{profileId}/default", (string profileId, AccountList accounts) =>
        {
            var index = accounts.FindAccountByProfileId(profileId);
            if (index == -1)
            {
                return ApiJson.NotFound("account_not_found", $"no account with profile id '{profileId}'");
            }

            accounts.SetDefaultAccount(accounts[index]);
            return Results.NoContent();
        }).Produces(204);

        // ------------------------------------------------------- MSA device-code login
        api.MapPost("/accounts/msa/devicecode", async (DeviceCodeSessionStore store,
            AuthEndpointsOptions auth, ILoggerFactory loggerFactory) =>
        {
            var session = store.Start(auth, loggerFactory);
            // wait (bounded) for Microsoft to hand out the device/user code pair
            var issued = await store.WaitForCodeAsync(session, TimeSpan.FromSeconds(10));
            if (!issued)
            {
                return ApiJson.Error("msa_error", session.FailReason is { Length: > 0 } reason
                    ? $"device authorization failed: {reason}"
                    : "device authorization failed", 502);
            }

            return Results.Accepted($"/api/v1/accounts/msa/devicecode/{session.DeviceId}/poll",
                new DeviceCodeStartResponse(
                    session.UserCode,
                    session.VerificationUri,
                    session.ExpiresInSeconds,
                    session.DeviceId));
        }).Produces<DeviceCodeStartResponse>(202);

        api.MapPost("/accounts/msa/devicecode/{deviceId}/poll", (string deviceId,
            DeviceCodeSessionStore store,
            AccountList accounts) =>
        {
            var session = store.Get(deviceId);
            return session is null
                ? ApiJson.NotFound("device_not_found", $"no device-code session '{deviceId}'")
                : Results.Ok(store.Poll(session, accounts));
        }).Produces<DeviceCodePollResponse>();
    }

    public static AccountView View(MinecraftAccount account,
        AccountList list) => new(
        account.ProfileId(),
        account.ProfileName(),
        account.TypeString(),
        ReferenceEquals(list.DefaultAccount, account));
}
