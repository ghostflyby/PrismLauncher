// SPDX-License-Identifier: GPL-3.0-only
// ATLauncher endpoints: the packsnew.json pass-through (the pack list and the
// per-pack version list, both mapped through AtLauncherPackIndex.LoadIndexedPack like the
// C++ AtlListModel) and the pack install — AtLauncherPackInstallTask run through the same
// staged-commit flow as POST /instances/vanilla (the ATL configs/mods downloads are small,
// so the flow is synchronous: 201 with the committed instance, 500 creation_failed with the
// task's FailReason otherwise, e.g. a Configs.json 404 for an unknown pack/version).
//
// The ATL download-server/API URL hooks (AtLauncherApi.DownloadServerUrl / ApiBaseUrl) are
// per-host state (runtime.AtLauncher — the headless equivalent of the C++ BuildConfig
// constants). The install's `serverUrl` body field selects a per-request derived client
// instead of mutating the shared one, so an override can never leak into other requests
// (and needs no restore).

using System.Text.Json;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.ModPlatform.AtLauncher;
using FlintLauncher.Web.Models;
using FlintLauncher.Web.Services;
using TruePath;

namespace FlintLauncher.Web.Endpoints;

public static class AtLauncherEndpoints
{
    public static void MapAtLauncher(this RouteGroupBuilder api)
    {
        api.WithTags("ModPlatform");
        // ------------------------------------------------ modplatform/atlauncher/packs
        // the packsnew.json pass-through (AtlListModel::loadIndexedPack's mapping)
        api.MapGet("/modplatform/atlauncher/packs", (LauncherRuntime runtime) => WithIndexAsync(
                runtime, packs => Results.Ok(packs.Select(PackView).ToArray())))
            .Produces<AtLauncherPackView[]>();

        // ---------------------------------------- modplatform/atlauncher/packs/{name}/versions
        // the index carries each pack's version list (the ATL CDN has no per-pack index URL,
        // so this re-reads packsnew.json and matches the pack by name, case-insensitively)
        api.MapGet("/modplatform/atlauncher/packs/{name}/versions", (string name,
                LauncherRuntime runtime) => WithIndexAsync(
                runtime,
                packs =>
                {
                    var pack = packs.FirstOrDefault(candidate =>
                        string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
                    return pack is null
                        ? ApiJson.NotFound("pack_not_found", $"no ATLauncher pack '{name}'")
                        : Results.Ok(pack.Versions
                            .Select(version => new AtLauncherPackVersionView(version.Version, version.Minecraft))
                            .ToArray());
                }))
            .Produces<AtLauncherPackVersionView[]>();

        // ------------------------------------------------ modplatform/atlauncher/install
        api.MapPost("/modplatform/atlauncher/install", async Task<IResult> (
            AtLauncherInstallRequest request, LauncherRuntime runtime) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Version))
            {
                return ApiJson.Error("invalid_body", "'name' and 'version' are required", 400);
            }

            // the request's client is resolved up front: a serverUrl override derives its own
            // client, the runtime's shared one is never mutated (nothing to restore)
            var atlauncher = ResolveApi(runtime, request.ServerUrl, out var serverError);
            if (serverError is not null)
            {
                return serverError;
            }

            await runtime.InstanceLock.WaitAsync();
            try
            {
                var task = new AtLauncherPackInstallTask(request.Name, request.Version, api: atlauncher)
                {
                    TargetDir = default,
                    Group = request.Group ?? "",
                    OptionalModsSelection = request.OptionalMods ?? new Dictionary<string, bool>(),
                    // the headless runtime registers no "ATLauncherPacks" meta-cache base, so
                    // the task downloads into a temporary folder (see the task header note)
                };
                // the staged commit derives the folder from the display name: the pack name is
                // the instance name (like the C++ ATL import dialog's default), the icon
                // follows the vanilla endpoint's "default" fallback
                task.SetName(request.Name);
                task.Icon = request.IconKey ?? "default";
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
                AbsolutePath inDir = runtime.Instances.PrimaryDir();
                var expectedId = FileNaming.DirNameFromString(request.Name, [inDir]);
                await wrapped.StartAsync();
                if (!wrapped.WasSuccessful)
                {
                    // the staging wrapper re-emits the inner task's FailReason (unknown
                    // pack/version surface its text)
                    return ApiJson.Error("creation_failed", wrapped.FailReason, 500);
                }

                runtime.Instances.LoadList();
                // the staged commit's expected dirname, resolved through the chain
                var inst = runtime.Instances.ResolveInstance(expectedId);
                return inst is null
                    ? ApiJson.Error("creation_failed", "instance was committed but not discovered", 500)
                    : Results.Created($"/api/v1/instances/{inst.Uuid}",
                        new InstanceCreatedResponse(inst.Id(), inst.Name()));
            }
            finally
            {
                runtime.InstanceLock.Release();
            }
        }).Produces<InstanceCreatedResponse>(201);
    }

    // ----------------------------------------------------------------- helpers

    /// <summary>The request's ATLauncher client: the runtime's own, or — with a `serverUrl`
    /// override — a derived client (same net stack), leaving the shared instance untouched.
    /// Returns the error result for a malformed override.</summary>
    private static AtLauncherApi ResolveApi(LauncherRuntime runtime, string? serverUrl, out IResult? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            return runtime.AtLauncher;
        }

        var trimmed = serverUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var server) ||
            server.Scheme is not ("http" or "https"))
        {
            error = ApiJson.Error("invalid_server_url",
                "'serverUrl' must be an absolute http(s) URL", 400);
            return runtime.AtLauncher;
        }

        // the URL builders concatenate directly onto the base, so keep the slash
        return new AtLauncherApi(runtime.AtLauncher.Net)
        {
            DownloadServerUrl = trimmed.EndsWith('/') ? trimmed : trimmed + "/",
            // (ApiBaseUrl is only read by the share-code flow, never by the install)
        };
    }

    /// <summary>Fetch + parse packsnew.json (the shared logic of the two GET endpoints) and
    /// hand the packs to <paramref name="map"/>; fetch/parse failures map to the uniform
    /// 502 error envelope instead.</summary>
    private static async Task<IResult> WithIndexAsync(LauncherRuntime runtime,
        Func<List<IndexedPack>, IResult> map)
    {
        var (job, request) = runtime.AtLauncher.FetchPackIndex();
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
        catch (JsonException e)
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
