// SPDX-License-Identifier: GPL-3.0-only
// Modplatform endpoints (phase P6-fix-b): the CurseForge search pass-through, the per-provider
// indexed version lists, and the per-instance mods surface (list / tracked install / tracked
// update). The install/update flows run as registered tasks (202 + taskId) like launch/import;
// the update task additionally publishes its found updates through the task view's optional
// result payload.
using System.Text.Json;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.ModPlatform;
using FlintLauncher.Core.ModPlatform.Flame;
using FlintLauncher.Core.ModPlatform.Modrinth;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Versioning;
using FlintLauncher.Web.Models;
using FlintLauncher.Web.Services;

namespace FlintLauncher.Web.Endpoints;

public static class ModPlatformEndpoints
{
    public static void MapModPlatform(this IEndpointRouteBuilder api)
    {
        // ------------------------------------------------------ modplatform/flame
        api.MapGet("/modplatform/flame/search", async Task<IResult> (
            LauncherRuntime runtime,
            string? query,
            string? version,
            string? loader,
            string? index,
            int offset = 0) =>
        {
            var loaders = string.IsNullOrWhiteSpace(loader)
                ? (ModLoaderType?)null
                : ModIndex.GetModLoaderFromString(loader.ToLowerInvariant());
            // the CurseForge search URL builder never rejects loaders (unlike the Modrinth
            // one), so validate the parsed loader against Flame's supported set here
            if (loaders is { } parsed && parsed != 0 && !FlameApi.ValidateModLoaders(parsed))
            {
                return ApiJson.Error("invalid_loader", $"loader '{loader}' is not supported by CurseForge", 400);
            }

            var args = new SearchArgs
            {
                Type = ResourceType.Mod,
                Offset = offset < 0 ? 0 : offset,
                Search = string.IsNullOrWhiteSpace(query) ? null : query,
                Versions = string.IsNullOrWhiteSpace(version) ? null : [new McVersion(version)],
                Loaders = loaders,
                Sorting = FlameSorting(index),
            };

            var url = FlameApi.Instance.GetSearchURL(args);
            if (url is null)
            {
                return ApiJson.Error("invalid_loader", $"loader '{loader}' is not supported by CurseForge", 400);
            }

            // pass-through of the core CurseForge search: run the net request and map the hits
            // (the x-api-key header is attached through the FlameConfig provider hook)
            var request = NetRequest.MakeByteArray(new Uri(url));
            FlameApi.AddApiKeyHeader(request);
            await request.StartAsync();
            if (request.State != Core.Tasks.TaskState.Succeeded)
            {
                return ApiJson.Error("flame_unavailable",
                    $"curseforge search failed: {request.FailReason}", 502);
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(request.BodyAsString);
            }
            catch (JsonException)
            {
                return ApiJson.Error("flame_invalid_response", "curseforge returned invalid JSON", 502);
            }

            using (doc)
            {
                var results = new List<FlameSearchResult>();
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("data", out var hits) &&
                    hits.ValueKind == JsonValueKind.Array)
                {
                    foreach (var hit in hits.EnumerateArray())
                    {
                        if (hit.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }
                        // map the hit through the core loader, like the C++ search page does
                        var pack = new IndexedPack();
                        try
                        {
                            FlameMod.LoadIndexedPack(pack, hit);
                        }
                        catch (FlintLauncher.Core.Json.JsonParseException)
                        {
                            continue;
                        }
                        results.Add(new FlameSearchResult(
                            pack.AddonId,
                            pack.Slug,
                            pack.Name,
                            pack.LogoUrl,
                            pack.Authors.Count != 0 ? pack.Authors[0].Name : "",
                            Downloads(hit)));
                    }
                }
                return Results.Ok(results.ToArray());
            }
        });

