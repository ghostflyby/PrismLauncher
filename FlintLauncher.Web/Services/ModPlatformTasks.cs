// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// The modplatform install/update orchestration behind POST /instances/{id}/mods/install and
// POST /instances/{id}/mods/update: resolve the selections, resolve their dependencies, then
// run one ResourceDownloadTask per resolved entry. The update path runs the provider's check
// pass and applies what it found through ModUpdateTask.
//
// The install phases are sequential because GetModDependenciesTask must be constructed with
// the already-resolved selected versions, so the phase chain cannot be built up front.
//
// "Latest version" for a selection without a pinned version reuses each provider's
// latest-version logic: Modrinth's shape dispatch and date sort, CurseForge's file-list pick.
//
// Mods without packwiz metadata are skipped: nothing records their update state.

using System.Text.Json;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Minecraft.Resources;
using FlintLauncher.Core.ModPlatform;
using FlintLauncher.Core.ModPlatform.Dependencies;
using FlintLauncher.Core.ModPlatform.Flame;
using FlintLauncher.Core.ModPlatform.Modrinth;
using FlintLauncher.Core.Tasks;
using FlintLauncher.Core.Versioning;
using FlintLauncher.ExternalApi;
using FlintLauncher.ExternalApi.Flame;
using FlintLauncher.ExternalApi.Modrinth;
using FlintLauncher.Web.Models;

namespace FlintLauncher.Web.Services;

/// <summary>A validated install selection (the API-level provider string already parsed).</summary>
public sealed record ModInstallRequest(ResourceProvider Provider, string ProjectId, string VersionId);

/// <summary>Raised when a selection/project cannot be resolved; the message is the reason
/// reported to the caller.</summary>
public sealed class ModResolveException(string message) : Exception(message);

/// <summary>Provider-string plumbing shared by the modplatform endpoints and tasks.</summary>
public static class ModPlatformTasks
{
    public const string ModrinthKey = "modrinth";
    public const string FlameKey = "flame";

    /// <summary>The lowercase API-level provider key ("modrinth" / "flame").</summary>
    public static string ProviderKey(ResourceProvider provider) =>
        provider == ResourceProvider.Modrinth ? ModrinthKey : FlameKey;

    public static bool TryParseProvider(string? name, out ResourceProvider provider)
    {
        switch ((name ?? "").Trim().ToLowerInvariant())
        {
            case ModrinthKey:
                provider = ResourceProvider.Modrinth;
                return true;
            case FlameKey:
                provider = ResourceProvider.Flame;
                return true;
            default:
                provider = ResourceProvider.Modrinth;
                return false;
        }
    }

    /// <summary>
    /// This port loads an instance's component list lazily (PackProfile.Load is internal to
    /// the core); the public trigger is the statusbar description path, which re-reads
    /// mmc-pack.json when the minecraft version is unknown. Needed before the modplatform
    /// flows read the instance's mc version / mod loaders.
    /// </summary>
    public static void EnsureProfileLoaded(MinecraftInstance instance)
    {
        if (instance.GetPackProfile().Components.Count == 0)
        {
            _ = instance.GetStatusbarDescription();
        }
    }
}

