// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
//
// CurseForge update detection. For every resource (its installed state taken from the packwiz
// Flame metadata), fetch the project's file list, pick the latest file matching the instance
// loaders and compare hashes. A file that CurseForge does not serve to third-party launchers
// is reported with a manual-download URL once the project info is known.
//
// The metadata lookup is injected rather than read off the resource: Resource carries no
// metadata field. An Update carries the resolved IndexedVersion, not a download task — the
// caller builds the downloads.
//
// CheckAsync returns both the updates and one failure entry per resource whose update could
// not be delivered, carrying the recover URL where there is one.

using System.Globalization;
using System.Text.Json;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Minecraft.Resources;
using FlintLauncher.Core.Versioning;
using FlintLauncher.ExternalApi;
using FlintLauncher.ExternalApi.Flame;

namespace FlintLauncher.Core.ModPlatform.Flame;

public sealed class FlameCheckUpdate(
    IReadOnlyList<Resource> resources,
    IReadOnlyList<McVersion> mcVersions,
    IReadOnlyList<ModLoaderType> loadersList,
    Func<Resource, MetadataHandler.ModStruct?> metadataLookup,
    FlameClient flame,
    HttpClient http,
    IReadOnlyList<IndexedVersionType>? releaseTypes = null)
{
    /// <summary>One available update: the installed and available version numbers, the
    /// available version's release channel and changelog, and the resolved version the caller
    /// downloads.</summary>
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

    /// <summary>The check's outcome: the updates found, plus one entry per resource whose
    /// update could not be delivered. A failure carries a recover URL when CurseForge offers a
    /// manual download for the file.</summary>
    public sealed record CheckResult(
        IReadOnlyList<Update> Updates,
        IReadOnlyList<(Resource Resource, string Reason, string RecoverUrl)> Failures);

    private readonly List<ModLoaderType> _loadersList = [.. loadersList];
    private readonly IReadOnlyList<IndexedVersionType> _releaseTypes = releaseTypes ?? [];

    private readonly Dictionary<Resource, string> _blocked = new();

    private readonly List<Update> _updates = [];
    private readonly List<(Resource Resource, string Reason, string RecoverUrl)> _failures = [];

    /// <summary>Runs the check. A resource without metadata to compare against fails the whole
    /// run; per-resource API failures are returned as <see cref="CheckResult.Failures"/>.</summary>
    public async Task<CheckResult> CheckAsync(CancellationToken cancellationToken)
    {
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
                throw new InvalidOperationException(
                    $"Resource {resource.Name} has no metadata to check against.");
            }

            // Fake pack with the necessary info to pass to the download task :)
            var project = new IndexedPack
            {
                AddonId = metadata.ProjectId,
            };

            FlameFileList versionsDoc;
            try
            {
                versionsDoc = await flame.Files
                    .List(project.AddonId, gameVersion: mcVersions.Count != 0 ? mcVersions[0].ToString() : null)
                    .SendTo(http, cancellationToken);
            }
            catch (FlameApiException)
            {
                continue; // a failed file-list request skips this resource
            }

            await GetLatestVersionCallback(resource, metadata, versionsDoc, cancellationToken);
        }

        await CollectBlockedMods(cancellationToken);
        return new CheckResult(_updates, _failures);
    }

    private async Task GetLatestVersionCallback(Resource resource, MetadataHandler.ModStruct metadata,
        FlameFileList versionsDoc, CancellationToken cancellationToken)
    {
        // Fake pack with the necessary info to pass to the download task :)
        var pack = new IndexedPack
        {
            Name = resource.Name,
            Slug = metadata.Slug,
            AddonId = metadata.ProjectId,
            Provider = ResourceProvider.Flame,
        };
        try
        {
            FlameMod.LoadIndexedPackVersions(pack, versionsDoc.Data);
        }
        catch (JsonException e)
        {
            // "Failed to parse response from a version request."
            _failures.Add((resource, e.Message, ""));
        }

        var latestVer = FlameMod.GetLatestVersion(pack.Versions, _loadersList, metadata.Loaders,
            checkLoaders: _loadersList.Count != 0, _releaseTypes);

        if (latestVer is null || latestVer.AddonId.Length == 0)
        {
            var reason = resource is Mod
                ? "No valid version found for this resource. It's probably unavailable for the current game version / mod loader."
                : "No valid version found for this resource. It's probably unavailable for the current game version.";
            _failures.Add((resource, reason, ""));
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

            var changelog = "";
            try
            {
                changelog = await flame.Files.Changelog(
                        int.Parse(latestVer.AddonId, CultureInfo.InvariantCulture),
                        int.Parse(latestVer.FileId, CultureInfo.InvariantCulture))
                    .SendTo(http, cancellationToken);
            }
            catch (FlameApiException)
            {
                // the changelog fetch is best-effort (the former "" on any failure)
            }

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

    /// <summary>Reports the blocked updates with a manual-download URL once the project info
    /// (websiteUrl) is known.</summary>
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
            return;
        }

        var single = addonIds.Count == 1;
        List<FlameProject?>? entries = null;
        try
        {
            entries = single
                ?
                [
                    (
                        await flame.Projects.ById(addonIds[0]).SendTo(http, cancellationToken)).Data
                    ?? throw new JsonException("'data' is not an object"),
                ]
                : (await flame.Projects.ByIds(addonIds).SendTo(http, cancellationToken)).Data
                  ?? throw new JsonException("'data' is not an array");
        }
        catch (Exception e) when (e is FlameApiException or JsonException)
        {
            // a failed fetch or a broken response body only skips the blocked-download
            // recovery lookup; the check itself still reports what it found
        }

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
                    var pack = new IndexedPack();
                    FlameMod.LoadIndexedPack(pack, entryObj);
                    var recoverUrl = $"{pack.WebsiteUrl}/download/{_blocked[resource]}";
                    _failures.Add((resource,
                        "Resource has a new update available, but is not downloadable using CurseForge.",
                        recoverUrl));
                }
                catch (JsonException) { }
            }
        }

        // a failed lookup only skips the blocked-download recovery
    }
}
