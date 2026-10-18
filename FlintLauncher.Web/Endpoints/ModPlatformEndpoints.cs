// SPDX-License-Identifier: GPL-3.0-only
// Modplatform endpoints (phase P6-fix-b + UI-API mapping Wave B): the merged per-provider
// search pass-through (provider = modrinth|flame), the per-provider indexed version lists,
// and the per-instance mods surface (list / tracked install / tracked update plus the
// enable/disable/uninstall renames). The install/update flows run as registered tasks
// (202 + taskId) like launch/import; the update task additionally publishes its found
// updates through the task view's optional result payload.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Json;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.ModPlatform;
using FlintLauncher.Core.ModPlatform.Flame;
using FlintLauncher.Core.ModPlatform.Modrinth;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Tasks;
using FlintLauncher.Core.Versioning;
using FlintLauncher.Web.Models;
using FlintLauncher.Web.Services;

namespace FlintLauncher.Web.Endpoints;

public static class ModPlatformEndpoints
{
    public static void MapModPlatform(this RouteGroupBuilder api)
    {
        api.WithTags("ModPlatform");
        // ------------------------------------------------ modplatform/atlauncher/*
        // the ATLauncher surface (packsnew.json pass-through + staged pack install)
        api.MapAtLauncher();

        // -------------------------------------------------------- modplatform/ftb/*
        // the FTB App API surface (/modpack/all + /modpack/{id} pass-throughs + staged
        // pack install)
        api.MapFtb();

        // ---------------------------------------------------- modplatform/technic/*
        // the Technic Platform surface (search/trending pass-through, pack metadata with
        // solder detection + staged zip/Solder pack install)
        api.MapTechnic();

        // ------------------------------------------------ modplatform/{provider}/search
        // the merged search route of the two former /modplatform/{modrinth,flame}/search
        // endpoints; each provider keeps its own header hooks and hit mapping. The `type`
        // query param (default mods, back-compat) selects the searched resource type.
        api.MapGet("/modplatform/{provider}/search", async Task<IResult> (
            string provider,
            string? query,
            string? version,
            string? loader,
            string? index,
            string? type,
            LauncherRuntime runtime,
            int offset = 0) =>
        {
            if (!ModPlatformTasks.TryParseProvider(provider, out var parsedProvider))
            {
                return ApiJson.Error("unknown_provider", "provider must be 'modrinth' or 'flame'", 400);
            }

            if (!TryParseSearchType(type, out var resourceType, out var typeError))
            {
                return ApiJson.Error("invalid_type", typeError!, 400);
            }

            resourceType = NormalizeSearchType(resourceType, parsedProvider, out var unsupportedReason);
            if (unsupportedReason is not null)
            {
                return ApiJson.Error("invalid_type", unsupportedReason, 400);
            }

            return parsedProvider == ResourceProvider.Flame
                ? await FlameSearchAsync(resourceType, query, version, loader, index, offset, runtime)
                : await ModrinthSearchAsync(resourceType, query, version, loader, index, offset, runtime);
        }).Produces<FlameSearchResult[]>().Produces<ModrinthSearchResult[]>();

        // ----------------------------------------------------- modplatform/{provider}/categories
        // GET /categories?gameId=432&classId=... (Flame) or GET /tag/category filtered by
        // project_type (Modrinth), mapped to [{id, name, icon}]. An explicit numeric classId
        // wins over `type` on Flame (search hits carry the raw CurseForge class id).
        api.MapGet("/modplatform/{provider}/categories", async Task<IResult> (
            string provider, string? type, string? classId, LauncherRuntime runtime) =>
        {
            if (!ModPlatformTasks.TryParseProvider(provider, out var parsedProvider))
            {
                return ApiJson.Error("unknown_provider", "provider must be 'modrinth' or 'flame'", 400);
            }

            if (parsedProvider == ResourceProvider.Flame)
            {
                int effectiveClassId;
                if (!string.IsNullOrWhiteSpace(classId))
                {
                    if (!int.TryParse(classId, out effectiveClassId))
                    {
                        return ApiJson.Error("invalid_request", "'classId' must be a number", 400);
                    }
                }
                else
                {
                    if (!TryParseSearchType(type, out var resourceType, out var typeError))
                    {
                        return ApiJson.Error("invalid_type", typeError!, 400);
                    }

                    resourceType = NormalizeSearchType(resourceType, parsedProvider, out var unsupported);
                    if (unsupported is not null)
                    {
                        return ApiJson.Error("invalid_type", unsupported, 400);
                    }

                    effectiveClassId = FlameApi.GetClassId(resourceType);
                }

                var (_, flameRequest) = runtime.Flame.GetCategoriesForClassId(effectiveClassId);
                runtime.Flame.AddApiKeyHeader(flameRequest);
                await flameRequest.StartAsync();
                if (flameRequest.State != TaskState.Succeeded)
                {
                    return ApiJson.Error("flame_unavailable",
                        $"curseforge categories failed: {flameRequest.FailReason}", 502);
                }

                return Results.Ok(ParseFlameCategories(flameRequest.BodyAsString).ToArray());
            }

            if (!TryParseSearchType(type, out var modrinthType, out var modrinthTypeError))
            {
                return ApiJson.Error("invalid_type", modrinthTypeError!, 400);
            }

            var projectType = ModrinthProjectType(modrinthType, out var unsupportedType);
            if (unsupportedType is not null)
            {
                return ApiJson.Error("invalid_type", unsupportedType, 400);
            }

            var (_, modrinthRequest) = runtime.Modrinth.GetModCategories();
            await modrinthRequest.StartAsync();
            if (modrinthRequest.State != TaskState.Succeeded)
            {
                return ApiJson.Error("modrinth_unavailable",
                    $"modrinth categories failed: {modrinthRequest.FailReason}", 502);
            }

            return Results.Ok(ParseModrinthCategories(modrinthRequest.BodyAsString, projectType).ToArray());
        }).Produces<ProviderCategoryView[]>();

        // --------------------------------------------------- modplatform/{provider}/mods/{projectId}/description
        api.MapGet("/modplatform/{provider}/mods/{projectId}/description",
            async Task<IResult> (string provider, string projectId, LauncherRuntime runtime) =>
            {
                if (!ModPlatformTasks.TryParseProvider(provider, out var parsedProvider))
                {
                    return ApiJson.Error("unknown_provider", "provider must be 'modrinth' or 'flame'", 400);
                }

                string body;
                if (parsedProvider == ResourceProvider.Flame)
                {
                    if (!int.TryParse(projectId, out var modId))
                    {
                        return ApiJson.Error("invalid_request", "'projectId' must be a CurseForge project id", 400);
                    }

                    body = await runtime.Flame.GetModDescription(modId);
                }
                else
                {
                    // the Modrinth body endpoint answers with plain text
                    body = await runtime.Modrinth.GetProjectDescription(projectId);
                }

                return Results.Ok(new ProviderBodyView(body));
            }).Produces<ProviderBodyView>();

        // ------------------------------------------------ modplatform/{provider}/mods/{projectId}/changelog
        api.MapGet("/modplatform/{provider}/mods/{projectId}/changelog",
            async Task<IResult> (string provider, string projectId, string? fileId,
                LauncherRuntime runtime) =>
            {
                if (!ModPlatformTasks.TryParseProvider(provider, out var parsedProvider))
                {
                    return ApiJson.Error("unknown_provider", "provider must be 'modrinth' or 'flame'", 400);
                }

                if (parsedProvider != ResourceProvider.Flame)
                {
                    // Modrinth has no per-file changelog endpoint (changelogs ride on versions)
                    return ApiJson.Error("invalid_request",
                        "'changelog' is only supported by CurseForge", 400);
                }

                if (string.IsNullOrWhiteSpace(fileId) || !int.TryParse(fileId, out var fileIdNum))
                {
                    return ApiJson.Error("invalid_request", "'fileId' is required", 400);
                }

                if (!int.TryParse(projectId, out var modId))
                {
                    return ApiJson.Error("invalid_request", "'projectId' must be a CurseForge project id", 400);
                }

                var body = await runtime.Flame.GetModFileChangelog(modId, fileIdNum);
                return Results.Ok(new ProviderBodyView(body));
            }).Produces<ProviderBodyView>();

        // ------------------------------------------------ modplatform/{provider}/versions
        api.MapGet("/modplatform/{provider}/versions", async Task<IResult> (
            string provider,
            string? projectId, LauncherRuntime runtime) =>
        {
            if (string.IsNullOrWhiteSpace(projectId))
            {
                return ApiJson.Error("invalid_request", "'projectId' is required", 400);
            }

            if (!ModPlatformTasks.TryParseProvider(provider, out var parsedProvider))
            {
                return ApiJson.Error("unknown_provider", "provider must be 'modrinth' or 'flame'", 400);
            }

            var url = parsedProvider == ResourceProvider.Flame
                ? runtime.Flame.GetVersionsUrl(new VersionSearchArgs
                {
                    Pack = new IndexedPack { AddonId = projectId, Provider = ResourceProvider.Flame },
                })
                : runtime.Modrinth.GetVersionsUrl(new VersionSearchArgs
                {
                    Pack = new IndexedPack { AddonId = projectId, Provider = ResourceProvider.Modrinth },
                });

            var request = NetRequest.MakeByteArray(new Uri(url));
            // the provider header hooks (Flame x-api-key / Modrinth PAT) mirror the request
            // builders the core API methods use
            if (parsedProvider == ResourceProvider.Flame)
            {
                runtime.Flame.AddApiKeyHeader(request);
            }
            else
            {
                runtime.Modrinth.AddApiHeaders(request);
            }

            await request.StartAsync();
            if (request.State != TaskState.Succeeded)
            {
                return ApiJson.Error($"{parsedProvider.ToString().ToLowerInvariant()}_unavailable",
                    $"{provider} version list failed: {request.FailReason}", 502);
            }

            JsonNode? bodyRoot;
            try
            {
                bodyRoot = JsonNode.Parse(request.BodyAsString);
            }
            catch (JsonException)
            {
                return ApiJson.Error($"{parsedProvider.ToString().ToLowerInvariant()}_invalid_response",
                    $"{provider} returned invalid JSON", 502);
            }

            {
                var pack = new IndexedPack { AddonId = projectId, Provider = parsedProvider };
                try
                {
                    if (parsedProvider == ResourceProvider.Flame)
                    {
                        // loads + validates every file, sorted newest first (a broken body
                        // surfaces as the invalid-response error below)
                        FlameMod.LoadIndexedPackVersionsFromResponse(pack, request.BodyAsString);
                    }
                    else if (ModrinthJson.VersionListFromNode(bodyRoot) is { } versionDtos)
                    {
                        foreach (var dto in versionDtos)
                        {
                            try
                            {
                                var version = ModrinthPackIndex.LoadIndexedPackVersion(dto);
                                if (version.FileId.Length != 0)
                                {
                                    pack.Versions.Add(version);
                                }
                            }
                            catch (JsonParseException)
                            {
                                // skip malformed entries, like the dependency resolver
                            }
                        }
                    }
                }
                catch (JsonParseException e)
                {
                    return ApiJson.Error($"{parsedProvider.ToString().ToLowerInvariant()}_invalid_response",
                        $"{provider} version list is invalid: {e.Message}", 502);
                }

                return Results.Ok(pack.Versions.Select(VersionView).ToArray());
            }
        }).Produces<ProviderVersionView[]>();

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
        }).Produces<InstalledModView[]>();

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
                    mcVersion, profile.GetSupportedModLoaders() ?? ModLoaderType.None, runtime.Modrinth, runtime.Flame);
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
        }).Produces<TaskAcceptedResponse>(202);

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

                // release channels (#3404): the request's explicit list wins; otherwise the
                // global ModUpdateReleaseTypes setting (the ResourceUpdateDialog fallback) —
                // read from the typed user layer (C5/D4)
                IReadOnlyList<IndexedVersionType> releaseTypes =
                    filter?.ReleaseTypes is { Count: > 0 } requested
                        ? IndexedVersionType.FromStringList(requested)
                        : IndexedVersionType.FromStringList(JsonUtils.ToStringList(
                            runtime.Config.Current.ModDownload.ModUpdateReleaseTypes ?? "[]"));

                var task = new ModsUpdateTask(instance, instance.LoaderModList(), filter?.Mods, releaseTypes, runtime.Modrinth, runtime.Flame);
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
        }).Produces<TaskAcceptedResponse>(202);
    }

    // ----------------------------------------------------------------- helpers

    /// <summary>
    /// The search `type` query param → the ResourceType wired into SearchArgs.Type (the
    /// value set mirrors the C++ resource-type search surfaces; default mods/back-compat):
    /// mods | resourcepacks | texturepacks | shaderpacks | datapacks | modpacks | worlds.
    /// </summary>
    private static bool TryParseSearchType(string? type, out ResourceType resourceType, out string? error)
    {
        resourceType = ResourceType.Mod;
        error = null;
        if (string.IsNullOrWhiteSpace(type))
        {
            return true; // default mods, like the C++ mod search pages
        }

        resourceType = type.Trim().ToLowerInvariant() switch
        {
            "mods" => ResourceType.Mod,
            "resourcepacks" => ResourceType.ResourcePack,
            "texturepacks" => ResourceType.TexturePack,
            "shaderpacks" => ResourceType.ShaderPack,
            "datapacks" => ResourceType.DataPack,
            "modpacks" => ResourceType.Modpack,
            "worlds" => ResourceType.World,
            _ => ResourceType.Unknown,
        };
        if (resourceType == ResourceType.Unknown)
        {
            error = $"type '{type}' must be one of mods, resourcepacks, texturepacks, shaderpacks, datapacks, modpacks, worlds";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Provider-specific search type resolution (mirrors the C++ search URL builders):
    /// texture packs share the resource-pack class/type on both providers (the C++
    /// texture-pack models derive from the resource-pack ones), and Modrinth has no world
    /// project type (the facet builder would emit an empty project_type).
    /// </summary>
    private static ResourceType NormalizeSearchType(ResourceType type, ResourceProvider provider, out string? error)
    {
        error = null;
        if (type == ResourceType.TexturePack)
        {
            return ResourceType.ResourcePack;
        }

        if (provider == ResourceProvider.Modrinth && type == ResourceType.World)
        {
            error = "type 'worlds' is not supported by modrinth";
        }

        return type;
    }

    /// <summary>The Modrinth project_type facet value for a search type ("" = unsupported;
    /// the C++ resourceTypeParameter map is mod/resourcepack/shader/datapack/modpack).</summary>
    private static string ModrinthProjectType(ResourceType type, out string? error)
    {
        error = null;
        if (type == ResourceType.TexturePack)
        {
            type = ResourceType.ResourcePack; // normalized like the search pass
        }

        var projectType = type switch
        {
            ResourceType.Mod => "mod",
            ResourceType.ResourcePack => "resourcepack",
            ResourceType.ShaderPack => "shader",
            ResourceType.DataPack => "datapack",
            ResourceType.Modpack => "modpack",
            _ => "",
        };
        if (projectType.Length == 0)
        {
            error = $"type '{type}' is not supported by modrinth";
        }

        return projectType;
    }

    /// <summary>The Flame /categories response mapped to [{id, name, icon}] (missing icons
    /// become "").</summary>
    private static List<ProviderCategoryView> ParseFlameCategories(string response)
    {
        var result = new List<ProviderCategoryView>();
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(response);
        }
        catch (JsonException)
        {
            return result;
        }

        if (root is JsonObject obj && obj["data"] is JsonArray data)
        {
            foreach (var entry in data)
            {
                if (entry is not JsonObject entryObj)
                {
                    continue;
                }

                var id = entryObj["id"] is JsonValue idValue &&
                         idValue.ToJsonString() is var idRaw &&
                         idRaw.Length != 0 && idRaw[0] != '"' && // numbers only (GetInt64 upstream)
                         long.TryParse(idRaw, CultureInfo.InvariantCulture, out var idNumber)
                    ? idNumber.ToString(CultureInfo.InvariantCulture)
                    : "";
                result.Add(new ProviderCategoryView(id, Str(entryObj, "name"), Str(entryObj, "icon")));
            }
        }

        return result;
    }

    /// <summary>The Modrinth /tag/category response filtered by project_type and mapped to
    /// [{id, name, icon}] (the Modrinth category id is its name, like LoadCategories).</summary>
    private static List<ProviderCategoryView> ParseModrinthCategories(string response, string projectType)
    {
        var result = new List<ProviderCategoryView>();
        var entries = ModrinthJson.ParseCategories(response);
        if (entries is null)
        {
            return result;
        }

        foreach (var entry in entries)
        {
            if (entry.ProjectType != projectType)
            {
                continue;
            }

            var name = entry.Name ?? "";
            result.Add(new ProviderCategoryView(name, name, entry.Icon ?? ""));
        }

        return result;
    }

    /// <summary>The CurseForge search pass-through (the x-api-key header is attached through
    /// the FlameConfig provider hook; hits map through FlameMod.LoadIndexedPack).</summary>
    private static async Task<IResult> FlameSearchAsync(ResourceType resourceType, string? query, string? version,
        string? loader, string? index, int offset, LauncherRuntime runtime)
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
            Type = resourceType,
            Offset = offset < 0 ? 0 : offset,
            Search = string.IsNullOrWhiteSpace(query) ? null : query,
            Versions = string.IsNullOrWhiteSpace(version) ? null : [new McVersion(version)],
            Loaders = loaders,
            Sorting = FlameSorting(index, runtime),
        };

        var url = runtime.Flame.GetSearchUrl(args);

        var request = NetRequest.MakeByteArray(new Uri(url));
        runtime.Flame.AddApiKeyHeader(request);
        await request.StartAsync();
        if (request.State != TaskState.Succeeded)
        {
            return ApiJson.Error("flame_unavailable",
                $"curseforge search failed: {request.FailReason}", 502);
        }

        JsonNode? bodyRoot;
        try
        {
            bodyRoot = JsonNode.Parse(request.BodyAsString);
        }
        catch (JsonException)
        {
            return ApiJson.Error("flame_invalid_response", "curseforge returned invalid JSON", 502);
        }

        {
            var results = new List<FlameSearchResult>();
            if (bodyRoot is JsonObject obj && obj["data"] is JsonArray hits)
            {
                foreach (var hit in hits)
                {
                    if (hit is not JsonObject hitObj)
                    {
                        continue;
                    }

                    // map the hit through the core loader, like the C++ search page does
                    var pack = new IndexedPack();
                    try
                    {
                        FlameMod.LoadIndexedPack(pack, hitObj);
                    }
                    catch (JsonParseException)
                    {
                        continue;
                    }

                    results.Add(new FlameSearchResult(
                        pack.AddonId,
                        pack.Slug,
                        pack.Name,
                        pack.LogoUrl,
                        pack.Authors.Count != 0 ? pack.Authors[0].Name : "",
                        Downloads(hitObj)));
                }
            }

            return Results.Ok(results.ToArray());
        }
    }

    /// <summary>The Modrinth search pass-through (PAT header through ModrinthApi.AddApiHeaders
    /// inside the core request builder; facets like the C++ search model builds them).</summary>
    private static async Task<IResult> ModrinthSearchAsync(ResourceType resourceType, string? query, string? version,
        string? loader, string? index, int offset, LauncherRuntime runtime)
    {
        var args = new SearchArgs
        {
            Type = resourceType,
            Offset = offset < 0 ? 0 : offset,
            Search = string.IsNullOrWhiteSpace(query) ? null : query,
            Versions = string.IsNullOrWhiteSpace(version) ? null : [new McVersion(version)],
            Loaders = string.IsNullOrWhiteSpace(loader)
                ? null
                : ModIndex.GetModLoaderFromString(loader.ToLowerInvariant()),
            Sorting = new SortingMethod
            {
                Index = 1,
                Name = string.IsNullOrWhiteSpace(index) ? "relevance" : index,
                ReadableName = "Sort by " + (string.IsNullOrWhiteSpace(index) ? "Relevance" : index),
            },
        };

        var url = runtime.Modrinth.GetSearchUrl(args);
        if (url is null)
        {
            return ApiJson.Error("invalid_loader", $"loader '{loader}' is not supported by Modrinth", 400);
        }

        var request = NetRequest.MakeByteArray(new Uri(url));
        await request.StartAsync();
        if (request.State != TaskState.Succeeded)
        {
            return ApiJson.Error("modrinth_unavailable",
                $"modrinth search failed: {request.FailReason}", 502);
        }

        ModrinthSearchResponseJson? search;
        try
        {
            search = JsonSerializer.Deserialize(request.BodyAsString,
                ModrinthJsonContext.Default.ModrinthSearchResponseJson);
        }
        catch (JsonException)
        {
            return ApiJson.Error("modrinth_invalid_response", "modrinth returned invalid JSON", 502);
        }

        {
            var results = new List<ModrinthSearchResult>();
            if (search?.Hits is { } hits)
            {
                foreach (var hit in hits)
                {
                    results.Add(new ModrinthSearchResult(
                        hit.ProjectId ?? "",
                        hit.Slug ?? "",
                        hit.Title ?? "",
                        hit.IconUrl ?? "",
                        hit.Author ?? "",
                        hit.Downloads is { } downloads &&
                        downloads.ValueKind == JsonValueKind.Number &&
                        downloads.TryGetInt64(out var downloadCount)
                            ? downloadCount
                            : 0));
                }
            }

            return Results.Ok(results.ToArray());
        }
    }

    /// <summary>A string property of a JSON hit object ("" when absent or of another kind).</summary>
    private static string Str(JsonObject obj, string name) =>
        obj[name] is JsonValue value && value.TryGetValue<string>(out var str) ? str : "";

    /// <summary>The MinecraftInstance for a {ref} (null when missing or not a Minecraft
    /// instance — the mods surface only exists for real instances, like launch).
    /// D2: every endpoint resolves through the addressing chain (GLOBAL §1).</summary>
    private static MinecraftInstance? LookupInstance(LauncherRuntime runtime, string reference) =>
        runtime.Instances.ResolveInstance(reference) as MinecraftInstance;

    /// <summary>The Flame sorting method by (case-insensitive) name, defaulting to Popularity
    /// (the CurseForge API's default sortField).</summary>
    private static SortingMethod FlameSorting(string? index, LauncherRuntime runtime)
    {
        var methods = runtime.Flame.GetSortingMethods();
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

    /// <summary>The optional hit download count (FlameMod does not load it; read it raw).
    /// Only JSON number tokens count — a quoted "123" string is not a number.</summary>
    private static long? Downloads(JsonObject hit)
    {
        foreach (var key in new[] { "downloadCount", "downloads" })
        {
            if (hit[key] is JsonValue value &&
                value.ToJsonString() is var raw &&
                raw.Length != 0 && raw[0] != '"' &&
                long.TryParse(raw, CultureInfo.InvariantCulture, out var number))
            {
                return number;
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
