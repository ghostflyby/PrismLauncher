// SPDX-License-Identifier: GPL-3.0-only
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
    public static void MapLaunch(this IEndpointRouteBuilder api)
    {
        api.MapPost("/instances/{id}/launch", async (string id, LaunchRequest request,
                                                     LauncherRuntime runtime,
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

            await runtime.InstanceLock.WaitAsync();
            try
            {
                var inst = runtime.Instances.GetInstanceById(id);
                if (inst is null)
                {
                    return ApiJson.NotFound("instance_not_found", $"no instance '{id}'");
                }
                if (inst is not MinecraftInstance instance)
                {
                    return ApiJson.Conflict("not_launchable",
                        $"instance '{id}' is of type '{inst.InstanceType()}' and cannot be launched");
                }
                // core CanLaunch: refuses broken versions and already-running instances —
                // this also guards against two concurrent API launches of the same instance
                // (creating a LaunchTask marks the instance running).
                if (!instance.CanLaunch())
                {
                    return ApiJson.Conflict("instance_unavailable",
                        instance.HasVersionBroken
                            ? $"instance '{id}' has a broken version and cannot be launched"
                            : $"instance '{id}' is already running or launching");
                }

                MinecraftAccount? account = null;
                if (request.AccountProfileId is { } profileId)
                {
                    var index = runtime.Accounts.FindAccountByProfileId(profileId);
                    if (index == -1)
                    {
                        return ApiJson.Error("unknown_account", $"no account with profile id '{profileId}'", 400);
                    }
                    account = runtime.Accounts.At(index);
                }

                var controller = new LaunchController
                {
                    Instance = instance,
                    WantedLaunchMode = mode.Value,
                    OfflineName = request.OfflineName ?? "",
                    AccountToUse = account,
                    AccountsProvider = () => runtime.Accounts,
                    AutoProceed = false,
                };
                // static hook (C++ APPLICATION->settings()); set per built app in the tests
                LaunchController.GlobalSettingsProvider = () => runtime.GlobalSettings;

                var entry = registry.Register("launch", controller, instanceId: inst.Id(), controller: controller);
                _ = controller.StartAsync();
                return Results.Accepted($"/api/v1/tasks/{entry.Id}", new TaskAcceptedResponse(entry.Id));
            }
            finally
            {
                runtime.InstanceLock.Release();
            }
        });
    }
}
