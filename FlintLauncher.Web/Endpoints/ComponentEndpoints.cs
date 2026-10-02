// SPDX-License-Identifier: GPL-3.0-only
// Component-stack endpoints (UI-API mapping Wave B, the VersionPage): list the instance's
// component list (PackProfile), change a component's pinned version / important flag,
// install a loader component, customize (materialize the metadata patch as a local file),
// revert (delete the local patch and fall back to metadata), remove and reorder.
//
// C++ operation → endpoint mapping (docs/UI-API-MAPPING.md §2 VersionPage):
//   list                  → GET  /instances/{id}/components
//   change version        → PATCH /instances/{id}/components/{uid} {version}
//   important toggle      → PATCH /instances/{id}/components/{uid} {important}
//   install loader        → POST /instances/{id}/components {uid, version?, important?}
//   customize/edit        → POST /instances/{id}/components/{uid}/customize
//   revert                → POST /instances/{id}/components/{uid}/revert
//   remove                → DELETE /instances/{id}/components/{uid}
//   move up/down          → POST /instances/{id}/components/{uid}/move {delta}
//  (the C++ "add empty patch" and the jarmod/agent/custom-jar imports of PackProfile are
//   not exposed here — deferred; see the Wave B delivery notes)
//
// Every mutation runs through PackProfile (the same code paths the C++ page drives) and
// SaveNow()s the profile afterwards (the C++ schedules a 5s deferred save; the port saves
// eagerly because there is no event loop at this layer).
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Web.Models;
using FlintLauncher.Web.Services;

namespace FlintLauncher.Web.Endpoints;

