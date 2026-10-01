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

    /// <summary>Top-level files of the whitelist, in import order.</summary>
    public static readonly string[] ImportFiles =
    [
        "accounts.json", "instgroups.json", "prismlauncher.cfg", "polymc.cfg", "mmc.cfg",
    ];

    public static IReadOnlyList<PrismImportPlanItem> Plan(string sourceDir, string targetDataDir, ImportMode mode)
    {
        var items = new List<PrismImportPlanItem>();
        foreach (var dir in ImportDirectories)
        {
            var src = System.IO.Path.Combine(sourceDir, dir);
            if (!Directory.Exists(src))
            {
                continue;
            }
            var dst = System.IO.Path.Combine(targetDataDir, dir);
            if (Directory.Exists(dst))
            {
                items.Add(new PrismImportPlanItem(dir, IsDirectory: true, PlannedMethod.DestinationExists,
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
            var src = System.IO.Path.Combine(sourceDir, file);
            if (!File.Exists(src))
            {
                continue;
            }
            var dst = System.IO.Path.Combine(targetDataDir, file);
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
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        var count = 0L;
        foreach (var _ in Directory.EnumerateFiles(dir, "*", options))
        {
            count++;
        }
        return count;
    }

    public static long SizeOf(string dir)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        var total = 0L;
        foreach (var file in Directory.EnumerateFiles(dir, "*", options))
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
}

/// <summary>
/// The import task: transfers the whitelisted top-level items from a Prism data directory
/// into the Flint data directory per the chosen mode. Directories move with one same-volume
/// rename when possible, otherwise per file through
/// <see cref="FileTransfer.TransferFile"/>; destination files are never overwritten (the
/// import is an idempotent merge). On full success the "declined" marker is written so a
/// detector does not propose the same source twice.
/// </summary>
public sealed class PrismImportTask : FlintTask
{
    private readonly string _sourceDir;
    private readonly string _targetDataDir;
    private readonly ImportMode _mode;

    public PrismImportTask(string sourceDir, string targetDataDir, ImportMode mode)
    {
        _sourceDir = sourceDir;
        _targetDataDir = targetDataDir;
        _mode = mode;
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        EmitStatus("Scanning the source directory...");

        var plan = PrismImportPlanner.Plan(_sourceDir, _targetDataDir, _mode);
        var totalFiles = plan.Sum(item => item.Files);
        if (totalFiles == 0)
        {
            EmitFailed("Nothing to import: the source directory has no whitelisted content.");
            return Task.CompletedTask;
        }

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

            var srcItem = System.IO.Path.Combine(_sourceDir, item.Name);
            var dstItem = System.IO.Path.Combine(_targetDataDir, item.Name);

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
                var relative = System.IO.Path.GetRelativePath(_sourceDir, file).Replace('\\', '/');
                var dst = System.IO.Path.Combine(_targetDataDir, relative);
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
            return Task.CompletedTask;
        }

        DataMigration.SetDoNotMigrate(_targetDataDir, "prism");
        EmitSucceeded();
        return Task.CompletedTask;
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
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        return Directory.EnumerateFiles(dir, "*", options);
    }

    private static string Truncate(string relative) =>
        relative.Length > 50 ? relative[..20] + "…" + relative[^29..] : relative;
}

/// <summary>Detects candidate Prism-family data directories at the per-OS default locations.</summary>
public static class PrismSourceDetector
{
    /// <summary>Legacy launcher name → its own config file name.</summary>
    public static readonly (string Name, string ConfigFile)[] KnownLaunchers =
    [
        ("prism", "prismlauncher.cfg"),
        ("polymc", "polymc.cfg"),
        ("multimc", "mmc.cfg"),
    ];

    /// <summary>Default parent directories that may contain launcher data, per platform.</summary>
    public static IReadOnlyList<string> DefaultParents()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
        {
            return [System.IO.Path.Combine(home, "Library", "Application Support")];
        }
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return [appData];
        }
        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var baseDir = string.IsNullOrWhiteSpace(xdgDataHome)
            ? System.IO.Path.Combine(home, ".local", "share")
            : xdgDataHome;
        return [baseDir, System.IO.Path.Combine(home, ".config")];
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
    /// known config files, with the migration decision for the given Flint data directory.</summary>
    public static IReadOnlyList<PrismSourceCandidate> Detect(string targetDataDir) =>
        Detect(targetDataDir, DefaultParents());

    /// <summary>Detect over explicit parent directories (tests inject a sandbox root).</summary>
    public static IReadOnlyList<PrismSourceCandidate> Detect(string targetDataDir, IEnumerable<string> parents)
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
                var dir = System.IO.Path.Combine(parent, NameToDirectory(name));
                if (!Directory.Exists(dir) || found.Any(c => string.Equals(c.Path, dir, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                var hasConfig = File.Exists(System.IO.Path.Combine(dir, configFile))
                    || File.Exists(System.IO.Path.Combine(dir, "prismlauncher.cfg"));
                if (!hasConfig)
                {
                    continue;
                }
                var instances = System.IO.Path.Combine(dir, "instances");
                var instancesFound = Directory.Exists(instances)
                    ? Directory.EnumerateDirectories(instances).Count()
                    : 0;
                var decision = DataMigration.Decide(targetDataDir, dir, name,
                    configFile, "flint.cfg");
                found.Add(new PrismSourceCandidate(dir, name, configFile,
                    ConfigFound: true, MarkerPresent: decision == DataMigration.MigrationDecision.MarkerPresent,
                    TargetHasOwnConfig: decision == DataMigration.MigrationDecision.CurrentDataExists,
                    decision, instancesFound));
            }
        }
        return found;
    }

    private static string NameToDirectory(string name) => name switch
    {
        "multimc" => "MultiMC",
        "polymc" => "PolyMC",
        _ => "PrismLauncher",
    };
}
