// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/DataMigrationTask.{h,cpp} plus the data-level half of
// Application::handleDataMigration (the file/dir inventory, the "_nomigrate.txt" marker and the
// idempotency rules): migrate existing data from other MMC-like launchers into the Flint data
// directory.
//
// Deviations:
//  - The C++ task drives FS::copy on a thread pool with a dry run first; the port performs the
//    same dry-run-then-copy walk inline (counting matched files, then copying with per-file
//    status/progress), preserving FS::copy's matcher/whitelist semantics.
//  - Application::handleDataMigration's QMessageBox question is not ported; DataMigration
//    exposes the decision inputs (old config present? marker present? current config present?)
//    so a host can ask the user itself, and the C++ decision order is encoded in
//    DataMigration.Decide().
//  - The migration inventory is the exact filter list from Application::handleDataMigration;
//    the launcher's own config file name comes from FlintConfig ("flint.cfg", Prism's
//    "prismlauncher.cfg").
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Tasks;

namespace FlintLauncher.Core.Migration;

/// <summary>Port of launcher/Filter.h (Filter = std::function&lt;bool(const QString&amp;)&gt; and the
/// Filters combinators), specialized to the relative-path predicates used by the migration.</summary>
public static class Filters
{
    public static Func<string, bool> Inverse(Func<string, bool> filter) => src => !filter(src);

    public static Func<string, bool> Any(IReadOnlyList<Func<string, bool>> filters) =>
        src =>
        {
            foreach (var filter in filters)
            {
                if (filter(src))
                {
                    return true;
                }
            }
            return false;
        };

    public static Func<string, bool> Equals_(string pattern) => src => src == pattern;

    public static Func<string, bool> EqualsAny(IReadOnlyList<string> patterns) =>
        src => patterns.Count == 0 || patterns.Contains(src);

    public static Func<string, bool> EqualsOrEmpty(string pattern) => src => src.Length == 0 || src == pattern;

    public static Func<string, bool> Contains(string pattern) => src => src.Contains(pattern, StringComparison.Ordinal);

    public static Func<string, bool> StartsWith(string pattern) => src => src.StartsWith(pattern, StringComparison.Ordinal);

    public static Func<string, bool> Regexp(System.Text.RegularExpressions.Regex pattern) =>
        src => pattern.IsMatch(src);
}

/// <summary>
/// Port of DataMigrationTask: copies everything the whitelist matcher accepts from
/// <paramref name="sourcePath"/> into <paramref name="targetPath"/> (relative paths preserved,
/// destination files are not overwritten), after a dry run that counts the files for the
/// progress bar.
/// </summary>
public sealed class DataMigrationTask : FlintTask
{
    private readonly string _sourcePath;
    private readonly string _targetPath;
    private readonly Func<string, bool> _pathMatcher;

    public DataMigrationTask(string sourcePath, string targetPath, Func<string, bool> pathMatcher)
    {
        _sourcePath = sourcePath;
        _targetPath = targetPath;
        _pathMatcher = pathMatcher;
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        EmitStatus("Scanning files...");

        // 1. Scan — dry run to collect the amount of files.
        if (!Walk(out var files))
        {
            EmitFailed("Failed to scan source path.");
            return Task.CompletedTask;
        }

        var toCopy = files.Count;

        // 2. Copy — actually copy all files now.
        var copied = 0;
        var failed = false;
        foreach (var relative in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            copied++;
            var shortenedName = relative;
            // shorten the filename to hopefully fit into one line
            if (shortenedName.Length > 50)
            {
                shortenedName = relative[..20] + "…" + relative[^29..];
            }
            SetProgress(copied, toCopy);
            EmitStatus($"Copying {shortenedName}…");
            if (!CopyFile(relative))
            {
                // C++ collects per-file failures (fs::copy err), keeps copying the remaining
                // files, and fails the whole task at the end with this message.
                failed = true;
            }
        }

        if (failed)
        {
            EmitFailed("Some paths could not be copied!");
            return Task.CompletedTask;
        }

        EmitSucceeded();
        return Task.CompletedTask;
    }

