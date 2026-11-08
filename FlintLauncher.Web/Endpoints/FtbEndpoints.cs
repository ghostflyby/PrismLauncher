// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// FTB endpoints, mirroring the ATLauncher surface: the /modpack/all index
// pass-through, the per-pack description + version list (decoded through FtbJson's
// loaders like the C++ FtbListModel) and the pack install — FtbPackInstallTask run through
// the same staged-commit flow as POST /modplatform/atlauncher/install (the modpacks.ch
// metadata and the version files are small, so the flow is synchronous: 201 with the
// committed instance, 500 creation_failed with the failure message otherwise).
//
// The FTB base URL is per-host state (FtbClient — the headless equivalent of the C++
// BuildConfig.FTB_API_BASE_URL constant). The install's `serverUrl` body field selects a
// per-request derived client instead of mutating the shared one, so an override can never
// leak into other requests (and needs no restore).

using System.Text.Json;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.ModPlatform.Ftb;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Tasks;
using FlintLauncher.ExternalApi.Flame;
using FlintLauncher.ExternalApi.Ftb;
using FlintLauncher.ExternalApi.Modrinth;
using FlintLauncher.Web.Models;
using FlintLauncher.Web.Services;
using TruePath;

namespace FlintLauncher.Web.Endpoints;

public static class FtbEndpoints
{
    public static void MapFtb(this RouteGroupBuilder api)
    {
        api.WithTags("ModPlatform");
        // ---------------------------------------------------- modplatform/ftb/packs
        // the /modpack/all index pass-through (FtbListModel::requestFinished's parse): the
        // index payload only carries pack ids, so the response is {"packs":[{"id"},...]}
        api.MapGet("/modplatform/ftb/packs", async (FtbClient ftb, HttpClient http) =>
        {
            List<int> ids;
            try
            {
                ids = await ftb.Packs.All().FetchAsync(http);
            }
            catch (FtbApiException e)
            {
                return ApiJson.Error("ftb_unavailable",
                    $"ftb pack index failed: {e.Message}", 502);
            }

            return Results.Ok(new FtbPackIndexResponse(
                ids.Select(id => new FtbPackIndexEntry(id)).ToArray()));
        }).Produces<FtbPackIndexResponse>();

        // ---------------------------------------------- modplatform/ftb/packs/{id}
        // the /modpack/{id} description (FtbListModel::packRequestFinished's mapping)
        api.MapGet("/modplatform/ftb/packs/{id:int}", async Task<IResult> (int id,
                    FtbClient ftb, HttpClient http) =>
                await WithPackAsync(ftb, http, id, pack => Results.Ok(PackView(pack))))
            .Produces<FtbPackView>();

        // -------------------------------------- modplatform/ftb/packs/{id}/versions
        // the description's version list (the FTB App API has no separate version index)
        api.MapGet("/modplatform/ftb/packs/{id:int}/versions", async Task<IResult> (int id,
                    FtbClient ftb, HttpClient http) =>
                await WithPackAsync(ftb, http, id, pack => Results.Ok(pack.Versions
                    .Select(version => new FtbPackVersionView(version.Id, version.Name,
                        version.Type, version.Updated))
                    .ToArray())))
            .Produces<FtbPackVersionView[]>();

        // -------------------------------------------------- modplatform/ftb/install
        api.MapPost("/modplatform/ftb/install", async Task<IResult> (
            FtbInstallRequest request, FtbClient ftbApi, ModrinthClient modrinth, HttpClient http, FlameClient flame, InstanceList instances, InstanceMutationLock instanceLock) =>
        {
            if (request.PackId <= 0)
            {
                return ApiJson.Error("invalid_body", "'packId' is required", 400);
            }

            // the request's client is resolved up front: a serverUrl override derives its own
            // client, the shared one is never mutated (nothing to restore)
            var ftb = ResolveApi(ftbApi, request.ServerUrl, out var serverError);
            if (serverError is not null)
            {
                return serverError;
            }

            await instanceLock.WaitAsync();
            try
            {
                // the ctor needs the parsed pack description (its version list is what the
                // task's own lookup matches the version name against)
                FtbModpack pack;
                try
                {
                    pack = await ftb.Packs.ById(request.PackId).FetchAsync(http);
                }
                catch (FtbApiException e) when (e.InnerException is JsonException)
                {
                    return ApiJson.Error("creation_failed",
                        $"could not understand pack manifest:\n{e.Message}", 500);
                }
                catch (FtbApiException e)
                {
                    return ApiJson.Error("creation_failed",
                        $"fetching the pack failed: {e.Message}", 500);
                }

                // resolve the version id to the name the task matches against; no
                // versionId = the first entry (the FTB App API lists versions
                // newest-first, so that is the latest). An id the pack does not know
                // becomes its own "name" — the task then fails with its "Failed to find
                // pack version" message.
                var versionName = request.VersionId is { } wanted
                    ? pack.Versions.FirstOrDefault(v => v.Id == wanted)?.Name
                      ?? wanted.ToString()
                    : pack.Versions.Count != 0
                        ? pack.Versions[0].Name
                        : "";

                var task = new FtbPackInstallTask(pack, versionName, modrinth, http, flame, ftb)
                {
                    Group = request.Group ?? "",
                };
                // the staged commit derives the folder from the display name: the pack
                // name is the instance name (like the C++ FTB import dialog's default),
                // the icon follows the vanilla endpoint's "default" fallback
                task.SetName(string.IsNullOrWhiteSpace(request.Name)
                    ? pack.Name
                    : request.Name.Trim());
                task.Icon = request.IconKey ?? "default";

                // staged-commit path: the task runs inside an InstanceList staging folder
                // that InstanceStaging commits into instances/ on success
                // (WrapInstanceTask)
                var wrapped = instances.WrapInstanceTask(task);
                // compute the expected id BEFORE running the task: after the commit the
                // directory already exists and DirNameFromString would return
                // "<name>(1)"
                AbsolutePath inDir = new AbsolutePath(instances.PrimaryDir());
                var expectedId = FileNaming.DirNameFromString(task.Name(), [inDir]);
                try
                {
                    await wrapped(CancellationToken.None);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // an unknown version surfaces the task's "Failed to find pack version" text
                    return ApiJson.Error("creation_failed", TaskWork.ReasonOf(e), 500);
                }

                instances.LoadList();
                // the staged commit's expected dirname, resolved through the chain
                var inst = instances.ResolveInstance(expectedId);
                return inst is null
                    ? ApiJson.Error("creation_failed", "instance was committed but not discovered", 500)
                    : Results.Created($"/api/v1/instances/{inst.Uuid}",
                        new InstanceCreatedResponse(inst.Id(), inst.Name()));
            }
            finally
            {
                instanceLock.Release();
            }
        }).Produces<InstanceCreatedResponse>(201);
    }

