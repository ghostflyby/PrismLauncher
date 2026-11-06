// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Tests for the data-level half of Application::handleDataMigration: the whitelist
// inventory, the migration decision records (with the legacy "_nomigrate.txt" marker
// upgrade) and the idempotency decision tree.

using FlintLauncher.Core.Migration;
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
    public void Migration_Is_Idempotent_Through_The_Decision_Record()
    {
        var decisions = Decisions();
        var current = MakeDir();
        var old = MakeDir(PolyMcConfigFile, "accounts.json", "instances/1.21/world/level.dat");

        Assert.Equal(DataMigration.MigrationDecision.ReadyToMigrate,
            DataMigration.Decide(new AbsolutePath(current), new AbsolutePath(old), "PolyMC", PolyMcConfigFile, LauncherConfigFile, decisions));

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
}