    /// <summary>The FS::copy dry run: files (including hidden ones) below the source
    /// directory, matched against the whitelist on the '/'-separated relative path. A missing
    /// source directory fails the walk — a deliberate divergence: C++ FS::copy actually
    /// returns true for a missing root (empty QDirListing, FileSystem.cpp:467; the
    /// non-directory root is then filtered out by the whitelist matcher on the empty relative
    /// path, FileSystem.cpp:404/474-475; <c>return err.value() == 0</c>, FileSystem.cpp:514),
    /// so the C++ task "succeeds" with 0 files and its scan-failure message is unreachable.
    /// This port prefers a loud failure for direct callers (tests/embedded hosts); the real
    /// pipeline never reaches this — Decide requires an existing old config first. File
    /// symlinks are matched (and copied by content below — the C++ ResolveSymlinks walk);
    /// directory links are not descended into.</summary>
    private bool Walk(out List<string> matched)
    {
        matched = [];
        if (!Directory.Exists(_sourcePath))
        {
            return false;
        }
        foreach (var file in FileTransfer.EnumerateFilesDeep(_sourcePath))
        {
            var relative = Path.GetRelativePath(_sourcePath, file).Replace('\\', '/');
            if (_pathMatcher(relative))
            {
                matched.Add(relative);
            }
        }
        return true;
    }

    private bool CopyFile(string relative)
    {
        var dstPath = FileSystem.PathCombine(_targetPath, relative);
        if (!FileSystem.EnsureFilePathExists(dstPath))
        {
            return false;
        }
        var srcPath = FileSystem.PathCombine(_sourcePath, relative);
        try
        {
            File.Copy(srcPath, dstPath, overwrite: false);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                 or NotSupportedException)
        {
            return false;
        }
    }
}

/// <summary>
/// Data-level port of Application::handleDataMigration: which legacy launcher directories can
/// be migrated, the exact inventory copied, and the one-shot per-source decision that keeps
/// the migration from proposing twice — since STATE S3 that decision lives in the
/// <see cref="MigrationDecisionRecord"/> files (the C++/pre-reform "_nomigrate.txt" empty
/// markers are read only as a legacy input and upgraded away).
/// </summary>
public static class DataMigration
{
    /// <summary>The marker file name: name + "_nomigrate.txt" (created empty).</summary>
    public static string NoMigrateMarkerName(string name) => name + "_nomigrate.txt";

    /// <summary>The marker file path inside the current data directory.</summary>
    public static string NoMigrateMarkerPath(string currentData, string name) =>
        FileSystem.PathCombine(currentData, NoMigrateMarkerName(name));

    /// <summary>Config candidates looked for in the old data directory: the legacy launcher's
    /// own config first, then the Prism/Flint one ("it's possible that we already used that
    /// directory before").</summary>
    public static IReadOnlyList<string> OldConfigCandidates(string oldData, string configFile, string launcherConfigFile) =>
    [
        FileSystem.PathCombine(oldData, configFile),
        FileSystem.PathCombine(oldData, launcherConfigFile),
    ];

    /// <summary>
    /// The exact whitelist inventory of Application::handleDataMigration (in order): the legacy
    /// config, the launcher config, logs/, accounts.json, accounts/, assets/, icons/,
    /// instances/, libraries/, mods/, themes/.
    /// </summary>
    public static Func<string, bool> BuildFilter(string configFile, string launcherConfigFile)
    {
        var filters = new List<Func<string, bool>>
        {
            Filters.Equals_(configFile),
            Filters.Equals_(launcherConfigFile), // it's possible that we already used that directory before
            Filters.StartsWith("logs/"),
            Filters.Equals_("accounts.json"),
            Filters.StartsWith("accounts/"),
            Filters.StartsWith("assets/"),
            Filters.StartsWith("icons/"),
            Filters.StartsWith("instances/"),
            Filters.StartsWith("libraries/"),
            Filters.StartsWith("mods/"),
            Filters.StartsWith("themes/"),
        };
        return Filters.Any(filters);
    }

    [Flags]
    public enum MigrationDecision
    {
        /// <summary>No legacy config at the old location (no migration needed).</summary>
        NoOldConfig = 0,
        /// <summary>The "name_nomigrate.txt" marker exists — declined or already migrated.</summary>
        MarkerPresent = 1,
        /// <summary>The current data directory already has its own launcher config.</summary>
        CurrentDataExists = 2,
        /// <summary>Set (alone) when a migration would run.</summary>
        ReadyToMigrate = 4,
    }

