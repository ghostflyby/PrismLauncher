// SPDX-License-Identifier: GPL-3.0-only
// CACHE E5 分层完备性派生视图 (the "dpkg --verify" model): the store roots are content
// stores whose completeness is DERIVED from the manifests that installed them — the version
// manifest's libraries[] for the libraries root, the asset index's objects map for the
// assets root. Nothing is written: manifest-driven reads + file/ledger existence checks only
// (the ledger does not record ownership, E5; the manifest is the sole "config → file" truth).
// The Web layer serves this as GET /store/status/libraries/{versionRef} and
// GET /store/status/assets/{assetsId}.

using FlintLauncher.Core.Json;
using FlintLauncher.Core.Launch;
using FlintLauncher.Core.Meta;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Net;

namespace FlintLauncher.Core.Store;

/// <summary>The completeness report of one store area ({complete, missing[], bytes}; the
/// wire shape is fixed by CACHE E5 and shared by both routes).</summary>
public sealed record StoreStatusReport(bool Complete, IReadOnlyList<string> Missing, long Bytes);

/// <summary>The E5 derivation. Completeness semantics per area:
///  - libraries: every manifest library must exist under the libraries root AND carry a
///    non-stale ledger entry (base "libraries") — a present file without a usable entry is
///    reported missing because the download path (Library.GetDownloads resolves the entry
///    stale → re-downloads) would fetch it again;
///  - assets: every index object must exist under the assets root at its expected size —
///    the exact re-download condition of AssetObject.GetDownloadAction (the object files
///    download directly, MakeFile — they are not ledgered, so there is no entry to check).
/// Native ${arch} classifiers expand to the 32/64 pair (the GetApplicableFiles semantics).
/// Local libraries are excluded: they are instance-anchored overrides, never store content.</summary>
public static class StoreCompleteness
{
    /// <summary>The libraries status of one meta version. versionRef is the meta version
    /// descriptor of net.minecraft (e.g. "1.21.4"); the manifest is read from the meta
    /// cache base (cache/meta/net.minecraft/&lt;versionRef&gt;.json) — the derivation is a
    /// pure view, it never triggers a meta download.</summary>
    public static StoreStatusReport? LibrariesStatus(FlintPaths paths, HttpMetaCache metaCache,
        string versionRef, RuntimeContext runtimeContext)
    {
        if (IsUnsafeRef(versionRef))
        {
            return null;
        }

        var manifestPath = Path.Combine(paths.MetaDir, "net.minecraft", versionRef + ".json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        var metaVersion = new MetaVersion("net.minecraft", versionRef);
        MetaJsonFormat.ParseVersion(
            JsonUtils.RequireObject(JsonUtils.RequireNodeFromFile(manifestPath, manifestPath)),
            metaVersion);
        var manifest = metaVersion.Data ?? throw new MetaParseException(
            $"the version manifest '{manifestPath}' parsed without a version file");

        // the manifest's library set: the same pool LibrariesTask downloads (Libraries +
        // MavenFiles + Agents + MainJar), filtered to what this platform would install
        var pool = new List<Library?>();
        pool.AddRange(manifest.Libraries);
        pool.AddRange(manifest.MavenFiles);
        foreach (var agent in manifest.Agents)
        {
            pool.Add(agent.Library);
        }

        pool.Add(manifest.MainJar);

        var missing = new List<string>();
        long bytes = 0;
        foreach (var lib in pool)
        {
            if (lib is null || lib.IsLocal() || !lib.IsActive(runtimeContext))
            {
                continue;
            }

            foreach (var storage in ExpandStorage(lib.StorageRelativePath(runtimeContext)))
            {
                if (storage.Length == 0)
                {
                    continue;
                }

                var file = new FileInfo(Path.Combine(paths.LibrariesRoot, storage));
                var entry = metaCache.GetEntry("libraries", storage);
                if (!file.Exists || entry is null || entry.IsStale)
                {
                    missing.Add(storage);
                }
                else
                {
                    bytes += file.Length;
                }
            }
        }

        return new StoreStatusReport(missing.Count == 0, missing, bytes);
    }

    /// <summary>The assets status of one asset index. assetsId is the asset index id (e.g.
    /// "17"); the index is read from the store's indexes dir
    /// (store/assets/indexes/&lt;assetsId&gt;.json).</summary>
    public static StoreStatusReport? AssetsStatus(FlintPaths paths, string assetsId)
    {
        if (IsUnsafeRef(assetsId))
        {
            return null;
        }

        var indexPath = Path.Combine(paths.AssetIndexesDir, assetsId + ".json");
        if (!File.Exists(indexPath))
        {
            return null;
        }

        var index = new AssetsIndex();
        if (!AssetsUtils.LoadAssetsIndexJson(assetsId, indexPath, index))
        {
            return null;
        }

        var missing = new List<string>();
        long bytes = 0;
        foreach (var (objectName, assetObject) in index.Objects)
        {
            var file = new FileInfo(assetObject.GetLocalPath(paths.AssetsRoot));
            // the GetDownloadAction re-download condition: absent OR wrong size
            if (!file.Exists || file.Length != assetObject.Size)
            {
                missing.Add(objectName);
            }
            else
            {
                bytes += file.Length;
            }
        }

        return new StoreStatusReport(missing.Count == 0, missing, bytes);
    }

    /// <summary>A ${arch} storage expands to the 32/64 pair (the two files the launch path
    /// expects — GetApplicableFiles); anything else is a single path.</summary>
    private static IEnumerable<string> ExpandStorage(string storage)
    {
        if (storage.Contains("${arch}", StringComparison.Ordinal))
        {
            yield return storage.Replace("${arch}", "32");
            yield return storage.Replace("${arch}", "64");
        }
        else
        {
            yield return storage;
        }
    }

    /// <summary>Route refs become file names — anything path-like is refused rather than
    /// escaped into a surprising location (the MigrationDecisionStore guard convention).</summary>
    private static bool IsUnsafeRef(string reference) =>
        string.IsNullOrEmpty(reference) || reference.Contains('/') || reference.Contains('\\') ||
        reference == "." || reference == ".." ||
        reference.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0;
}
