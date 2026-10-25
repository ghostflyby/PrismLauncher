// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Tests for the data-migration port (launcher/DataMigrationTask.{h,cpp} plus the data-level
// half of Application::handleDataMigration): the whitelist inventory, the dry-run/copy task
// flow, the migration decision records (with the legacy "_nomigrate.txt" marker upgrade)
// and the idempotency decision tree.

using FlintLauncher.Core.Migration;
using FlintLauncher.Core.Tasks;
using FlintLauncher.Web;
using TruePath;
using Xunit;

namespace FlintLauncher.Tests;

public class DataMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"flint-migrate-{Guid.NewGuid():N}");

    /// <summary>The decision store over a throwaway migration directory.</summary>
    private MigrationDecisionStore Decisions() =>
        new(new AbsolutePath(Path.Combine(_dir, Guid.NewGuid().ToString("N"), "migration")),
            FlintJsonContext.Default.MigrationDecisionRecord);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException) { }
    }

    private string MakeDir(params string[] relativePaths)
    {
        var root = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        foreach (var relative in relativePaths)
        {
            var fullPath = Path.Combine(root, relative);
            var fullDir = Path.GetDirectoryName(fullPath);
            Assert.NotNull(fullDir);
            Directory.CreateDirectory(fullDir);
            File.WriteAllText(fullPath, "content of " + relative);
        }

        return root;
    }

    private const string LauncherConfigFile = "flint.cfg"; // FlintConfig.LauncherConfigFile
    private const string PolyMcConfigFile = "polymc.cfg";
    private const string MultiMcConfigFile = "multimc.cfg";

    // ---------------------------------------------------------------- inventory

    [Fact]
    public void BuildFilter_Matches_The_Exact_Application_Inventory()
    {
        var filter = DataMigration.BuildFilter(PolyMcConfigFile, LauncherConfigFile);

        // configs (legacy + our own, the latter for "we already used that directory before")
        Assert.True(filter("polymc.cfg"));
        Assert.True(filter("flint.cfg"));
        // plain files at the root of a migrated tree
        Assert.True(filter("accounts.json"));
        // whole folders
        Assert.True(filter("logs/launcher-0.log"));
        Assert.True(filter("accounts/someuser.json"));
        Assert.True(filter("assets/indexes/17.json"));
        Assert.True(filter("assets/objects/aa/aabb.ext"));
        Assert.True(filter("icons/icon.png"));
        Assert.True(filter("instances/1.21.4/minecraft/options.txt"));
        Assert.True(filter("instances/1.21.4/mm-pack.json"));
        Assert.True(filter("libraries/net/minecraftforge/forge.jar"));
        Assert.True(filter("mods/create.jar"));
        Assert.True(filter("themes/dark/theme.json"));

        // everything else stays behind
        Assert.False(filter("other.cfg"));
        Assert.False(filter("unrelated.txt"));
        Assert.False(filter("polymc.cfg.bak"));
        Assert.False(filter("accounts.json.bak"));
        Assert.False(filter("assets_x/asset.json")); // prefix is the folder, not the name
        Assert.False(filter("instances-old/a.zip"));
        Assert.False(filter("log.txt"));
        Assert.False(filter("logs.txt"));
    }

    [Fact]
    public void BuildFilter_Uses_The_Passed_Config_Names()
    {
        var multiMcFilter = DataMigration.BuildFilter(MultiMcConfigFile, LauncherConfigFile);
        Assert.True(multiMcFilter("multimc.cfg"));
        Assert.False(multiMcFilter("polymc.cfg"));
    }

    // ---------------------------------------------------------------- marker + decisions

    [Fact]
    public void Marker_Name_And_Path()
    {
        Assert.Equal("PolyMC_nomigrate.txt", DataMigration.NoMigrateMarkerName("PolyMC"));
        var current = Path.Combine(_dir, "current");
        Assert.Equal(Path.Combine(current, "MultiMC_nomigrate.txt"),
            DataMigration.NoMigrateMarkerPath(new AbsolutePath(current), "MultiMC").Value);
    }

    [Fact]
    public void Decline_Writes_A_Decision_Record()
    {
        var decisions = Decisions();
        decisions.Write(new MigrationDecisionRecord
        {
            Source = "PolyMC",
            Decision = MigrationDecisionRecord.Declined,
            At = DateTimeOffset.Now.ToString("o"),
        });

        var record = decisions.Read("PolyMC");
        Assert.NotNull(record);
        Assert.Equal(MigrationDecisionRecord.Declined, record.Decision);
        Assert.Equal("PolyMC", record.Source);
        // undecided sources read back as null
        Assert.Null(decisions.Read("prism"));
    }

    [Fact]
    public void Decide_Fresh_Install_With_Legacy_Config_Is_ReadyToMigrate()
    {
        var current = MakeDir();
        var old = MakeDir(PolyMcConfigFile, "instances/1.20/a.zip", "accounts.json");

        Assert.Equal(DataMigration.MigrationDecision.ReadyToMigrate,
            DataMigration.Decide(new AbsolutePath(current), new AbsolutePath(old), "PolyMC", PolyMcConfigFile, LauncherConfigFile));
    }

    // the old location accepts either the legacy config or the Prism config we already wrote there
    [Fact]
    public void Decide_Accepts_The_Launcher_Config_As_The_Old_Config()
    {
        var current = MakeDir();
        var old = MakeDir(LauncherConfigFile); // previously used as a Prism dir
        Assert.Equal(DataMigration.MigrationDecision.ReadyToMigrate,
            DataMigration.Decide(new AbsolutePath(current), new AbsolutePath(old), "PolyMC", PolyMcConfigFile, LauncherConfigFile));

        var oldEmpty = MakeDir();
        Assert.Equal(DataMigration.MigrationDecision.NoOldConfig,
            DataMigration.Decide(new AbsolutePath(current), new AbsolutePath(oldEmpty), "PolyMC", PolyMcConfigFile, LauncherConfigFile));
    }

    [Fact]
    public void Decide_Decision_Record_Wins_Over_Everything()
    {
        var decisions = Decisions();
        decisions.Write(new MigrationDecisionRecord
        {
            Source = "PolyMC", Decision = MigrationDecisionRecord.Imported,
            At = "2026-10-02T00:00:00.0000000+08:00", Mode = "cow", Instances = 3, Bytes = 1024,
        });
        var current = MakeDir();
        var old = MakeDir(PolyMcConfigFile, "instances/a.zip");

        Assert.Equal(DataMigration.MigrationDecision.MarkerPresent,
            DataMigration.Decide(new AbsolutePath(current), new AbsolutePath(old), "PolyMC", PolyMcConfigFile, LauncherConfigFile, decisions));
    }

    [Fact]
    public void Decide_Legacy_Marker_Still_Counts_Without_A_Store()
    {
        var current = MakeDir("PolyMC_nomigrate.txt");
        var old = MakeDir(PolyMcConfigFile, "instances/a.zip");

        Assert.Equal(DataMigration.MigrationDecision.MarkerPresent,
            DataMigration.Decide(new AbsolutePath(current), new AbsolutePath(old), "PolyMC", PolyMcConfigFile, LauncherConfigFile));
        // without a store the marker is left alone (nothing upgrades it away)
        Assert.True(File.Exists(DataMigration.NoMigrateMarkerPath(new AbsolutePath(current), "PolyMC").Value));
    }

    // a legacy empty marker upgrades in place on first sight — declined@unknown record,
    // marker file deleted
    [Fact]
    public void Decide_Upgrades_The_Legacy_Marker_Into_A_Decision_Record()
    {
        var decisions = Decisions();
        var current = MakeDir("PolyMC_nomigrate.txt");
        var old = MakeDir(PolyMcConfigFile, "instances/a.zip");

        Assert.Equal(DataMigration.MigrationDecision.MarkerPresent,
            DataMigration.Decide(new AbsolutePath(current), new AbsolutePath(old), "PolyMC", PolyMcConfigFile, LauncherConfigFile, decisions));

        Assert.False(File.Exists(DataMigration.NoMigrateMarkerPath(new AbsolutePath(current), "PolyMC").Value));
        var record = decisions.Read("PolyMC");
        Assert.NotNull(record);
        Assert.Equal(MigrationDecisionRecord.Declined, record.Decision);
        Assert.Equal(MigrationDecisionRecord.UnknownAt, record.At);
        Assert.Equal("", record.Mode);

        // the upgraded record carries the decision from here on
        var current2 = MakeDir();
        Assert.Equal(DataMigration.MigrationDecision.MarkerPresent,
            DataMigration.Decide(new AbsolutePath(current2), new AbsolutePath(old), "PolyMC", PolyMcConfigFile, LauncherConfigFile, decisions));
    }

    // C++ wrote the marker under the DISPLAY launcher name (PolyMC/MultiMC,
    // Application.cpp:599/604+1977) while this port's source ids are lowercase — a
    // C++-era display-case marker must still be honored when the decision is probed under
    // the lowercase id, on case-sensitive filesystems too
    [Fact]
    public void Decide_Honors_The_Cpp_Display_Case_Marker_Under_The_Lowercase_Id()
    {
        var decisions = Decisions();
        var current = MakeDir("PolyMC_nomigrate.txt");
        var old = MakeDir(PolyMcConfigFile, "instances/a.zip");

        Assert.Equal(DataMigration.MigrationDecision.MarkerPresent,
            DataMigration.Decide(new AbsolutePath(current), new AbsolutePath(old), "polymc", PolyMcConfigFile, LauncherConfigFile, decisions));

        // the upgrade ran: the display-case marker is deleted and the lowercase-id record
        // carries the declined@unknown decision
        Assert.False(File.Exists(Path.Combine(current, "PolyMC_nomigrate.txt")));
        var record = decisions.Read("polymc");
        Assert.NotNull(record);
        Assert.Equal(MigrationDecisionRecord.Declined, record.Decision);
        Assert.Equal(MigrationDecisionRecord.UnknownAt, record.At);
    }

    // the no-store variant of the same display-case scenario: the marker counts
    // (case-insensitively) and stays in place — without a store nothing upgrades it away
    [Fact]
    public void Decide_Legacy_Display_Case_Marker_Counts_Without_A_Store()
    {
        var current = MakeDir("PolyMC_nomigrate.txt");
        var old = MakeDir(PolyMcConfigFile, "instances/a.zip");

        Assert.Equal(DataMigration.MigrationDecision.MarkerPresent,
            DataMigration.Decide(new AbsolutePath(current), new AbsolutePath(old), "polymc", PolyMcConfigFile, LauncherConfigFile));
        Assert.True(File.Exists(Path.Combine(current, "PolyMC_nomigrate.txt")));
    }

    // a decision-store write failure must not escape Decide: the marker on disk already
    // encodes the decision, so the probe still reports decided and the marker stays in place
    [Fact]
    public void Decide_Tolerates_A_Failing_Decision_Store_And_Keeps_The_Marker()
    {
        var current = MakeDir("PolyMC_nomigrate.txt");
        var old = MakeDir(PolyMcConfigFile, "instances/a.zip");
        // a FILE occupies the migration records path, so every store Write throws
        var blocker = Path.Combine(_dir, "blocked-store-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "a file blocks the migration records directory");
        var decisions = new MigrationDecisionStore(new AbsolutePath(blocker), FlintJsonContext.Default.MigrationDecisionRecord);

        Assert.Equal(DataMigration.MigrationDecision.MarkerPresent,
            DataMigration.Decide(new AbsolutePath(current), new AbsolutePath(old), "PolyMC", PolyMcConfigFile, LauncherConfigFile, decisions));
        Assert.True(File.Exists(DataMigration.NoMigrateMarkerPath(new AbsolutePath(current), "PolyMC").Value));
    }

    // idempotency: after a successful migration the decision record exists, so re-running is
    // a no-op
    [Fact]
    public async Task Migration_Is_Idempotent_Through_The_Decision_Record()
    {
        var decisions = Decisions();
        var current = MakeDir();
        var old = MakeDir(PolyMcConfigFile, "accounts.json", "instances/1.21/world/level.dat");

        Assert.Equal(DataMigration.MigrationDecision.ReadyToMigrate,
            DataMigration.Decide(new AbsolutePath(current), new AbsolutePath(old), "PolyMC", PolyMcConfigFile, LauncherConfigFile, decisions));

        var task = new DataMigrationTask(new AbsolutePath(old), new AbsolutePath(current), DataMigration.BuildFilter(PolyMcConfigFile, LauncherConfigFile));
        await task.StartAsync();
        Assert.Equal(TaskState.Succeeded, task.State);
        Assert.True(File.Exists(Path.Combine(current, "polymc.cfg")));
        Assert.True(File.Exists(Path.Combine(current, "instances", "1.21", "world", "level.dat")));

        // the host records the completed migration (the decision record replaces setDoNotMigrate)
        decisions.Write(new MigrationDecisionRecord
        {
            Source = "PolyMC",
            Decision = MigrationDecisionRecord.Imported,
            At = DateTimeOffset.Now.ToString("o"),
            Mode = "copy",
        });

        Assert.Equal(DataMigration.MigrationDecision.MarkerPresent,
            DataMigration.Decide(new AbsolutePath(current), new AbsolutePath(old), "PolyMC", PolyMcConfigFile, LauncherConfigFile, decisions));
    }

    // an existing flint.cfg in the current directory skips the copy entirely
    [Fact]
    public void Decide_Existing_Current_Data_Is_Skipped()
    {
        var current = MakeDir(LauncherConfigFile);
        var old = MakeDir(PolyMcConfigFile, "instances/a.zip");

        Assert.Equal(DataMigration.MigrationDecision.CurrentDataExists,
            DataMigration.Decide(new AbsolutePath(current), new AbsolutePath(old), "PolyMC", PolyMcConfigFile, LauncherConfigFile));
    }

    // post-reform the authoritative own-config position is the CONFIG root's flint.json
    // — the flat <data>/flint.cfg probe is dead there, so the C++ existing-data
    // protection (Application.cpp:1994-1999, 2029, 2055-2057) keys off the config-root path
    // the caller passes
    [Fact]
    public void Decide_Probes_The_Config_Root_Flint_Json_For_The_Existing_Data_Protection()
    {
        var current = MakeDir(); // the post-reform reality: NO flint.cfg at the data root
        var old = MakeDir(PolyMcConfigFile, "instances/a.zip");
        var configRoot = Path.Combine(_dir, "cfgroot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(configRoot);
        var flintJson = Path.Combine(configRoot, "flint.json");

        // no own config yet → ready to migrate
        Assert.Equal(DataMigration.MigrationDecision.ReadyToMigrate,
            DataMigration.Decide(new AbsolutePath(current), new AbsolutePath(old), "PolyMC", PolyMcConfigFile, LauncherConfigFile,
                currentConfigFile: new AbsolutePath(flintJson)));

        // flint.json appears (Flint configured / INI imported) → the protection fires
        File.WriteAllText(flintJson, "{}");
        Assert.Equal(DataMigration.MigrationDecision.CurrentDataExists,
            DataMigration.Decide(new AbsolutePath(current), new AbsolutePath(old), "PolyMC", PolyMcConfigFile, LauncherConfigFile,
                currentConfigFile: new AbsolutePath(flintJson)));
    }

    // ---------------------------------------------------------------- task flow

    [Fact]
    public async Task Task_Copies_The_Whitelisted_Tree()
    {
        var source = MakeDir(
            "polymc.cfg",
            "flint.cfg",
            "accounts.json",
            "accounts/steve@gmail.com.json",
            "assets/indexes/6.json",
            "assets/objects/ab/abcdef",
            "icons/default.png",
            "instances/1.21.4/instance.cfg",
            "instances/1.21.4/minecraft/options.txt",
            "libraries/org/ow2/asm/asm.jar",
            "mods/create.jar",
            "themes/peppermint/theme.json",
            "logs/flint-0.log",
            // not whitelisted:
            "unrelated.cfg",
            "metacache",
            "PolyMC_nomigrate.txt",
            "assetsother/not-included.json");

        var target = Path.Combine(_dir, "target-" + Guid.NewGuid().ToString("N"));

        var progress = new List<(long current, long total)>();
        var statuses = new List<string>();
        var task = new DataMigrationTask(new AbsolutePath(source), new AbsolutePath(target), DataMigration.BuildFilter(PolyMcConfigFile, LauncherConfigFile));
        task.Progress += (_, p) => progress.Add((p.Current, p.Total));
        task.StatusChanged += (_, s) => statuses.Add(s);

        await task.StartAsync();

        Assert.Equal(TaskState.Succeeded, task.State);

        // copied
        Assert.Equal("content of polymc.cfg", await File.ReadAllTextAsync(Path.Combine(target, "polymc.cfg")));
        Assert.Equal("content of flint.cfg", await File.ReadAllTextAsync(Path.Combine(target, "flint.cfg")));
        Assert.Equal("content of accounts.json", await File.ReadAllTextAsync(Path.Combine(target, "accounts.json")));
        Assert.True(File.Exists(Path.Combine(target, "accounts", "steve@gmail.com.json")));
        Assert.True(File.Exists(Path.Combine(target, "assets", "indexes", "6.json")));
        Assert.True(File.Exists(Path.Combine(target, "assets", "objects", "ab", "abcdef")));
        Assert.True(File.Exists(Path.Combine(target, "icons", "default.png")));
        Assert.True(File.Exists(Path.Combine(target, "instances", "1.21.4", "instance.cfg")));
        Assert.True(File.Exists(Path.Combine(target, "instances", "1.21.4", "minecraft", "options.txt")));
        Assert.True(File.Exists(Path.Combine(target, "libraries", "org", "ow2", "asm", "asm.jar")));
        Assert.True(File.Exists(Path.Combine(target, "mods", "create.jar")));
        Assert.True(File.Exists(Path.Combine(target, "themes", "peppermint", "theme.json")));
        Assert.True(File.Exists(Path.Combine(target, "logs", "flint-0.log")));

        // left behind
        Assert.False(File.Exists(Path.Combine(target, "unrelated.cfg")));
        Assert.False(File.Exists(Path.Combine(target, "metacache")));
        Assert.False(File.Exists(Path.Combine(target, "assetsother", "not-included.json")));

        // progress: dry-run count first, then one step per copied file (13 whitelisted files)
        Assert.Equal(13, progress.Last().total);
        Assert.Equal((13, 13), progress.Last());
        Assert.Equal("Scanning files...", statuses[0]);
        Assert.StartsWith("Copying ", statuses[1]);
    }

    [Fact]
    public async Task Task_Does_Not_Overwrite_Existing_Target_Files()
    {
        var source = MakeDir("polymc.cfg");
        var target = MakeDir("polymc.cfg");
        await File.WriteAllTextAsync(Path.Combine(target, "polymc.cfg"), "target version");

        var task = new DataMigrationTask(new AbsolutePath(source), new AbsolutePath(target), DataMigration.BuildFilter(PolyMcConfigFile, LauncherConfigFile));
        await task.StartAsync();

        // C++ semantics: copy_options::none reports an error for existing destination files, so
        // the task FAILS with this message while the existing target file stays untouched.
        Assert.Equal(TaskState.Failed, task.State);
        Assert.Equal("Some paths could not be copied!", task.FailReason);
        Assert.Equal("target version", await File.ReadAllTextAsync(Path.Combine(target, "polymc.cfg")));
    }

    [Fact]
    public async Task Missing_Source_Fails_With_The_Cpp_Message()
    {
        var target = Path.Combine(_dir, "target-" + Guid.NewGuid().ToString("N"));
        var task = new DataMigrationTask(new AbsolutePath(Path.Combine(_dir, "does-not-exist")), new AbsolutePath(target),
            DataMigration.BuildFilter(PolyMcConfigFile, LauncherConfigFile));
        await task.StartAsync();

        Assert.Equal(TaskState.Failed, task.State);
        Assert.Equal("Failed to scan source path.", task.FailReason);
    }

    // a file symlink inside the migrated tree is copied by its TARGET'S CONTENT (the
    // C++ FS::copy default follows file symlinks — m_followSymlinks=true, FileSystem.h:159,
    // ResolveSymlinks enumeration, FileSystem.cpp:461) — the old ReparsePoint skip silently
    // pruned it from both the dry-run count and the copy
    [Fact]
    public async Task Task_Copies_File_Symlinks_By_Content()
    {
        if (!SymlinkSupport.CanCreateLinks())
        {
            return; // Windows without symlink privilege
        }

        var source = Path.Combine(_dir, "src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(source, "instances", "demo"));
        // the link's target sits OUTSIDE the whitelisted tree — only link.cfg itself matches
        await File.WriteAllTextAsync(Path.Combine(source, "marker.txt"), "marker-content");
        File.CreateSymbolicLink(Path.Combine(source, "instances", "demo", "link.cfg"),
            Path.Combine(source, "marker.txt"));
        var target = Path.Combine(_dir, "target-" + Guid.NewGuid().ToString("N"));

        var progress = new List<(long current, long total)>();
        var task = new DataMigrationTask(new AbsolutePath(source), new AbsolutePath(target),
            DataMigration.BuildFilter(PolyMcConfigFile, LauncherConfigFile));
        task.Progress += (_, p) => progress.Add((p.Current, p.Total));
        await task.StartAsync();

        Assert.Equal(TaskState.Succeeded, task.State);
        var landed = Path.Combine(target, "instances", "demo", "link.cfg");
        Assert.Equal("marker-content", await File.ReadAllTextAsync(landed));
        Assert.Null(File.ResolveLinkTarget(landed, returnFinalTarget: false)); // content, not a re-link
        Assert.Equal(1, progress.Last().total);                                // the plan count includes the link too
    }

    // the C++ shortens long status names: left(20) + "…" + right(29)
    [Fact]
    public async Task Long_Paths_Are_Shortened_In_The_Status()
    {
        var deep = string.Join('/', Enumerable.Repeat("directory-name", 10));
        var source = MakeDir($"instances/{deep}/options.txt");
        var target = Path.Combine(_dir, "target-" + Guid.NewGuid().ToString("N"));

        var statuses = new List<string>();
        var task = new DataMigrationTask(new AbsolutePath(source), new AbsolutePath(target),
            DataMigration.BuildFilter(PolyMcConfigFile, LauncherConfigFile));
        task.StatusChanged += (_, s) => statuses.Add(s);
        await task.StartAsync();

        Assert.Equal(TaskState.Succeeded, task.State);
        var copying = statuses.Last(s => s.StartsWith("Copying "));
        var relative = $"instances/{deep}/options.txt";
        Assert.Equal($"Copying {relative[..20]}…{relative[^29..]}…", copying);
    }
}
