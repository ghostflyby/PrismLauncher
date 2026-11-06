// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Modplatform endpoints: the merged per-provider
// search pass-through (provider = modrinth|flame), the per-provider indexed version lists,
// and the per-instance mods surface (list / tracked install / tracked update plus the
// enable/disable/uninstall renames). The install/update flows run as registered tasks
// (202 + taskId) like launch/import; the update task additionally publishes its found
// updates through the task view's optional result payload.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using FlintLauncher.Core.Config;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.ModPlatform;
using FlintLauncher.Core.ModPlatform.Flame;
using FlintLauncher.Core.ModPlatform.Modrinth;
using FlintLauncher.Core.Settings;
using FlintLauncher.Core.Versioning;
using FlintLauncher.ExternalApi;
using FlintLauncher.ExternalApi.Flame;
using FlintLauncher.ExternalApi.Modrinth;
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
            ModrinthClient modrinth,
            HttpClient http,
            FlameClient flame,
            CancellationToken cancellationToken,
            string query = "",
            string version = "",
            string loader = "",
            string index = "",
            string type = "",
            int offset = 0) =>
        {
            if (!ModPlatformTasks.TryParseProvider(provider, out var parsedProvider))
            {
                return ApiJson.Error("unknown_provider", "provider must be 'modrinth' or 'flame'", 400);
            }

            if (!TryParseSearchType(type, out var resourceType, out var typeError))
            {
                return ApiJson.Error("invalid_type", typeError, 400);
            }

            resourceType = NormalizeSearchType(resourceType, parsedProvider, out var unsupportedReason);
            if (unsupportedReason is not null)
            {
                return ApiJson.Error("invalid_type", unsupportedReason, 400);
            }

            return parsedProvider == ResourceProvider.Flame
                ? await FlameSearchAsync(resourceType, flame, http, cancellationToken,
                    query, version, loader, index, offset)
                : await ModrinthSearchAsync(resourceType, modrinth, http, cancellationToken,
                    query, version, loader, index, offset);
        }).Produces<FlameSearchResult[]>().Produces<ModrinthSearchResultView[]>();

        // ----------------------------------------------------- modplatform/{provider}/categories
        // GET /categories?gameId=432&classId=... (Flame) or GET /tag/category filtered by
        // project_type (Modrinth), mapped to [{id, name, icon}]. An explicit numeric classId
        // wins over `type` on Flame (search hits carry the raw CurseForge class id).
        api.MapGet("/modplatform/{provider}/categories", async Task<IResult> (
            string provider, ModrinthClient modrinth, HttpClient http, FlameClient flame,
            CancellationToken cancellationToken, string type = "", string classId = "") =>
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
                        return ApiJson.Error("invalid_type", typeError, 400);
                    }

                    resourceType = NormalizeSearchType(resourceType, parsedProvider, out var unsupported);
                    if (unsupported is not null)
                    {
                        return ApiJson.Error("invalid_type", unsupported, 400);
                    }

                    effectiveClassId = FlameMod.GetClassId(resourceType);
                }

                FlameCategoryList categories;
                try
                {
                    categories = await flame.Categories.ForClassId(effectiveClassId)
                        .SendTo(http, cancellationToken);
                }
                catch (Exception e) when (e is FlameApiException)
                {
                    return ApiJson.Error("flame_unavailable",
                        $"curseforge categories failed: {e.Message}", 502);
                }

                return Results.Ok(ParseFlameCategories(categories).ToArray());
            }

            if (!TryParseSearchType(type, out var modrinthType, out var modrinthTypeError))
            {
                return ApiJson.Error("invalid_type", modrinthTypeError, 400);
            }

            var projectType = ModrinthProjectType(modrinthType, out var unsupportedType);
            if (unsupportedType is not null)
            {
                return ApiJson.Error("invalid_type", unsupportedType, 400);
            }

            List<ModrinthCategory>? modrinthCategories;
            try
            {
                modrinthCategories = await modrinth.Tags.Categories().SendTo(http, cancellationToken);
            }
            catch (Exception e) when (e is ModrinthApiException)
            {
                return ApiJson.Error("modrinth_unavailable",
                    $"modrinth categories failed: {e.Message}", 502);
            }

            return Results.Ok(ParseModrinthCategories(modrinthCategories, projectType).ToArray());
        }).Produces<ProviderCategoryView[]>();

        // --------------------------------------------------- modplatform/{provider}/mods/{projectId}/description
        api.MapGet("/modplatform/{provider}/mods/{projectId}/description",
            async Task<IResult> (string provider, string projectId, ModrinthClient modrinth, HttpClient http,
                FlameClient flame, CancellationToken cancellationToken) =>
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

                    try
                    {
                        body = await flame.Projects.Description(modId).SendTo(http, cancellationToken);
                    }
                    catch (FlameApiException)
                    {
                        // the failed fetch degrades to an empty body (the endpoint's own rule)
                        body = "";
                    }
                }
                else
                {
                    // the Modrinth body endpoint answers with plain text; a failed fetch
                    // degrades to an empty body (the endpoint's own status rule)
                    try
                    {
                        body = await modrinth.Projects.Description(projectId).SendTo(http, cancellationToken);
                    }
                    catch (ModrinthApiException)
                    {
                        body = "";
                    }
                }

                return Results.Ok(new ProviderBodyView(body));
            }).Produces<ProviderBodyView>();

        // ------------------------------------------------ modplatform/{provider}/mods/{projectId}/changelog
        api.MapGet("/modplatform/{provider}/mods/{projectId}/changelog",
            async Task<IResult> (string provider, string projectId, FlameClient flame, HttpClient http,
                CancellationToken cancellationToken, string fileId = "") =>
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

                string body;
                try
                {
                    body = await flame.Files.Changelog(modId, fileIdNum).SendTo(http, cancellationToken);
                }
                catch (FlameApiException)
                {
                    // the failed fetch degrades to an empty body (the endpoint's own rule)
                    body = "";
                }

                return Results.Ok(new ProviderBodyView(body));
            }).Produces<ProviderBodyView>();

        // ------------------------------------------------ modplatform/{provider}/versions
        api.MapGet("/modplatform/{provider}/versions", async Task<IResult> (
            string provider, ModrinthClient modrinth, HttpClient http, FlameClient flame, string projectId = "") =>
        {
            if (string.IsNullOrWhiteSpace(projectId))
            {
                return ApiJson.Error("invalid_request", "'projectId' is required", 400);
            }

            if (!ModPlatformTasks.TryParseProvider(provider, out var parsedProvider))
            {
                return ApiJson.Error("unknown_provider", "provider must be 'modrinth' or 'flame'", 400);
            }

            var pack = new IndexedPack { AddonId = projectId, Provider = parsedProvider };
            if (parsedProvider == ResourceProvider.Flame)
            {
                FlameFileList doc;
                try
                {
                    doc = await flame.Files.List(projectId).SendTo(http);
                }
                catch (Exception e) when (e is FlameApiException)
                {
                    return ApiJson.Error("flame_unavailable",
                        $"{provider} version list failed: {e.Message}", 502);
                }

                try
                {
                    // loads + validates every file, sorted newest first (a broken body
                    // surfaces as the invalid-response error below)
                    FlameMod.LoadIndexedPackVersionsFromResponse(pack, doc);
                }
                catch (JsonException e)
                {
                    return ApiJson.Error("flame_invalid_response",
                        $"{provider} version list is invalid: {e.Message}", 502);
                }
            }
            else
            {
                List<ModrinthVersion> versionDtos;
                try
                {
                    versionDtos = await modrinth.Versions.Query(projectId).SendTo(http);
                }
                catch (Exception e) when (e is ModrinthApiException)
                {
                    return ApiJson.Error("modrinth_unavailable",
                        $"{provider} version list failed: {e.Message}", 502);
                }

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
                    catch (JsonException)
                    {
                        // skip malformed entries, like the dependency resolver
                    }
                }
            }

            return Results.Ok(pack.Versions.Select(VersionView).ToArray());
        }).Produces<ProviderVersionView[]>();

        // ------------------------------------------------------ instances/{id}/mods
        api.MapGet("/instances/{id}/mods", async (string id,
            InstanceList instances, InstanceMutationLock instanceLock) =>
        {
            await instanceLock.WaitAsync();
            try
            {
                if (LookupInstance(instances, id) is not { } instance)
                {
                    return ApiJson.NotFound("instance_not_found", $"no launchable instance '{id}'");
                }

                var folder = instance.LoaderModList();
                folder.Update();
                var mods = folder.Mods
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
                            metadata is { VersionNumber: { Length: > 0 } versionNumber } ? versionNumber : null,
                            metadata is { Provider: { } provider } ? ModPlatformTasks.ProviderKey(provider) : null,
                            mod.Enabled);
                    })
                    .ToArray();
                return Results.Ok(mods);
            }
            finally
            {
                instanceLock.Release();
            }
        }).Produces<InstalledModView[]>();

        api.MapPost("/instances/{id}/mods/install", async (string id, List<ModInstallSelection>? selections,
            InstanceList instances, InstanceMutationLock instanceLock,
            ModrinthClient modrinth, HttpClient http, FlameClient flame,
            FlintTaskRegistry registry) =>
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

            await instanceLock.WaitAsync();
            try
            {
                if (LookupInstance(instances, id) is not { } instance)
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
                    mcVersion, profile.GetSupportedModLoaders() ?? ModLoaderType.None,
                    modrinth, http, flame);

                // the payload is the install's own result; a failed install has none, which is
                // what the failure branch means
                var entry = registry.Start("mods-install", handle => handle.RunAsync(async token => handle.Result = await task.RunAsync(token).ConfigureAwait(false)),
                    instance.Id());

                return Results.Accepted($"/api/v1/tasks/{entry.Id}", new TaskAcceptedResponse(entry.Id));
            }
            finally
            {
                instanceLock.Release();
            }
        }).Produces<TaskAcceptedResponse>(202);

        api.MapPost("/instances/{id}/mods/update", async (string id, ModsUpdateRequest? filter,
            InstanceList instances, InstanceMutationLock instanceLock, FlintConfigStore config,
            ModrinthClient modrinth, HttpClient http, FlameClient flame, FlintTaskRegistry registry) =>
        {
            await instanceLock.WaitAsync();
            try
            {
                if (LookupInstance(instances, id) is not { } instance)
                {
                    return ApiJson.NotFound("instance_not_found", $"no launchable instance '{id}'");
                }

                ModPlatformTasks.EnsureProfileLoaded(instance);

                // release channels (#3404): the request's explicit list wins; otherwise the
                // global ModUpdateReleaseTypes setting (the ResourceUpdateDialog fallback) —
                // read from the typed user layer
                IReadOnlyList<IndexedVersionType> releaseTypes =
                    filter?.ReleaseTypes is { Count: > 0 } requested
                        ? IndexedVersionType.FromStringList(requested)
                        : IndexedVersionType.FromStringList(SettingsJson.ToStringList(
                            config.Current.ModDownload.ModUpdateReleaseTypes ?? "[]"));

                var task = new ModsUpdateTask(instance, instance.LoaderModList(), filter?.Mods, modrinth, http, flame, releaseTypes);

                // the payload is the check's own result; a failed check has none
                var entry = registry.Start("mods-update", handle => handle.RunAsync(async token => handle.Result = await task.RunAsync(token).ConfigureAwait(false)),
                    instance.Id());

                return Results.Accepted($"/api/v1/tasks/{entry.Id}", new TaskAcceptedResponse(entry.Id));
            }
            finally
            {
                instanceLock.Release();
            }
        }).Produces<TaskAcceptedResponse>(202);
    }

    // ----------------------------------------------------------------- helpers

    /// <summary>
    /// The search `type` query param → the ResourceType of the search (the
    /// value set mirrors the C++ resource-type search surfaces; default mods/back-compat):
    /// mods | resourcepacks | texturepacks | shaderpacks | datapacks | modpacks | worlds.
    /// </summary>
    private static bool TryParseSearchType(string type, out ResourceType resourceType, [NotNullWhen(false)] out string? error)
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
    private static List<ProviderCategoryView> ParseFlameCategories(FlameCategoryList doc)
    {
        var result = new List<ProviderCategoryView>();
        foreach (var entry in doc.Data ?? [])
        {
            if (entry is not { } cat)
            {
                continue;
            }

            result.Add(new ProviderCategoryView(
                (cat.Id ?? 0).ToString(CultureInfo.InvariantCulture),
                cat.Name ?? "",
                cat.Icon ?? ""));
        }

        return result;
    }

    /// <summary>The Modrinth /tag/category response filtered by project_type and mapped to
    /// [{id, name, icon}] (the Modrinth category id is its name, like LoadCategories).</summary>
    private static List<ProviderCategoryView> ParseModrinthCategories(List<ModrinthCategory>? entries,
        string projectType)
    {
        var result = new List<ProviderCategoryView>();
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

            var name = entry.Name;
            result.Add(new ProviderCategoryView(name, name, entry.Icon));
        }

        return result;
    }

    /// <summary>The CurseForge search pass-through (the x-api-key header rides the
    /// operation's request builder; hits map through FlameMod.LoadIndexedPack).</summary>
    private static async Task<IResult> FlameSearchAsync(ResourceType resourceType, FlameClient flame,
        HttpClient http, CancellationToken cancellationToken, string query = "", string version = "",
        string loader = "", string index = "", int offset = 0)
    {
        var loaders = string.IsNullOrWhiteSpace(loader)
            ? (ModLoaderType?)null
            : ModIndex.GetModLoaderFromString(loader.ToLowerInvariant());
        // the CurseForge search never rejects loaders (unlike the Modrinth one), so validate
        // the parsed loader against Flame's supported set here
        if (loaders is { } parsed && parsed != 0 && !FlameMod.ValidateModLoaders(parsed))
        {
            return ApiJson.Error("invalid_loader", $"loader '{loader}' is not supported by CurseForge", 400);
        }

        IReadOnlyList<int>? loaderIds = null;
        if (loaders is { } selected)
        {
            selected &= ~ModLoaderType.DataPack;
            if (selected != 0)
            {
                loaderIds = FlameMod.GetModLoaderFilterIds(selected);
            }
        }

        var sorting = FlameSorting(index);

        FlameProjectList doc;
        try
        {
            doc = await flame.Search.Query(
                    classId: FlameMod.GetClassId(resourceType),
                    offset: offset < 0 ? 0 : offset,
                    searchFilter: string.IsNullOrWhiteSpace(query) ? null : query,
                    sortField: (int)sorting.Index,
                    modLoaderTypes: loaderIds,
                    gameVersion: string.IsNullOrWhiteSpace(version) ? null : version)
                .SendTo(http, cancellationToken);
        }
        catch (Exception e) when (e is FlameApiException)
        {
            return ApiJson.Error("flame_unavailable",
                $"curseforge search failed: {e.Message}", 502);
        }

        var results = new List<FlameSearchResult>();
        foreach (var hit in doc.Data ?? [])
        {
            if (hit is not { } hitDto)
            {
                continue;
            }

            // map the hit through the core loader, like the C++ search page does
            var pack = new IndexedPack();
            try
            {
                FlameMod.LoadIndexedPack(pack, hitDto);
            }
            catch (JsonException)
            {
                continue;
            }

            results.Add(new FlameSearchResult(
                pack.AddonId,
                pack.Slug,
                pack.Name,
                pack.LogoUrl,
                pack.Authors.Count != 0 ? pack.Authors[0].Name : "",
                hitDto.DownloadCount));
        }

        return Results.Ok(results.ToArray());
    }

    /// <summary>The loaders Modrinth search can facet (the C++ ModrinthAPI
    /// getModLoaderStrings set — DataPack facets exist, Cauldron is not a search loader).</summary>
    private static readonly ModLoaderType[] ModrinthSearchLoaders =
    [
        ModLoaderType.NeoForge, ModLoaderType.Forge, ModLoaderType.Fabric,
        ModLoaderType.Quilt, ModLoaderType.LiteLoader, ModLoaderType.DataPack,
        ModLoaderType.Babric, ModLoaderType.Bta, ModLoaderType.LegacyFabric,
        ModLoaderType.Ornithe, ModLoaderType.Rift,
    ];

    /// <summary>The Modrinth search pass-through (PAT header through the operation's request
    /// builder; the launcher search vocabulary translates into the wire's facet groups —
    /// each group OR-s its values, the groups AND together).</summary>
    private static async Task<IResult> ModrinthSearchAsync(ResourceType resourceType, ModrinthClient modrinth,
        HttpClient http, CancellationToken cancellationToken, string query = "", string version = "",
        string loader = "", string index = "", int offset = 0)
    {
        var projectType = ModrinthProjectType(resourceType, out var typeError);
        if (projectType.Length == 0)
        {
            return ApiJson.Error("invalid_type",
                typeError ?? $"type '{resourceType}' is not supported by modrinth", 400);
        }

        var loaderFlags = string.IsNullOrWhiteSpace(loader)
            ? (ModLoaderType?)null
            : ModIndex.GetModLoaderFromString(loader.ToLowerInvariant());
        if (loaderFlags is { } requested && ModrinthSearchLoaders.All(known => (requested & known) == 0))
        {
            return ApiJson.Error("invalid_loader", $"loader '{loader}' is not supported by Modrinth", 400);
        }

        var facets = new List<IReadOnlyList<string>>();
        if (loaderFlags is { } supported)
        {
            facets.Add(ModrinthSearchLoaders.Where(known => (supported & known) != 0)
                .Select(ModIndex.GetModLoaderAsString).Select(name => "categories:" + name).ToList());
        }

        if (!string.IsNullOrWhiteSpace(version))
        {
            facets.Add(["versions:" + ModrinthVersionFormat.ToWire(new McVersion(version))]);
        }

        facets.Add(["project_type:" + projectType]);

        ModrinthSearchResults search;
        try
        {
            search = await modrinth.Search.Query(
                offset: offset < 0 ? 0 : offset,
                query: string.IsNullOrWhiteSpace(query) ? null : query,
                index: string.IsNullOrWhiteSpace(index) ? "relevance" : index,
                projectType: projectType,
                facets: facets).SendTo(http, cancellationToken);
        }
        catch (Exception e) when (e is ModrinthApiException)
        {
            return ApiJson.Error("modrinth_unavailable", $"modrinth search failed: {e.Message}", 502);
        }

        var results = new List<ModrinthSearchResultView>();
        foreach (var hit in search.Hits)
        {
            results.Add(new ModrinthSearchResultView(
                hit.ProjectId,
                hit.Slug,
                hit.Title,
                hit.IconUrl ?? "",
                hit.Author,
                hit.Downloads));
        }

        return Results.Ok(results.ToArray());
    }

    /// <summary>The MinecraftInstance for a {ref} (null when missing or not a Minecraft
    /// instance — the mods surface only exists for real instances, like launch).
    /// every endpoint resolves through the addressing chain.</summary>
    private static MinecraftInstance? LookupInstance(InstanceList instances, string reference) =>
        instances.ResolveInstance(reference) as MinecraftInstance;

    /// <summary>The Flame sorting method by (case-insensitive) name, defaulting to Popularity
    /// (the CurseForge API's default sortField).</summary>
    private static SortingMethod FlameSorting(string index)
    {
        var methods = FlameMod.GetSortingMethods();
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
            new SortingMethod { Index = 2, Name = "Popularity" });
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