    /// <summary>Port of the handleDataMigration decision tree (without the dialog): a migration
    /// runs only when an old config exists, the decision was not made yet, and the current
    /// directory has no launcher config yet. The "decision made" check is the S3 record
    /// (<see cref="MigrationDecisionStore"/>/<paramref name="decisions"/>) when one is
    /// available, falling back to the legacy empty marker — which the record store upgrades
    /// on sight (declined@unknown record + marker deleted, see
    /// <see cref="DecisionAlreadyMade"/>).
    /// <paramref name="currentConfigFile"/> is the launcher's own config at its CURRENT
    /// authoritative position — post-reform <c>&lt;config-root&gt;/flint.json</c> (CONFIG
    /// C5); when omitted the pre-reform flat position
    /// <c>&lt;currentData&gt;/&lt;launcherConfigFile&gt;</c> is probed (bare fixtures). The
    /// C++ existing-data protection (Application.cpp:1994-1999, 2029, 2055-2057: "Migration
    /// was skipped, due to existing data") keys off exactly this probe.</summary>
    public static MigrationDecision Decide(string currentData, string oldData, string name, string configFile,
        string launcherConfigFile, MigrationDecisionStore? decisions = null, string? currentConfigFile = null)
    {
        var configExists = false;
        foreach (var configPath in OldConfigCandidates(oldData, configFile, launcherConfigFile))
        {
            configExists |= File.Exists(configPath);
        }

        if (!configExists || DecisionAlreadyMade(currentData, name, decisions))
        {
            return configExists ? MigrationDecision.MarkerPresent : MigrationDecision.NoOldConfig;
        }

        var currentExists = File.Exists(currentConfigFile ??
            FileSystem.PathCombine(currentData, launcherConfigFile));
        return currentExists ? MigrationDecision.CurrentDataExists : MigrationDecision.ReadyToMigrate;
    }

    /// <summary>S3: has the one-shot decision for this source been made? A decision record
    /// counts; a legacy <c>&lt;source&gt;_nomigrate.txt</c> empty marker counts too and is
    /// upgraded on sight — it becomes a declined@unknown record and the marker file is
    /// deleted (the markers stop existing; the record carries the decision). Without a
    /// <paramref name="decisions"/> store only the legacy marker is consulted (bare
    /// fixtures); it is then left in place. The upgrade write is best-effort (the same
    /// tolerance PrismImportTask's record write has): the marker on disk already encodes the
    /// decision, so a failed record write keeps it in place and the decision still
    /// holds.</summary>
    internal static bool DecisionAlreadyMade(string currentData, string name, MigrationDecisionStore? decisions)
    {
        if (decisions is not null && decisions.Read(name) is not null)
        {
            return true;
        }

        var legacyMarker = FindLegacyMarker(currentData, name);
        if (legacyMarker is null)
        {
            return false;
        }
        if (decisions is not null)
        {
            // both steps are best-effort (the same filter PrismImportTask.RecordImportDecision
            // uses): the disk marker itself already encodes the decision — the same stance as
            // the no-store branch — so a failed record write keeps it in place and a failed
            // delete only leaves an inert leftover; either way no failure escapes
            try
            {
                decisions.Write(new MigrationDecisionRecord
                {
                    Source = name,
                    Decision = MigrationDecisionRecord.Declined,
                    At = MigrationDecisionRecord.UnknownAt,
                });
                File.Delete(legacyMarker);
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException
                                     or NotSupportedException or System.Security.SecurityException)
            {
            }
        }
        return true;
    }

    /// <summary>The legacy "<c>name</c>_nomigrate.txt" marker for <paramref name="name"/> in
    /// <paramref name="currentData"/>, or null when none is present. The exact-path probe
    /// comes first (this port's own lowercase ids); when it misses, ANY file whose name
    /// matches case-insensitively counts — C++ wrote the marker under the DISPLAY launcher
    /// name ("PolyMC"/"MultiMC", Application.cpp:599/604+1977) while this port's source ids
    /// are lowercase (PrismImport.KnownLaunchers), and only a case-insensitive match keeps a
    /// C++-era decision honored on case-sensitive filesystems.</summary>
    private static string? FindLegacyMarker(string currentData, string name)
    {
        var exact = NoMigrateMarkerPath(currentData, name);
        if (File.Exists(exact))
        {
            return exact;
        }
        if (Directory.Exists(currentData))
        {
            foreach (var file in Directory.EnumerateFiles(currentData, "*_nomigrate.txt"))
            {
                if (string.Equals(Path.GetFileName(file), NoMigrateMarkerName(name),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return file;
                }
            }
        }
        return null;
    }
}
