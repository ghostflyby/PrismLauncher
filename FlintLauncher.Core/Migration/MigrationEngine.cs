// SPDX-License-Identifier: GPL-3.0-only
// The unified migration engine (GLOBAL G2): the Prism→Flint import pipeline
// (FileTransfer merge semantics + whitelists) is promoted to a general engine whose SOURCES
// are profiled. Two profiles:
//  - prism-family  : prism/polymc/multimc — the existing whitelist, API-driven (never
//                    auto-runs; the /api/v1/migration/prism endpoints drive it);
//  - flint-legacy  : the old flat Flint layout (flint.cfg / metacache.json / meta / cache /
//                    assets / libraries / java at the root layer) remounted onto the
//                    five-root layout — the startup probe auto-runs it ONCE (assembly step
//                    5, FlintLegacyMigration.ExecuteIfNeeded) and doubles as the E8 cleanup
//                    of the Prism bare metacache ledger.
// The profiles share the S3 decision records (state/migration/<source>.json), so both are
// one-shot decisions a host can display.

using System.Globalization;
using System.Security;
using FlintLauncher.Core.Config;
using FlintLauncher.Core.IO;

namespace FlintLauncher.Core.Migration;

/// <summary>One importable source a profile detected.</summary>
public sealed record MigrationSourceCandidate
{
    /// <summary>The source directory on disk.</summary>
    public required string Path { get; init; }

    /// <summary>The profile-level source id (also the S3 decision-record key).</summary>
    public required string SourceId { get; init; }

    /// <summary>Whether the one-shot decision for this source was already made (S3 record;
    /// decided sources are not proposed again).</summary>
    public bool DecisionRecorded { get; init; }
}

/// <summary>G2: one source family the engine knows how to detect/import.</summary>
public interface IMigrationSourceProfile
{
    /// <summary>The profile key (also the S3 decision-record source for profile-level
    /// decisions: state/migration/&lt;Name&gt;.json).</summary>
    string Name { get; }

    /// <summary>G2: prism-family is API-driven and never auto-runs; flint-legacy auto-runs
    /// exactly once at startup (its detection being positive AND its decision unrecorded).</summary>
    bool AutoRunAtStartup { get; }

    /// <summary>Enumerate the importable sources of this profile against the given layout.
    /// Empty = nothing to migrate.</summary>
    IReadOnlyList<MigrationSourceCandidate> Detect(FlintPaths paths, MigrationDecisionStore? decisions);

    /// <summary>The planned import for one source (the dry-run shape the API exposes), or
    /// null when this profile cannot plan against the source yet.</summary>
    IReadOnlyList<PrismImportPlanItem>? Plan(string sourcePath, FlintPaths paths, ImportMode mode);
}

/// <summary>G2 prism-family profile: the Prism/PolyMC/MultiMC data directories at their
/// platform-default locations, imported with the existing whitelist through the
/// <see cref="PrismImportTask"/> pipeline. API-driven only.</summary>
public sealed class PrismFamilyMigrationProfile : IMigrationSourceProfile
{
    public const string ProfileName = "prism-family";

    private readonly IEnumerable<string>? _parentsOverride;

    /// <summary><paramref name="parentsOverride"/> replaces the platform-default parent scan
    /// (embedding hosts/tests point it at their own roots).</summary>
    public PrismFamilyMigrationProfile(IEnumerable<string>? parentsOverride = null)
    {
        _parentsOverride = parentsOverride;
    }

    public string Name => ProfileName;

    public bool AutoRunAtStartup => false;

