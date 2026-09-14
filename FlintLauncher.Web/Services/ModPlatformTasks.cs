// SPDX-License-Identifier: GPL-3.0-only
// The modplatform install/update orchestration behind POST /instances/{id}/mods/install and
// POST /instances/{id}/mods/update — the headless port of the C++ ModrinthPage/FlamePage
// download-dialog flow (resolve selections -> GetModDependenciesTask -> a ResourceDownloadTask
// per resolved entry) and of ModFolderPage::updateMods (provider CheckUpdate pass ->
// ModUpdateTask over the per-mod downloads).
//
// Deviations:
//  - The C++ drives the phases through chained UI dialogs; here ModInstallTask is a custom
//    FlintTask that awaits the phases in order (SequentialTask semantics: the first failure
//    fails the whole task). The phases cannot be one statically-built SequentialTask because
//    GetModDependenciesTask must be constructed with the already-resolved selected versions.
//  - "Latest version" for a selection without a pinned version reuses each provider's
//    latest-version logic: the Modrinth shape dispatch/date-sort of
//    GetModDependenciesTask::getDependencyVersion (modplatform/ResourceAPI.cpp) and
//    FlameAPI::getLatestVersion over the project's file list.
//  - ModsUpdateTask skips mods without packwiz metadata (the C++ update dialog reports them
//    as an error row; the API surface has no per-resource error channel yet).
using System.Text.Json;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Json;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Minecraft.Resources;
using FlintLauncher.Core.ModPlatform;
using FlintLauncher.Core.ModPlatform.Dependencies;
using FlintLauncher.Core.ModPlatform.Flame;
using FlintLauncher.Core.ModPlatform.Modrinth;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Tasks;
using FlintLauncher.Core.Versioning;
using FlintLauncher.Web.Models;

namespace FlintLauncher.Web.Services;

/// <summary>A validated install selection (the API-level provider string already parsed).</summary>
public sealed record ModInstallRequest(ResourceProvider Provider, string ProjectId, string VersionId);

/// <summary>Raised when a selection/project cannot be resolved; the message becomes the task's FailReason.</summary>
public sealed class ModResolveException(string message) : Exception(message);

/// <summary>Provider-string plumbing shared by the modplatform endpoints and tasks.</summary>
public static class ModPlatformTasks
{
    public const string ModrinthKey = "modrinth";
    public const string FlameKey = "flame";

    /// <summary>The lowercase API-level provider key ("modrinth" / "flame").</summary>
    public static string ProviderKey(ResourceProvider provider) =>
        provider == ResourceProvider.MODRINTH ? ModrinthKey : FlameKey;