/// <summary>
/// Resolves (provider, projectId, versionId?) selections into the provider-neutral
/// (IndexedPack, IndexedVersion) pairs: project info through the provider getProject, the
/// version either pinned by id or the latest one matching the instance's mc version/loader.
/// </summary>
public sealed class ModSelectionResolver(
    McVersion mcVersion,
    ModLoaderType loaders,
    IReadOnlyList<ModLoaderType> loaderList,
    ModrinthClient modrinth,
    HttpClient http,
    FlameClient? flame = null)
{
    private readonly FlameClient _flame = flame ?? new FlameClient();

    public async Task<(IndexedPack Pack, IndexedVersion Version)> ResolveAsync(
        ResourceProvider provider, string projectId, string versionId, CancellationToken cancellationToken)
    {
        var pack = new IndexedPack { AddonId = projectId, Provider = provider };

        if (provider == ResourceProvider.Flame)
        {
            FlameProjectEnvelope envelope;
            try
            {
                envelope = await _flame.Projects.ById(projectId).SendTo(http, cancellationToken);
            }
            catch (Exception e) when (e is FlameApiException)
            {
                throw new ModResolveException($"fetching the CurseForge project {projectId} failed: {e.Message}");
            }

            if (envelope.Data is not { } project)
            {
                throw new ModResolveException($"the CurseForge project response for {projectId} has no data object");
            }

            try
            {
                FlameMod.LoadIndexedPack(pack, project);
            }
            catch (JsonException e)
            {
                throw new ModResolveException($"the CurseForge project response for {projectId} is invalid: {e.Message}");
            }
        }
        else
        {
            ModrinthProject dto;
            try
            {
                dto = await modrinth.Projects.ById(projectId).SendTo(http, cancellationToken);
            }
            catch (Exception e) when (e is ModrinthApiException)
            {
                throw new ModResolveException($"fetching the Modrinth project {projectId} failed: {e.Message}");
            }

            ModrinthPackIndex.LoadIndexedPack(pack, dto);
        }

        var version = versionId.Length != 0
            ? await FetchPinnedVersionAsync(provider, projectId, versionId, cancellationToken)
            : await FetchLatestVersionAsync(provider, pack, cancellationToken);

        if (version is null || version.FileId.Length == 0 || version.DownloadUrl.Length == 0)
        {
            throw new ModResolveException(
                $"no downloadable {(versionId.Length != 0 ? $"version '{versionId}' " : "version ")}of {projectId}" +
                " matches this instance");
        }

        return (pack, version);
    }

    /// <summary>The pinned version: GET /version/{id} (Modrinth) or GET /mods/{a}/files/{f} (Flame).</summary>
    private async Task<IndexedVersion?> FetchPinnedVersionAsync(ResourceProvider provider, string projectId,
        string versionId, CancellationToken cancellationToken)
    {
        try
        {
            if (provider == ResourceProvider.Flame)
            {
                FlameFileEnvelope envelope;
                try
                {
                    envelope = await _flame.Files.ById(projectId, versionId).SendTo(http, cancellationToken);
                }
                catch (Exception e) when (e is FlameApiException)
                {
                    throw new ModResolveException($"fetching the CurseForge file {versionId} failed: {e.Message}");
                }

                if (envelope.Data is not { } file)
                {
                    throw new ModResolveException($"the CurseForge file response for {versionId} has no data object");
                }

                return FlameMod.LoadIndexedPackVersion(file);
            }

            ModrinthVersion dto;
            try
            {
                dto = await modrinth.Versions.ById(versionId).SendTo(http, cancellationToken);
            }
            catch (Exception e) when (e is ModrinthApiException)
            {
                throw new ModResolveException($"fetching the Modrinth version {versionId} failed: {e.Message}");
            }

            return ModrinthPackIndex.LoadIndexedPackVersion(dto);
        }
        catch (JsonException e)
        {
            throw new ModResolveException($"the response for version {versionId} is invalid: {e.Message}");
        }
    }

    /// <summary>
    /// The latest version matching the instance's mc version (and loader): the Modrinth
    /// version list dispatch + date-sort of getDependencyVersion, or the Flame file list run
    /// through FlameAPI::getLatestVersion.
    /// </summary>
    private async Task<IndexedVersion?> FetchLatestVersionAsync(ResourceProvider provider, IndexedPack pack,
        CancellationToken cancellationToken)
    {
        if (provider == ResourceProvider.Flame)
        {
            FlameFileList doc;
            try
            {
                doc = await _flame.Files.List(pack.AddonId, gameVersion: mcVersion.ToString())
                    .SendTo(http, cancellationToken);
            }
            catch (Exception e) when (e is FlameApiException)
            {
                throw new ModResolveException($"fetching the version list of {pack.AddonId} failed: {e.Message}");
            }

            try
            {
                if (!FlameMod.LoadIndexedPackVersionsFromResponse(pack, doc))
                {
                    throw new ModResolveException($"the CurseForge version list of {pack.AddonId} has no data array");
                }
            }
            catch (JsonException e)
            {
                throw new ModResolveException($"the CurseForge version list of {pack.AddonId} is invalid: {e.Message}");
            }

            return FlameMod.GetLatestVersion(pack.Versions, loaderList, ModLoaderType.None,
                checkLoaders: loaderList.Count != 0);
        }

        List<ModrinthVersion> dtos;
        try
        {
            dtos = await modrinth.Versions.Query(pack.AddonId,
                    [ModrinthVersionFormat.ToWire(mcVersion)],
                    ModIndex.ModLoaderTypesToList(loaders).Select(ModIndex.GetModLoaderAsString).ToList())
                .SendTo(http, cancellationToken);
        }
        catch (Exception e) when (e is ModrinthApiException)
        {
            throw new ModResolveException($"fetching the version list of {pack.AddonId} failed: {e.Message}");
        }

        // keep the valid ones for the instance's loader (the getDependencyVersion heuristic)
        // and take the newest by published date
        var versions = new List<IndexedVersion>();
        foreach (var dto in dtos)
        {
            var file = ModrinthPackIndex.LoadIndexedPackVersion(dto);

            if (file.FileId.Length != 0 &&
                (file.Loaders == ModLoaderType.None || (loaders & file.Loaders) != 0))
            {
                versions.Add(file);
            }
        }

        versions.Sort((a, b) => string.CompareOrdinal(b.Date, a.Date));
        return versions.Count != 0 ? versions[0] : null;
    }
}