public static class ComponentEndpoints
{
    public static void MapComponents(this IEndpointRouteBuilder api)
    {
        // ------------------------------------------------------------------ list
        api.MapGet("/instances/{id}/components", async (string id, LauncherRuntime runtime) =>
        {
            await runtime.InstanceLock.WaitAsync();
            try
            {
                if (LookupInstance(runtime, id) is not { } instance)
                {
                    return ApiJson.NotFound("instance_not_found", $"no launchable instance '{id}'");
                }
                // offline resolve first (the EnsureProfileLoaded idiom: the synchronous part
                // of the C++ reload(Offline) — re-read mmc-pack.json when not loaded yet)
                ModPlatformTasks.EnsureProfileLoaded(instance);
                var profile = instance.GetPackProfile();
                return Results.Ok(profile.Components.Select(ToView).ToArray());
            }
            finally
            {
                runtime.InstanceLock.Release();
            }
        });

        // ------------------------------------------------- change version / important
        api.MapPatch("/instances/{id}/components/{uid}", async (string id, string uid,
            ComponentPatchRequest request, LauncherRuntime runtime) =>
        {
            if (request.Version is null && request.Important is null)
            {
                return ApiJson.Error("invalid_body", "body must set 'version' and/or 'important'", 400);
            }
            if (request.Version is { Length: 0 })
            {
                return ApiJson.Error("invalid_body", "'version' must not be empty", 400);
            }

            await runtime.InstanceLock.WaitAsync();
            try
            {
                if (LookupInstance(runtime, id) is not { } instance)
                {
                    return ApiJson.NotFound("instance_not_found", $"no launchable instance '{id}'");
                }
                ModPlatformTasks.EnsureProfileLoaded(instance);
                var profile = instance.GetPackProfile();
                var component = profile.GetComponent(uid);
                if (component is null)
                {
                    return ApiJson.NotFound("component_not_found", $"no component '{uid}'");
                }

                if (request.Version is { } version)
                {
                    // PackProfile.SetComponentVersion reverts a custom patch first, then pins
                    // the new version (the C++ changeVersion flow)
                    if (!await profile.SetComponentVersion(uid, version))
                    {
                        return ApiJson.Conflict("version_change_failed",
                            $"the version of '{uid}' could not be changed (its custom patch could not be reverted)");
                    }
                }
                if (request.Important is { } important)
                {
                    component.SetImportant(important);
                }
                profile.SaveNow();
                return Results.NoContent();
            }
            finally
            {
                runtime.InstanceLock.Release();
            }
        });

        // ---------------------------------------------------------------- install loader
        api.MapPost("/instances/{id}/components", async (string id, ComponentInstallRequest request,
            LauncherRuntime runtime) =>
        {
            if (string.IsNullOrWhiteSpace(request.Uid))
            {
                return ApiJson.Error("invalid_body", "'uid' is required", 400);
            }
            if (request.Version is { Length: 0 })
            {
                return ApiJson.Error("invalid_body", "'version' must not be empty", 400);
            }

            await runtime.InstanceLock.WaitAsync();
            try
            {
                if (LookupInstance(runtime, id) is not { } instance)
                {
                    return ApiJson.NotFound("instance_not_found", $"no launchable instance '{id}'");
                }
                ModPlatformTasks.EnsureProfileLoaded(instance);
                var profile = instance.GetPackProfile();

                // install = set/add the component version, important by default (the C++
                // loader-install flow marks forge/fabric as important so they cannot be
                // removed accidentally)
                if (!await profile.SetComponentVersion(request.Uid, request.Version ?? "",
                        important: request.Important ?? true))
                {
                    return ApiJson.Conflict("install_failed",
                        $"the version of '{request.Uid}' could not be installed (its custom patch could not be reverted)");
                }
                profile.SaveNow();
                var component = profile.GetComponent(request.Uid)!;
                return Results.Created(
                    $"/api/v1/instances/{Uri.EscapeDataString(id)}/components/{Uri.EscapeDataString(request.Uid)}",
                    ToView(component));
            }
            finally
            {
                runtime.InstanceLock.Release();
            }
        });

        // -------------------------------------------------------------- customize/revert
        api.MapPost("/instances/{id}/components/{uid}/customize", async (string id, string uid,
            LauncherRuntime runtime) =>
        {
            await runtime.InstanceLock.WaitAsync();
            try
            {
                if (LookupInstance(runtime, id) is not { } instance)
                {
                    return ApiJson.NotFound("instance_not_found", $"no launchable instance '{id}'");
                }
                ModPlatformTasks.EnsureProfileLoaded(instance);
                var profile = instance.GetPackProfile();
                var index = IndexOfUid(profile, uid);
                if (index < 0)
                {
                    return ApiJson.NotFound("component_not_found", $"no component '{uid}'");
                }
                if (!profile.Customize(index))
                {
                    return ApiJson.Conflict("not_customizable",
                        $"component '{uid}' cannot be customized (not loaded from metadata, or already customized)");
                }
                profile.SaveNow();
                return Results.NoContent();
            }
            finally
            {
                runtime.InstanceLock.Release();
            }
        });

        api.MapPost("/instances/{id}/components/{uid}/revert", async (string id, string uid,
            LauncherRuntime runtime) =>
        {
            await runtime.InstanceLock.WaitAsync();
            try
            {
                if (LookupInstance(runtime, id) is not { } instance)
                {
                    return ApiJson.NotFound("instance_not_found", $"no launchable instance '{id}'");
                }
                ModPlatformTasks.EnsureProfileLoaded(instance);
                var profile = instance.GetPackProfile();
                var index = IndexOfUid(profile, uid);
                if (index < 0)
                {
                    return ApiJson.NotFound("component_not_found", $"no component '{uid}'");
                }
                if (!profile.RevertToBase(index))
                {
                    return ApiJson.Conflict("not_revertible",
                        $"component '{uid}' cannot be reverted (no local patch, or no metadata to revert to)");
                }
                profile.SaveNow();
                return Results.NoContent();
            }
            finally
            {
                runtime.InstanceLock.Release();
            }
        });

        // ----------------------------------------------------------------------- remove
        api.MapDelete("/instances/{id}/components/{uid}", async (string id, string uid,
            LauncherRuntime runtime) =>
        {
            await runtime.InstanceLock.WaitAsync();
            try
            {
                if (LookupInstance(runtime, id) is not { } instance)
                {
                    return ApiJson.NotFound("instance_not_found", $"no launchable instance '{id}'");
                }
                ModPlatformTasks.EnsureProfileLoaded(instance);
                var profile = instance.GetPackProfile();
                if (profile.GetComponent(uid) is null)
                {
                    return ApiJson.NotFound("component_not_found", $"no component '{uid}'");
                }
                if (!profile.Remove(uid))
                {
                    return ApiJson.Conflict("not_removable",
                        $"component '{uid}' cannot be removed (marked important)");
                }
                profile.SaveNow();
                return Results.NoContent();
            }
            finally
            {
                runtime.InstanceLock.Release();
            }
        });

        // ------------------------------------------------------------------------- move
        api.MapPost("/instances/{id}/components/{uid}/move", async (string id, string uid,
            ComponentMoveRequest request, LauncherRuntime runtime) =>
        {
            if (request.Delta is not (-1 or 1))
            {
                return ApiJson.Error("invalid_body", "'delta' must be -1 (up) or 1 (down)", 400);
            }

            await runtime.InstanceLock.WaitAsync();
            try
            {
                if (LookupInstance(runtime, id) is not { } instance)
                {
                    return ApiJson.NotFound("instance_not_found", $"no launchable instance '{id}'");
                }
                ModPlatformTasks.EnsureProfileLoaded(instance);
                var profile = instance.GetPackProfile();
                var index = IndexOfUid(profile, uid);
                if (index < 0)
                {
                    return ApiJson.NotFound("component_not_found", $"no component '{uid}'");
                }
                // PackProfile.Move silently ignores non-moveable pairs (like the C++ void
                // Move()); the reorder result is observable through the next GET
                profile.Move(index, request.Delta == -1
                    ? PackProfile.MoveDirection.MoveUp
                    : PackProfile.MoveDirection.MoveDown);
                profile.SaveNow();
                return Results.NoContent();
            }
            finally
            {
                runtime.InstanceLock.Release();
            }
        });
    }

    // ----------------------------------------------------------------------- helpers

    /// <summary>The MinecraftInstance for a {ref} (the component stack needs the PackProfile).
    /// D2: every endpoint resolves through the addressing chain (GLOBAL §1).</summary>
    private static MinecraftInstance? LookupInstance(LauncherRuntime runtime, string reference) =>
        runtime.Instances.ResolveInstance(reference) is MinecraftInstance instance ? instance : null;

    private static int IndexOfUid(PackProfile profile, string uid)
    {
        for (var i = 0; i < profile.Components.Count; i++)
        {
            if (profile.Components[i].GetID() == uid)
            {
                return i;
            }
        }
        return -1;
    }

    private static ComponentView ToView(Component component)
    {
        // problems are only reported for components whose patch is actually loaded (the
        // unloaded metadata-backed components would all report "Patch is not loaded yet")
        ComponentProblemView[]? problems = null;
        if (component.Loaded)
        {
            var list = component.GetProblems();
            if (list.Count != 0)
            {
                problems = list.Select(p => new ComponentProblemView(
                    p.Severity switch
                    {
                        ProblemSeverity.Warning => "warning",
                        ProblemSeverity.Error => "error",
                        _ => "none",
                    },
                    p.Description)).ToArray();
            }
        }
        return new ComponentView(
            component.GetID(),
            component.GetVersion().Length != 0 ? component.GetVersion() : null,
            component.GetName(),
            component.Important,
            component.IsCustom(),
            component.IsEnabled(),
            problems);
    }
}
