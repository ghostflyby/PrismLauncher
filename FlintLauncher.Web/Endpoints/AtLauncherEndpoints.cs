// SPDX-License-Identifier: GPL-3.0-only
// ATLauncher endpoints (fix-2 step 1): the packsnew.json pass-through (the pack list and the
// per-pack version list, both mapped through AtLauncherPackIndex.LoadIndexedPack like the
// C++ AtlListModel) and the pack install — AtLauncherPackInstallTask run through the same
// staged-commit flow as POST /instances/vanilla (the ATL configs/mods downloads are small,
// so the flow is synchronous: 201 with the committed instance, 500 creation_failed with the
// task's FailReason otherwise, e.g. a Configs.json 404 for an unknown pack/version).
//
// The ATL download-server/API URL hooks (AtLauncherApi.DownloadServerUrl / ApiBaseUrl) are
// process-global statics — the headless equivalent of the C++ BuildConfig constants. The
// install's `serverUrl` body field swaps them for the duration of the call; both are saved
// before and restored in `finally` so a per-request override never leaks into other requests
// (the tests serialize on the "AtLauncherApi" collection for the same reason).
using FlintLauncher.Core.IO;
using FlintLauncher.Core.ModPlatform.AtLauncher;
using FlintLauncher.Web.Models;
using FlintLauncher.Web.Services;

namespace FlintLauncher.Web.Endpoints;

public static class AtLauncherEndpoints
{
    public static void MapAtLauncher(this IEndpointRouteBuilder api)
    {
        // ------------------------------------------------ modplatform/atlauncher/packs
        // the packsnew.json pass-through (AtlListModel::loadIndexedPack's mapping)
        api.MapGet("/modplatform/atlauncher/packs", () => WithIndexAsync(
            packs => Results.Ok(packs.Select(PackView).ToArray())));

        // ---------------------------------------- modplatform/atlauncher/packs/{name}/versions
        // the index carries each pack's version list (the ATL CDN has no per-pack index URL,
        // so this re-reads packsnew.json and matches the pack by name, case-insensitively)
        api.MapGet("/modplatform/atlauncher/packs/{name}/versions", (string name) => WithIndexAsync(
            packs =>
            {
                var pack = packs.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
                return pack is null
                    ? ApiJson.NotFound("pack_not_found", $"no ATLauncher pack '{name}'")
                    : Results.Ok(pack.Versions
                        .Select(version => new AtLauncherPackVersionView(version.Version, version.Minecraft))
                        .ToArray());
            }));

        // ------------------------------------------------ modplatform/atlauncher/install
        api.MapPost("/modplatform/atlauncher/install", async Task<IResult> (
            AtLauncherInstallRequest request, LauncherRuntime runtime) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Version))
            {
                return ApiJson.Error("invalid_body", "'name' and 'version' are required", 400);
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
                // the URL builders concatenate directly onto the hook, so keep the slash
                serverUrl = trimmed.EndsWith('/') ? trimmed : trimmed + "/";
            }

            // save the static hooks before anything can touch them; restored in finally
            var previousDownloadServer = AtLauncherApi.DownloadServerUrl;
            var previousApiBase = AtLauncherApi.ApiBaseUrl;

            await runtime.InstanceLock.WaitAsync();
            try
            {
                if (serverUrl is not null)
                {
                    AtLauncherApi.DownloadServerUrl = serverUrl;
                    // (ApiBaseUrl is only read by the share-code flow, never by the install)
                }

                var task = new AtLauncherPackInstallTask(request.Name, request.Version)
                {
                    TargetDir = "",
                    Group = request.Group ?? "",
                    OptionalModsSelection = request.OptionalMods ?? new Dictionary<string, bool>(),
                    // the headless runtime registers no "ATLauncherPacks" meta-cache base, so
                    // the task downloads into a temporary folder (see the task header note)
                };
                // the staged commit derives the folder from the display name: the pack name is
                // the instance name (like the C++ ATL import dialog's default), the icon
                // follows the vanilla endpoint's "default" fallback
                task.SetName(request.Name);
                task.SetIcon(request.IconKey ?? "default");
                if (!string.IsNullOrWhiteSpace(request.ChooseLoaderVersion))
                {
                    // the loader "choose" mode's pre-set answer
                    task.ChooseLoaderVersion = (_, _) => request.ChooseLoaderVersion;
                }

                // staged-commit path: the task runs inside an InstanceList staging folder that
                // InstanceStaging commits into instances/ on success (WrapInstanceTask)
                var wrapped = runtime.Instances.WrapInstanceTask(task);
                // compute the expected id BEFORE running the task: after the commit the
                // directory already exists and DirNameFromString would return "<name>(1)"
                var expectedId = FileSystem.DirNameFromString(request.Name, runtime.Instances.PrimaryDir());
                await wrapped.StartAsync();
                if (!wrapped.WasSuccessful)
                {
                    // the staging wrapper re-emits the inner task's FailReason (unknown
                    // pack/version surface its text)
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
                AtLauncherApi.DownloadServerUrl = previousDownloadServer;
                AtLauncherApi.ApiBaseUrl = previousApiBase;
                runtime.InstanceLock.Release();
            }
        });
    }

    // ----------------------------------------------------------------- helpers

    /// <summary>Fetch + parse packsnew.json (the shared logic of the two GET endpoints) and
    /// hand the packs to <paramref name="map"/>; fetch/parse failures map to the uniform
    /// 502 error envelope instead.</summary>
    private static async Task<IResult> WithIndexAsync(Func<List<IndexedPack>, IResult> map)
    {
        var (job, request) = AtLauncherApi.FetchPackIndex();
        await job.StartAsync();
        if (!job.WasSuccessful)
        {
            return ApiJson.Error("atlauncher_unavailable",
                $"atlauncher pack index failed: {job.FailReason}", 502);
        }

        List<IndexedPack> packs;
        try
        {
            packs = AtLauncherApi.LoadPackIndex(request.BodyAsString);
        }
        catch (FlintLauncher.Core.Json.JsonParseException e)
        {
            return ApiJson.Error("atlauncher_invalid_response",
                $"atlauncher pack index is invalid: {e.Message}", 502);
        }
        return map(packs);
    }

    /// <summary>The packsnew.json entry as the C++ AtlListModel maps it (safeName is the
    /// normalized pack *image* name, "&lt;safe&gt;.png"; type is "public"/"private").</summary>
    private static AtLauncherPackView PackView(IndexedPack pack) => new(
        pack.Name,
        pack.SafeName,
        pack.Description,
        pack.Type == PackType.Private ? "private" : "public",
        pack.Versions.Select(version => new AtLauncherPackVersionView(version.Version, version.Minecraft))
            .ToList());
}