        // ------------------------------------------------ modplatform/{provider}/versions
        api.MapGet("/modplatform/{provider}/versions", async Task<IResult> (
            string provider,
            string? projectId) =>
        {
            if (string.IsNullOrWhiteSpace(projectId))
            {
                return ApiJson.Error("invalid_request", "'projectId' is required", 400);
            }
            if (!ModPlatformTasks.TryParseProvider(provider, out var parsedProvider))
            {
                return ApiJson.Error("unknown_provider", "provider must be 'modrinth' or 'flame'", 400);
            }

            var url = parsedProvider == ResourceProvider.FLAME
                ? FlameApi.Instance.GetVersionsURL(new VersionSearchArgs
                {
                    Pack = new IndexedPack { AddonId = projectId, Provider = ResourceProvider.FLAME },
                })
                : ModrinthApi.Instance.GetVersionsURL(new VersionSearchArgs
                {
                    Pack = new IndexedPack { AddonId = projectId, Provider = ResourceProvider.MODRINTH },
                });
            if (url is null)
            {
                return ApiJson.Error("invalid_request", $"cannot build the version-list request for '{projectId}'", 400);
            }

            var request = NetRequest.MakeByteArray(new Uri(url));
            // the provider header hooks (Flame x-api-key / Modrinth PAT) mirror the request
            // builders the core API methods use
            if (parsedProvider == ResourceProvider.FLAME)
            {
                FlameApi.AddApiKeyHeader(request);
            }
            else
            {
                ModrinthApi.AddApiHeaders(request);
            }
            await request.StartAsync();
            if (request.State != Core.Tasks.TaskState.Succeeded)
            {
                return ApiJson.Error($"{parsedProvider.ToString().ToLowerInvariant()}_unavailable",
                    $"{provider} version list failed: {request.FailReason}", 502);
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(request.BodyAsString);
            }
            catch (JsonException)
            {
                return ApiJson.Error($"{parsedProvider.ToString().ToLowerInvariant()}_invalid_response",
                    $"{provider} returned invalid JSON", 502);
            }

            using (doc)
            {
                var pack = new IndexedPack { AddonId = projectId, Provider = parsedProvider };
                try
                {
                    if (parsedProvider == ResourceProvider.FLAME)
                    {
                        if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                            doc.RootElement.TryGetProperty("data", out var data) &&
                            data.ValueKind == JsonValueKind.Array)
                        {
                            // loads + validates every file, sorted newest first
                            FlameMod.LoadIndexedPackVersions(pack, data);
                        }
                    }
                    else if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var entry in doc.RootElement.EnumerateArray())
                        {
                            if (entry.ValueKind != JsonValueKind.Object)
                            {
                                continue;
                            }
                            try
                            {
                                var version = ModrinthPackIndex.LoadIndexedPackVersion(entry);
                                if (version.FileId.Length != 0)
                                {
                                    pack.Versions.Add(version);
                                }
                            }
                            catch (FlintLauncher.Core.Json.JsonParseException)
                            {
                                // skip malformed entries, like the dependency resolver
                            }
                        }
                    }
                }
                catch (FlintLauncher.Core.Json.JsonParseException e)
                {
                    return ApiJson.Error($"{parsedProvider.ToString().ToLowerInvariant()}_invalid_response",
                        $"{provider} version list is invalid: {e.Message}", 502);
                }

