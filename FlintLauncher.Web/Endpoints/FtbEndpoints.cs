// SPDX-License-Identifier: GPL-3.0-only
// FTB endpoints (fix-2 step 2), mirroring the ATLauncher surface: the /modpack/all index
// pass-through, the per-pack description + version list (mapped through FtbPackManifest's
// loaders like the C++ FtbListModel) and the pack install — FtbPackInstallTask run through
// the same staged-commit flow as POST /modplatform/atlauncher/install (the modpacks.ch
// metadata and the version files are small, so the flow is synchronous: 201 with the
// committed instance, 500 creation_failed with the failure message otherwise).
//
// FtbApi.BaseUrl is a process-global static — the headless equivalent of the C++
// BuildConfig.FTB_API_BASE_URL constant. The install's `serverUrl` body field swaps it for
// the duration of the call; it is saved before and restored in `finally` so a per-request
// override never leaks into other requests (the tests serialize on the "FlameApi" collection
// for the same reason — the FTB install also resolves CurseForge-only files through the
// Flame layer's static base URL).
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Json;
using FlintLauncher.Core.ModPlatform.Ftb;
using FlintLauncher.Web.Models;
using FlintLauncher.Web.Services;

namespace FlintLauncher.Web.Endpoints;

public static class FtbEndpoints
{
    public static void MapFtb(this IEndpointRouteBuilder api)
    {
        // ---------------------------------------------------- modplatform/ftb/packs
        // the /modpack/all index pass-through (FtbListModel::requestFinished's parse): the
        // index payload only carries pack ids, so the response is {"packs":[{"id"},...]}
        api.MapGet("/modplatform/ftb/packs", async () =>
        {
            var (job, request) = FtbApi.FetchModpacks();
            await job.StartAsync();
            if (!job.WasSuccessful)
            {
                return ApiJson.Error("ftb_unavailable",
                    $"ftb pack index failed: {job.FailReason}", 502);
            }

            List<int> ids;
            try
            {
                ids = FtbApi.LoadModpackIndex(request.BodyAsString);
            }
            catch (JsonParseException e)
            {
                return ApiJson.Error("ftb_invalid_response",
                    $"ftb pack index is invalid: {e.Message}", 502);
            }
            return Results.Ok(new FtbPackIndexResponse(
                ids.Select(id => new FtbPackIndexEntry(id)).ToArray()));
        });

        // ---------------------------------------------- modplatform/ftb/packs/{id}
        // the /modpack/{id} description (FtbListModel::packRequestFinished's mapping)
        api.MapGet("/modplatform/ftb/packs/{id}", async Task<IResult> (int id) =>
            await WithPackAsync(id, pack => Results.Ok(PackView(pack))));

        // -------------------------------------- modplatform/ftb/packs/{id}/versions
        // the description's version list (the FTB App API has no separate version index)
        api.MapGet("/modplatform/ftb/packs/{id}/versions", async Task<IResult> (int id) =>
            await WithPackAsync(id, pack => Results.Ok(pack.Versions
                .Select(version => new FtbPackVersionView(version.Id, version.Name,
                    version.Type, version.Updated))
                .ToArray())));

        // -------------------------------------------------- modplatform/ftb/install
        api.MapPost("/modplatform/ftb/install", async Task<IResult> (
            FtbInstallRequest request, LauncherRuntime runtime) =>
        {
            if (request.PackId <= 0)
            {
                return ApiJson.Error("invalid_body", "'packId' is required", 400);
            }

            string? serverUrl = null;
            if (!string.IsNullOrWhiteSpace(request.ServerUrl))
            {
                var trimmed = request.ServerUrl.Trim();
                if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var server) ||
                    server.Scheme is not ("http" or "https"))
                {
                    return ApiJson.Error("invalid_server_url",
                        "'serverUrl' must be an absolute http(s) URL", 400);
                }
                // the FTB URL builders concatenate "/modpack/..." onto the hook, so
                // unlike the ATLauncher one this hook must NOT carry a trailing slash
                serverUrl = trimmed.TrimEnd('/');
            }

            // save the static hook before anything can touch it; restored in finally
            var previousBaseUrl = FtbApi.BaseUrl;

            await runtime.InstanceLock.WaitAsync();
            try
            {
                if (serverUrl is not null)
                {
                    FtbApi.BaseUrl = serverUrl;
                }

                // the ctor needs the parsed pack description (its version list is what the
                // task's own lookup matches the version name against)
                var (job, fetchRequest) = FtbApi.FetchModpack(request.PackId);
                await job.StartAsync();
                if (!job.WasSuccessful)
                {
                    return ApiJson.Error("creation_failed",
                        $"fetching the pack failed: {job.FailReason}", 500);
                }

                FtbModpack pack;
                try
                {
                    pack = FtbApi.LoadModpack(fetchRequest.BodyAsString);
                }
                catch (JsonParseException e)
                {
                    return ApiJson.Error("creation_failed",
                        $"could not understand pack manifest:\n{e.Message}", 500);
                }

                // resolve the version id to the name the task matches against; no
                // versionId = the first entry (the FTB App API lists versions
                // newest-first, so that is the latest). An id the pack does not know
                // becomes its own "name" — the task then fails with its "Failed to find
                // pack version" message.
                var versionName = request.VersionId is { } wanted
                    ? pack.Versions.FirstOrDefault(v => v.Id == wanted)?.Name
                        ?? wanted.ToString()
                    : pack.Versions.Count != 0 ? pack.Versions[0].Name : "";

                var task = new FtbPackInstallTask(pack, versionName, runtime.Flame)
                {
                    Group = request.Group ?? "",
                };
                // the staged commit derives the folder from the display name: the pack
                // name is the instance name (like the C++ FTB import dialog's default),
                // the icon follows the vanilla endpoint's "default" fallback
                task.SetName(string.IsNullOrWhiteSpace(request.Name)
                    ? pack.Name
                    : request.Name.Trim());
                task.SetIcon(request.IconKey ?? "default");

                // staged-commit path: the task runs inside an InstanceList staging folder
                // that InstanceStaging commits into instances/ on success
                // (WrapInstanceTask)
                var wrapped = runtime.Instances.WrapInstanceTask(task);
                // compute the expected id BEFORE running the task: after the commit the
                // directory already exists and DirNameFromString would return
                // "<name>(1)"
                var expectedId = FileSystem.DirNameFromString(task.Name(),
                    runtime.Instances.PrimaryDir());
                await wrapped.StartAsync();
                if (!wrapped.WasSuccessful)
                {
                    // the staging wrapper re-emits the inner task's FailReason (an
                    // unknown version surfaces the task's "Failed to find pack version"
                    // text)
                    return ApiJson.Error("creation_failed", wrapped.FailReason, 500);
                }

                runtime.Instances.LoadList();
                var inst = runtime.Instances.GetInstanceById(expectedId);
                return inst is null
                    ? ApiJson.Error("creation_failed", "instance was committed but not discovered", 500)
                    : Results.Created($"/api/v1/instances/{inst.Id()}",
                        new InstanceCreatedResponse(inst.Id(), inst.Name()));
            }
            finally
            {
                FtbApi.BaseUrl = previousBaseUrl;
                runtime.InstanceLock.Release();
            }
        });
    }

    // ----------------------------------------------------------------- helpers

    /// <summary>Fetch + parse /modpack/{id} (the shared logic of the two GET endpoints) and
    /// hand the pack to <paramref name="map"/>; fetch/parse failures map to the uniform 502
    /// error envelope instead.</summary>
    private static async Task<IResult> WithPackAsync(int packId, Func<FtbModpack, IResult> map)
    {
        var (job, request) = FtbApi.FetchModpack(packId);
        await job.StartAsync();
        if (!job.WasSuccessful)
        {
            return ApiJson.Error("ftb_unavailable",
                $"ftb pack fetch failed: {job.FailReason}", 502);
        }

        FtbModpack pack;
        try
        {
            pack = FtbApi.LoadModpack(request.BodyAsString);
        }
        catch (JsonParseException e)
        {
            return ApiJson.Error("ftb_invalid_response",
                $"ftb pack manifest is invalid: {e.Message}", 502);
        }
        return map(pack);
    }

    /// <summary>The /modpack/{id} description as mapped through FtbPackManifest.LoadModpack
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