    public IReadOnlyList<MigrationSourceCandidate> Detect(FlintPaths paths, MigrationDecisionStore? decisions)
    {
        var candidates = new List<MigrationSourceCandidate>();
        // the existing-data probe runs against the CONFIG root's flint.json (CONFIG C5) —
        // the pre-reform <data>/flint.cfg position is dead, and without the config-root
        // path the C++ "already has data → skip migration" protection would never fire
        var detected = _parentsOverride is null
            ? PrismSourceDetector.Detect(paths.DataRoot, decisions, paths.ConfigFile)
            : PrismSourceDetector.Detect(paths.DataRoot, _parentsOverride, decisions, paths.ConfigFile);
        foreach (var candidate in detected)
        {
            candidates.Add(new MigrationSourceCandidate
            {
                Path = candidate.Path,
                SourceId = candidate.LegacyName,
                DecisionRecorded = candidate.MarkerPresent,
            });
        }
        return candidates;
    }

    public IReadOnlyList<PrismImportPlanItem> Plan(string sourcePath, FlintPaths paths, ImportMode mode) =>
        PrismImportPlanner.Plan(sourcePath, paths.DataRoot, mode);
}

/// <summary>G2 flint-legacy profile: the pre-reform flat Flint layout (config/ledger/content
/// anchors at the root layer) remounted onto the five-root layout. Detected against the data
/// root (where the old flat layout actually lives); auto-runs ONCE at startup (assembly step
/// 5, <see cref="FlintLegacyMigration.ExecuteIfNeeded"/>) — eager, no lazy dual-read
/// (GLOBAL G2: "迁移应该一次完成").
///
/// NOTE on the anchor set: only items that MOVED off the data root count as legacy anchors
/// (flint.cfg / the pre-reform ledgers / meta / cache / assets / libraries / java).
/// <c>instances</c> is deliberately NOT an anchor — the instance directory's anchor IS the
/// data root in the new layout too, and discovery creates it on every fresh install, so it
/// cannot discriminate a legacy layout.</summary>
public sealed class FlintLegacyMigrationProfile : IMigrationSourceProfile
{
    public const string ProfileName = "flint-legacy";

    public string Name => ProfileName;

    public bool AutoRunAtStartup => true;

    /// <summary>The root-layer anchors of the old flat layout: the moved config/ledger files
    /// (flint.cfg, both ledger spellings — Flint's metacache.json and the Prism bare
    /// metacache) plus the moved content directories (meta/cache were cache-bound, E3;
    /// assets/libraries/java are store-bound, E2).</summary>
    public static readonly IReadOnlyList<string> LegacyAnchors =
    [
        "flint.cfg", "metacache.json", "metacache", "meta", "cache", "assets", "libraries", "java",
    ];

    /// <summary>One remount of the G2 whitelist: the old data-root child and the five-root
    /// destination it lands on. The per-child shape of the old <c>cache</c> area is resolved
    /// dynamically (each child moves 1:1 under the cache root — the "general" base content
    /// included); everything else is a fixed mapping.</summary>
    public sealed record LegacyMount(string Item, Func<FlintPaths, string> Destination, bool IsDirectory);

    /// <summary>The fixed half of the flat→five-root whitelist (GLOBAL G2). Items NOT listed
    /// here stay where they are: instances/accounts.json/instgroups.json/icons already sit on
    /// their (data-root) anchors, logs/ stay behind (the S4 logger anchor moved, history does
    /// not), api-token/*_nomigrate.txt are S2/S3 business.</summary>
    public static readonly IReadOnlyList<LegacyMount> Mounts =
    [
        // config: the INI chain file lands on the config side (the C5 importer probes the
        // config root FIRST, so the import reads it in its new home; the file is kept, C5)
        new("flint.cfg", p => Path.Combine(p.ConfigRoot, "flint.cfg"), IsDirectory: false),
        // cache (E3): the pre-reform ledger and the metadata content; the old in-data cache
        // area's children follow below
        new("metacache.json", p => p.MetacacheFile, IsDirectory: false),
        new("meta", p => p.MetaDir, IsDirectory: true),
        // store (E2): shared installed content
        new("assets", p => p.AssetsRoot, IsDirectory: true),
        new("libraries", p => p.LibrariesRoot, IsDirectory: true),
        new("java", p => p.JavaRoot, IsDirectory: true),
    ];

