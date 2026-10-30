// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
//
// Hash-based update detection against Modrinth. For every mod (its installed hash taken from
// the packwiz metadata), ask Modrinth for the latest version matching the game versions and,
// in loader passes, the instance loaders; a mod has an update when the latest preferred
// file's hash differs from the installed one.
//
// The metadata lookup is injected rather than read off the resource: Resource carries no
// metadata field. An Update carries the resolved IndexedVersion, not a download task — the
// caller builds the downloads. Dependencies are not resolved by the check.
//
// CheckAsync returns both the updates and one failure entry per resource the API could not
// resolve, so a caller that wants the per-resource outcome does not subscribe to an event.

using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Minecraft.Resources;
using FlintLauncher.Core.ModPlatform.Hashing;
using FlintLauncher.Core.Versioning;
using FlintLauncher.ExternalApi;
using FlintLauncher.ExternalApi.Modrinth;

namespace FlintLauncher.Core.ModPlatform.Modrinth;

public sealed class ModrinthCheckUpdate
{
    /// <summary>One available update: the installed and available version numbers, and the
    /// resolved version the caller downloads.</summary>
    public sealed class Update
    {
        public required string Name { get; init; }
        public required string OldVersion { get; init; }
        public required string NewVersion { get; init; }
        public required ResourceProvider Provider { get; init; }
        public required IndexedVersion Version { get; init; }
    }

    /// <summary>The check's outcome: the updates found, plus one entry per resource the API
    /// could not resolve.</summary>
    public sealed record CheckResult(
        IReadOnlyList<Update> Updates,
        IReadOnlyList<(Resource Resource, string Reason)> Failures);

    private readonly IReadOnlyList<Resource> _resources;
    private readonly IReadOnlyList<McVersion> _gameVersions;
    private readonly List<ModLoaderType> _loadersList;
    private readonly Func<Resource, MetadataHandler.ModStruct?> _metadataLookup;
    private readonly IReadOnlyList<IndexedVersionType> _releaseTypes;
    private readonly ModrinthClient _modrinth;
    private readonly HttpClient _http;

    private readonly string _hashType = ProviderCapabilities.HashType(ResourceProvider.Modrinth)[0];
    private readonly Dictionary<string, Resource> _mappings = new(StringComparer.Ordinal); // hash -> resource

    private readonly List<Update> _updates = [];
    private readonly List<(Resource Resource, string Reason)> _failures = [];
    private int _loaderIdx;
    private readonly int _initialSize;

    public ModrinthCheckUpdate(IReadOnlyList<Resource> resources,
        IReadOnlyList<McVersion> mcVersions,
        IReadOnlyList<ModLoaderType> loadersList,
        Func<Resource, MetadataHandler.ModStruct?> metadataLookup,
        ModrinthClient modrinth,
        HttpClient http,
        IReadOnlyList<IndexedVersionType>? releaseTypes = null)
    {
        _modrinth = modrinth;
        _http = http;
        _resources = resources;
        _gameVersions = mcVersions;
        _loadersList = [.. loadersList];
        _metadataLookup = metadataLookup;
        _releaseTypes = releaseTypes ?? [];

        if (_loadersList.Count != 0)
        {
            // this is for mods so append all the other posible loaders to the initial list
            _initialSize = _loadersList.Count;
            ModLoaderType modLoaders = ModLoaderType.None;
            foreach (var m in resources)
            {
                modLoaders |= _metadataLookup(m)?.Loaders ?? ModLoaderType.None;
            }

            foreach (var l in _loadersList)
            {
                modLoaders &= ~l;
            }

            _loadersList.AddRange(ModIndex.ModLoaderTypesToList(modLoaders));
        }
    }

    /// <summary>Runs the check. A resource without metadata to compare against fails the whole
    /// run; resources the API could not resolve are returned as <see cref="CheckResult.Failures"/>.</summary>
    public async Task<CheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        // Sadly the API can only handle one hash type per call, so we need to generate a new
        // hash when the stored one is inadequate (hashing is synchronous in this port).
        foreach (var resource in _resources)
        {
            var metadata = _metadataLookup(resource);
            if (metadata is null)
            {
                throw new InvalidOperationException(
                    $"Resource {resource.Name} has no metadata to check against.");
            }

            var hash = metadata.Hash;

            if (metadata.HashFormat != _hashType)
            {
                hash = HashUtils.HashFile(resource.Fileinfo.FullName, HashUtils.HasherAlgorithm(ResourceProvider.Modrinth));
                if (hash.Length == 0)
                {
                    throw new InvalidOperationException($"Empty hash for {resource.Name}!");
                }
            }

            _mappings[hash] = resource;
        }