    // ----------------------------------------------------------------- helpers

    /// <summary>The request's FTB client: the shared one, or — with a `serverUrl`
    /// override — a derived client, leaving the shared instance untouched. Returns the error
    /// result for a malformed override.</summary>
    private static FtbClient ResolveApi(FtbClient ftb, string? serverUrl, out IResult? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            return ftb;
        }

        var trimmed = serverUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var server) ||
            server.Scheme is not ("http" or "https"))
        {
            error = ApiJson.Error("invalid_server_url",
                "'serverUrl' must be an absolute http(s) URL", 400);
            return ftb;
        }

        // the FTB operations concatenate "/modpack/..." onto the base, so unlike the
        // ATLauncher one this derived base must NOT carry a trailing slash
        return new FtbClient(new FtbClientOptions { BaseUrl = trimmed.TrimEnd('/') });
    }

    /// <summary>Fetch + parse /modpack/{id} (the shared logic of the two GET endpoints) and
    /// hand the pack to <paramref name="map"/>; fetch/parse failures map to the uniform 502
    /// error envelope instead.</summary>
    private static async Task<IResult> WithPackAsync(FtbClient ftb, HttpClient http, int packId,
        Func<FtbModpack, IResult> map)
    {
        FtbModpack pack;
        try
        {
            pack = await ftb.Packs.ById(packId).FetchAsync(http);
        }
        catch (FtbApiException e) when (e.InnerException is JsonException)
        {
            return ApiJson.Error("ftb_invalid_response",
                $"ftb pack manifest is invalid: {e.Message}", 502);
        }
        catch (FtbApiException e)
        {
            return ApiJson.Error("ftb_unavailable",
                $"ftb pack fetch failed: {e.Message}", 502);
        }

        return map(pack);
    }

    /// <summary>The /modpack/{id} description as mapped through FtbJson.LoadModpack
    /// (safeName is the normalized pack *image* name, "&lt;safe&gt;.png"; tags/authors are
    /// flattened to their names/urls).</summary>
    private static FtbPackView PackView(FtbModpack pack) => new(
        pack.Id,
        pack.Name,
        pack.Synopsis,
        pack.Description,
        pack.Type,
        pack.Featured,
        pack.Installs,
        pack.Plays,
        pack.Updated,
        pack.SafeName,
        pack.Art.Select(art => new FtbPackArtView(art.Url, art.Type, art.Width, art.Height))
            .ToList(),
        pack.Authors.Select(author => new FtbPackAuthorView(author.Name, author.Type,
                author.Website))
            .ToList(),
        pack.Versions.Select(version => new FtbPackVersionView(version.Id, version.Name,
                version.Type, version.Updated))
            .ToList(),
        pack.Tags.Select(tag => tag.Name).ToList());
}
