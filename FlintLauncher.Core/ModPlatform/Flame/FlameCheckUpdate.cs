// SPDX-License-Identifier: GPL-3.0-only
// Port of modplatform/flame/FlameCheckUpdate.{h,cpp}: CurseForge update detection.
// For every resource (its installed state taken from the packwiz Flame metadata), fetch the
// project's file list, pick the latest file matching the instance loaders (FlameApi.
// GetLatestVersion) and compare hashes; files without a download URL are reported through
// the checkFailed(recoverUrl) path after the project info was fetched (collectBlockedMods).
//
// Deviations (headless port):
//  - Resource::metadata() becomes the injectable metadataLookup delegate (the P3b Mod has
//    no metadata field; ModFolderModel.GetMetadata is the intended provider).
//  - The ResourceDownloadTask / GetModDependenciesTask wiring of the C++ Update struct is
//    not ported yet (P6-c); the update record carries the resolved IndexedVersion instead.
//  - checkFailed carries the recoverUrl as a third event argument ("" when none).
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.Json;
using FlintLauncher.Core.Minecraft.Resources;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Tasks;
using FlintLauncher.Core.Versioning;

namespace FlintLauncher.Core.ModPlatform.Flame;

using FlintLauncher.Core.Minecraft;

public sealed class FlameCheckUpdate : FlintTask
{
    /// <summary>Port of CheckUpdateTask::Update (minus the download task, see header note).</summary>
    public sealed class Update
    {
        public required string Name { get; init; }
        public required string OldHash { get; init; }
        public required string OldVersion { get; init; }
        public required string NewVersion { get; init; }
        public IndexedVersionType NewVersionType { get; init; } = IndexedVersionType.Invalid;
        public string Changelog { get; init; } = "";
        public required ResourceProvider Provider { get; init; }
        public required IndexedVersion Version { get; init; }
        public bool Enabled { get; init; } = true;
    }

    /// <summary>Port of the checkFailed(resource, reason, recoverUrl = "") signal.</summary>
    public event Action<Resource, string, string>? CheckFailed;

    private readonly IReadOnlyList<Resource> _resources;
    private readonly IReadOnlyList<McVersion> _gameVersions;
    private readonly List<ModLoaderType> _loadersList;
    private readonly Func<Resource, MetadataHandler.ModStruct?> _metadataLookup;
    private readonly IReadOnlyList<IndexedVersionType> _releaseTypes;
    private readonly FlameApi _flame;

    private readonly Dictionary<Resource, string> _blocked = new();

    private readonly List<Update> _updates = [];

    public FlameCheckUpdate(IReadOnlyList<Resource> resources,
                            IReadOnlyList<McVersion> mcVersions,
                            IReadOnlyList<ModLoaderType> loadersList,
                            Func<Resource, MetadataHandler.ModStruct?> metadataLookup,
                            IReadOnlyList<IndexedVersionType>? releaseTypes = null,
                            FlameApi? flame = null)
    {
        _flame = flame ?? new FlameApi();
        _resources = resources;
        _gameVersions = mcVersions;
        _loadersList = [.. loadersList];
        _metadataLookup = metadataLookup;
        _releaseTypes = releaseTypes ?? [];
    }

    public IReadOnlyList<Update> GetUpdates() => _updates;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        EmitStatus("Preparing resources for CurseForge...");

        /* Check for update:
         * - Get latest version available
         * - Compare hash of the latest version with the current hash
         * - If equal, no updates, else, there's updates, so add to the list
         * */
        foreach (var resource in _resources)
        {
            var metadata = _metadataLookup(resource);
            if (metadata is null)
            {
                EmitFailed($"Resource {resource.Name} has no metadata to check against.");
                return;
            }

            // Fake pack with the necessary info to pass to the download task :)
            var project = new IndexedPack
            {
                AddonId = metadata.ProjectId,
            };
            var versionsUrl = GetVersionsUrl(project);
            if (versionsUrl is null)
            {
                continue;
            }

            var request = NetRequest.MakeByteArray(new Uri(versionsUrl));
            await request.StartAsync(cancellationToken);
            if (!request.WasSuccessful)
            {
                continue; // (the C++ reports per-request failures through the NetJob callbacks)
            }
            await GetLatestVersionCallback(resource, metadata, request.BodyAsString, cancellationToken);
        }