                return Results.Ok(pack.Versions.Select(VersionView).ToArray());
            }
        });

        // ------------------------------------------------------ instances/{id}/mods
        api.MapGet("/instances/{id}/mods", async (string id, LauncherRuntime runtime) =>
        {
            await runtime.InstanceLock.WaitAsync();
            try
            {
                if (LookupInstance(runtime, id) is not { } instance)
                {
                    return ApiJson.NotFound("instance_not_found", $"no launchable instance '{id}'");
                }

                var folder = instance.LoaderModList();
                folder.Update();
                var mods = folder.AllMods()
                    .Where(mod => !mod.Fileinfo.Name.StartsWith('.'))
                    .Select(mod =>
                    {
                        var metadata = folder.GetMetadata(mod);
                        var fileName = mod.Fileinfo.Name;
                        var name = metadata is { } meta && meta.Name.Length != 0
                            ? meta.Name
                            : Path.GetFileNameWithoutExtension(fileName);
                        return new InstalledModView(
                            fileName,
                            name,
                            metadata is { } m && m.VersionNumber.Length != 0 ? m.VersionNumber : null,
                            metadata is { } provider ? ModPlatformTasks.ProviderKey(provider.Provider) : null,
                            mod.Enabled);
                    })
                    .ToArray();
                return Results.Ok(mods);
            }
            finally
            {
                runtime.InstanceLock.Release();
            }
        });

        api.MapPost("/instances/{id}/mods/install", async (string id, List<ModInstallSelection>? selections,
            LauncherRuntime runtime, FlintTaskRegistry registry) =>
        {
            if (selections is null || selections.Count == 0)
            {
                return ApiJson.Error("invalid_body", "body must be a non-empty list of {provider, projectId}", 400);
            }

            var requests = new List<ModInstallRequest>();
            foreach (var selection in selections)
            {
                if (!ModPlatformTasks.TryParseProvider(selection.Provider, out var provider))
                {
                    return ApiJson.Error("unknown_provider",
                        $"provider '{selection.Provider}' must be 'modrinth' or 'flame'", 400);
                }
                if (string.IsNullOrWhiteSpace(selection.ProjectId))
                {
                    return ApiJson.Error("invalid_body", "'projectId' is required for every selection", 400);
                }
                requests.Add(new ModInstallRequest(provider, selection.ProjectId, selection.VersionId ?? ""));
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
                var mcVersion = profile.GetComponentVersion("net.minecraft");
                if (mcVersion.Length == 0)
                {
                    return ApiJson.Error("instance_unresolved",
                        $"instance '{id}' has no resolved minecraft version; its components must be resolved first", 409);
                }

                var task = new ModInstallTask(instance, instance.LoaderModList(), requests,
                    mcVersion, profile.GetSupportedModLoaders() ?? ModLoaderType.None);
                var entry = registry.Register("mods-install", task, instanceId: instance.Id());
                // the result payload lists the installed entries (selected + dependencies);
                // published on terminal events so a download failure still shows what landed
                task.Succeeded += _ => entry.Result = task.BuildResult();
                task.Failed += (_, _) => entry.Result = task.BuildResult();
                _ = task.StartAsync();
                return Results.Accepted($"/api/v1/tasks/{entry.Id}", new TaskAcceptedResponse(entry.Id));
            }
            finally
            {
                runtime.InstanceLock.Release();
            }
        });

        api.MapPost("/instances/{id}/mods/update", async (string id, ModsUpdateRequest? filter,
            LauncherRuntime runtime, FlintTaskRegistry registry) =>
        {
            await runtime.InstanceLock.WaitAsync();
            try
            {
                if (LookupInstance(runtime, id) is not { } instance)
                {
                    return ApiJson.NotFound("instance_not_found", $"no launchable instance '{id}'");
                }

                ModPlatformTasks.EnsureProfileLoaded(instance);

                var task = new ModsUpdateTask(instance, instance.LoaderModList(), filter?.Mods);
                var entry = registry.Register("mods-update", task, instanceId: instance.Id());
                // the result payload carries the found/applied updates; it is published once
                // the task reaches a terminal state (the check pass fills it before applying)
                task.Succeeded += _ => entry.Result = task.BuildResult();
                task.Failed += (_, _) => entry.Result = task.BuildResult();
                _ = task.StartAsync();
                return Results.Accepted($"/api/v1/tasks/{entry.Id}", new TaskAcceptedResponse(entry.Id));
            }
            finally
            {
                runtime.InstanceLock.Release();
            }
        });
    }

    // ----------------------------------------------------------------- helpers

    /// <summary>The MinecraftInstance for an id (null when missing or not a Minecraft
    /// instance — the mods surface only exists for real instances, like launch).</summary>
    private static MinecraftInstance? LookupInstance(LauncherRuntime runtime, string id) =>
        runtime.Instances.GetInstanceById(id) is MinecraftInstance instance ? instance : null;

    /// <summary>The Flame sorting method by (case-insensitive) name, defaulting to Popularity
    /// (the CurseForge API's default sortField).</summary>
    private static SortingMethod FlameSorting(string? index)
    {
        var methods = FlameApi.Instance.GetSortingMethods();
        if (!string.IsNullOrWhiteSpace(index))
        {
            foreach (var method in methods)
            {
                if (string.Equals(method.Name, index, StringComparison.OrdinalIgnoreCase))
                {
                    return method;
                }
            }
        }
        return methods.FirstOrDefault(m => m.Name == "Popularity",
            new SortingMethod { Index = 2, Name = "Popularity", ReadableName = "Sort by Popularity" });
    }

    /// <summary>The optional hit download count (FlameMod does not load it; read it raw).</summary>
    private static long? Downloads(JsonElement hit)
    {
        foreach (var key in new[] { "downloadCount", "downloads" })
        {
            if (hit.ValueKind == JsonValueKind.Object &&
                hit.TryGetProperty(key, out var value) &&
                value.ValueKind == JsonValueKind.Number)
            {
                return value.GetInt64();
            }
        }
        return null;
    }

    private static ProviderVersionView VersionView(IndexedVersion version) => new(
        version.FileId,
        // CurseForge has no version numbers — fall back to the display name (the
        // Packwiz::createModFormat rule)
        version.VersionNumber.Length != 0 ? version.VersionNumber : version.Version,
        version.Date,
        version.FileName,
        version.DownloadUrl,
        ModIndex.ModLoaderTypesToList(version.Loaders).Select(ModIndex.GetModLoaderAsString).ToArray(),
        version.McVersion.Distinct().ToArray());
}
