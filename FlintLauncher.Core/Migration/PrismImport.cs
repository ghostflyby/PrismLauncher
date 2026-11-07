// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// The Prism→Flint import layer on top of the DataMigration port: a work unit that moves or
// copies a typical Prism data directory into the Flint data directory (one
// whitelist per top-level item, idempotent "destination exists → skip"), a dry-run planner
// and a source detector for the migration endpoints (M* endpoints, active operation only —
// there is no automatic startup migration).
//
// Inventory deviations from the C++ whitelist (documented):
//  - "meta/" and "instgroups.json" are imported too: Flint reads the same formats and this
//    saves the metadata re-download while keeping instance groups.
//  - "instgroups.json" lands verbatim: the member uuid-ification (and the hidden->state
//    split) happen on the FIRST GROUP LOAD (InstanceGroupModel.Load, migratingLegacyGroups
//    promote per the FlintPaths.GroupsFile contract), not inside this pipeline — the
//    "sorts everything in a single pass" wording is met in outcome (converged terminal
//    state), not in timing. Do not add a converter here; the group model already owns the
//    conversion.
//  - java/, cache/, catpacks/, skins/, translations/ are not imported (Flint manages its
//    own runtimes; the rest is regenerated or Prism-specific display data).

using System.Globalization;
using System.Security;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Tasks;
using TruePath;
using TruePath.SystemIo;

namespace FlintLauncher.Core.Migration;

/// <summary>How the numbers in a plan were derived (plan/diagnostics output).</summary>
public enum PlannedMethod
{
    /// <summary>Whole subtree moves with one same-volume rename.</summary>
    RenameDirectory,

    /// <summary>Per-file transfer (clone/copy/rename per the chosen mode).</summary>
    PerFile,

    /// <summary>Destination already has this item — nothing to do (idempotent resume).</summary>
    DestinationExists,
}

/// <summary>One planned top-level item of the import.</summary>
public sealed record PrismImportPlanItem(
    string Name,
    bool IsDirectory,
    PlannedMethod Method,
    long Files,
    long Bytes);

/// <summary>Dry-run planner: what would be imported, how, and how big it is.</summary>
public static class PrismImportPlanner
{
    /// <summary>Top-level directories of the whitelist, in import order.</summary>
    public static readonly string[] ImportDirectories =
    [
        "logs", "accounts", "assets", "icons", "instances", "libraries", "mods", "themes", "meta",
    ];

    /// <summary>Top-level files of the whitelist, in import order. The MultiMC config is
    /// dual-name (upstream MultiMC writes <c>multimc.cfg</c> — C++
    /// Application.cpp:604 — while the historical detector carried only <c>mmc.cfg</c>, a
    /// name the C++ tree never uses): both are imported when present. NOTE: the
    /// Prism bare <c>metacache</c> ledger is deliberately NOT whitelisted — Flint never
    /// reads it, so importing it would only move dead stock; existing copies in the target
    /// directory are deleted once after a successful import (see PrismImportTask).</summary>
    public static readonly string[] ImportFiles =
    [
        "accounts.json", "instgroups.json", "prismlauncher.cfg", "polymc.cfg", "multimc.cfg",
        "mmc.cfg",
    ];

    public static IReadOnlyList<PrismImportPlanItem> Plan(AbsolutePath sourceDir, AbsolutePath targetDataDir,
        ImportMode mode)
    {
        var items = new List<PrismImportPlanItem>();
        foreach (var dir in ImportDirectories)
        {
            var src = sourceDir / dir;
            if (!src.ExistsDirectory())
            {
                continue;
            }

            var dst = targetDataDir / dir;
            if (dst.ExistsDirectory())
            {
                // Flint ALWAYS creates <data>/instances (InstanceList's constructor
                // ensures the folder), so a flat "destination exists → skip" silently
                // strands the whole imported tree behind a success report + imported record.
                // The C++ migration runs before InstanceList and merges FILE BY FILE
                // (FileSystem.cpp:403-471): drill down and plan the per-file merge whenever
                // any source file is missing at the destination — files already there keep
                // skipping (the idempotent merge), the planner's Files/Bytes stay the
                // source basis so the walk's progress accounting stays exact.
                var method = HasFilesMissingAtDestination(src, dst)
                    ? PlannedMethod.PerFile
                    : PlannedMethod.DestinationExists;
                items.Add(new PrismImportPlanItem(dir, IsDirectory: true, method,
                    CountFiles(src), SizeOf(src)));
                continue;
            }

            var moveRename = mode == ImportMode.Move && FileTransfer.SameVolume(src.Value, dst.Value);
            items.Add(new PrismImportPlanItem(dir, IsDirectory: true,
                moveRename ? PlannedMethod.RenameDirectory : PlannedMethod.PerFile,
                CountFiles(src), SizeOf(src)));
        }

        foreach (var file in ImportFiles)
        {
            var src = sourceDir / file;
            if (!src.ExistsFile())
            {
                continue;
            }

            var dst = targetDataDir / file;
            var method = dst.ExistsFile()
                ? PlannedMethod.DestinationExists
                : mode == ImportMode.Move && FileTransfer.SameVolume(src.Value, dst.Value)
                    ? PlannedMethod.RenameDirectory
                    : PlannedMethod.PerFile;
            items.Add(new PrismImportPlanItem(file, IsDirectory: false, method, Files: 1,
                new FileInfo(src.Value).Length));
        }

        return items;
    }