    public IReadOnlyList<MigrationSourceCandidate> Detect(FlintPaths paths, MigrationDecisionStore? decisions)
    {
        var sourceRoot = LegacySourceRoot(paths);
        if (sourceRoot is null)
        {
            return [];
        }
        var decided = decisions?.Read(ProfileName) is not null;
        return
        [
            new MigrationSourceCandidate
            {
                Path = sourceRoot,
                SourceId = ProfileName,
                DecisionRecorded = decided,
            },
        ];
    }

    /// <summary>The first root carrying a legacy flat-layout anchor: the data root (a precise
    /// --data-dir laid on an old directory), then the home root (G1 --home-dir: the old
    /// "total directory" — X/data is a fresh child that sees none of it).</summary>
    public static string? LegacySourceRoot(FlintPaths paths)
    {
        if (HasLegacyAnchor(paths.DataRoot))
        {
            return paths.DataRoot;
        }
        if (paths.HomeRoot is { } home && home != paths.DataRoot && HasLegacyAnchor(home))
        {
            return home;
        }
        return null;
    }

    /// <summary>Whether <paramref name="root"/> carries any legacy flat-layout anchor.</summary>
    public static bool HasLegacyAnchor(string root) =>
        LegacyAnchors.Any(anchor =>
        {
            var full = Path.Combine(root, anchor);
            return Directory.Exists(full) || File.Exists(full);
        });

    public IReadOnlyList<PrismImportPlanItem>? Plan(string sourcePath, FlintPaths paths, ImportMode mode)
    {
        if (!Directory.Exists(sourcePath))
        {
            return null;
        }
        var items = new List<PrismImportPlanItem>();
        void PlanItem(string sourceItem, string destination, bool isDirectory)
        {
            if (isDirectory ? !Directory.Exists(sourceItem) : !File.Exists(sourceItem))
            {
                return;
            }
            var exists = isDirectory ? Directory.Exists(destination) : File.Exists(destination);
            var method = exists ? PlannedMethod.DestinationExists
                : mode == ImportMode.Move && FileTransfer.SameVolume(sourceItem, destination)
                    ? PlannedMethod.RenameDirectory
                    : PlannedMethod.PerFile;
            items.Add(new PrismImportPlanItem(Path.GetFileName(sourceItem), isDirectory, method,
                isDirectory ? PrismImportPlanner.CountFiles(sourceItem) : 1,
                isDirectory ? PrismImportPlanner.SizeOf(sourceItem) : new FileInfo(sourceItem).Length));
        }

        foreach (var mount in Mounts)
        {
            PlanItem(Path.Combine(sourcePath, mount.Item), mount.Destination(paths), mount.IsDirectory);
        }
        // the old in-data cache area: every child moves 1:1 under the cache root
        var oldCacheDir = Path.Combine(sourcePath, "cache");
        if (Directory.Exists(oldCacheDir))
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(oldCacheDir))
            {
                PlanItem(child, Path.Combine(paths.CacheRoot, Path.GetFileName(child)),
                    Directory.Exists(child));
            }
        }
        return items;
    }
}

/// <summary>The flint-legacy eager pass (GLOBAL G2, assembly step 5): the startup probe that
/// remounts the pre-reform flat layout onto the five roots ONCE — no decision record means
/// run it, then seal with the S3 record so the probe never fires again. The pass doubles as
/// the E8 one-time cleanup (a Prism bare <c>metacache</c> ledger in the target directory is
/// dead weight — the whitelist never carried it, existing stock is deleted). A failed
/// transfer writes no record: the next startup resumes the idempotent merge. Two entries:
/// <see cref="ExecuteIfNeeded"/> is the gated startup auto-run; <see cref="Execute"/> is the
/// record-less transfer the prism import-finished hook runs (the record gates the AUTO-RUN,
/// never the remount itself).</summary>
public static class FlintLegacyMigration
{
    /// <summary>What the eager pass did (the host logs it; tests assert it).</summary>
    public sealed record Result(bool Ran, int ItemsMoved, long Bytes, bool LegacyLedgerCleaned, string? Error)
    {
        public static readonly Result Skipped = new(false, 0, 0, false, null);
    }

