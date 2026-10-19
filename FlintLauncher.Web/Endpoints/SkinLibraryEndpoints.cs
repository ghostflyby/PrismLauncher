// SPDX-License-Identifier: GPL-3.0-only
// The local skin library endpoints (DATA D6, user-picked plan B) — the SkinManageDialog's
// LOCAL half, which the port never had:
//   GET    /skins                -> the stored skins (index.json via SkinListStore)
//   GET    /skins/{name}         -> one stored skin
//   POST   /skins                -> store a texture (multipart {file, name?, model?})
//   DELETE /skins/{name}         -> drop the entry + texture
//   POST   /skins/{name}/apply   -> make an account wear the stored skin
//
// Apply is a COMPOSITION of the existing account upload path (D6): the stored texture is
// uploaded through the same SkinUpload core the PUT /accounts/{profileId}/skin endpoint
// uses (Mojang minecraftservices "minecraft/profile/skins") — no second upload mechanism,
// no new account surface. The account skin/cape endpoints themselves are untouched (D6).
// The SkinsDir SETTING decides where the library lives (LauncherRuntime resolves it
// data-root-relative, the C++ convention).

using System.Diagnostics.CodeAnalysis;
using FlintLauncher.Core.Skins;
using FlintLauncher.Web.Models;
using FlintLauncher.Web.OpenApi;
using FlintLauncher.Web.Services;

namespace FlintLauncher.Web.Endpoints;

public static class SkinLibraryEndpoints
{
    public static void MapSkinLibrary(this RouteGroupBuilder api)
    {
        api.WithTags("Skins");
        api.MapGet("/skins", (LauncherRuntime runtime) =>
            Results.Ok(runtime.Skins.List()
                .Select(ToView)
                .ToArray())).Produces<SkinView[]>();

        api.MapGet("/skins/{name}", (string name, LauncherRuntime runtime) =>
            runtime.Skins.Get(name) is { } skin
                ? Results.Ok(ToView(skin))
                : ApiJson.NotFound("skin_not_found", $"no stored skin '{name}'")).Produces<SkinView>();

        api.MapPost("/skins", async (HttpRequest request, LauncherRuntime runtime) =>
        {
            if (!request.HasFormContentType)
            {
                return ApiJson.Error("invalid_body",
                    "a multipart 'file' part with a skin png is required", 400);
            }

            var file = request.Form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
            {
                return ApiJson.Error("invalid_body",
                    "a multipart 'file' part with a skin png is required", 400);
            }

            var name = request.Form["name"].ToString().Trim();
            if (name.Length == 0)
            {
                name = Path.GetFileNameWithoutExtension(file.FileName);
            }

            if (name.Length == 0)
            {
                return ApiJson.Error("invalid_body", "the skin needs a non-empty 'name'", 400);
            }

            if (!TryParseModel(request.Form["model"].ToString(), out var model, out var modelError))
            {
                return modelError;
            }

            // stage the upload with its original suffix (Install re-checks readability; the
            // texture is stored as <name>.png regardless)
            var temp = Path.Combine(Path.GetTempPath(), $"flint-skin-{Guid.NewGuid():N}{Path.GetExtension(file.FileName)}");
            await using (var stream = File.Create(temp))
            {
                await file.CopyToAsync(stream);
            }

            try
            {
                return runtime.Skins.Install(temp, name, model)
                    ? Results.Created($"/api/v1/skins/{Uri.EscapeDataString(name)}",
                        new SkinStoredResponse(name))
                    : ApiJson.Error("skin_store_failed",
                        File.Exists(Path.Combine(runtime.Skins.SkinsDir, name + ".png"))
                            ? $"a skin named '{name}' already exists (delete it first to replace)"
                            : $"could not store skin '{name}'", 409);
            }
            finally
            {
                try
                {
                    File.Delete(temp);
                }
                catch (IOException) { }
            }
        }).WithMetadata(new FormUploadMarker("multipart form: skin image file (png) + optional name/model fields")).Produces<SkinStoredResponse>(201);

        api.MapDelete("/skins/{name}", (string name, LauncherRuntime runtime) =>
            runtime.Skins.Delete(name)
                ? Results.NoContent()
                : ApiJson.NotFound("skin_not_found", $"no stored skin '{name}'")).Produces(204);

        // apply = the D6 composition: SkinUpload (the account upload core) over the stored
        // texture; the model override picks classic/slim, defaulting to the stored variant
        api.MapPost("/skins/{name}/apply", async Task<IResult> (string name,
            SkinApplyRequest? request, LauncherRuntime runtime) =>
        {
            if (string.IsNullOrWhiteSpace(request?.ProfileId))
            {
                return ApiJson.Error("invalid_body", "'profileId' is required", 400);
            }

            var skin = runtime.Skins.Get(name);
            if (skin is null)
            {
                return ApiJson.NotFound("skin_not_found", $"no stored skin '{name}'");
            }

            var texture = runtime.Skins.TextureFileOf(skin);
            if (texture.Length == 0)
            {
                return ApiJson.Error("skin_texture_missing",
                    $"the texture of skin '{name}' is missing from {runtime.Skins.SkinsDir}", 409);
            }

            var index = runtime.Accounts.FindAccountByProfileId(request.ProfileId);
            if (index == -1)
            {
                return ApiJson.Error("unknown_account",
                    $"no account with profile id '{request.ProfileId}'", 400);
            }

            var account = runtime.Accounts.At(index);
            var variant = ParseVariant(request.Model) ?? (skin.Model == SkinModelKind.Slim ? "slim" : "classic");
            var upload = SkinUpload.Make(account.AccessToken(), texture, variant, runtime.Auth, runtime.Net);
            await upload.StartAsync();
            return upload.WasSuccessful
                ? Results.NoContent()
                : ApiJson.Error("skin_upload_failed", upload.FailReason, 400);
        }).Produces(204);
    }

    private static SkinView ToView(SkinModel skin) => new(
        skin.Name(),
        skin.GetModelString(),
        skin.CapeId,
        skin.Url,
        File.Exists(skin.Path));

    /// <summary>The form/body model field: "classic"/"slim" (the account upload spelling);
    /// omitted/empty = classic (the SkinManageDialog default).</summary>
    private static bool TryParseModel(string raw, out SkinModelKind kind,
        [NotNullWhen(false)] out IResult? error)
    {
        var value = raw.Trim().ToLowerInvariant();
        switch (value)
        {
            case "" or "classic":
                kind = SkinModelKind.Classic;
                error = null;
                return true;
            case "slim":
                kind = SkinModelKind.Slim;
                error = null;
                return true;
            default:
                kind = SkinModelKind.Classic;
                error = ApiJson.Error("invalid_model", "'model' must be 'classic' or 'slim'", 400);
                return false;
        }
    }

    private static string? ParseVariant(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "classic" => "classic",
        "slim" => "slim",
        _ => null,
    };
}