/// <summary>
/// Install one or more mods into an instance's mods folder: resolve every selection
/// (project + version), run the recursive dependency resolution against the instance's
/// installed mods, then download a ResourceDownloadTask per resolved entry (each writing its
/// own packwiz metadata). The first failure ends the install with that reason.
/// </summary>
public sealed class ModInstallTask(
    MinecraftInstance instance,
    ModFolderModel folder,
    IReadOnlyList<ModInstallRequest> selections,
    string mcVersion,
    ModLoaderType loaders,
    ModrinthClient modrinth,
    HttpClient http,
    FlameClient? flame = null)
{
    private readonly McVersion _mcVersion = new(mcVersion);
    private readonly List<ModLoaderType> _loaderList = ModIndex.ModLoaderTypesToList(loaders);
    private readonly FlameClient _flame = flame ?? new FlameClient();

    /// <summary>Resolves, then downloads, every selection and returns the installed entries.
    /// Any exception is a failure whose message is the reason.</summary>
    public async Task<TaskResultView> RunAsync(CancellationToken cancellationToken)
    {
        var installed = new List<ModInstallEntryView>();

        // pick up already-installed mods for the dependency resolution
        folder.Update();

        var selected = new List<GetModDependenciesTask.PackDependency>();
        try
        {
            var resolver = new ModSelectionResolver(_mcVersion, loaders, _loaderList,
                modrinth, http, _flame);
            foreach (var selection in selections)
            {
                var (pack, version) = await resolver.ResolveAsync(
                    selection.Provider, selection.ProjectId, selection.VersionId, cancellationToken);
                selected.Add(new GetModDependenciesTask.PackDependency(pack, version));
            }
        }
        catch (ModResolveException e)
        {
            throw new InvalidOperationException(e.Message, e);
        }
        var dependencyTask = new GetModDependenciesTask(instance, folder, selected,
            modrinth, http, _flame);
        await dependencyTask.RunAsync(cancellationToken).ConfigureAwait(false);

        var dependencies = dependencyTask.GetDependencies();
        var extraInfo = dependencyTask.GetExtraInfo();

        void RecordInstalled(GetModDependenciesTask.PackDependency entry, bool isDependency, string? requiredBy)
        {
            installed.Add(new ModInstallEntryView(
                entry.Version.FileName,
                ModPlatformTasks.ProviderKey(entry.Pack.Provider),
                entry.Pack.AddonId,
                entry.Version.Version,
                isDependency,
                requiredBy));
        }
        var planned = new List<(GetModDependenciesTask.PackDependency Entry, bool IsDependency, string? RequiredBy)>();
        List<ResourceDownloadTask> downloads = [];
        foreach (var entry in selected.Concat(dependencies))
        {
            var isDependency = !selected.Contains(entry);
            string? requiredBy = null;
            if (isDependency && extraInfo.TryGetValue(entry.Pack.AddonId, out var extra) &&
                extra.RequiredByIds.Count != 0)
            {
                requiredBy = extra.RequiredByIds[0];
            }

            planned.Add((entry, isDependency, requiredBy));

            if (entry.Version.DownloadUrl.Length == 0)
            {
                throw new InvalidOperationException(
                    $"no downloadable version of {entry.Pack.AddonId} was resolved");
            }

            downloads.Add(new ResourceDownloadTask(
                entry.Pack,
                entry.Version,
                folder,
                modrinth,
                http,
                isIndexed: true,
                downloadReason: isDependency ? "dependency" : "standalone",
                dependentOn: requiredBy ?? "",
                instance));
        }

        await TaskWork.SequenceAsync(downloads, (download, token) => download.RunAsync(token),
            cancellationToken).ConfigureAwait(false);

        // only entries whose download actually succeeded reach the result payload
        foreach (var (entry, isDependency, requiredBy) in planned)
        {
            RecordInstalled(entry, isDependency, requiredBy);
        }

        return new TaskResultView(null, installed);
    }
}