    public static bool TryParseProvider(string? name, out ResourceProvider provider)
    {
        switch ((name ?? "").Trim().ToLowerInvariant())
        {
            case ModrinthKey:
                provider = ResourceProvider.MODRINTH;
                return true;
            case FlameKey:
                provider = ResourceProvider.FLAME;
                return true;
            default:
                provider = ResourceProvider.MODRINTH;
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
public sealed class ModSelectionResolver(McVersion mcVersion, ModLoaderType loaders, IReadOnlyList<ModLoaderType> loaderList)
{
    public async Task<(IndexedPack Pack, IndexedVersion Version)> ResolveAsync(
        ResourceProvider provider, string projectId, string versionId, CancellationToken cancellationToken)
    {
        var pack = new IndexedPack { AddonId = projectId, Provider = provider };

        if (provider == ResourceProvider.FLAME)
        {
            var (job, request) = FlameApi.Instance.GetProject(projectId);
            await job.StartAsync(cancellationToken);
            if (!job.WasSuccessful)
            {
                throw new ModResolveException($"fetching the CurseForge project {projectId} failed: {job.FailReason}");
            }
            using (var doc = ParseBody(request.BodyAsString, $"the CurseForge project {projectId}"))
            {
                // Flame nests the project under "data" (the C++ Json::requireObject(doc, "data"))
                if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                    !doc.RootElement.TryGetProperty("data", out var data) ||
                    data.ValueKind != JsonValueKind.Object)
                {
                    throw new ModResolveException($"the CurseForge project response for {projectId} has no data object");
                }
                try
                {
                    FlameMod.LoadIndexedPack(pack, data);
                }
                catch (JsonParseException e)
                {
                    throw new ModResolveException($"the CurseForge project response for {projectId} is invalid: {e.Message}");
                }
            }
        }
        else
        {
            var (job, request) = ModrinthApi.Instance.GetProject(projectId);
            if (job is null || request is null)
            {
                throw new ModResolveException($"cannot build the Modrinth project request for {projectId}");
            }
            await job.StartAsync(cancellationToken);
            if (!job.WasSuccessful)
            {
                throw new ModResolveException($"fetching the Modrinth project {projectId} failed: {job.FailReason}");
            }
            using (var doc = ParseBody(request.BodyAsString, $"the Modrinth project {projectId}"))
            {
                try
                {
                    ModrinthPackIndex.LoadIndexedPack(pack, doc.RootElement);
                }
                catch (JsonParseException e)
                {
                    throw new ModResolveException($"the Modrinth project response for {projectId} is invalid: {e.Message}");
                }
            }
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
            if (provider == ResourceProvider.FLAME)
            {
                var (job, request) = FlameApi.GetFile(projectId, versionId);
                await job.StartAsync(cancellationToken);
                if (!job.WasSuccessful)
                {
                    throw new ModResolveException($"fetching the CurseForge file {versionId} failed: {job.FailReason}");
                }
                using var doc = ParseBody(request.BodyAsString, $"the CurseForge file {versionId}");
                if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                    !doc.RootElement.TryGetProperty("data", out var data) ||
                    data.ValueKind != JsonValueKind.Object)
                {
                    throw new ModResolveException($"the CurseForge file response for {versionId} has no data object");
                }
                return FlameMod.LoadIndexedPackVersion(data);
            }

            var pinnedUrl = $"{ModrinthApi.BaseUrl}/version/{versionId}";
            var pinnedRequest = NetRequest.MakeByteArray(new Uri(pinnedUrl));
            await pinnedRequest.StartAsync(cancellationToken);
            if (!pinnedRequest.WasSuccessful)
            {
                throw new ModResolveException($"fetching the Modrinth version {versionId} failed: {pinnedRequest.FailReason}");
            }
            using var pinnedDoc = ParseBody(pinnedRequest.BodyAsString, $"the Modrinth version {versionId}");
            return ModrinthPackIndex.LoadIndexedPackVersion(pinnedDoc.RootElement);
        }
        catch (JsonParseException e)
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
        var args = new VersionSearchArgs { Pack = pack, McVersions = [mcVersion] };
        var url = provider == ResourceProvider.FLAME
            ? FlameApi.Instance.GetVersionsURL(args)
            : ModrinthApi.Instance.GetVersionsURL(new VersionSearchArgs
            {
                Pack = pack,
                McVersions = [mcVersion],
                Loaders = loaders,
            });
        if (url is null)
        {
            throw new ModResolveException($"cannot build the version-list request for {pack.AddonId}");
        }

        var request = NetRequest.MakeByteArray(new Uri(url));
        await request.StartAsync(cancellationToken);
        if (!request.WasSuccessful)
        {
            throw new ModResolveException($"fetching the version list of {pack.AddonId} failed: {request.FailReason}");
        }

        using var doc = ParseBody(request.BodyAsString, $"the version list of {pack.AddonId}");
        if (provider == ResourceProvider.FLAME)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                throw new ModResolveException($"the CurseForge version list of {pack.AddonId} has no data array");
            }
            try
            {
                FlameMod.LoadIndexedPackVersions(pack, data);
            }
            catch (JsonParseException e)
            {
                throw new ModResolveException($"the CurseForge version list of {pack.AddonId} is invalid: {e.Message}");
            }
            return FlameApi.GetLatestVersion(pack.Versions, loaderList, ModLoaderType.None,
                checkLoaders: loaderList.Count != 0);
        }

        // Modrinth: an array of versions; keep the valid ones for the instance's loader
        // (the getDependencyVersion heuristic) and take the newest by published date
        var versions = new List<IndexedVersion>();
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                IndexedVersion file;
                try
                {
                    file = ModrinthPackIndex.LoadIndexedPackVersion(entry);
                }
                catch (JsonParseException)
                {
                    continue;
                }
                if (file.FileId.Length != 0 &&
                    (file.Loaders == ModLoaderType.None || (loaders & file.Loaders) != 0))
                {
                    versions.Add(file);
                }
            }
        }
        versions.Sort((a, b) => string.CompareOrdinal(b.Date, a.Date));
        return versions.Count != 0 ? versions[0] : null;
    }

    private static JsonDocument ParseBody(string body, string what)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException e)
        {
            throw new ModResolveException($"the response for {what} is not valid JSON: {e.Message}");
        }
    }
}

/// <summary>
/// Install one or more mods into an instance's mods folder: resolve every selection
/// (project + version), run the recursive dependency resolution against the instance's
/// installed mods, then download a ResourceDownloadTask per resolved entry (each writing its
/// own packwiz metadata). Fails with the resolution/download reason, like a SequentialTask.
/// </summary>
public sealed class ModInstallTask : FlintTask
{
    private readonly MinecraftInstance _instance;
    private readonly ModFolderModel _folder;
    private readonly IReadOnlyList<ModInstallRequest> _selections;
    private readonly McVersion _mcVersion;
    private readonly ModLoaderType _loaders;
    private readonly List<ModLoaderType> _loaderList;

