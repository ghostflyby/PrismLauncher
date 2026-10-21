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
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Minecraft.Resources;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Tasks;
using FlintLauncher.Core.Versioning;

namespace FlintLauncher.Core.ModPlatform.Flame;

public sealed class FlameCheckUpdate(
    IReadOnlyList<Resource> resources,
    IReadOnlyList<McVersion> mcVersions,
    IReadOnlyList<ModLoaderType> loadersList,
    Func<Resource, MetadataHandler.ModStruct?> metadataLookup,
    IReadOnlyList<IndexedVersionType>? releaseTypes = null,
    FlameApi? flame = null)
    : FlintTask
{
    /// <summary>Port of CheckUpdateTask::Update (minus the download task, see header note);
    /// the UI-only old-hash/enabled members are not ported.</summary>
    public sealed class Update
    {
        public required string Name { get; init; }
        public required string OldVersion { get; init; }
        public required string NewVersion { get; init; }
        public IndexedVersionType NewVersionType { get; init; } = IndexedVersionType.Invalid;
        public string Changelog { get; init; } = "";
        public required ResourceProvider Provider { get; init; }
        public required IndexedVersion Version { get; init; }
    }

    /// <summary>Port of the checkFailed(resource, reason, recoverUrl = "") signal.</summary>
    public event Action<Resource, string, string>? CheckFailed;

    private readonly List<ModLoaderType> _loadersList = [.. loadersList];
    private readonly IReadOnlyList<IndexedVersionType> _releaseTypes = releaseTypes ?? [];
    private readonly FlameApi _flame = flame ?? new FlameApi();

    private readonly Dictionary<Resource, string> _blocked = new();

    private readonly List<Update> _updates = [];

    public IReadOnlyList<Update> GetUpdates() => _updates;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        EmitStatus("Preparing resources for CurseForge...");

        /* Check for update:
         * - Get latest version available
         * - Compare hash of the latest version with the current hash
         * - If equal, no updates, else, there's updates, so add to the list
         * */
        foreach (var resource in resources)
        {
            var metadata = metadataLookup(resource);
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

    private string GetVersionsUrl(IndexedPack project)
    {
        return _flame.GetVersionsUrl(new VersionSearchArgs
        {
            Pack = project,
            McVersions = mcVersions.ToList(),
        });
    }

    private async Task GetLatestVersionCallback(Resource resource, MetadataHandler.ModStruct metadata,
        string responseBody, CancellationToken cancellationToken)
    {
        try
        {
            // parse-shape validation only (the typed reads below re-parse the body)
            JsonNode.Parse(responseBody);
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
            Provider = ResourceProvider.Flame,
        };
        {
            try
            {
                var versionsDoc = FlameMod.ParseResponse(responseBody,
                    FlameModJsonContext.Default.FlameFileListJson);

                FlameMod.LoadIndexedPackVersions(pack, versionsDoc.Data);
            }
            catch (JsonException e)
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
                OldVersion = oldVersion,
                NewVersion = latestVer.Version,
                NewVersionType = latestVer.VersionType,
                Changelog = changelog,
                Provider = ResourceProvider.Flame,
                Version = latestVer,
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
            var metadata = metadataLookup(resource);
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
            // a broken response body only skips the blocked-download recovery lookup (the
            // task finishes regardless, like the C++)
            List<FlameProjectJson?>? entries = null;
            try
            {
                if (single)
                {
                    entries =
                    [
                        FlameMod.ParseResponse(projRequest.BodyAsString,
                            FlameModJsonContext.Default.FlameProjectEnvelopeJson).Data
                        ?? throw new JsonException("'data' is not an object"),
                    ];
                }
                else
                {
                    entries = FlameMod.ParseResponse(projRequest.BodyAsString,
                                  FlameModJsonContext.Default.FlameProjectListJson).Data
                              ?? throw new JsonException("'data' is not an array");
                }
            }
            catch (JsonException) { }

            if (entries is not null)
            {
                foreach (var entryObj in entries)
                {
                    if (entryObj is null)
                    {
                        continue;
                    }

                    var id = (entryObj.Id ?? 0).ToString(CultureInfo.InvariantCulture);
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
                    catch (JsonException) { }
                }
            }
        }

        // do not care much about errors (the C++ finishes the task regardless)
        EmitSucceeded();
    }
}