    public static long CountFiles(AbsolutePath dir)
    {
        var count = 0L;
        foreach (var _ in FileTransfer.EnumerateFilesDeep(dir))
        {
            count++;
        }

        return count;
    }

    public static long SizeOf(AbsolutePath dir)
    {
        var total = 0L;
        foreach (var file in FileTransfer.EnumerateFilesDeep(dir))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        return total;
    }

    /// <summary>Whether any file below <paramref name="srcDir"/> is missing at
    /// <paramref name="dstDir"/> — the drill-down condition that turns a
    /// "destination exists" directory into a resumable per-file merge. The same
    /// enumeration and the same destination layout the import walk uses, so plan and
    /// transfer can never disagree.</summary>
    private static bool HasFilesMissingAtDestination(AbsolutePath srcDir, AbsolutePath dstDir)
    {
        foreach (var file in FileTransfer.EnumerateFilesDeep(srcDir))
        {
            var relative = Path.GetRelativePath(srcDir.Value, file);
            if (!(dstDir / relative).ExistsFile())
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// The import task: transfers the whitelisted top-level items from a Prism data directory
/// into the Flint data directory per the chosen mode. Directories move with one same-volume
/// rename when possible, otherwise per file through
/// <see cref="FileTransfer.TransferFile"/>; destination files are never overwritten (the
/// import is an idempotent merge). On full success the import decision is recorded (a
/// {source, decision: imported, at, mode, instances, bytes} under the state root's
/// migration/ directory) so a detector does not propose the same source twice.
/// </summary>
public sealed class PrismImportTask
{
    private readonly AbsolutePath _sourceDir;
    private readonly AbsolutePath _targetDataDir;
    private readonly ImportMode _mode;
    private readonly string _sourceName;
    private readonly MigrationDecisionStore? _decisions;

    /// <summary><paramref name="sourceName"/> is the actual legacy-launcher id the record is
    /// filed under ("prism" was hardcoded before; candidates carry polymc/multimc too).
    /// <paramref name="decisions"/> null (bare fixtures) skips the record write.</summary>
    public PrismImportTask(AbsolutePath sourceDir, AbsolutePath targetDataDir, ImportMode mode,
        string sourceName = "prism", MigrationDecisionStore? decisions = null)
    {
        _sourceDir = sourceDir;
        _targetDataDir = targetDataDir;
        _mode = mode;
        _sourceName = sourceName;
        _decisions = decisions;
    }

    /// <summary>Transfers the whitelisted items. Returning is success; any other exception is a
    /// failure whose message is the reason. <paramref name="progress"/> null runs without
    /// reporting.</summary>
    public async Task ImportAsync(IProgress<TaskProgress>? progress, CancellationToken cancellationToken)
    {
        var plan = PrismImportPlanner.Plan(_sourceDir, _targetDataDir, _mode);
        var totalFiles = plan.Sum(item => item.Files);
        if (totalFiles == 0)
        {
            throw new InvalidOperationException(
                "Nothing to import: the source directory has no whitelisted content.");
        }

        // the source inventory is read BEFORE any transfer: a same-volume move empties the
        // source mid-task, so counting afterwards would undercount the record's Instances
        var sourceInstances = CountSourceInstances();

        var done = 0L;
        var failed = false;
        foreach (var item in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Method == PlannedMethod.DestinationExists)
            {
                done += item.Files;
                continue;
            }

            var srcItem = _sourceDir / item.Name;
            var dstItem = _targetDataDir / item.Name;

            if (item is { IsDirectory: true, Method: PlannedMethod.RenameDirectory })
            {
                if (FileTransfer.TryMoveDirectory(srcItem.Value, dstItem.Value))
                {
                    done += item.Files;
                    continue;
                }
                // volume differs or destination appeared — fall through per file
            }

            // report per file rather than per item: a per-file line flashes past faster than a
            // poller can read it, while the item name says which part of the tree is moving
            foreach (var file in EnumerateFiles(srcItem))
            {
                cancellationToken.ThrowIfCancellationRequested();
                done++;
                progress?.Report(new TaskProgress(done, totalFiles));
                var relative = Path.GetRelativePath(_sourceDir.Value, file).Replace('\\', '/');
                var dst = _targetDataDir / relative;
                if (!FileTransfer.TransferFile(file, dst.Value, _mode, out _))
                {
                    failed = true;
                }
            }
        }

        progress?.Report(new TaskProgress(totalFiles, totalFiles));
        if (failed)
        {
            throw new InvalidOperationException("Some paths could not be imported!");
        }

        // the target may still carry the Prism bare `metacache` ledger from
        // historical imports (the whitelist never carried it; nothing reads it) — dead
        // stock, deleted once here. Best effort: the data has fully landed.
        try
        {
            var legacyLedger = _targetDataDir / "metacache";
            if (legacyLedger.ExistsFile())
            {
                legacyLedger.Delete();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or SecurityException or ArgumentException
                                      or NotSupportedException)
        {
        }

        RecordImportDecision(plan, sourceInstances);
    }

    /// <summary>The completed import lands as an `imported` decision record under the
    /// actual source name — instances counts the instance directories observed at the source
    /// BEFORE the transfer ran (planned basis), bytes the planned transfer size — the
    /// planned figure is the reproducible one).</summary>
    private void RecordImportDecision(IReadOnlyList<PrismImportPlanItem> plan, long sourceInstances)
    {
        if (_decisions is null)
        {
            return;
        }

        try
        {
            _decisions.Write(new MigrationDecisionRecord
            {
                Source = _sourceName,
                Decision = MigrationDecisionRecord.Imported,
                At = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
                Mode = _mode switch
                {
                    ImportMode.CopyOnWrite => "cow",
                    ImportMode.Move => "move",
                    _ => "copy",
                },
                Instances = sourceInstances,
                Bytes = plan.Sum(item => item.Bytes),
            });
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException
                                      or NotSupportedException or SecurityException)
        {
            // best-effort (the legacy marker write was the same, DataMigration.Decide's
            // upgrade path included): the data has fully landed — a failed decision-record
            // write must not report the import itself as failed. The next detect/decide
            // pass will re-propose the source, which is the honest outcome of a missing
            // record.
        }
    }

    /// <summary>The instance-directory count of the source (the record's planned Instances
    /// basis — called before any transfer so a same-volume move of instances/ cannot
    /// undercount).</summary>
    private long CountSourceInstances()
    {
        var instancesDir = _sourceDir / "instances";
        return instancesDir.ExistsDirectory()
            ? Directory.EnumerateDirectories(instancesDir.Value).Count()
            : 0;
    }

    private static IEnumerable<string> EnumerateFiles(AbsolutePath dir)
    {
        // top-level whitelist FILES (accounts.json, instgroups.json) arrive here as file paths
        if (dir.ExistsFile())
        {
            return [dir.Value];
        }

        if (!dir.ExistsDirectory())
        {
            return [];
        }

        // the transfer enumeration — file symlinks are yielded (copied by content,
        // like the C++ ResolveSymlinks walk), directory links are not descended into
        return FileTransfer.EnumerateFilesDeep(dir);
    }
}

/// <summary>Detects candidate Prism-family data directories at the per-OS default locations.</summary>
public static class PrismSourceDetector
{
    /// <summary>Legacy launcher name → its own config file name. MultiMC appears twice
    /// (dual-name probe): upstream MultiMC writes <c>multimc.cfg</c> (C++
    /// Application.cpp:604; the C++ tree carries no <c>mmc.cfg</c>), while the historical
    /// detector only probed <c>mmc.cfg</c> — so a genuine MultiMC directory never entered
    /// the candidates. The Detect loop probes both names for the one directory (the
    /// path-dedupe collapses the rows once the first name matched); upstream's
    /// <c>multimc.cfg</c> wins when both exist.</summary>
    public static readonly (string Name, string ConfigFile)[] KnownLaunchers =
    [
        ("prism", "prismlauncher.cfg"),
        ("polymc", "polymc.cfg"),
        ("multimc", "multimc.cfg"),
        ("multimc", "mmc.cfg"),
    ];

    /// <summary>Default parent directories that may contain launcher data, per platform.</summary>
    public static IReadOnlyList<AbsolutePath> DefaultParents()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
        {
            return [new AbsolutePath(home) / "Library" / "Application Support"];
        }

        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return [new AbsolutePath(appData)];
        }

        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var baseDir = string.IsNullOrWhiteSpace(xdgDataHome)
            ? new AbsolutePath(home) / ".local" / "share"
            : new AbsolutePath(xdgDataHome);
        return [baseDir, new AbsolutePath(home) / ".config"];
    }

    public sealed record PrismSourceCandidate(
        AbsolutePath Path,
        string LegacyName,
        string ConfigFile,
        bool ConfigFound,
        bool MarkerPresent,
        bool TargetHasOwnConfig,
        DataMigration.MigrationDecision Decision,
        int InstancesFound);

    /// <summary>Scan the default parents for launcher data directories carrying one of the
    /// known config files, with the migration decision for the given Flint data directory
    /// (<paramref name="decisions"/> carries the per-source decision records and upgrades
    /// any legacy marker encountered). <paramref name="currentConfigFile"/> is Flint's own
    /// config at its authoritative position (post-reform <c>&lt;config-root&gt;/flint.json</c>)
    /// — the existing-data probe behind <see cref="PrismSourceCandidate.TargetHasOwnConfig"/>.</summary>
    public static IReadOnlyList<PrismSourceCandidate> Detect(AbsolutePath targetDataDir,
        MigrationDecisionStore? decisions = null, AbsolutePath? currentConfigFile = null) =>
        Detect(targetDataDir, DefaultParents(), decisions, currentConfigFile);

    /// <summary>Detect over explicit parent directories (tests inject a sandbox root).</summary>
    public static IReadOnlyList<PrismSourceCandidate> Detect(AbsolutePath targetDataDir,
        IEnumerable<AbsolutePath> parents,
        MigrationDecisionStore? decisions = null, AbsolutePath? currentConfigFile = null)
    {
        var found = new List<PrismSourceCandidate>();
        foreach (var parent in parents)
        {
            if (!parent.ExistsDirectory())
            {
                continue;
            }

            foreach (var (name, configFile) in KnownLaunchers)
            {
                string a1 = NameToDirectory(name);
                var dir = parent / a1;
                if (!dir.ExistsDirectory() ||
                    found.Any(c => string.Equals(c.Path.Value, dir.Value, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var hasConfig = (dir / configFile).ExistsFile()
                                || (dir / "prismlauncher.cfg").ExistsFile();
                if (!hasConfig)
                {
                    continue;
                }

                var instances = dir / "instances";
                var instancesFound = instances.ExistsDirectory()
                    ? Directory.EnumerateDirectories(instances.Value).Count()
                    : 0;
                var decision = DataMigration.Decide(targetDataDir, dir, name,
                    configFile, "flint.cfg", decisions, currentConfigFile);
                found.Add(new PrismSourceCandidate(dir, name, configFile,
                    ConfigFound: true, MarkerPresent: decision == DataMigration.MigrationDecision.MarkerPresent,
                    TargetHasOwnConfig: decision == DataMigration.MigrationDecision.CurrentDataExists,
                    decision, instancesFound));
            }
        }

        return found;
    }

    /// <summary>The directory a legacy launcher's data lives under inside the scanned
    /// parents. Linux aligns with the C++ probe path (Application.cpp:603): the MultiMC
    /// sibling of the app-data location is the LOWERCASE <c>multimc</c> (the probe is
    /// <c>AppDataLocation/../../multimc</c>, i.e. <c>~/.local/share/multimc</c> — exactly
    /// where <see cref="DefaultParents"/> scans); the other platforms keep the installed
    /// directory's capitalized name. Public because the migration endpoints map a source
    /// directory back to its launcher id through it.</summary>
    public static string NameToDirectory(string name) => name switch
    {
        "multimc" => OperatingSystem.IsLinux() ? "multimc" : "MultiMC",
        "polymc" => "PolyMC",
        _ => "PrismLauncher",
    };
}
