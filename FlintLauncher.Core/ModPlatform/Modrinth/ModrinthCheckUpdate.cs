// SPDX-License-Identifier: GPL-3.0-only
// Port of modplatform/modrinth/ModrinthCheckUpdate.{h,cpp}: hash-based update detection.
// For every mod (its installed hash taken from the packwiz metadata), ask Modrinth for the
// latest version matching the game versions (+ loader, in loader passes); a mod has an
// update when the latest preferred file's hash differs from the installed one.
//
// Deviations (headless port):
//  - Resource::metadata() becomes the injectable metadataLookup delegate (the P3b Mod has
//    no metadata field; ModFolderModel.GetMetadata is the intended provider).
//  - The ResourceDownloadTask / GetModDependenciesTask wiring of the C++ Update struct is
//    not ported yet (P6-c); the update record carries the resolved IndexedVersion instead.
//  - The dependency list (m_deps) is not ported.
using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.Json;
using FlintLauncher.Core.Minecraft.Resources;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Tasks;
using FlintLauncher.Core.Versioning;

namespace FlintLauncher.Core.ModPlatform.Modrinth;

public sealed class ModrinthCheckUpdate : FlintTask
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

    /// <summary>Port of the checkFailed(resource, reason) signal.</summary>
    public event Action<Resource, string>? CheckFailed;

    private readonly IReadOnlyList<Resource> _resources;
    private readonly IReadOnlyList<McVersion> _gameVersions;
    private readonly List<Minecraft.ModLoaderType> _loadersList;
    private readonly Func<Resource, MetadataHandler.ModStruct?> _metadataLookup;
    private readonly IReadOnlyList<IndexedVersionType> _releaseTypes;
    private readonly ModrinthApi _modrinth;

    private readonly string _hashType = ProviderCapabilities.HashType(ResourceProvider.MODRINTH)[0];
    private readonly Dictionary<string, Resource> _mappings = new(StringComparer.Ordinal); // hash -> resource

    private readonly List<Update> _updates = [];
    private int _loaderIdx;
    private int _initialSize;

    public ModrinthCheckUpdate(IReadOnlyList<Resource> resources,
                               IReadOnlyList<McVersion> mcVersions,
                               IReadOnlyList<Minecraft.ModLoaderType> loadersList,
                               Func<Resource, MetadataHandler.ModStruct?> metadataLookup,
                               IReadOnlyList<IndexedVersionType>? releaseTypes = null,
                               ModrinthApi? modrinth = null)
    {
        _modrinth = modrinth ?? new ModrinthApi();
        _resources = resources;
        _gameVersions = mcVersions;
        _loadersList = [.. loadersList];
        _metadataLookup = metadataLookup;
        _releaseTypes = releaseTypes ?? [];

        if (_loadersList.Count != 0)
        { // this is for mods so append all the other posible loaders to the initial list
            _initialSize = _loadersList.Count;
            Minecraft.ModLoaderType modLoaders = Minecraft.ModLoaderType.None;
            foreach (var m in resources)
            {
                modLoaders |= _metadataLookup(m)?.Loaders ?? Minecraft.ModLoaderType.None;
            }
            foreach (var l in _loadersList)
            {
                modLoaders &= ~l;
            }
            _loadersList.AddRange(ModIndex.ModLoaderTypesToList(modLoaders));
        }
    }

    public IReadOnlyList<Update> GetUpdates() => _updates;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        EmitStatus("Preparing resources for Modrinth...");

        // Sadly the API can only handle one hash type per call, so we need to generate a new
        // hash when the stored one is inadequate (hashing is synchronous in this port).
        foreach (var resource in _resources)
        {
            var metadata = _metadataLookup(resource);
            if (metadata is null)
            {
                EmitFailed($"Resource {resource.Name} has no metadata to check against.");
                return;
            }
            var hash = metadata.Hash;

            if (metadata.HashFormat != _hashType)
            {
                var hashTask = Hashing.HashUtils.CreateHasher(resource.Fileinfo.FullName, ResourceProvider.MODRINTH);
                await hashTask.StartAsync(cancellationToken);
                if (!hashTask.WasSuccessful)
                {
                    EmitFailed("Failed to generate hash");
                    return;
                }
                hash = hashTask.GetResult();
            }
            _mappings[hash] = resource;
        }

        await CheckNextLoader(cancellationToken);
    }

    private async Task GetUpdateModsForLoader(Minecraft.ModLoaderType? loader, bool forceModLoaderCheck,
        CancellationToken cancellationToken)
    {
        _loaderIdx++;

        EmitStatus("Waiting for the API response from Modrinth...");

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

        var (job, request) = _modrinth.LatestVersions(hashes, _hashType, _gameVersions, loader,
            _releaseTypes);
        await job.StartAsync(cancellationToken);
        if (!job.WasSuccessful)
        {
            await CheckNextLoader(cancellationToken);
            return;
        }
        await CheckVersionsResponse(request.BodyAsString, loader, cancellationToken);
    }

    private async Task CheckVersionsResponse(string response, Minecraft.ModLoaderType? loader,
        CancellationToken cancellationToken)
    {
        EmitStatus("Parsing the API response from Modrinth...");

        JsonNode entries;
        try
        {
            entries = JsonNode.Parse(response) ?? throw new JsonException("empty response");
        }
        catch (JsonException e)
        {
            EmitFailed(e.Message);
            return;
        }

        {
            var processed = new List<string>();
            foreach (var (hash, resource) in _mappings)
            {
                // If the returned project is empty, but we have Modrinth metadata,
                // it means this specific version is not available
                if (entries[hash] is not { } projectNode)
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
                var projectVer = ModrinthPackIndex.LoadIndexedPackVersion(projectNode, _hashType, loaderFilter);
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
                        OldHash = hash,
                        OldVersion = oldVersion,
                        NewVersion = projectVer.VersionNumber,
                        NewVersionType = projectVer.VersionType,
                        Changelog = projectVer.Changelog,
                        Provider = ResourceProvider.MODRINTH,
                        Version = projectVer,
                        Enabled = resource.Enabled,
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
            EmitSucceeded();
            return;
        }
        if (_loaderIdx < _loadersList.Count)
        { // this are mods so check with loaders
            await GetUpdateModsForLoader(_loadersList[_loaderIdx], _loaderIdx > _initialSize, cancellationToken);
            return;
        }
        if (_loadersList.Count == 0 && _loaderIdx == 0)
        { // this are other resources no need to check more than once with empty loader
            await GetUpdateModsForLoader(null, forceModLoaderCheck: false, cancellationToken);
            return;
        }

        foreach (var resource in _mappings.Values)
        {
            CheckFailed?.Invoke(resource,
                "No valid version found for this resource. It's probably unavailable for the current game version / mod loader.");
        }

        EmitSucceeded();
    }
}