    /// <summary>The STARTUP entry (assembly step 5): probe and, when an UNDECIDED legacy
    /// layout is detected, remount it (Move semantics: same-volume rename, cross-volume
    /// clone/copy + source delete — the flat layout is gone after the pass) and seal with
    /// the decision record. Every detected legacy root remounts in the ONE pass (the data
    /// root and a legacy home root — the double-legacy state an import crash leaves behind)
    /// and one complete aggregate seals the single profile-level record. The S3 record
    /// gates the AUTO-RUN only — it must never gate the remount itself (an upgraded user
    /// carries the record permanently; a second prism
    /// import lands fresh shared content at the data root afterwards, and that remount is
    /// the record-less <see cref="Execute"/> entry). A no-op probe (anchors present but
    /// nothing whitelisted to remount) writes NO record: nothing was imported, and the
    /// next startup's probe is a handful of existence checks. A FAILED transfer — exception
    /// OR per-file transfer failure — writes NO record either: the next startup resumes
    /// the idempotent merge (destination exists → skip), which is the honest outcome of a
    /// half-done remount.</summary>
    public static Result ExecuteIfNeeded(FlintPaths paths, MigrationDecisionStore? decisions)
    {
        var profile = new FlintLegacyMigrationProfile();
        var candidates = profile.Detect(paths, decisions);
        if (candidates.Count == 0 || candidates[0].DecisionRecorded)
        {
            return Result.Skipped;
        }

        // BOTH legacy roots remount in one eager pass (a double-legacy state: a prism import
        // landed shared content at the data root and the process died before the
        // OnImportFinished remount, while the home root is itself a pre-reform total
        // directory — remounting only the first detected root would strand the other behind
        // the sealed record forever). The probe mirrors LegacySourceRoot's predicates: data
        // root first, home root second; a single-legacy layout degenerates to one iteration.
        var roots = new List<string>();
        if (FlintLegacyMigrationProfile.HasLegacyAnchor(paths.DataRoot))
        {
            roots.Add(paths.DataRoot);
        }
        if (paths.HomeRoot is { } home && home != paths.DataRoot &&
            FlintLegacyMigrationProfile.HasLegacyAnchor(home))
        {
            roots.Add(home);
        }

        var ran = false;
        var moved = 0;
        long bytes = 0;
        var ledgerCleaned = false;
        string? error = null;
        foreach (var root in roots)
        {
            var pass = Execute(paths, root);
            ran |= pass.Ran;
            moved += pass.ItemsMoved;
            bytes += pass.Bytes;
            ledgerCleaned |= pass.LegacyLedgerCleaned;
            if (pass.Error is not null)
            {
                // the single-source semantics hold across roots: an incomplete pass stops the
                // aggregate and seals NO record — the next startup resumes the idempotent merge
                error = pass.Error;
                break;
            }
        }

        if (ran && error is null)
        {
            // a COMPLETE remount seals the startup auto-run: the probe never fires again.
            // Fault-tolerant like PrismImportTask.RecordImportDecision: the record seals a
            // COMPLETE remount; a failed seal write resumes as an honest no-op probe next
            // startup (the remount itself already tolerated IO failures).
            try
            {
                decisions?.Write(new MigrationDecisionRecord
                {
                    Source = FlintLegacyMigrationProfile.ProfileName,
                    Decision = MigrationDecisionRecord.Imported,
                    At = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
                    Mode = "move",
                    Instances = Directory.Exists(Path.Combine(paths.DataRoot, "instances"))
                        ? Directory.EnumerateDirectories(Path.Combine(paths.DataRoot, "instances")).Count()
                        : 0,
                    Bytes = bytes,
                });
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException
                                     or NotSupportedException or SecurityException)
            {
            }
        }
        return new Result(ran, moved, bytes, ledgerCleaned, error);
    }