        await CheckNextLoader(cancellationToken);
        return new CheckResult(_updates, _failures);
    }

    private async Task GetUpdateModsForLoader(ModLoaderType? loader, bool forceModLoaderCheck,
        CancellationToken cancellationToken)
    {
        _loaderIdx++;

        List<string> hashes;
        if (forceModLoaderCheck && loader is { } filter)
        {
            hashes = _mappings.Where(kv => ((_metadataLookup(kv.Value)?.Loaders ?? 0) & filter) != 0)
                .Select(kv => kv.Key).ToList();
        }
        else
        {
            hashes = [.. _mappings.Keys];
        }

        if (hashes.Count == 0)
        {
            await CheckNextLoader(cancellationToken);
            return;
        }

        Dictionary<string, ModrinthVersion> entries;
        try
        {
            // #3404: "version_types" is only written when at least one valid release
            // channel survives the translation
            List<string>? versionTypes = null;
            if (_releaseTypes is { Count: > 0 })
            {
                var types = IndexedVersionType.ToModrinthList(_releaseTypes);
                versionTypes = types.Count != 0 ? types : null;
            }

            entries = await _modrinth.Versions.Latest(hashes, _hashType,
                loader is { } loaderFlags
                    ? ModIndex.ModLoaderTypesToList(loaderFlags).Select(ModIndex.GetModLoaderAsString).ToList()
                    : null,
                [.. _gameVersions.Select(ModrinthVersionFormat.ToWire)],
                versionTypes).SendTo(_http, cancellationToken);
        }
        catch (Exception e) when (e is ModrinthApiException)
        {
            await CheckNextLoader(cancellationToken);
            return;
        }

        await CheckVersionsResponse(entries, loader, cancellationToken);
    }

    private async Task CheckVersionsResponse(Dictionary<string, ModrinthVersion> entries, ModLoaderType? loader,
        CancellationToken cancellationToken)
    {
        {
            var processed = new List<string>();
            foreach (var (hash, resource) in _mappings)
            {
                // If the returned project is empty, but we have Modrinth metadata,
                // it means this specific version is not available
                // doc.value()[hash] on a non-object document is undefined upstream: every
                // mapping just stays unprocessed
                if (!entries.TryGetValue(hash, out var projectVersion))
                {
                    // stays in the mappings for the next loader pass
                    continue;
                }

                processed.Add(hash);

                // Sometimes a version may have multiple files, one with "forge" and one with "fabric",
                // so we may want to filter it
                string loaderFilter = "";
                if (loader is { } loaderValue && loaderValue != 0)
                {
                    var modLoaders = ModIndex.ModLoaderTypesToList(loaderValue);
                    if (modLoaders.Count != 0)
                    {
                        loaderFilter = ModIndex.GetModLoaderAsString(modLoaders[0]);
                    }
                }

                // Currently, we rely on a couple heuristics to determine whether an update is actually
                // available or not:
                // - The file needs to be preferred: It is either the primary file, or the one found via
                //   (explicit) usage of the loader_filter
                // - The version reported by the JAR is different from the version reported by the
                //   indexed version (it's usually the case)
                var projectVer = ModrinthPackIndex.LoadIndexedPackVersion(projectVersion, _hashType, loaderFilter);
                if (projectVer.DownloadUrl.Length == 0)
                {
                    // "Modrinth mod without download url!"
                    continue;
                }

                var metadata = _metadataLookup(resource);
                var status = resource.Status;
                if ((projectVer.Hash != hash && projectVer.IsPreferred) || status == ResourceStatus.NotInstalled)
                {
                    var oldVersion = metadata?.VersionNumber ?? "";
                    if (oldVersion.Length == 0)
                    {
                        oldVersion = status == ResourceStatus.NotInstalled ? "Not installed" : "Unknown";
                    }

                    _updates.Add(new Update
                    {
                        Name = metadata?.Name ?? resource.Name,
                        OldVersion = oldVersion,
                        NewVersion = projectVer.VersionNumber,
                        Provider = ResourceProvider.Modrinth,
                        Version = projectVer,
                    });
                }
            }

            foreach (var hash in processed)
            {
                _mappings.Remove(hash);
            }
        }

        await CheckNextLoader(cancellationToken);
    }

    private async Task CheckNextLoader(CancellationToken cancellationToken)
    {
        if (_mappings.Count == 0)
        {
            return;
        }

        if (_loaderIdx < _loadersList.Count)
        {
            // this are mods so check with loaders
            await GetUpdateModsForLoader(_loadersList[_loaderIdx], _loaderIdx > _initialSize, cancellationToken);
            return;
        }

        if (_loadersList.Count == 0 && _loaderIdx == 0)
        {
            // this are other resources no need to check more than once with empty loader
            await GetUpdateModsForLoader(null, forceModLoaderCheck: false, cancellationToken);
            return;
        }

        foreach (var resource in _mappings.Values)
        {
            _failures.Add((resource,
                "No valid version found for this resource. It's probably unavailable for the current game version / mod loader."));
        }
    }
}
