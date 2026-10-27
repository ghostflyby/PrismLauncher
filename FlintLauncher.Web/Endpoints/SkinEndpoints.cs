// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Account skin endpoints: the AccountPage/SkinManageDialog skin surface —
//   GET    /accounts/{profileId}/skin   -> the profile's current skin {url, variant}
//   PUT    /accounts/{profileId}/skin   -> SkinUpload (multipart {file, model}, sync)
//   DELETE /accounts/{profileId}/skin   -> SkinDelete (DELETE skins/active)
//   PUT    /accounts/{profileId}/cape   -> CapeChange equip (multipart {capeId})
//   DELETE /accounts/{profileId}/cape   -> CapeChange with the empty id: the C++
//                                          CapeChange::getReply turns an empty cape id into
//                                          a bare DELETE of capes/active (cape removal)
// The upload/delete/change requests are single-round-trip Minecraft services calls, so they
// run synchronously: 204 on success, 400 with the task/request FailReason otherwise. They go
// through the injected AuthEndpointsOptions (defaults byte-identical to the hardcoded
// upstream URL; tests construct options pointed at their stub).
//
// Cape equip by id is the whole upstream capability: neither the Mojang API nor the ported
// CapeChange accepts a cape *file*, so a file-only PUT body is rejected (a file part may
// accompany the id, mirroring the multipart shape, but it cannot be used).

using System.Diagnostics.CodeAnalysis;
using FlintLauncher.Core.Auth;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Skins;
using FlintLauncher.Web.Models;
using FlintLauncher.Web.OpenApi;

namespace FlintLauncher.Web.Endpoints;

public static class SkinEndpoints
{
    public static void MapSkins(this RouteGroupBuilder api)
    {
        api.WithTags("Accounts");
        // ------------------------------------------------- accounts/{id}/skin (read)
        api.MapGet("/accounts/{profileId}/skin", (string profileId, AccountList accounts) =>
        {
            if (!TryLookupAccount(accounts, profileId, out var account, out var notFound))
            {
                return notFound;
            }

            // the profile skin data the login flow stored on the account
            // (AccountData::MinecraftProfile::skin)
            var skin = account.AccountData.MinecraftProfile.SkinField;
            return Results.Ok(new AccountSkinView(skin.Url, skin.Variant));
        }).Produces<AccountSkinView>();

        // ------------------------------------------------- accounts/{id}/skin (upload)
        api.MapPut("/accounts/{profileId}/skin", async Task<IResult> (string profileId,
            HttpRequest request, AccountList accounts, AuthEndpointsOptions auth, HttpClient http) =>
        {
            if (!TryLookupAccount(accounts, profileId, out var account, out var notFound))
            {
                return notFound;
            }

            var form = await request.ReadFormAsync();
            var file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
            {
                return ApiJson.Error("invalid_body", "a multipart 'file' part with a skin image is required", 400);
            }

            // the SkinManageDialog's model choice: classic (wide) or slim (Alex)
            var model = form["model"].ToString().Trim().ToLowerInvariant();
            if (model.Length == 0)
            {
                model = "classic";
            }

            if (model is not ("classic" or "slim"))
            {
                return ApiJson.Error("invalid_model", "'model' must be 'classic' or 'slim'", 400);
            }

            using var body = new MemoryStream();
            await file.CopyToAsync(body);
            var upload = SkinUpload.Make(account.AccessToken(), body.ToArray(), model, auth, http);
            await upload.StartAsync();
            return upload.WasSuccessful
                ? Results.NoContent()
                : ApiJson.Error("skin_upload_failed", upload.FailReason, 400);
        }).WithMetadata(new FormUploadMarker("multipart form: skin image file (png) + optional model field (slim/classic)")).Produces(204);

        // ------------------------------------------------- accounts/{id}/skin (delete)
        api.MapDelete("/accounts/{profileId}/skin", async Task<IResult> (string profileId,
            AccountList accounts, AuthEndpointsOptions auth) =>
        {
            if (!TryLookupAccount(accounts, profileId, out var account, out var notFound))
            {
                return notFound;
            }

            try
            {
                await SkinDelete.DeleteAsync(account.AccessToken(), auth);
            }
            catch (FlintHttpException e)
            {
                return ApiJson.Error("skin_delete_failed", e.Reason, 400);
            }

            return Results.NoContent();
        }).Produces(204);

        // ------------------------------------------------- accounts/{id}/cape (equip)
        api.MapPut("/accounts/{profileId}/cape", async Task<IResult> (string profileId,
            HttpRequest request, AccountList accounts, AuthEndpointsOptions auth) =>
        {
            if (!TryLookupAccount(accounts, profileId, out var account, out var notFound))
            {
                return notFound;
            }

            var form = await request.ReadFormAsync();
            var capeId = form["capeId"].ToString().Trim();
            if (capeId.Length == 0)
            {
                // equip works by id only (CapeChange makes a {"capeId": ...} PUT); there is
                // no cape *file* upload in the Minecraft services API
                return ApiJson.Error("invalid_body",
                    "a 'capeId' form field is required (capes cannot be uploaded as files)", 400);
            }

            try
            {
                await CapeChange.ChangeAsync(account.AccessToken(), capeId, auth);
            }
            catch (FlintHttpException e)
            {
                return ApiJson.Error("cape_change_failed", e.Reason, 400);
            }

            return Results.NoContent();
        }).WithMetadata(new FormUploadMarker("form field capeId")).Produces(204);

        // ------------------------------------------------- accounts/{id}/cape (remove)
        // CapeChange's delete semantics: the empty cape id turns the request into a bare
        // DELETE of minecraft/profile/capes/active
        api.MapDelete("/accounts/{profileId}/cape", async Task<IResult> (string profileId,
            AccountList accounts, AuthEndpointsOptions auth) =>
        {
            if (!TryLookupAccount(accounts, profileId, out var account, out var notFound))
            {
                return notFound;
            }

            try
            {
                await CapeChange.ChangeAsync(account.AccessToken(), "", auth);
            }
            catch (FlintHttpException e)
            {
                return ApiJson.Error("cape_change_failed", e.Reason, 400);
            }

            return Results.NoContent();
        }).Produces(204);
    }

    /// <summary>The account for a profile id, or the uniform 404 through
    /// <paramref name="notFound"/> (AccountList lookups are index-based).</summary>
    private static bool TryLookupAccount(AccountList accounts, string profileId,
        [NotNullWhen(true)] out MinecraftAccount? account, [NotNullWhen(false)] out IResult? notFound)
    {
        var index = accounts.FindAccountByProfileId(profileId);
        if (index == -1)
        {
            account = null;
            notFound = ApiJson.NotFound("account_not_found",
                $"no account with profile id '{profileId}'");
            return false;
        }

        account = accounts.At(index);
        notFound = null;
        return true;
    }
}