    /// <summary>The RECORD-LESS transfer entry (the prism import-finished hook,
    /// MigrationEndpoints.OnImportFinished): performs exactly the startup remount — the
    /// whitelist mounts, the in-data cache children, the E8 bare-ledger cleanup — WITHOUT
    /// consulting or writing the S3 decision record. The record exists to keep the startup
    /// AUTO-RUN one-shot (G2); the hook runs after every prism import, where a recorded
    /// decision (every upgraded user, any second import) would otherwise strand the
    /// freshly imported assets/libraries/meta at the data root forever — the store side
    /// would re-download GBs of content that is already on disk (GLOBAL G2: the import
    /// pipeline completes ALL sorting in a single pass). The transfer is idempotent
    /// (destination exists → skip), and a failed pass leaves the data at the data root for
    /// the next import's retry (or the startup resume when no record seals it).</summary>
    public static Result Execute(FlintPaths paths) => Execute(paths, paths.DataRoot);

    public static Result Execute(FlintPaths paths, string sourceRoot)
    {
        var homeSource = sourceRoot != paths.DataRoot;
        // the same cheap anchor probe the profile's Detect runs: no legacy anchor at the
        // source root → nothing to remount (Ran=false, honest no-op)
        var anchorPresent = FlintLegacyMigrationProfile.HasLegacyAnchor(sourceRoot);
        if (!anchorPresent)
        {
            return Result.Skipped;
        }

        var moved = 0;
        long bytes = 0;
        var ledgerCleaned = false;
        var failedFiles = 0;
        try
        {
            foreach (var mount in FlintLegacyMigrationProfile.Mounts)
            {
                var src = Path.Combine(sourceRoot, mount.Item);
                bytes += Transfer(src, mount.Destination(paths), mount.IsDirectory, ref moved, ref failedFiles);
            }
            // the old in-data cache area: children 1:1 into the cache root (the "general"
            // base content included). Under a home source the old cache root IS the new
            // cache root (home/cache), so the children skip as already-in-place — correct.
            var oldCacheDir = Path.Combine(sourceRoot, "cache");
            if (Directory.Exists(oldCacheDir))
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(oldCacheDir).ToList())
                {
                    bytes += Transfer(child,
                        Path.Combine(paths.CacheRoot, Path.GetFileName(child)),
                        Directory.Exists(child), ref moved, ref failedFiles);
                }
            }
            if (homeSource)
            {
                // a G1 home source is the whole pre-reform total directory, not just the
                // remount set: the data-root anchors (instances/accounts/instgroups/icons/
                // skins) move into the fresh X/data — per child, because the destination
                // directory may already exist on a resumed pass and a whole-dir move would
                // strand the source forever — the INI chain's fallback names land on the
                // config side (the importer probes ConfigRoot first), the S2 token file is
                // dead stock (zero-persistence), and the S3 markers move to the data root
                // where DataMigration.Decide upgrades them to records
                FileSystem.EnsureFolderPathExists(paths.InstancesDir);
                MoveChildren(sourceRoot, "instances", paths.InstancesDir, ref moved, ref bytes, ref failedFiles);
                bytes += Transfer(Path.Combine(sourceRoot, "accounts.json"), paths.AccountsFile,
                    false, ref moved, ref failedFiles);
                bytes += Transfer(Path.Combine(sourceRoot, "instgroups.json"), paths.InstgroupsFile,
                    false, ref moved, ref failedFiles);
                FileSystem.EnsureFolderPathExists(Path.Combine(paths.DataRoot, "icons"));
                MoveChildren(sourceRoot, "icons", Path.Combine(paths.DataRoot, "icons"),
                    ref moved, ref bytes, ref failedFiles);
                FileSystem.EnsureFolderPathExists(Path.Combine(paths.DataRoot, "skins"));
                MoveChildren(sourceRoot, "skins", Path.Combine(paths.DataRoot, "skins"),
                    ref moved, ref bytes, ref failedFiles);
                foreach (var ini in FlintConfigImporter.IniChain)
                {
                    if (ini == "flint.cfg")
                    {
                        continue; // already carried by the shared Mounts
                    }
                    bytes += Transfer(Path.Combine(sourceRoot, ini),
                        Path.Combine(paths.ConfigRoot, ini),
                        false, ref moved, ref failedFiles);
                }
                var token = Path.Combine(sourceRoot, "api-token");
                if (File.Exists(token))
                {
                    File.Delete(token); // S2: zero-persistence — the pre-reform token file is dead stock
                }
                foreach (var marker in Directory.EnumerateFiles(sourceRoot, "*_nomigrate.txt"))
                {
                    bytes += Transfer(marker,
                        Path.Combine(paths.DataRoot, Path.GetFileName(marker)),
                        false, ref moved, ref failedFiles);
                }
            }
            // E8: the Prism bare ledger is dead stock — the import whitelist never carried
            // it and nothing reads it; delete it once and for all
            var bareLedger = Path.Combine(sourceRoot, "metacache");
            if (File.Exists(bareLedger))
            {
                File.Delete(bareLedger);
                ledgerCleaned = true;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                     or ArgumentException or NotSupportedException
                                     or SecurityException)
        {
            // no record — the next startup resumes the idempotent merge (destination
            // exists → skip), which is the honest outcome of a half-done remount
            return new Result(true, moved, bytes, ledgerCleaned, e.Message);
        }

