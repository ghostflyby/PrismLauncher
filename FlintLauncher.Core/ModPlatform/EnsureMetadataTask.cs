// SPDX-License-Identifier: GPL-3.0-only
// Port of modplatform/EnsureMetadataTask.{h,cpp} (Modrinth branch) plus the Modrinth
// callers' view of minecraft/mod/tasks/LocalResourceUpdateTask.{h,cpp}: make sure every
// downloaded resource has its .pw.toml metadata written.
//
// Deviations:
//  - The Flame branch is P6-b (the constructor fails the task for it).
//  - The ConcurrentTask hash-priming pass lives in the callers (HashUtils.HashFile is
//    synchronous here), so the task takes the hash → resource map directly.
//  - Resource::setMetadata does not exist yet (the P3b Mod port has no metadata field),
//    so the freshly created ModStruct is handed to callers through the MetadataReady
//    event (ModFolderModel re-reads it from disk through its hook).
using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Json;
using FlintLauncher.Core.Minecraft.Resources;
using FlintLauncher.Core.Tasks;

namespace FlintLauncher.Core.ModPlatform;

using FlintLauncher.Core.ModPlatform.Modrinth;

public sealed class EnsureMetadataTask : FlintTask
{
    private readonly Dictionary<string, Resource> _resources; // hash -> resource
    private readonly string _indexDir;
    private readonly ResourceProvider _provider;
    private readonly Dictionary<string, IndexedVersion> _tempVersions = new(StringComparer.Ordinal);

    public EnsureMetadataTask(Dictionary<string, Resource> resources, string indexDir, ResourceProvider provider)
    {
        _resources = resources;
        _indexDir = indexDir;
        _provider = provider;
    }

    /// <summary>Port of the metadataReady(resource) signal, carrying the fresh metadata.</summary>
    public event Action<Resource, MetadataHandler.ModStruct>? MetadataReady;

    /// <summary>Port of the metadataFailed(resource) signal.</summary>
    public event Action<Resource, string>? MetadataFailed;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        EmitStatus("Checking if resources have metadata...");

        switch (_provider)
        {
            case ResourceProvider.MODRINTH:
                await ModrinthFlow(cancellationToken);
                break;
            default:
                // TODO(P6-b): the Flame metadata flow (fingerprint matching).
                EmitFailed("Metadata generation is only implemented for Modrinth in this phase.");
                return;
        }

