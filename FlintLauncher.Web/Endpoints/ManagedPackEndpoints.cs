// SPDX-License-Identifier: GPL-3.0-only
// Managed-pack endpoints (UI-API mapping Wave B, the ManagedPackPage): read the pack
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
using FlintLauncher.Core.Versioning;
using FlintLauncher.Web.Models;
using FlintLauncher.Web.Services;

namespace FlintLauncher.Web.Endpoints;

public static class ManagedPackEndpoints
{
    public static void MapManagedPack(this IEndpointRouteBuilder api)
    {
        api.MapGet("/instances/{id}/managedpack", async (string id, LauncherRuntime runtime) =>
        {
            await runtime.InstanceLock.WaitAsync();
            try
            {
                // the managed-pack metadata lives in instance.cfg on every instance type
                if (runtime.Instances.GetInstanceById(id) is not { } instance)
                {
                    return ApiJson.NotFound("instance_not_found", $"no instance '{id}'");
                }
                return Results.Ok(new ManagedPackView(
                    instance.IsManagedPack(),
                    instance.GetManagedPackType(),
                    instance.GetManagedPackID(),
                    instance.GetManagedPackName(),
                    instance.GetManagedPackVersionID(),
                    instance.GetManagedPackVersionName()));
            }
            finally
            {
                runtime.InstanceLock.Release();
            }
        });

        api.MapPost("/instances/{id}/managedpack/update-check", async (string id,
            HttpContext httpContext, LauncherRuntime runtime) =>
        {
            await runtime.InstanceLock.WaitAsync();
            try
            {
                if (runtime.Instances.GetInstanceById(id) is not MinecraftInstance instance)
                {
                    return ApiJson.NotFound("instance_not_found", $"no launchable instance '{id}'");
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
                        $"instance '{id}' has no resolved minecraft version; its components must be resolved first",
                        409);
                }

                var provider = type == ModPlatformTasks.FlameKey
                    ? ResourceProvider.FLAME
                    : ResourceProvider.MODRINTH;
                var loadersList = profile.GetModLoadersList();
                var resolver = new ModSelectionResolver(new McVersion(mcVersion),
                    profile.GetModLoaders() ?? ModLoaderType.None, loadersList, runtime.Modrinth, runtime.Flame);
                IndexedVersion latest;
                try
                {
                    // the pack-level analogue of the provider CheckUpdate pass: resolve the
                    // pack project's latest matching version (empty versionId = latest)
                    (_, latest) = await resolver.ResolveAsync(provider, instance.GetManagedPackID(),
                        "", httpContext.RequestAborted);
                }
                catch (ModResolveException e)
                {
                    return ApiJson.Error("update_check_failed", e.Message, 502);
                }

                var currentVersion = instance.GetManagedPackVersionID();
                var latestVersion = latest.FileId;
                return Results.Ok(new ManagedPackUpdateResponse(
                    currentVersion,
                    latestVersion,
                    !string.Equals(currentVersion, latestVersion, StringComparison.Ordinal)));
            }
            finally
            {
                runtime.InstanceLock.Release();
            }
        });
    }
}