        if (failedFiles > 0)
        {
            // per-file transfer failures count the same as exceptions: the remount is
            // INCOMPLETE, so the decision record must not seal it — a sealed record would
            // make the probe skip the leftovers forever (destination exists → they never
            // retry, silently diverging from the 'eager once, complete' G2 contract)
            return new Result(true, moved, bytes, ledgerCleaned,
                $"{failedFiles} file(s) failed to transfer — the remount is incomplete and will resume");
        }

        if (moved == 0 && !ledgerCleaned)
        {
            // anchors (e.g. a lone instances/ directory) but nothing the whitelist carries:
            // nothing was imported — the entry stays cheap and honest (NO record here; the
            // startup entry seals the record over a complete run, this one never writes)
            return new Result(false, 0, 0, false, null);
        }

        return new Result(true, moved, bytes, ledgerCleaned, null);
    }

    /// <summary>Transfer one whitelist item: a whole-subtree rename when the volume matches,
    /// otherwise a per-file walk with the merge rule (destination exists → skip). An
    /// existing destination DIRECTORY resumes the merge file by file instead of skipping
    /// the whole item (B3: a bare skip would strand every not-yet-transferred source file
    /// forever — the "resumes the idempotent merge" contract never fires). Returns the
    /// bytes that actually landed; <paramref name="moved"/> counts transferred items and
    /// <paramref name="failedFiles"/> every file that could not be transferred (a failure
    /// never aborts the walk — the remaining items still move — but the caller must not
    /// seal the decision record over it).</summary>
    private static long Transfer(string src, string dst, bool isDirectory, ref int moved, ref int failedFiles)
    {
        if (isDirectory ? !Directory.Exists(src) : !File.Exists(src))
        {
            return 0;
        }
        var alreadyThere = isDirectory ? Directory.Exists(dst) : File.Exists(dst);
        if (alreadyThere)
        {
            if (!isDirectory)
            {
                return 0; // idempotent resume: the destination file won
            }
            // the destination directory exists (an earlier startup created it, or a
            // previous pass stopped halfway): merge the source into it file by file —
            // files already at the destination skip, missing ones transfer
            long resumed = 0;
            foreach (var file in FileTransfer.EnumerateFilesDeep(src))
            {
                var relative = Path.GetRelativePath(src, file);
                var target = Path.Combine(dst, relative);
                if (FileTransfer.TransferFile(file, target, ImportMode.Move, out _))
                {
                    resumed += new FileInfo(target).Length;
                }
                else
                {
                    failedFiles++;
                }
            }
            moved++;
            return resumed;
        }

        if (isDirectory)
        {
            if (FileTransfer.TryMoveDirectory(src, dst))
            {
                moved++;
                return PrismImportPlanner.SizeOf(dst);
            }
            // volume differs — fall through to the per-file walk
            long bytes = 0;
            foreach (var file in FileTransfer.EnumerateFilesDeep(src))
            {
                var relative = Path.GetRelativePath(src, file);
                var target = Path.Combine(dst, relative);
                if (FileTransfer.TransferFile(file, target, ImportMode.Move, out _))
                {
                    bytes += new FileInfo(target).Length;
                }
                else
                {
                    failedFiles++;
                }
            }
            moved++;
            return bytes;
        }

        if (!FileTransfer.TransferFile(src, dst, ImportMode.Move, out _))
        {
            failedFiles++;
            return 0;
        }
        moved++;
        return new FileInfo(dst).Length;
    }

    /// <summary>Move the children of one legacy directory into a destination directory, one
    /// entry at a time. The destination directory itself may already exist (created by an
    /// earlier startup or a resumed pass) — a whole-dir move would hit the
    /// destination-exists skip and strand the source children forever.</summary>
    private static void MoveChildren(string parent, string child, string destinationDir,
        ref int moved, ref long bytes, ref int failedFiles)
    {
        var sourceDir = Path.Combine(parent, child);
        if (!Directory.Exists(sourceDir))
        {
            return;
        }
        foreach (var entry in Directory.EnumerateFileSystemEntries(sourceDir).ToList())
        {
            bytes += Transfer(entry,
                Path.Combine(destinationDir, Path.GetFileName(entry)),
                Directory.Exists(entry), ref moved, ref failedFiles);
        }
    }
}

