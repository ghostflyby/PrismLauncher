// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Managed-pack endpoints (the ManagedPackPage): read the pack
// metadata an import stamped onto the instance, and run the pack-level update check.
//
// The update check reuses the provider CheckUpdate semantics at the PACK level: for
// modrinth/flame packs the provider's version list of the pack project is queried and the
// newest version (matching the instance's mc version / loaders) is compared with the
// recorded ManagedPackVersionID — the same resolution the mod install flow uses, lifted one
// level. atlauncher/ftb (and unmanaged instances) return 400 not_supported: the port has no
// pack-level update path for them (their C++ flows re-download the pack instance).

using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.ModPlatform;
using FlintLauncher.Core.ModPlatform.Flame;
using FlintLauncher.Core.Versioning;
using FlintLauncher.ExternalApi;
using FlintLauncher.ExternalApi.Flame;
using FlintLauncher.ExternalApi.Modrinth;
using FlintLauncher.Web.Models;
using FlintLauncher.Web.Services;

namespace FlintLauncher.Web.Endpoints;

public static class ManagedPackEndpoints
{
    public static void MapManagedPack(this RouteGroupBuilder api)
    {
        api.WithTags("ManagedPack");
        api.MapGet("/instances/{ref}/managedpack", async (string @ref,
            InstanceList instances, InstanceMutationLock instanceLock) =>
        {
            await instanceLock.WaitAsync();
            try
            {
                // the managed-pack metadata lives in instance.cfg on every instance type
                if (instances.ResolveInstance(@ref) is not { } instance)
                {
                    return ApiJson.NotFound("instance_not_found", $"no instance '{@ref}'");
                }

                return Results.Ok(new ManagedPackView(
                    instance.IsManagedPack(),
                    instance.GetManagedPackType(),
                    instance.GetManagedPackId(),
                    instance.GetManagedPackName(),
                    instance.GetManagedPackVersionId(),
                    instance.GetManagedPackVersionName()));
            }
            finally
            {
                instanceLock.Release();
            }
        }).Produces<ManagedPackView>();

        api.MapPost("/instances/{ref}/managedpack/update-check", async (string @ref,
            HttpContext httpContext, InstanceList instances, InstanceMutationLock instanceLock,
            ModrinthClient modrinth, HttpClient http, FlameClient flame) =>
        {
            await instanceLock.WaitAsync();
            try
            {
                if (instances.ResolveInstance(@ref) is not MinecraftInstance instance)
                {
                    return ApiJson.NotFound("instance_not_found", $"no launchable instance '{@ref}'");
                }

                var type = instance.GetManagedPackType().Trim().ToLowerInvariant();
                if (!instance.IsManagedPack() ||
                    (type != ModPlatformTasks.ModrinthKey && type != ModPlatformTasks.FlameKey))
                {
                    // atlauncher/ftb packs have no pack-level update check in the port (their
                    // C++ flows re-download the pack instance) — documented limitation
                    return ApiJson.Error("not_supported",
                        $"update checks are only supported for managed packs of type 'modrinth' or 'flame' (got '{type}')",
                        400);
                }

                ModPlatformTasks.EnsureProfileLoaded(instance);
                var profile = instance.GetPackProfile();
                var mcVersion = profile.GetComponentVersion("net.minecraft");
                if (mcVersion.Length == 0)
                {
                    return ApiJson.Error("instance_unresolved",
                        $"instance '{@ref}' has no resolved minecraft version; its components must be resolved first",
                        409);
                }

                var provider = type == ModPlatformTasks.FlameKey
                    ? ResourceProvider.Flame
                    : ResourceProvider.Modrinth;
                var loadersList = profile.GetModLoadersList();
                var resolver = new ModSelectionResolver(new McVersion(mcVersion),
                    profile.GetModLoaders() ?? ModLoaderType.None, loadersList,
                    modrinth, http, flame);
                IndexedVersion latest;
                try
                {
                    // the pack-level analogue of the provider CheckUpdate pass: resolve the
                    // pack project's latest matching version (empty versionId = latest)
                    (_, latest) = await resolver.ResolveAsync(provider, instance.GetManagedPackId(),
                        "", httpContext.RequestAborted);
                }
                catch (ModResolveException e)
                {
                    return ApiJson.Error("update_check_failed", e.Message, 502);
                }

                var currentVersion = instance.GetManagedPackVersionId();
                var latestVersion = latest.FileId;
                return Results.Ok(new ManagedPackUpdateResponse(
                    currentVersion,
                    latestVersion,
                    !string.Equals(currentVersion, latestVersion, StringComparison.Ordinal)));
            }
            finally
            {
                instanceLock.Release();
            }
        }).Produces<ManagedPackUpdateResponse>();
    }
}
