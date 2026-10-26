// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
// SPDX-FileCopyrightText: 2022 flowln <flowlnlnln@gmail.com>
// SPDX-FileCopyrightText: 2022 Sefa Eyeoglu <contact@scrumplex.net>
//
// Port of modplatform/EnsureMetadataTask.{h,cpp} (Modrinth + Flame branches) plus the
// callers' view of minecraft/mod/tasks/LocalResourceUpdateTask.{h,cpp}: make sure every
// downloaded resource has its .pw.toml metadata written.
//
// Deviations:
//  - The ConcurrentTask hash-priming pass lives in the callers (HashUtils.HashFile is
//    synchronous here), so the task takes the hash → resource map directly.
//  - Resource::setMetadata does not exist yet (the ported Mod has no metadata field),
//    so the freshly created ModStruct is handed to callers through the MetadataReady
//    event (ModFolderModel re-reads it from disk through its hook).

using System.Globalization;
using System.Text.Json;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Minecraft.Resources;
using FlintLauncher.Core.ModPlatform.Flame;
using FlintLauncher.Core.ModPlatform.Modrinth;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Tasks;
using FlintLauncher.ExternalApi;
using TruePath;

namespace FlintLauncher.Core.ModPlatform;

public sealed class EnsureMetadataTask(
    Dictionary<string, Resource> resources,
    AbsolutePath indexDir,
    ResourceProvider provider,
    ModrinthApi modrinth,
    FlameApi? flame = null)
    : FlintTask
{
    // hash -> resource
    private readonly ModrinthApi _modrinth = modrinth;
    private readonly FlameApi _flame = flame ?? new FlameApi(modrinth.Http, modrinth.Settings);
    private readonly Dictionary<string, IndexedVersion> _tempVersions = new(StringComparer.Ordinal);
    public bool LockUpdate { get; init; }

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

        // ---------------------------------------------------------- versions lookup
        var versionEntries = await _modrinth.Client.Versions
            .ByFileHashes(resources.Keys.ToList(), hashType).SendTo(_modrinth.Client, cancellationToken);

        {
            foreach (var hash in resources.Keys)
            {
                var resource = resources[hash];
                if (!versionEntries.TryGetValue(hash, out var entry))
                {
                    EmitMetadataFailed(resource, hash + " is not an object");
                    continue;
                }

                EmitStatus($"Parsing API response from Modrinth for '{resource.Name}'...");
                _tempVersions[hash] = ModrinthPackIndex.LoadIndexedPackVersion(entry);
            }
        }

        // ---------------------------------------------------------- projects lookup
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

        List<ModrinthProject> projectEntries;
        if (addonIds.Count == 1)
        {
            projectEntries = [await _modrinth.Client.Projects.ById(addonIds.Keys.First()).SendTo(_modrinth.Client, cancellationToken)];
        }
        else
        {
            projectEntries = await _modrinth.Client.Projects.ByIds(addonIds.Keys.ToList()).SendTo(_modrinth.Client, cancellationToken);
        }

        foreach (var dto in projectEntries)
        {
            IndexedPack pack;
            try
            {
                pack = new IndexedPack();
                ModrinthPackIndex.LoadIndexedPack(pack, dto);
            }
            catch (JsonException)
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

        string fingerprintBody;
        try
        {
            fingerprintBody = await _flame.MatchFingerprintsAsync(fingerprints, cancellationToken);
        }
        catch (FlintHttpException e)
        {
            EmitFailed(e.Reason);
            return;
        }

        FlameFingerprintResponseJson? response;
        try
        {
            response = FlameMod.ParseResponse(fingerprintBody,
                FlameModJsonContext.Default.FlameFingerprintResponseJson);
        }
        catch (JsonException)
        {
            EmitFailed("Fingerprint response is not a JSON object");
            return;
        }

        {
            var exactMatches = response.Data?.ExactMatches;
            if (exactMatches is null or { Count: 0 })
            {
                // "No matches found for fingerprint search!" — the leftover invalidation in
                // ExecuteAsync reports the failures
                return;
            }

            foreach (var match in exactMatches)
            {
                if (match.File is not { } fileObj)
                {
                    // "Fingerprint match is empty!"
                    return;
                }

                var fingerprint = (fileObj.FileFingerprint ?? 0).ToString(CultureInfo.InvariantCulture);
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
                catch (JsonException)
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

        var multiple = addonIds.Count != 1;
        string projectBody;
        try
        {
            projectBody = multiple
                ? await _flame.GetProjectsAsync(addonIds.Keys.ToList(), cancellationToken)
                : await _flame.GetProjectAsync(addonIds.Keys.First(), cancellationToken);
        }
        catch (FlintHttpException e)
        {
            EmitFailed(e.Reason);
            return;
        }

        {
            List<FlameProjectJson?> entries;
            try
            {
                if (multiple)
                {
                    entries = FlameMod.ParseResponse(projectBody,
                                  FlameModJsonContext.Default.FlameProjectListJson).Data
                              ?? throw new JsonException("'data' is not an array");
                }
                else
                {
                    entries =
                    [
                        FlameMod.ParseResponse(projectBody,
                            FlameModJsonContext.Default.FlameProjectEnvelopeJson).Data
                        ?? throw new JsonException("'data' is not an object"),
                    ];
                }
            }
            catch (JsonException)
            {
                return;
            }

            foreach (var entry in entries)
            {
                if (entry is null)
                {
                    continue;
                }

                var id = (entry.Id ?? 0).ToString(CultureInfo.InvariantCulture);
                if (!addonIds.TryGetValue(id, out var hash) || !resources.TryGetValue(hash, out var resource)
                                                            || !_tempVersions.TryGetValue(hash, out var version))
                {
                    continue;
                }

                var pack = new IndexedPack();
                try
                {
                    EmitStatus($"Parsing API response from CurseForge for '{resource.Name}'...");
                    FlameMod.LoadIndexedPack(pack, entry);
                }
                catch (JsonException)
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
        if (!FileOps.EnsureFolderPathExists(indexDir))
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