/// <summary>The engine facade: the registered profiles, lookups by name, and the G2
/// auto-run predicate (a profile that auto-runs AND has an undecided detected source).</summary>
public sealed class MigrationEngine
{
    private readonly IReadOnlyList<IMigrationSourceProfile> _profiles;

    public MigrationEngine(IEnumerable<IMigrationSourceProfile> profiles)
    {
        _profiles = [.. profiles];
    }

    /// <summary>The default engine: both G2 profiles (prism-family, flint-legacy).</summary>
    public static MigrationEngine CreateDefault() =>
        new([new PrismFamilyMigrationProfile(), new FlintLegacyMigrationProfile()]);

    public IReadOnlyList<IMigrationSourceProfile> Profiles => _profiles;

    public IMigrationSourceProfile? Find(string name) =>
        _profiles.FirstOrDefault(profile =>
            string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>All sources any profile detects (per-profile detection, shared decision
    /// store).</summary>
    public IReadOnlyList<MigrationSourceCandidate> DetectAll(FlintPaths paths, MigrationDecisionStore? decisions)
    {
        var found = new List<MigrationSourceCandidate>();
        foreach (var profile in _profiles)
        {
            found.AddRange(profile.Detect(paths, decisions));
        }
        return found;
    }

    /// <summary>G2: profiles that would auto-run at startup — AutoRunAtStartup profiles with
    /// at least one detected, still-undecided source.</summary>
    public IReadOnlyList<IMigrationSourceProfile> AutoRunCandidates(FlintPaths paths,
        MigrationDecisionStore? decisions) =>
        _profiles.Where(profile => profile.AutoRunAtStartup &&
                                   profile.Detect(paths, decisions).Any(candidate => !candidate.DecisionRecorded))
            .ToList();
}