        await CollectBlockedMods(cancellationToken);
    }

    private string? GetVersionsUrl(IndexedPack project)
    {
        return _flame.GetVersionsURL(new VersionSearchArgs
        {
            Pack = project,
            McVersions = _gameVersions.ToList(),
        });
    }

    private async Task GetLatestVersionCallback(Resource resource, MetadataHandler.ModStruct metadata,
        string responseBody, CancellationToken cancellationToken)
    {
        JsonNode? doc;
        try
        {
            doc = JsonNode.Parse(responseBody);
        }
        catch (JsonException e)
        {
            // (the C++ only logs parse problems here)
            EmitStatus("Failed to parse the API response: " + e.Message);
            return;
        }

        // Fake pack with the necessary info to pass to the download task :)
        var pack = new IndexedPack
        {
            Name = resource.Name,
            Slug = metadata.Slug,
            AddonId = metadata.ProjectId,
            Provider = ResourceProvider.FLAME,
        };
        {
            try
            {
                var obj = JsonUtils.RequireObject(doc);
                var arr = FlameJson.RequireArray(obj, "data");

                FlameMod.LoadIndexedPackVersions(pack, arr);
            }
            catch (JsonParseException e)
            {
                // "Failed to parse response from a version request."
                EmitStatus(e.Message);
            }
        }

        var latestVer = FlameApi.GetLatestVersion(pack.Versions, _loadersList, metadata.Loaders,
            checkLoaders: _loadersList.Count != 0, _releaseTypes);

        EmitStatus($"Parsing the API response from CurseForge for '{resource.Name}'...");

        if (latestVer is null || latestVer.AddonId.Length == 0)
        {
            var reason = resource is Mod
                ? "No valid version found for this resource. It's probably unavailable for the current game version / mod loader."
                : "No valid version found for this resource. It's probably unavailable for the current game version.";
            CheckFailed?.Invoke(resource, reason, "");
            return;
        }

        if (latestVer.DownloadUrl.Length == 0 && latestVer.FileId != metadata.FileId)
        {
            _blocked[resource] = latestVer.FileId;
            return;
        }

        if (latestVer.Hash.Length != 0
            && (metadata.Hash != latestVer.Hash || resource.Status == ResourceStatus.NotInstalled))
        {
            var oldVersion = metadata.VersionNumber;
            if (oldVersion.Length == 0)
            {
                oldVersion = resource.Status == ResourceStatus.NotInstalled ? "Not installed" : "Unknown";
            }

            var changelog = await _flame.GetModFileChangelog(
                int.Parse(latestVer.AddonId, CultureInfo.InvariantCulture),
                int.Parse(latestVer.FileId, CultureInfo.InvariantCulture), cancellationToken);

            _updates.Add(new Update
            {
                Name = pack.Name,
                OldHash = metadata.Hash,
                OldVersion = oldVersion,
                NewVersion = latestVer.Version,
                NewVersionType = latestVer.VersionType,
                Changelog = changelog,
                Provider = ResourceProvider.FLAME,
                Version = latestVer,
                Enabled = resource.Enabled,
            });
        }
    }

    /// <summary>Port of FlameCheckUpdate::collectBlockedMods: report the blocked updates
    /// with a manual-download URL once the project info (websiteUrl) is known.</summary>
    private async Task CollectBlockedMods(CancellationToken cancellationToken)
    {
        var addonIds = new List<string>();
        var quickSearch = new Dictionary<string, Resource>();
        foreach (var (resource, _) in _blocked)
        {
            var metadata = _metadataLookup(resource);
            if (metadata is null)
            {
                continue;
            }
            var addonId = metadata.ProjectId;
            addonIds.Add(addonId);
            quickSearch[addonId] = resource;
        }

        if (addonIds.Count == 0)
        {
            EmitSucceeded();
            return;
        }

        NetJob projJob;
        NetRequest projRequest;
        var single = addonIds.Count == 1;
        if (single)
        {
            (projJob, projRequest) = _flame.GetProject(addonIds[0]);
        }
        else
        {
            (projJob, projRequest) = _flame.GetProjects(addonIds);
        }

        await projJob.StartAsync(cancellationToken);
        if (projJob.WasSuccessful)
        {
            JsonNode? doc;
            try
            {
                doc = JsonNode.Parse(projRequest.BodyAsString);
            }
            catch (JsonException)
            {
                doc = null;
            }

            if (doc is not null)
            {
                {
                    try
                    {
                        var rootObj = JsonUtils.RequireObject(doc);
                        var data = FlameJson.Ptr(rootObj, "data");
                        List<JsonNode> entries;
                        if (single)
                        {
                            entries = [JsonUtils.RequireObject(data, "'data'")];
                        }
                        else
                        {
                            entries = JsonUtils.ToNodeList(JsonUtils.RequireArray(data, "'data'"));
                        }

                        foreach (var entryObj in entries.Where(e => e is not null))
                        {

                            var id = FlameJson.RequireInteger(entryObj, "id").ToString(CultureInfo.InvariantCulture);
                            if (!quickSearch.TryGetValue(id, out var resource))
                            {
                                continue;
                            }

                            try
                            {
                                EmitStatus($"Parsing API response from CurseForge for '{resource.Name}'...");

                                var pack = new IndexedPack();
                                FlameMod.LoadIndexedPack(pack, entryObj);
                                var recoverUrl = $"{pack.WebsiteUrl}/download/{_blocked[resource]}";
                                CheckFailed?.Invoke(resource,
                                    "Resource has a new update available, but is not downloadable using CurseForge.",
                                    recoverUrl);
                            }
                            catch (JsonParseException)
                            {
                            }
                        }
                    }
                    catch (JsonParseException)
                    {
                    }
                }
            }
        }
        // do not care much about errors (the C++ finishes the task regardless)
        EmitSucceeded();
    }
}