    public ModInstallTask(MinecraftInstance instance, ModFolderModel folder,
        IReadOnlyList<ModInstallRequest> selections, string mcVersion, ModLoaderType loaders)
    {
        _instance = instance;
        _folder = folder;
        _selections = selections;
        _mcVersion = new McVersion(mcVersion);
        _loaders = loaders;
        _loaderList = ModIndex.ModLoaderTypesToList(loaders);
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        // pick up already-installed mods for the dependency resolution
        _folder.Update();

        var selected = new List<GetModDependenciesTask.PackDependency>();
        try
        {
            var resolver = new ModSelectionResolver(_mcVersion, _loaders, _loaderList);
            var current = 0;
            foreach (var selection in _selections)
            {
                EmitStatus($"Resolving {ModPlatformTasks.ProviderKey(selection.Provider)} project {selection.ProjectId}...");
                var (pack, version) = await resolver.ResolveAsync(
                    selection.Provider, selection.ProjectId, selection.VersionId, cancellationToken);
                selected.Add(new GetModDependenciesTask.PackDependency(pack, version));
                SetProgress(++current, _selections.Count + 2);
            }
        }
        catch (ModResolveException e)
        {
            EmitFailed(e.Message);
            return;
        }

        EmitStatus("Resolving dependencies...");
        var dependencyTask = new GetModDependenciesTask(_instance, _folder, selected);
        await dependencyTask.StartAsync(cancellationToken);
        if (!dependencyTask.WasSuccessful)
        {
            EmitFailed(dependencyTask.FailReason.Length != 0 ? dependencyTask.FailReason : "dependency resolution failed");
            return;
        }
        SetProgress(_selections.Count + 1, _selections.Count + 2);

        var dependencies = dependencyTask.GetDependencies();
        var extraInfo = dependencyTask.GetExtraInfo();

        EmitStatus("Downloading mods...");
        var downloads = new SequentialTask("Download mods");
        foreach (var entry in selected.Concat(dependencies))
        {
            var isDependency = !selected.Contains(entry);
            string? requiredBy = null;
            if (isDependency && extraInfo.TryGetValue(entry.Pack.AddonId, out var extra) &&
                extra.RequiredByIds.Count != 0)
            {
                requiredBy = extra.RequiredByIds[0];
            }

            if (entry.Version.DownloadUrl.Length == 0)
            {
                EmitFailed($"no downloadable version of {entry.Pack.AddonId} was resolved");
                return;
            }

            downloads.AddTask(new ResourceDownloadTask(
                entry.Pack,
                entry.Version,
                _folder,
                isIndexed: true,
                downloadReason: isDependency ? "dependency" : "standalone",
                dependentOn: requiredBy ?? "",
                _instance));
        }

        await downloads.StartAsync(cancellationToken);
        if (!downloads.WasSuccessful)
        {
            EmitFailed(downloads.FailReason.Length != 0 ? downloads.FailReason : "the mod download failed");
            return;
        }
        SetProgress(_selections.Count + 2, _selections.Count + 2);
        EmitSucceeded();
    }

}

/// <summary>
/// Check an instance's installed (metadata-tracked) mods for updates through the provider
/// check passes, then apply everything found with a ModUpdateTask. The found/applied updates
/// are exposed for the task view's result payload ({@link BuildResult}).
/// </summary>
public sealed class ModsUpdateTask : FlintTask
{
    private sealed record PendingUpdate(ModUpdateEntryView View, IndexedVersion Version, string ProjectId, string Slug, string Name);

    private readonly MinecraftInstance _instance;
    private readonly ModFolderModel _folder;
    private readonly IReadOnlyList<string>? _filter;
    private readonly List<PendingUpdate> _updates = [];

    public ModsUpdateTask(MinecraftInstance instance, ModFolderModel folder, IReadOnlyList<string>? filter)
    {
        _instance = instance;
        _folder = folder;
        _filter = filter;
    }

    /// <summary>The update entries for the task view's optional result payload.</summary>
    public TaskResultView BuildResult() => new(_updates.Select(u => u.View).ToArray());

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        _folder.Update();

        var profile = _instance.GetPackProfile();
        var gameVersions = new List<McVersion> { new(profile.GetComponentVersion("net.minecraft")) };
        var loadersList = profile.GetModLoadersList();

