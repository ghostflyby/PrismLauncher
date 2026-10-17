// SPDX-License-Identifier: GPL-3.0-only
// The Prism→Flint import layer on top of the DataMigration port: a FlintTask that moves or
// reflink/CoW-copies a typical Prism data directory into the Flint data directory (one
// whitelist per top-level item, idempotent "destination exists → skip"), a dry-run planner
// and a source detector for the migration endpoints (M* endpoints, active operation only —
// there is no automatic startup migration).
//
// Inventory deviations from the C++ whitelist (documented):
//  - "meta/" and "instgroups.json" are imported too: Flint reads the same formats and this
//    saves the metadata re-download while keeping instance groups.
//  - "instgroups.json" lands verbatim: the D3 member uuid-ification (and the hidden->state
//    split) happen on the FIRST GROUP LOAD (InstanceGroupModel.Load, migratingLegacyGroups
//    promote per the FlintPaths.GroupsFile contract), not inside this pipeline — G2's
//    "sorts everything in a single pass" wording is met in outcome (converged terminal
//    state), not in timing. Do not add a converter here; the group model already owns the
//    conversion.
//  - java/, cache/, catpacks/, skins/, translations/ are not imported (Flint manages its
//    own runtimes; the rest is regenerated or Prism-specific display data).
using System.Globalization;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Tasks;

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
    /// dual-name (CONFIG.md:23: upstream MultiMC writes <c>multimc.cfg</c> — C++
    /// Application.cpp:604 — while the historical detector carried only <c>mmc.cfg</c>, a
    /// name the C++ tree never uses): both are imported when present. NOTE (CACHE E8): the
    /// Prism bare <c>metacache</c> ledger is deliberately NOT whitelisted — Flint never
    /// reads it, so importing it would only move dead stock; existing copies in the target
    /// directory are deleted once after a successful import (see PrismImportTask).</summary>
    public static readonly string[] ImportFiles =
    [
        "accounts.json", "instgroups.json", "prismlauncher.cfg", "polymc.cfg", "multimc.cfg",
        "mmc.cfg",
    ];

    public static IReadOnlyList<PrismImportPlanItem> Plan(string sourceDir, string targetDataDir, ImportMode mode)
    {
        var items = new List<PrismImportPlanItem>();
        foreach (var dir in ImportDirectories)
        {
            var src = Path.Combine(sourceDir, dir);
            if (!Directory.Exists(src))
            {
                continue;
            }
            var dst = Path.Combine(targetDataDir, dir);
            if (Directory.Exists(dst))
            {
                // B2: Flint ALWAYS creates <data>/instances (InstanceList's constructor
                // ensures the folder), so a flat "destination exists → skip" silently
                // strands the whole imported tree behind an EmitSucceeded + imported record.
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
            var moveRename = mode == ImportMode.Move && FileTransfer.SameVolume(src, dst);
            items.Add(new PrismImportPlanItem(dir, IsDirectory: true,
                moveRename ? PlannedMethod.RenameDirectory : PlannedMethod.PerFile,
                CountFiles(src), SizeOf(src)));
        }

        foreach (var file in ImportFiles)
        {
            var src = Path.Combine(sourceDir, file);
            if (!File.Exists(src))
            {
                continue;
            }
            var dst = Path.Combine(targetDataDir, file);
            var method = File.Exists(dst) ? PlannedMethod.DestinationExists
                : mode == ImportMode.Move && FileTransfer.SameVolume(src, dst)
                    ? PlannedMethod.RenameDirectory
                    : PlannedMethod.PerFile;
            items.Add(new PrismImportPlanItem(file, IsDirectory: false, method, Files: 1,
                new FileInfo(src).Length));
        }
        return items;
    }

    public static long CountFiles(string dir)
    {
        var count = 0L;
        foreach (var _ in FileTransfer.EnumerateFilesDeep(dir))
        {
            count++;
        }
        return count;
    }

    public static long SizeOf(string dir)
    {
        var total = 0L;
        foreach (var file in FileTransfer.EnumerateFilesDeep(dir))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        return total;
    }

    /// <summary>B2: whether any file below <paramref name="srcDir"/> is missing at
    /// <paramref name="dstDir"/> — the drill-down condition that turns a
    /// "destination exists" directory into a resumable per-file merge. The same
    /// enumeration and the same destination layout the import walk uses, so plan and
    /// transfer can never disagree.</summary>
    private static bool HasFilesMissingAtDestination(string srcDir, string dstDir)
    {
        foreach (var file in FileTransfer.EnumerateFilesDeep(srcDir))
        {
            var relative = Path.GetRelativePath(srcDir, file);
            if (!File.Exists(Path.Combine(dstDir, relative)))
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
/// import is an idempotent merge). On full success the import decision is recorded (S3:
/// {source, decision: imported, at, mode, instances, bytes} under the state root's
/// migration/ directory) so a detector does not propose the same source twice.
/// </summary>
public sealed class PrismImportTask : FlintTask
{
    private readonly string _sourceDir;
    private readonly string _targetDataDir;
    private readonly ImportMode _mode;
    private readonly string _sourceName;
    private readonly MigrationDecisionStore? _decisions;

    /// <summary><paramref name="sourceName"/> is the actual legacy-launcher id the record is
    /// filed under (S7: "prism" was hardcoded before; candidates carry polymc/multimc too).
    /// <paramref name="decisions"/> null (bare fixtures) skips the record write.</summary>
    public PrismImportTask(string sourceDir, string targetDataDir, ImportMode mode,
        string sourceName = "prism", MigrationDecisionStore? decisions = null)
    {
        _sourceDir = sourceDir;
        _targetDataDir = targetDataDir;
        _mode = mode;
        _sourceName = sourceName;
        _decisions = decisions;
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        // C++ Task::start() ran the body on its own QThread; without a yield this body (no
        // awaits below) would complete INLINE on the caller's thread — the migration
        // endpoint's fire-and-forget StartAsync would block the POST until the whole
        // transfer finished and an abort() could never arrive mid-import (the client only
        // learns the task id from the 202, by which time the task is terminal). The yield
        // makes the 202 truthful and the cooperative cancellation points below reachable.
        await Task.Yield();

        EmitStatus("Scanning the source directory...");

        var plan = PrismImportPlanner.Plan(_sourceDir, _targetDataDir, _mode);
        var totalFiles = plan.Sum(item => item.Files);
        if (totalFiles == 0)
        {
            EmitFailed("Nothing to import: the source directory has no whitelisted content.");
            return;
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

            var srcItem = Path.Combine(_sourceDir, item.Name);
            var dstItem = Path.Combine(_targetDataDir, item.Name);

            if (item.IsDirectory && item.Method == PlannedMethod.RenameDirectory)
            {
                EmitStatus($"Moving {item.Name}…");
                if (FileTransfer.TryMoveDirectory(srcItem, dstItem))
                {
                    done += item.Files;
                    continue;
                }
                // volume differs or destination appeared — fall through per file
            }

            foreach (var file in EnumerateFiles(srcItem))
            {
                cancellationToken.ThrowIfCancellationRequested();
                done++;
                SetProgress(done, totalFiles);
                var relative = Path.GetRelativePath(_sourceDir, file).Replace('\\', '/');
                var dst = Path.Combine(_targetDataDir, relative);
                EmitStatus($"Importing {Truncate(relative)}…");
                if (!FileTransfer.TransferFile(file, dst, _mode, out _))
                {
                    failed = true;
                }
            }
        }

        SetProgress(totalFiles, totalFiles);
        if (failed)
        {
            EmitFailed("Some paths could not be imported!");
            return;
        }

        // CACHE E8: the target may still carry the Prism bare `metacache` ledger from
        // historical imports (the whitelist never carried it; nothing reads it) — dead
        // stock, deleted once here. Best effort: the data has fully landed.
        try
        {
            var legacyLedger = Path.Combine(_targetDataDir, "metacache");
            if (File.Exists(legacyLedger))
            {
                File.Delete(legacyLedger);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                     or System.Security.SecurityException or ArgumentException
                                     or NotSupportedException)
        {
        }

        RecordImportDecision(plan, sourceInstances);
        EmitSucceeded();
    }

    /// <summary>S3/S7: the completed import lands as an `imported` decision record under the
    /// actual source name — instances counts the instance directories observed at the source
    /// BEFORE the transfer ran (planned basis), bytes the planned transfer size (the
    /// 计划量 vs 实成量 口径 stays open per STATE §4; the planned figure is the reproducible
    /// one).</summary>
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
                                  or NotSupportedException or System.Security.SecurityException)
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
        var instancesDir = Path.Combine(_sourceDir, "instances");
        return Directory.Exists(instancesDir)
            ? Directory.EnumerateDirectories(instancesDir).Count()
            : 0;
    }

    private static IEnumerable<string> EnumerateFiles(string dir)
    {
        // top-level whitelist FILES (accounts.json, instgroups.json) arrive here as file paths
        if (File.Exists(dir))
        {
            return [dir];
        }
        if (!Directory.Exists(dir))
        {
            return [];
        }
        // B4: the transfer enumeration — file symlinks are yielded (copied by content,
        // like the C++ ResolveSymlinks walk), directory links are not descended into
        return FileTransfer.EnumerateFilesDeep(dir);
    }

    private static string Truncate(string relative) =>
        relative.Length > 50 ? relative[..20] + "…" + relative[^29..] : relative;
}

/// <summary>Detects candidate Prism-family data directories at the per-OS default locations.</summary>
public static class PrismSourceDetector
{
    /// <summary>Legacy launcher name → its own config file name. MultiMC appears twice
    /// (CONFIG.md:23 dual-name probe): upstream MultiMC writes <c>multimc.cfg</c> (C++
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
    public static IReadOnlyList<string> DefaultParents()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
        {
            return [Path.Combine(home, "Library", "Application Support")];
        }
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return [appData];
        }
        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var baseDir = string.IsNullOrWhiteSpace(xdgDataHome)
            ? Path.Combine(home, ".local", "share")
            : xdgDataHome;
        return [baseDir, Path.Combine(home, ".config")];
    }

    public sealed record PrismSourceCandidate(
        string Path,
        string LegacyName,
        string ConfigFile,
        bool ConfigFound,
        bool MarkerPresent,
        bool TargetHasOwnConfig,
        DataMigration.MigrationDecision Decision,
        int InstancesFound);

    /// <summary>Scan the default parents for launcher data directories carrying one of the
    /// known config files, with the migration decision for the given Flint data directory
    /// (S3: <paramref name="decisions"/> carries the per-source decision records and upgrades
    /// any legacy marker encountered). <paramref name="currentConfigFile"/> is Flint's own
    /// config at its authoritative position (post-reform <c>&lt;config-root&gt;/flint.json</c>)
    /// — the existing-data probe behind <see cref="PrismSourceCandidate.TargetHasOwnConfig"/>.</summary>
    public static IReadOnlyList<PrismSourceCandidate> Detect(string targetDataDir,
        MigrationDecisionStore? decisions = null, string? currentConfigFile = null) =>
        Detect(targetDataDir, DefaultParents(), decisions, currentConfigFile);

    /// <summary>Detect over explicit parent directories (tests inject a sandbox root).</summary>
    public static IReadOnlyList<PrismSourceCandidate> Detect(string targetDataDir, IEnumerable<string> parents,
        MigrationDecisionStore? decisions = null, string? currentConfigFile = null)
    {
        var found = new List<PrismSourceCandidate>();
        foreach (var parent in parents)
        {
            if (!Directory.Exists(parent))
            {
                continue;
            }
            foreach (var (name, configFile) in KnownLaunchers)
            {
                var dir = Path.Combine(parent, NameToDirectory(name));
                if (!Directory.Exists(dir) || found.Any(c => string.Equals(c.Path, dir, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                var hasConfig = File.Exists(Path.Combine(dir, configFile))
                    || File.Exists(Path.Combine(dir, "prismlauncher.cfg"));
                if (!hasConfig)
                {
                    continue;
                }
                var instances = Path.Combine(dir, "instances");
                var instancesFound = Directory.Exists(instances)
                    ? Directory.EnumerateDirectories(instances).Count()
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
