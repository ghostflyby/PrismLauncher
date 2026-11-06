// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
// SPDX-FileCopyrightText: 2022 flowln <flowlnlnln@gmail.com>
// SPDX-FileCopyrightText: 2022 Sefa Eyeoglu <contact@scrumplex.net>
//
// Writes the .pw.toml metadata for a batch of freshly downloaded resources: look the
// resources up by hash on the provider's API, match them to projects, and create or refresh
// the index entries. A resource that ends up without metadata is returned as a failure entry
// and does not fail the pass.
//
// The hash → resource map is the input rather than something the pass computes: hashing is a
// synchronous call here, so priming the map belongs to whoever produced the hashes.

using System.Globalization;
using System.Text.Json;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Minecraft.Resources;
using FlintLauncher.Core.ModPlatform.Flame;
using FlintLauncher.Core.ModPlatform.Modrinth;
using FlintLauncher.ExternalApi;
using FlintLauncher.ExternalApi.Flame;
using FlintLauncher.ExternalApi.Modrinth;
using TruePath;

namespace FlintLauncher.Core.ModPlatform;

public sealed class EnsureMetadataTask(
    Dictionary<string, Resource> resources,
    AbsolutePath indexDir,
    ResourceProvider provider,
    ModrinthClient modrinth,
    HttpClient http,
    FlameClient? flame = null)
{
    // hash -> resource
    private readonly FlameClient _flame = flame ?? new FlameClient();
    private readonly Dictionary<string, IndexedVersion> _tempVersions = new(StringComparer.Ordinal);
    public bool LockUpdate { get; init; }

    // hashes this pass has not settled yet; the C++ m_resources shrinks as entries are
    // reported, and whatever is left at the end is the leftover set. Rebuilt per run so a
    // second RunAsync starts from the caller's full set again.
    private Dictionary<string, Resource> _remaining = [];

    private readonly List<(Resource Resource, string Reason)> _failures = [];

    /// <summary>Resolves the metadata for every entry in <c>resources</c>, writing the index
    /// files. Returns the resources that ended up without metadata; the pass itself throws only
    /// on an unexpected fault.</summary>
    public async Task<IReadOnlyList<(Resource Resource, string Reason)>> RunAsync(
        CancellationToken cancellationToken)
    {
        _remaining = new Dictionary<string, Resource>(resources, StringComparer.Ordinal);
        _tempVersions.Clear();
        _failures.Clear();

        switch (provider)
        {
            case ResourceProvider.Modrinth:
                await ModrinthFlow(cancellationToken);
                break;
            case ResourceProvider.Flame:
                await FlameFlow(cancellationToken);
                break;
        }

        // invalidadeLeftover(): everything that made it this far without metadata failed.
        // The snapshot is required: EmitMetadataFailed removes from _remaining.
        foreach (var (hash, resource) in _remaining.ToList())
        {
            EmitMetadataFailed(hash, resource, $"No metadata was generated for hash {hash}");
        }

        return _failures;
    }

    private async Task ModrinthFlow(CancellationToken cancellationToken)
    {
        var hashType = ProviderCapabilities.HashType(ResourceProvider.Modrinth)[0];

        // ---------------------------------------------------------- versions lookup
        var versionEntries = await modrinth.Versions
            .ByFileHashes(_remaining.Keys.ToList(), hashType).SendTo(http, cancellationToken);

        {
            foreach (var hash in _remaining.Keys.ToList())
            {
                var resource = _remaining[hash];
                if (!versionEntries.TryGetValue(hash, out var entry))
                {
                    EmitMetadataFailed(hash, resource, hash + " is not an object");
                    continue;
                }

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
            projectEntries = [await modrinth.Projects.ById(addonIds.Keys.First()).SendTo(http, cancellationToken)];
        }
        else
        {
            projectEntries = await modrinth.Projects.ByIds(addonIds.Keys.ToList()).SendTo(http, cancellationToken);
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

            if (!_remaining.TryGetValue(hash, out var resource))
            {
                continue;
            }

            if (_tempVersions.TryGetValue(hash, out var version))
            {
                UpdateMetadata(hash, pack, version, resource);
            }
        }
    }

    /// <summary>
    /// Matches the mods by murmur2 fingerprint (the hash keys), then loads the project
    /// metadata and writes the .pw.toml files through <see cref="UpdateMetadata"/>.
    /// </summary>
    private async Task FlameFlow(CancellationToken cancellationToken)
    {
        // ---------------------------------------------------------- versions task
        var fingerprints = new List<uint>();
        foreach (var murmur in _remaining.Keys.ToList())
        {
            if (uint.TryParse(murmur, NumberStyles.None, CultureInfo.InvariantCulture, out var fp))
            {
                fingerprints.Add(fp);
            }
        }

        FlameFingerprintResponse response;
        try
        {
            response = await _flame.Fingerprints.Match(fingerprints).SendTo(http, cancellationToken);
        }
        catch (FlameApiException e)
        {
            throw new InvalidOperationException(e.Message, e);
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
                if (!_remaining.TryGetValue(fingerprint, out var resource))
                {
                    // "Invalid fingerprint from the API response."
                    continue;
                }

                try
                {
                    _tempVersions[fingerprint] = FlameMod.LoadIndexedPackVersion(fileObj);
                }
                catch (JsonException)
                {
                    EmitMetadataFailed(fingerprint, resource, "Invalid data returned from the API.");
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
        List<FlameProject?> entries;
        try
        {
            entries = multiple
                ? (await _flame.Projects.ByIds(addonIds.Keys.ToList()).SendTo(http, cancellationToken)).Data
                  ?? throw new JsonException("'data' is not an array")
                :
                [
                    (
                        await _flame.Projects.ById(addonIds.Keys.First()).SendTo(http, cancellationToken)).Data
                    ?? throw new JsonException("'data' is not an object"),
                ];
        }
        catch (FlameApiException e)
        {
            throw new InvalidOperationException(e.Message, e);
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
            if (!addonIds.TryGetValue(id, out var hash) || !_remaining.TryGetValue(hash, out var resource)
                                                        || !_tempVersions.TryGetValue(hash, out var version))
            {
                continue;
            }

            var pack = new IndexedPack();
            try
            {
                FlameMod.LoadIndexedPack(pack, entry);
            }
            catch (JsonException)
            {
                EmitMetadataFailed(hash, resource, "Invalid data returned from the API.");
                continue;
            }

            UpdateMetadata(hash, pack, version, resource);
        }
    }

    /// <summary>
    /// Writes one resource's index entry: rescue a missing slug from the existing metadata,
    /// then create and store the .pw.toml. The version's file name is forced to the downloaded
    /// file's name so the index points at what is on disk.
    /// </summary>
    private void UpdateMetadata(string hash, IndexedPack pack, IndexedVersion ver, Resource resource)
    {
        // Prevent file name mismatch
        ver.FileName = resource.Fileinfo.Name;
        if (ver.FileName.EndsWith(".disabled", StringComparison.Ordinal))
        {
            ver.FileName = ver.FileName[..^9];
        }

        // LocalResourceUpdateTask: ensure a '.index' folder exists in the mods folder
        if (!FileOps.EnsureFolderPathExists(indexDir))
        {
            EmitMetadataFailed(hash, resource, $"Unable to create index directory at {indexDir}!");
            return;
        }

        var oldMetadata = MetadataHandler.Get(indexDir, pack.AddonId);
        if (oldMetadata is { IsValid: true } && pack.Slug.Length == 0)
        {
            pack.Slug = oldMetadata.Slug;
        }

        var pwMod = MetadataHandler.Create(indexDir, pack, ver);
        if (pwMod is { IsValid: true })
        {
            pwMod.LockUpdate = LockUpdate;
            MetadataHandler.Update(indexDir, pwMod);
            // settled: it must not be reported as a leftover at the end of the pass
            _remaining.Remove(hash);
        }
        else
        {
            EmitMetadataFailed(hash, resource, "Invalid metadata");
        }
    }

    /// <summary>Records why one resource could not be given metadata and drops it from the
    /// leftover set, so the end-of-pass sweep does not report it twice.</summary>
    private void EmitMetadataFailed(string hash, Resource resource, string reason)
    {
        _failures.Add((resource, reason));
        _remaining.Remove(hash);
    }
}
