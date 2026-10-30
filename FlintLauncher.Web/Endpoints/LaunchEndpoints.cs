// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Launch endpoint: builds a headless LaunchController (AutoProceed=false — the client
// drives the ReadyForLaunch gate through POST /tasks/{id}/proceed) and hands the tracked
// task id back (202).

using FlintLauncher.Core.Auth;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Launch;
using FlintLauncher.Web.Models;
using FlintLauncher.Web.Services;

namespace FlintLauncher.Web.Endpoints;

public static class LaunchEndpoints
{
    public static void MapLaunch(this RouteGroupBuilder api)
    {
        api.WithTags("Launch");
        api.MapPost("/instances/{ref}/launch", async (string @ref, LaunchRequest request,
            InstanceList instances,
            InstanceMutationLock instanceLock,
            AccountList accounts,
            LaunchServices services,
            FlintTaskRegistry registry) =>
        {
            var mode = (request.Mode ?? "normal").ToLowerInvariant() switch
            {
                "normal" => LaunchMode.Normal,
                "offline" => LaunchMode.Offline,
                "demo" => LaunchMode.Demo,
                _ => (LaunchMode?)null,
            };
            if (mode is null)
            {
                return ApiJson.Error("invalid_mode", "'mode' must be normal, offline or demo", 400);
            }

            // direct join target (LaunchController.setTargetToJoin): bypasses the instance
            // JoinServerOnLaunch setting; 'server' and 'world' are mutually exclusive
            MinecraftTarget? targetToJoin = null;
            if (request.Server is not null && !string.IsNullOrWhiteSpace(request.World))
            {
                return ApiJson.Error("invalid_request",
                    "'server' and 'world' are mutually exclusive join targets", 400);
            }

            if (request.Server is not null)
            {
                if (string.IsNullOrWhiteSpace(request.Server.Address))
                {
                    return ApiJson.Error("invalid_request", "'server.address' is required", 400);
                }

                if (request.Server.Port is { } and (< 0 or > 65535))
                {
                    return ApiJson.Error("invalid_request", "'server.port' must be 0-65535", 400);
                }

                // MinecraftTarget.Parse(fullAddress): "host:port" parsing with the 25565 default
                targetToJoin = MinecraftTarget.Parse(
                    request.Server.Port is { } p ? $"{request.Server.Address}:{p}" : request.Server.Address,
                    useWorld: false);
            }
            else if (!string.IsNullOrWhiteSpace(request.World))
            {
                // a world join target carries only the world name (port 0, like the C++)
                targetToJoin = MinecraftTarget.Parse(request.World, useWorld: true);
            }

            await instanceLock.WaitAsync();
            try
            {
                var inst = instances.ResolveInstance(@ref); // the addressing chain
                if (inst is null)
                {
                    return ApiJson.NotFound("instance_not_found", $"no instance '{@ref}'");
                }

                if (inst is not MinecraftInstance instance)
                {
                    return ApiJson.Conflict("not_launchable",
                        $"instance '{@ref}' is of type '{inst.InstanceType()}' and cannot be launched");
                }

                // core CanLaunch: refuses broken versions and already-running instances —
                // this also guards against two concurrent API launches of the same instance
                // (creating a LaunchTask marks the instance running).
                if (!instance.CanLaunch())
                {
                    return ApiJson.Conflict("instance_unavailable",
                        instance.HasVersionBroken
                            ? $"instance '{@ref}' has a broken version and cannot be launched"
                            : $"instance '{@ref}' is already running or launching");
                }

                MinecraftAccount? account = null;
                if (request.AccountProfileId is { } profileId)
                {
                    var index = accounts.FindAccountByProfileId(profileId);
                    if (index == -1)
                    {
                        return ApiJson.Error("unknown_account", $"no account with profile id '{profileId}'", 400);
                    }

                    account = accounts.At(index);
                }

                var controller = new LaunchController
                {
                    Instance = instance,
                    WantedLaunchMode = mode.Value,
                    OfflineName = request.OfflineName ?? "",
                    AccountToUse = account,
                    AccountsProvider = () => accounts,
                    AutoProceed = false,
                    TargetToJoin = targetToJoin,
                    // injected launch dependencies (C++ APPLICATION->settings()/logger),
                    // built by the composition root (WebHostBuilder) for this app
                    Services = services,
                };

                // claim the instance before the work can start and finish: the launch runs off
                // the request thread, and a second request in between would otherwise pass
                // CanLaunch. A launch that fails immediately resets the flag itself.
                instance.SetRunning(true);
                var entry = registry.Start("launch",
                    handle => handle.RunAsync(token => controller.RunAsync(token)),
                    instanceId: inst.Id(),
                    controller: controller);
                return Results.Accepted($"/api/v1/tasks/{entry.Id}", new TaskAcceptedResponse(entry.Id));
            }
            finally
            {
                instanceLock.Release();
            }
        }).Produces<TaskAcceptedResponse>(202);
    }
}
