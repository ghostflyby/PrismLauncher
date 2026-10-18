// SPDX-License-Identifier: GPL-3.0-only
// Port of modplatform/EnsureMetadataTask.{h,cpp} (Modrinth + Flame branches) plus the
// callers' view of minecraft/mod/tasks/LocalResourceUpdateTask.{h,cpp}: make sure every
// downloaded resource has its .pw.toml metadata written.
//
// Deviations:
//  - The ConcurrentTask hash-priming pass lives in the callers (HashUtils.HashFile is
//    synchronous here), so the task takes the hash → resource map directly.
//  - Resource::setMetadata does not exist yet (the P3b Mod port has no metadata field),
//    so the freshly created ModStruct is handed to callers through the MetadataReady
//    event (ModFolderModel re-reads it from disk through its hook).

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Json;
using FlintLauncher.Core.Minecraft.Resources;
using FlintLauncher.Core.ModPlatform.Flame;
using FlintLauncher.Core.ModPlatform.Modrinth;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Tasks;

namespace FlintLauncher.Core.ModPlatform;

public sealed class EnsureMetadataTask(
    Dictionary<string, Resource> resources,
    string indexDir,
    ResourceProvider provider,
    ModrinthApi? modrinth = null,
    FlameApi? flame = null)
    : FlintTask
{
    // hash -> resource
    private readonly ModrinthApi _modrinth = modrinth ?? new ModrinthApi();
    private readonly FlameApi _flame = flame ?? new FlameApi();
    private readonly Dictionary<string, IndexedVersion> _tempVersions = new(StringComparer.Ordinal);
    public bool LockUpdate { get; set; }

    /// <summary>Port of the metadataReady(resource) signal, carrying the fresh metadata.</summary>
    public event Action<Resource, MetadataHandler.ModStruct>? MetadataReady;

    /// <summary>Port of the metadataFailed(resource) signal.</summary>
    public event Action<Resource, string>? MetadataFailed;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        EmitStatus("Checking if resources have metadata...");

        switch (provider)
        {
            case ResourceProvider.Modrinth:
                await ModrinthFlow(cancellationToken);
                break;
            case ResourceProvider.Flame:
                await FlameFlow(cancellationToken);
                break;
        }

        // invalidadeLeftover(): everything that made it this far without metadata failed
        foreach (var (hash, resource) in resources)
        {
            EmitMetadataFailed(resource, $"No metadata was generated for hash {hash}");
        }

        EmitSucceeded();
    }

    private async Task ModrinthFlow(CancellationToken cancellationToken)
    {
        var hashType = ProviderCapabilities.HashType(ResourceProvider.Modrinth)[0];

        // ---------------------------------------------------------- versions task
        var (versionJob, versionRequest) = _modrinth.CurrentVersions(resources.Keys.ToList(), hashType);
        await versionJob.StartAsync(cancellationToken);
        if (!versionJob.WasSuccessful)
        {
            EmitFailed(versionJob.FailReason);
            return;
        }

        var versionEntries = ModrinthJson.ParseVersionLookup(versionRequest.BodyAsString);
        if (versionEntries is null)
        {
            EmitFailed("Version response is not a JSON object");
            return;
        }

        {
            foreach (var hash in resources.Keys)
            {
                var resource = resources[hash];
                try
                {
                    if (!versionEntries.TryGetValue(hash, out var entry))
                    {
                        throw new JsonParseException(hash + " is not an object");
                    }

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

        NetJob projectJob;
        NetRequest projectRequest;
        bool multiple;
        if (addonIds.Count == 1)
        {
            var (job, request) = _modrinth.GetProject(addonIds.Keys.First());
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
            (projectJob, projectRequest) = _modrinth.GetProjects(addonIds.Keys.ToList());
            multiple = true;
        }

        await projectJob.StartAsync(cancellationToken);
        if (!projectJob.WasSuccessful)
        {
            EmitFailed(projectJob.FailReason);
            return;
        }

        JsonNode projectDoc;
        try
        {
            projectDoc = JsonNode.Parse(projectRequest.BodyAsString)
                         ?? throw new JsonException("empty response");
        }
        catch (JsonException e)
        {
            EmitFailed(e.Message);
            return;
        }

        {
            List<ModrinthProjectJson> projectEntries = multiple
                ? ModrinthJson.ProjectListFromNode(projectDoc) ?? []
                : ModrinthJson.ProjectFromNode(projectDoc) is { } single
                    ? [single]
                    : [];

            foreach (var dto in projectEntries)
            {
                IndexedPack pack;
                try
                {
                    pack = new IndexedPack();
                    ModrinthPackIndex.LoadIndexedPack(pack, dto);
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

                if (!resources.TryGetValue(hash, out var resource))
                {
                    continue;
                }

                EmitStatus($"Parsing API response from Modrinth for '{resource.Name}'...");

                if (_tempVersions.TryGetValue(hash, out var version))
                {
                    await UpdateMetadata(pack, version, resource);
                }
            }
        }
    }

    /// <summary>
    /// Port of EnsureMetadataTask::flameVersionsTask + flameProjectsTask: match the mods by
    /// murmur2 fingerprint (the hash keys), then load the project metadata and write the
    /// .pw.toml files through the shared updateMetadata.
    /// </summary>
    private async Task FlameFlow(CancellationToken cancellationToken)
    {
        // ---------------------------------------------------------- versions task
        var fingerprints = new List<uint>();
        foreach (var murmur in resources.Keys)
        {
            if (uint.TryParse(murmur, NumberStyles.None, CultureInfo.InvariantCulture, out var fp))
            {
                fingerprints.Add(fp);
            }
        }

        var (job, request) = _flame.MatchFingerprints(fingerprints);
        await job.StartAsync(cancellationToken);
        if (!job.WasSuccessful)
        {
            EmitFailed(job.FailReason);
            return;
        }

        JsonObject entriesObj;
        try
        {
            entriesObj = JsonUtils.RequireObject(
                JsonNode.Parse(request.BodyAsString), "fingerprint response");
        }
        catch (Exception e) when (e is JsonException or JsonParseException)
        {
            EmitFailed("Fingerprint response is not a JSON object");
            return;
        }

        {
            var exactMatches = (entriesObj["data"] as JsonObject)?["exactMatches"] as JsonArray;
            if (exactMatches is null || exactMatches.Count == 0)
            {
                // "No matches found for fingerprint search!" — the leftover invalidation in
                // ExecuteAsync reports the failures
                return;
            }

            foreach (var matchNode in exactMatches)
            {
                if (matchNode is not JsonObject matchObj || matchObj["file"] is not JsonObject fileObj)
                {
                    // "Fingerprint match is empty!"
                    return;
                }

                var fingerprint = FlameJson.Long(fileObj["fileFingerprint"]).ToString(CultureInfo.InvariantCulture);
                if (!resources.TryGetValue(fingerprint, out var resource))
                {
                    // "Invalid fingerprint from the API response."
                    continue;
                }

                EmitStatus($"Parsing API response from CurseForge for '{resource.Name}'...");
                try
                {
                    _tempVersions[fingerprint] = FlameMod.LoadIndexedPackVersion(fileObj);
                }
                catch (JsonParseException)
                {
                    EmitMetadataFailed(resource, "Invalid data returned from the API.");
                }
            }
        }

        // ---------------------------------------------------------- projects task
        var addonIds = new Dictionary<string, string>(StringComparer.Ordinal); // addonId -> hash
        foreach (var (hash, data) in _tempVersions)
        {
            if (data.AddonId.Length != 0)
            {
                addonIds.TryAdd(data.AddonId, hash);
            }
        }

        if (addonIds.Count == 0)
        {
            // "No addonId found!" — the leftover invalidation in ExecuteAsync reports the failures
            return;
        }

        NetJob projectJob;
        NetRequest projectRequest;
        bool multiple;
        if (addonIds.Count == 1)
        {
            (projectJob, projectRequest) = _flame.GetProject(addonIds.Keys.First());
            multiple = false;
        }
        else
        {
            (projectJob, projectRequest) = _flame.GetProjects(addonIds.Keys.ToList());
            multiple = true;
        }

        await projectJob.StartAsync(cancellationToken);
        if (!projectJob.WasSuccessful)
        {
            EmitFailed(projectJob.FailReason);
            return;
        }

        JsonNode? projectDoc;
        try
        {
            projectDoc = JsonNode.Parse(projectRequest.BodyAsString);
        }
        catch (JsonException e)
        {
            EmitFailed(e.Message);
            return;
        }

        {
            List<JsonNode> entries;
            try
            {
                var rootObj = JsonUtils.RequireObject(projectDoc);
                var data = FlameJson.Ptr(rootObj, "data");
                entries = multiple
                    ? JsonUtils.ToNodeList(JsonUtils.RequireArray(data, "'data'"))
                    : [JsonUtils.RequireObject(data, "'data'")];
            }
            catch (JsonParseException)
            {
                return;
            }

            foreach (var entry in entries)
            {
                JsonNode entryObj;
                try
                {
                    entryObj = JsonUtils.RequireObject(entry);
                }
                catch (JsonParseException)
                {
                    continue;
                }

                var id = FlameJson.RequireInteger(entryObj, "id").ToString(CultureInfo.InvariantCulture);
                if (!addonIds.TryGetValue(id, out var hash) || !resources.TryGetValue(hash, out var resource)
                                                            || !_tempVersions.TryGetValue(hash, out var version))
                {
                    continue;
                }

                var pack = new IndexedPack();
                try
                {
                    EmitStatus($"Parsing API response from CurseForge for '{resource.Name}'...");
                    FlameMod.LoadIndexedPack(pack, entryObj);
                }
                catch (JsonParseException)
                {
                    EmitMetadataFailed(resource, "Invalid data returned from the API.");
                    continue;
                }

                await UpdateMetadata(pack, version, resource);
            }
        }
    }

    /// <summary>
    /// Port of EnsureMetadataTask::updateMetadata + updateMetadataCallback, with the
    /// LocalResourceUpdateTask::executeTask semantics inlined (old-metadata slug rescue,
    /// create + write the .pw.toml index).
    /// </summary>
    private async Task UpdateMetadata(IndexedPack pack, IndexedVersion ver, Resource resource)
    {
        await Task.CompletedTask; // async signature for future network work; AOT-friendly no-op

        // Prevent file name mismatch
        ver.FileName = resource.Fileinfo.Name;
        if (ver.FileName.EndsWith(".disabled", StringComparison.Ordinal))
        {
            ver.FileName = ver.FileName[..^9];
        }

        // LocalResourceUpdateTask: ensure a '.index' folder exists in the mods folder
        if (!FileSystem.EnsureFolderPathExists(indexDir))
        {
            EmitMetadataFailed(resource, $"Unable to create index directory at {indexDir}!");
            return;
        }

        var oldMetadata = MetadataHandler.Get(indexDir, pack.AddonId);
        if (oldMetadata.IsValid)
        {
            if (pack.Slug.Length == 0)
            {
                pack.Slug = oldMetadata.Slug;
            }
        }

        var pwMod = MetadataHandler.Create(indexDir, pack, ver);
        if (pwMod.IsValid)
        {
            pwMod.LockUpdate = LockUpdate;
            MetadataHandler.Update(indexDir, pwMod);
            MetadataReady?.Invoke(resource, pwMod);
        }
        else
        {
            EmitMetadataFailed(resource, "Invalid metadata");
        }
    }

    private void EmitMetadataFailed(Resource resource, string reason) => MetadataFailed?.Invoke(resource, reason);
}