        // invalidadeLeftover(): everything that made it this far without metadata failed
        foreach (var (hash, resource) in _resources)
        {
            EmitMetadataFailed(resource, $"No metadata was generated for hash {hash}");
        }
        EmitSucceeded();
    }

    private async Task ModrinthFlow(CancellationToken cancellationToken)
    {
        var hashType = ProviderCapabilities.HashType(ResourceProvider.MODRINTH)[0];

        // ---------------------------------------------------------- versions task
        var (versionJob, versionRequest) = ModrinthApi.CurrentVersions(_resources.Keys.ToList(), hashType);
        await versionJob.StartAsync(cancellationToken);
        if (!versionJob.WasSuccessful)
        {
            EmitFailed(versionJob.FailReason);
            return;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(versionRequest.BodyAsString);
        }
        catch (JsonException e)
        {
            EmitFailed(e.Message);
            return;
        }

        using (doc)
        {
            JsonObject entriesObj;
            try
            {
                // QJsonObject-style access through the JsonObject-based JsonUtils helpers
                entriesObj = JsonNode.Parse(doc.RootElement.GetRawText())!.AsObject();
            }
            catch (JsonException)
            {
                EmitFailed("Version response is not a JSON object");
                return;
            }

            foreach (var hash in _resources.Keys)
            {
                var resource = _resources[hash];
                try
                {
                    var entry = JsonUtils.RequireObject(entriesObj, hash);

                    EmitStatus($"Parsing API response from Modrinth for '{resource.Name}'...");
                    _tempVersions[hash] = ModrinthPackIndex.LoadIndexedPackVersion(entry);
                }
                catch (JsonParseException e)
                {
                    EmitMetadataFailed(resource, e.Message);
                }
            }
        }

        // ---------------------------------------------------------- projects task
        var addonIds = new Dictionary<string, string>(StringComparer.Ordinal); // addonId -> hash
        foreach (var (hash, data) in _tempVersions)
        {
            addonIds.TryAdd(data.AddonId, hash);
        }

        if (addonIds.Count == 0)
        {
            // "No addonId found!" — the leftover invalidation in ExecuteAsync reports the failures
            return;
        }

        Net.NetJob projectJob;
        Net.NetRequest projectRequest;
        bool multiple;
        if (addonIds.Count == 1)
        {
            var (job, request) = ModrinthApi.Instance.GetProject(addonIds.Keys.First());
            if (job is null || request is null)
            {
                EmitFailed("Could not build the project request");
                return;
            }
            projectJob = job;
            projectRequest = request;
            multiple = false;
        }
        else
        {
            (projectJob, projectRequest) = ModrinthApi.Instance.GetProjects(addonIds.Keys.ToList());
            multiple = true;
        }

        await projectJob.StartAsync(cancellationToken);
        if (!projectJob.WasSuccessful)
        {
            EmitFailed(projectJob.FailReason);
            return;
        }

        JsonDocument projectDoc;
        try
        {
            projectDoc = JsonDocument.Parse(projectRequest.BodyAsString);
        }
        catch (JsonException e)
        {
            EmitFailed(e.Message);
            return;
        }

        using (projectDoc)
        {
            var entries = multiple
                ? JsonUtils.RequireArray(projectDoc).EnumerateArray().ToList()
                : [projectDoc.RootElement];

            foreach (var entry in entries)
            {
                IndexedPack pack;
                JsonElement entryObj;
                try
                {
                    entryObj = JsonUtils.RequireObject(entry);
                    pack = new IndexedPack();
                    ModrinthPackIndex.LoadIndexedPack(pack, entryObj);
                }
                catch (JsonParseException)
                {
                    // Skip this entry, since it has problems
                    continue;
                }

                if (!addonIds.TryGetValue(pack.AddonId, out var hash))
                {
                    // "Invalid project id from the API response."
                    continue;
                }
                if (!_resources.TryGetValue(hash, out var resource))
                {
                    continue;
                }

                EmitStatus($"Parsing API response from Modrinth for '{resource.Name}'...");

                if (_tempVersions.TryGetValue(hash, out var version))
                {
                    await UpdateMetadata(pack, version, resource, cancellationToken);
                }
            }
        }
    }

    /// <summary>
    /// Port of EnsureMetadataTask::updateMetadata + updateMetadataCallback, with the
    /// LocalResourceUpdateTask::executeTask semantics inlined (old-metadata slug rescue,
    /// create + write the .pw.toml index).
    /// </summary>
    private async Task UpdateMetadata(IndexedPack pack, IndexedVersion ver, Resource resource,
        CancellationToken cancellationToken)
    {
        await Task.CompletedTask; // async signature for future network work; AOT-friendly no-op

        // Prevent file name mismatch
        ver.FileName = resource.Fileinfo.Name;
        if (ver.FileName.EndsWith(".disabled", StringComparison.Ordinal))
        {
            ver.FileName = ver.FileName[..^9];
        }

        // LocalResourceUpdateTask: ensure a '.index' folder exists in the mods folder
        if (!FileSystem.EnsureFolderPathExists(_indexDir))
        {
            EmitMetadataFailed(resource, $"Unable to create index directory at {_indexDir}!");
            return;
        }

        var oldMetadata = MetadataHandler.Get(_indexDir, pack.AddonId);
        if (oldMetadata.IsValid)
        {
            if (pack.Slug.Length == 0)
            {
                pack.Slug = oldMetadata.Slug;
            }
        }

        var pwMod = MetadataHandler.Create(_indexDir, pack, ver);
        if (pwMod.IsValid)
        {
            MetadataHandler.Update(_indexDir, pwMod);
            MetadataReady?.Invoke(resource, pwMod);
        }
        else
        {
            EmitMetadataFailed(resource, "Invalid metadata");
        }
    }

    private void EmitMetadataFailed(Resource resource, string reason) => MetadataFailed?.Invoke(resource, reason);
}