        var candidates = _folder.AllMods().Where(m => !m.Fileinfo.Name.StartsWith('.'));
        if (_filter is { Count: > 0 })
        {
            var wanted = _filter.ToHashSet(StringComparer.Ordinal);
            candidates = candidates.Where(m => wanted.Contains(m.Fileinfo.Name) ||
                                               wanted.Contains(m.GetOriginalFileName()));
        }

        // the check passes refuse metadata-less resources; the API simply skips them
        var modrinth = new List<Resource>();
        var flame = new List<Resource>();
        foreach (var mod in candidates)
        {
            var metadata = _folder.GetMetadata(mod);
            if (metadata is null)
            {
                continue;
            }
            if (metadata.Provider == ResourceProvider.MODRINTH)
            {
                modrinth.Add(mod);
            }
            else if (metadata.Provider == ResourceProvider.FLAME)
            {
                flame.Add(mod);
            }
        }

        if (modrinth.Count != 0)
        {
            EmitStatus("Checking Modrinth updates...");
            var check = new ModrinthCheckUpdate(modrinth, gameVersions, loadersList, resource => _folder.GetMetadata(resource));
            await check.StartAsync(cancellationToken);
            if (!check.WasSuccessful)
            {
                EmitFailed(check.FailReason.Length != 0 ? check.FailReason : "the Modrinth update check failed");
                return;
            }
            var byProject = MetadataByProject(modrinth);
            foreach (var update in check.GetUpdates())
            {
                if (byProject.TryGetValue(update.Version.AddonId, out var metadata))
                {
                    Collect(update.Provider, update, metadata);
                }
            }
        }

        if (flame.Count != 0)
        {
            EmitStatus("Checking CurseForge updates...");
            var check = new FlameCheckUpdate(flame, gameVersions, loadersList, resource => _folder.GetMetadata(resource));
            await check.StartAsync(cancellationToken);
            if (!check.WasSuccessful)
            {
                EmitFailed(check.FailReason.Length != 0 ? check.FailReason : "the CurseForge update check failed");
                return;
            }
            var byProject = MetadataByProject(flame);
            foreach (var update in check.GetUpdates())
            {
                if (byProject.TryGetValue(update.Version.AddonId, out var metadata))
                {
                    Collect(update.Provider, update, metadata);
                }
            }
        }

        if (_updates.Count == 0)
        {
            EmitSucceeded();
            return;
        }

        EmitStatus($"Downloading {_updates.Count} update(s)...");
        // the fake pack of the C++ check tasks (only the fields the download task reads)
        var downloads = _updates.Select(update => new ResourceDownloadTask(
            new IndexedPack
            {
                AddonId = update.ProjectId,
                Slug = update.Slug,
                Name = update.Name,
                Provider = update.View.Provider == "modrinth" ? ResourceProvider.MODRINTH : ResourceProvider.FLAME,
            },
            update.Version,
            _folder,
            isIndexed: true,
            downloadReason: "update",
            instance: _instance));
        var apply = new ModUpdateTask(_folder, downloads);
        await apply.StartAsync(cancellationToken);
        if (!apply.WasSuccessful)
        {
            EmitFailed(apply.FailReason.Length != 0 ? apply.FailReason : "applying the updates failed");
            return;
        }
        EmitSucceeded();
    }

    // the two check passes expose structurally identical Update records (both are the port
    // of CheckUpdateTask::Update); one builder per shape keeps the mapping explicit
    private void Collect(ResourceProvider provider, ModrinthCheckUpdate.Update update, MetadataHandler.ModStruct metadata)
    {
        _updates.Add(new PendingUpdate(
            new ModUpdateEntryView(metadata.Filename, update.OldVersion, update.NewVersion,
                ModPlatformTasks.ProviderKey(provider)),
            update.Version,
            metadata.ProjectId,
            metadata.Slug,
            update.Name));
    }

    private void Collect(ResourceProvider provider, FlameCheckUpdate.Update update, MetadataHandler.ModStruct metadata)
    {
        _updates.Add(new PendingUpdate(
            new ModUpdateEntryView(metadata.Filename, update.OldVersion, update.NewVersion,
                ModPlatformTasks.ProviderKey(provider)),
            update.Version,
            metadata.ProjectId,
            metadata.Slug,
            update.Name));
    }

    private Dictionary<string, MetadataHandler.ModStruct> MetadataByProject(IEnumerable<Resource> resources)
    {
        var map = new Dictionary<string, MetadataHandler.ModStruct>(StringComparer.Ordinal);
        foreach (var resource in resources)
        {
            var metadata = _folder.GetMetadata(resource);
            if (metadata is { } value && !map.ContainsKey(value.ProjectId))
            {
                map[value.ProjectId] = value;
            }
        }
        return map;
    }

}
