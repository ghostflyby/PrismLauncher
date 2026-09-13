// SPDX-License-Identifier: GPL-3.0-only
// Tests for the data-migration port (launcher/DataMigrationTask.{h,cpp} plus the data-level
// half of Application::handleDataMigration): the whitelist inventory, the dry-run/copy task
// flow, the "_nomigrate.txt" marker and the idempotency decision tree.
using FlintLauncher.Core.Migration;
using FlintLauncher.Core.Tasks;
using Xunit;

namespace Flint.Tests;

public class DataMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"flint-migrate-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private string MakeDir(params string[] relativePaths)
    {
        var root = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        foreach (var relative in relativePaths)
        {
            var fullPath = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
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
            DataMigration.NoMigrateMarkerPath(current, "MultiMC"));
    }

    [Fact]
    public void SetDoNotMigrate_Creates_An_Empty_Marker()
    {
        var current = MakeDir(LauncherConfigFile);
        DataMigration.SetDoNotMigrate(current, "PolyMC");
        var marker = DataMigration.NoMigrateMarkerPath(current, "PolyMC");
        Assert.True(File.Exists(marker));
        Assert.Equal(0, new FileInfo(marker).Length);
    }

    [Fact]
    public void Decide_Fresh_Install_With_Legacy_Config_Is_ReadyToMigrate()
    {
        var current = MakeDir();
        var old = MakeDir(PolyMcConfigFile, "instances/1.20/a.zip", "accounts.json");

        Assert.Equal(DataMigration.MigrationDecision.ReadyToMigrate,
            DataMigration.Decide(current, old, "PolyMC", PolyMcConfigFile, LauncherConfigFile));
    }

    // the old location accepts either the legacy config or the Prism config we already wrote there
    [Fact]
    public void Decide_Accepts_The_Launcher_Config_As_The_Old_Config()
    {
        var current = MakeDir();
        var old = MakeDir(LauncherConfigFile); // previously used as a Prism dir
        Assert.Equal(DataMigration.MigrationDecision.ReadyToMigrate,
            DataMigration.Decide(current, old, "PolyMC", PolyMcConfigFile, LauncherConfigFile));

        var oldEmpty = MakeDir();
        Assert.Equal(DataMigration.MigrationDecision.NoOldConfig,
            DataMigration.Decide(current, oldEmpty, "PolyMC", PolyMcConfigFile, LauncherConfigFile));
    }

    [Fact]
    public void Decide_Marker_Present_Wins_Over_Everything()
    {
        var current = MakeDir("PolyMC_nomigrate.txt");
        var old = MakeDir(PolyMcConfigFile, "instances/a.zip");

        Assert.Equal(DataMigration.MigrationDecision.MarkerPresent,
            DataMigration.Decide(current, old, "PolyMC", PolyMcConfigFile, LauncherConfigFile));
    }

    // idempotency: after a successful migration the marker exists, so re-running is a no-op
    [Fact]
    public async Task Migration_Is_Idempotent_Through_The_Marker()
    {
        var current = MakeDir();
        var old = MakeDir(PolyMcConfigFile, "accounts.json", "instances/1.21/world/level.dat");

        Assert.Equal(DataMigration.MigrationDecision.ReadyToMigrate,
            DataMigration.Decide(current, old, "PolyMC", PolyMcConfigFile, LauncherConfigFile));

        var task = new DataMigrationTask(old, current, DataMigration.BuildFilter(PolyMcConfigFile, LauncherConfigFile));
        await task.StartAsync();
        Assert.Equal(TaskState.Succeeded, task.State);
        Assert.True(File.Exists(Path.Combine(current, "polymc.cfg")));
        Assert.True(File.Exists(Path.Combine(current, "instances", "1.21", "world", "level.dat")));

        // the host marks the migration done, like setDoNotMigrate() after a successful run
        DataMigration.SetDoNotMigrate(current, "PolyMC");

        Assert.Equal(DataMigration.MigrationDecision.MarkerPresent,
            DataMigration.Decide(current, old, "PolyMC", PolyMcConfigFile, LauncherConfigFile));
    }

    // an existing flint.cfg in the current directory skips the copy entirely
    [Fact]
    public void Decide_Existing_Current_Data_Is_Skipped()
    {
        var current = MakeDir(LauncherConfigFile);
        var old = MakeDir(PolyMcConfigFile, "instances/a.zip");

        Assert.Equal(DataMigration.MigrationDecision.CurrentDataExists,
            DataMigration.Decide(current, old, "PolyMC", PolyMcConfigFile, LauncherConfigFile));
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
        var task = new DataMigrationTask(source, target, DataMigration.BuildFilter(PolyMcConfigFile, LauncherConfigFile));
        task.Progress += (_, p) => progress.Add((p.Current, p.Total));
        task.StatusChanged += (_, s) => statuses.Add(s);

        await task.StartAsync();

        Assert.Equal(TaskState.Succeeded, task.State);

        // copied
        Assert.Equal("content of polymc.cfg", File.ReadAllText(Path.Combine(target, "polymc.cfg")));
        Assert.Equal("content of flint.cfg", File.ReadAllText(Path.Combine(target, "flint.cfg")));
        Assert.Equal("content of accounts.json", File.ReadAllText(Path.Combine(target, "accounts.json")));
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
        File.WriteAllText(Path.Combine(target, "polymc.cfg"), "target version");

        var task = new DataMigrationTask(source, target, DataMigration.BuildFilter(PolyMcConfigFile, LauncherConfigFile));
        await task.StartAsync();

        // C++ semantics: copy_options::none reports an error for existing destination files, so
        // the task FAILS with this message while the existing target file stays untouched.
        Assert.Equal(TaskState.Failed, task.State);
        Assert.Equal("Some paths could not be copied!", task.FailReason);
        Assert.Equal("target version", File.ReadAllText(Path.Combine(target, "polymc.cfg")));
    }

    [Fact]
    public async Task Missing_Source_Fails_With_The_Cpp_Message()
    {
        var target = Path.Combine(_dir, "target-" + Guid.NewGuid().ToString("N"));
        var task = new DataMigrationTask(Path.Combine(_dir, "does-not-exist"), target,
            DataMigration.BuildFilter(PolyMcConfigFile, LauncherConfigFile));
        await task.StartAsync();

        Assert.Equal(TaskState.Failed, task.State);
        Assert.Equal("Failed to scan source path.", task.FailReason);
    }

    // the C++ shortens long status names: left(20) + "…" + right(29)
    [Fact]
    public async Task Long_Paths_Are_Shortened_In_The_Status()
    {
        var deep = string.Join('/', Enumerable.Repeat("directory-name", 10));
        var source = MakeDir($"instances/{deep}/options.txt");
        var target = Path.Combine(_dir, "target-" + Guid.NewGuid().ToString("N"));

        var statuses = new List<string>();
        var task = new DataMigrationTask(source, target,
            DataMigration.BuildFilter(PolyMcConfigFile, LauncherConfigFile));
        task.StatusChanged += (_, s) => statuses.Add(s);
        await task.StartAsync();

        Assert.Equal(TaskState.Succeeded, task.State);
        var copying = statuses.Last(s => s.StartsWith("Copying "));
        var relative = $"instances/{deep}/options.txt";
        Assert.Equal($"Copying {relative[..20]}…{relative[^29..]}…", copying);
    }
}