/// <summary>
/// Check an instance's installed (metadata-tracked) mods for updates through the provider
/// check passes, then apply everything found with a ModUpdateTask. RunAsync returns the
/// found/applied updates as the task view's result payload.
/// </summary>
public sealed class ModsUpdateTask(
    MinecraftInstance instance,
    ModFolderModel folder,
    IReadOnlyList<string>? filter,
    ModrinthClient modrinth,
    HttpClient http,
    FlameClient flameApi,
    IReadOnlyList<IndexedVersionType>? releaseTypes = null)
{
    private sealed record PendingUpdate(ModUpdateEntryView View, IndexedVersion Version, string ProjectId, string Slug, string Name);

    /// <summary>Checks every installed mod, applies what was found, and returns the
    /// found/applied updates. Any exception is a failure whose message is the reason.</summary>
    public async Task<TaskResultView> RunAsync(CancellationToken cancellationToken)
    {
        var updates = new List<PendingUpdate>();
        folder.Update();

        var profile = instance.GetPackProfile();
        var gameVersions = new List<McVersion> { new(profile.GetComponentVersion("net.minecraft")) };
        var loadersList = profile.GetModLoadersList();

        var candidates = folder.Mods.Where(m => !m.Fileinfo.Name.StartsWith('.'));
        if (filter is { Count: > 0 })
        {
            var wanted = filter.ToHashSet(StringComparer.Ordinal);
            candidates = candidates.Where(m => wanted.Contains(m.Fileinfo.Name) ||
                                               wanted.Contains(m.GetOriginalFileName()));
        }

        // the check passes refuse metadata-less resources; the API simply skips them.
        // Locked resources (metadata lockUpdate) are skipped too — the data-level half of
        // ResourceUpdateDialog::onMetadataEnsured's `!metadata() || lockUpdate()` guard.
        var modrinthResources = new List<Resource>();
        var flameResources = new List<Resource>();
        foreach (var mod in candidates)
        {
            var metadata = folder.GetMetadata(mod);
            if (metadata is null || metadata.LockUpdate)
            {
                continue;
            }

            if (metadata.Provider == ResourceProvider.Modrinth)
            {
                modrinthResources.Add(mod);
            }
            else if (metadata.Provider == ResourceProvider.Flame)
            {
                flameResources.Add(mod);
            }
        }

        if (modrinthResources.Count != 0)
        {
            var check = new ModrinthCheckUpdate(modrinthResources, gameVersions, loadersList,
                resource => folder.GetMetadata(resource), modrinth, http, releaseTypes);
            var result = await check.CheckAsync(cancellationToken).ConfigureAwait(false);

            var byProject = MetadataByProject(modrinthResources);
            foreach (var update in result.Updates)
            {
                if (byProject.TryGetValue(update.Version.AddonId, out var metadata))
                {
                    Collect(updates, update.Provider, update, metadata);
                }
            }
        }

        if (flameResources.Count != 0)
        {
            var check = new FlameCheckUpdate(flameResources, gameVersions, loadersList,
                resource => folder.GetMetadata(resource), flameApi, http, releaseTypes);
            var result = await check.CheckAsync(cancellationToken).ConfigureAwait(false);

            var byProject = MetadataByProject(flameResources);
            foreach (var update in result.Updates)
            {
                if (byProject.TryGetValue(update.Version.AddonId, out var metadata))
                {
                    Collect(updates, update.Provider, update, metadata);
                }
            }
        }

        if (updates.Count == 0)
        {
            return new TaskResultView([], null);
        }
        // a pack carrying only the fields the download task reads
        var downloads = updates.Select(update => new ResourceDownloadTask(
            new IndexedPack
            {
                AddonId = update.ProjectId,
                Slug = update.Slug,
                Name = update.Name,
                Provider = update.View.Provider == "modrinth" ? ResourceProvider.Modrinth : ResourceProvider.Flame,
            },
            update.Version,
            folder,
            isIndexed: true,
            downloadReason: "update",
            instance: instance,
            modrinth: modrinth,
            http: http));
        await ModUpdateTask.ApplyAsync(folder, downloads, cancellationToken).ConfigureAwait(false);
        return new TaskResultView(updates.Select(update => update.View).ToArray(), null);
    }

    // the two check passes expose structurally identical Update records (both are the port
    // of CheckUpdateTask::Update); one builder per shape keeps the mapping explicit
    private static void Collect(List<PendingUpdate> updates, ResourceProvider provider,
        ModrinthCheckUpdate.Update update, MetadataHandler.ModStruct metadata)
    {
        if (metadata.ProjectId is not { } projectId)
        {
            return;
        }

        updates.Add(new PendingUpdate(
            new ModUpdateEntryView(metadata.Filename, update.OldVersion, update.NewVersion,
                ModPlatformTasks.ProviderKey(provider)),
            update.Version,
            projectId,
            metadata.Slug,
            update.Name));
    }

    private static void Collect(List<PendingUpdate> updates, ResourceProvider provider,
        FlameCheckUpdate.Update update, MetadataHandler.ModStruct metadata)
    {
        if (metadata.ProjectId is not { } projectId)
        {
            return;
        }

        updates.Add(new PendingUpdate(
            new ModUpdateEntryView(metadata.Filename, update.OldVersion, update.NewVersion,
                ModPlatformTasks.ProviderKey(provider)),
            update.Version,
            projectId,
            metadata.Slug,
            update.Name));
    }

    private Dictionary<string, MetadataHandler.ModStruct> MetadataByProject(IEnumerable<Resource> resources)
    {
        var map = new Dictionary<string, MetadataHandler.ModStruct>(StringComparer.Ordinal);
        foreach (var resource in resources)
        {
            var metadata = folder.GetMetadata(resource);
            if (metadata is { ProjectId: { } projectId } value && !map.ContainsKey(projectId))
            {
                map[projectId] = value;
            }
        }

        return map;
    }
}
