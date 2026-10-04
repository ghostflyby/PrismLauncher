// SPDX-License-Identifier: GPL-3.0-only
// Tests for the new state-cluster infrastructure: the FileSecretStore (CONFIG C4:
// data/secrets.json, 0600, named get/set/delete) and the G2 migration engine (profiles +
// auto-run candidates + the flint-legacy eager remount pass with its E8 bare-ledger cleanup).
using System.Text.Json;
using FlintLauncher.Core;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Migration;
using FlintLauncher.Core.Secrets;
using FlintLauncher.Web;
using Xunit;

namespace FlintLauncher.Tests;

public class FileSecretStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"flint-secrets-{Guid.NewGuid():N}");

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

    private FileSecretStore Store() =>
        new(Path.Combine(_dir, "secrets.json"), FlintJsonContext.Default.SecretsFile);

    private static ISecretStore AsInterface(FileSecretStore store) => store;

    [Fact]
    public void Get_Missing_Name_Is_Null()
    {
        Assert.Null(Store().Get("ModrinthToken"));
        Assert.False(File.Exists(Path.Combine(_dir, "secrets.json"))); // load-only never writes
    }

    [Fact]
    public void Set_Then_Get_RoundTrips_And_Replaces()
    {
        var store = Store();
        store.Set("ProxyPass", "hunter2");
        Assert.Equal("hunter2", store.Get("ProxyPass"));

        store.Set("ProxyPass", "n3w");
        Assert.Equal("n3w", AsInterface(store).Get("ProxyPass"));

        // a fresh instance over the same file sees the persisted value
        Assert.Equal("n3w", Store().Get("ProxyPass"));
    }

    [Fact]
    public void Set_Keeps_Other_Names_Intact()
    {
        var store = Store();
        store.Set("ProxyPass", "hunter2");
        store.Set("FlameKeyOverride", "flame-key");
        store.Set("ModrinthToken", "mr-token");

        Assert.True(store.Delete("FlameKeyOverride"));
        Assert.Null(store.Get("FlameKeyOverride"));
        Assert.Equal("hunter2", store.Get("ProxyPass"));
        Assert.Equal("mr-token", store.Get("ModrinthToken"));
        Assert.False(store.Delete("FlameKeyOverride")); // already gone
    }

    [Fact]
    public void Secrets_File_Is_0600_On_Unix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var store = Store();
        store.Set("ProxyPass", "hunter2");

        var mode = File.GetUnixFileMode(Path.Combine(_dir, "secrets.json"));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }
}

public class MigrationEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"flint-engine-{Guid.NewGuid():N}");

    public MigrationEngineTests()
    {
        Directory.CreateDirectory(_dir);
    }

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

    private MigrationDecisionStore Decisions() =>
        new(Path.Combine(_dir, "state", "migration"), FlintJsonContext.Default.MigrationDecisionRecord);

    [Fact]
    public void Default_Engine_Carries_Both_G2_Profiles()
    {
        var engine = MigrationEngine.CreateDefault();

        Assert.Equal(["prism-family", "flint-legacy"], engine.Profiles.Select(p => p.Name).ToList());
        Assert.False(engine.Find("prism-family")!.AutoRunAtStartup);  // API-driven (G2)
        Assert.True(engine.Find("flint-legacy")!.AutoRunAtStartup);   // startup probe runs once
        Assert.NotNull(engine.Find("FLINT-LEGACY"));                  // lookup is case-insensitive
        Assert.Null(engine.Find("unknown"));
    }

    [Fact]
    public void PrismFamily_Detects_Platform_Candidates_And_Plans()
    {
        var engine = new MigrationEngine([new PrismFamilyMigrationProfile([_dir])]);
        var prismDir = Path.Combine(_dir, "PrismLauncher");
        Directory.CreateDirectory(Path.Combine(prismDir, "instances", "one"));
        File.WriteAllText(Path.Combine(prismDir, "prismlauncher.cfg"), "x");
        var paths = FlintPaths.FromRoots(_dir, Path.Combine(_dir, "data"), Path.Combine(_dir, "store"),
            Path.Combine(_dir, "cache"), Path.Combine(_dir, "state"));

        var candidates = engine.Find("prism-family")!.Detect(paths, Decisions());

        var hit = Assert.Single(candidates);
        Assert.Equal(prismDir, hit.Path);
        Assert.Equal("prism", hit.SourceId);
        Assert.False(hit.DecisionRecorded);

        var plan = engine.Find("prism-family")!.Plan(prismDir, paths, ImportMode.Copy);
        Assert.NotNull(plan);
        Assert.Contains(plan!, item => item.Name == "instances");
    }

    // A2: the prism-family profile surfaces a MultiMC source under BOTH config names —
    // detection delegates to the dual-name probe (upstream multimc.cfg, CONFIG.md:23, plus
    // the historical mmc.cfg) under the same launcher id
    [Fact]
    public void PrismFamily_Detects_MultiMC_Under_Both_Config_Names()
    {
        var parent1 = Path.Combine(_dir, "p1");
        var parent2 = Path.Combine(_dir, "p2");
        Directory.CreateDirectory(parent1);
        Directory.CreateDirectory(parent2);
        var dirName = PrismSourceDetector.NameToDirectory("multimc");
        Directory.CreateDirectory(Path.Combine(parent1, dirName));
        File.WriteAllText(Path.Combine(parent1, dirName, "multimc.cfg"), "x");
        Directory.CreateDirectory(Path.Combine(parent2, dirName));
        File.WriteAllText(Path.Combine(parent2, dirName, "mmc.cfg"), "x");
        var engine = new MigrationEngine([new PrismFamilyMigrationProfile([parent1, parent2])]);
        var paths = FlintPaths.FromRoots(_dir, Path.Combine(_dir, "data"), Path.Combine(_dir, "store"),
            Path.Combine(_dir, "cache"), Path.Combine(_dir, "state"));

        var candidates = engine.Find("prism-family")!.Detect(paths, Decisions());

        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, candidate =>
        {
            Assert.Equal("multimc", candidate.SourceId);
            Assert.Equal(PrismSourceDetector.NameToDirectory("multimc"),
                Path.GetFileName(candidate.Path));
        });
    }

    [Fact]
    public async Task FlintLegacy_Detects_Root_Anchors_And_AutoRuns_Once()
    {
        var engine = MigrationEngine.CreateDefault();
        var data = Path.Combine(_dir, "data");
        Directory.CreateDirectory(data);
        var paths = FlintPaths.FromRoots(_dir, data, Path.Combine(_dir, "store"),
            Path.Combine(_dir, "cache"), Path.Combine(_dir, "state"));

        // no legacy anchor → nothing
        Assert.Empty(engine.Find("flint-legacy")!.Detect(paths, Decisions()));

        // the old flat layout appears (flint.cfg at the root layer)
        File.WriteAllText(Path.Combine(data, "flint.cfg"), "k=v");

        var decisions = Decisions();
        var candidates = engine.Find("flint-legacy")!.Detect(paths, decisions);
        var hit = Assert.Single(candidates);
        Assert.False(hit.DecisionRecorded);
        // the auto-run predicate lights up for the undecided detection (assembly step 5 hook)
        Assert.Equal([engine.Find("flint-legacy")!], engine.AutoRunCandidates(paths, decisions));

        // the S3 record settles it — the engine never proposes twice
        decisions.Write(new MigrationDecisionRecord
        {
            Source = FlintLegacyMigrationProfile.ProfileName,
            Decision = MigrationDecisionRecord.Imported,
            At = DateTimeOffset.Now.ToString("o"),
        });
        Assert.True(engine.Find("flint-legacy")!.Detect(paths, decisions).Single().DecisionRecorded);
        Assert.Empty(engine.AutoRunCandidates(paths, decisions));

        // the flat→five-root whitelist mapping (G2): flint.cfg plans onto the config root
        var plan = engine.Find("flint-legacy")!.Plan(data, paths, ImportMode.Move);
        var cfgItem = Assert.Single(plan!, item => item.Name == "flint.cfg");
        Assert.False(cfgItem.IsDirectory);
        Assert.Equal(PlannedMethod.RenameDirectory, cfgItem.Method);

        // an old in-data cache area plans its children 1:1 under the cache root
        Directory.CreateDirectory(Path.Combine(data, "cache", "icons"));
        await File.WriteAllTextAsync(Path.Combine(data, "cache", "icons", "i.png"), "x");
        var planWithCache = engine.Find("flint-legacy")!.Plan(data, paths, ImportMode.Move);
        Assert.Contains(planWithCache!, item => item.Name == "icons" && item.IsDirectory);
    }

    [Fact]
    public async Task FlintLegacy_Eager_Pass_Remounts_Once_And_Cleans_The_Bare_Ledger()
    {
        var home = Path.Combine(_dir, "roots");
        var data = Path.Combine(home, "data");
        var config = Path.Combine(home, "config");
        var store = Path.Combine(home, "store");
        var cache = Path.Combine(home, "cache");
        var state = Path.Combine(home, "state");
        var paths = FlintPaths.FromRoots(config, data, store, cache, state);
        foreach (var root in new[] { config, data, store, cache, state })
        {
            Directory.CreateDirectory(root);
        }
        var decisions = new MigrationDecisionStore(Path.Combine(state, "migration"),
            FlintJsonContext.Default.MigrationDecisionRecord);

        // the old flat layout: config file, a ledger WITH a real entry, meta content, store
        // content, the old in-data cache area, and the Prism bare ledger (E8 dead stock)
        File.WriteAllText(Path.Combine(data, "flint.cfg"), "JavaPath=old");
        var metaFile = Path.Combine(data, "meta", "net.minecraft", "index.json");
        Directory.CreateDirectory(Path.GetDirectoryName(metaFile)!);
        const string metaBody = "{}";
        await File.WriteAllTextAsync(metaFile, metaBody);
        var md5 = Convert.ToHexString(System.Security.Cryptography.MD5.HashData("{}"u8.ToArray()))
            .ToLowerInvariant();
        var ledgerJson = """
            {"version":"1","entries":[{"base":"meta","path":"net.minecraft/index.json","md5sum":"MD5","etag":"","last_changed_timestamp":0,"eternal":true}]}
            """.Replace("MD5", md5);
        await File.WriteAllTextAsync(Path.Combine(data, "metacache.json"), ledgerJson);
        Directory.CreateDirectory(Path.Combine(data, "assets", "indexes"));
        await File.WriteAllTextAsync(Path.Combine(data, "assets", "indexes", "5.json"), "{}");
        Directory.CreateDirectory(Path.Combine(data, "cache", "fmllibs"));
        await File.WriteAllTextAsync(Path.Combine(data, "cache", "fmllibs", "lib.jar"), "j");
        File.WriteAllText(Path.Combine(data, "metacache"), "prism-ledger");

        var result = FlintLegacyMigration.ExecuteIfNeeded(paths, decisions);

        // everything landed on its five-root destination
        Assert.True(result.Ran);
        Assert.Null(result.Error);
        Assert.True(result.LegacyLedgerCleaned);
        Assert.False(File.Exists(Path.Combine(data, "metacache")));                      // E8
        Assert.True(File.Exists(Path.Combine(config, "flint.cfg")));                     // config root
        Assert.True(File.Exists(Path.Combine(cache, "metacache.json")));                 // cache root (E3)
        Assert.True(File.Exists(Path.Combine(cache, "meta", "net.minecraft", "index.json")));
        Assert.True(File.Exists(Path.Combine(store, "assets", "indexes", "5.json")));    // store (E2)
        Assert.True(File.Exists(Path.Combine(cache, "fmllibs", "lib.jar")));             // cache (E3)

        // the pass sealed itself with the S3 record — a second run is a no-op
        var record = decisions.Read(FlintLegacyMigrationProfile.ProfileName);
        Assert.NotNull(record);
        Assert.Equal(MigrationDecisionRecord.Imported, record!.Decision);
        Assert.Equal("move", record.Mode);
        Assert.Equal(0, record.Instances); // no instances directory in this layout

        var second = FlintLegacyMigration.ExecuteIfNeeded(paths, decisions);
        Assert.False(second.Ran);

        // the REMOUNTED ledger is live for the cache: the entry loads (GetEntry) and
        // resolves against the moved content file (ResolveEntry verifies existence + md5 —
        // this pins the assembly-order guarantee: the eager pass runs before the runtime's
        // MetaCache.Load(), so the first ledger read sees the remounted file)
        using var ledger = new FlintLauncher.Core.Net.HttpMetaCache(paths.MetacacheFile);
        ledger.AddBase("meta", paths.MetaDir);
        ledger.Load();
        var loaded = ledger.GetEntry("meta", "net.minecraft/index.json");
        Assert.NotNull(loaded);
        Assert.False(loaded!.IsStale);
        var resolved = ledger.ResolveEntry("meta", "net.minecraft/index.json");
        Assert.False(resolved.IsStale);
        Assert.Equal(metaBody, await File.ReadAllTextAsync(resolved.GetFullPath()));
    }

    [Fact]
    public void FlintLegacy_HomeDir_Source_Migrates_The_Total_Directory()
    {
        // G1 --home-dir laid on a pre-reform total directory: the flat files sit at the
        // HOME root — X/data is a fresh child that sees none of them. This pins the
        // home-root probe plus the home-only mounts: the data-root anchors
        // (instances/accounts/instgroups/icons/skins) move per child into X/data, the INI
        // chain fallbacks land on the config side, the S2 token file is deleted, and the
        // S3 markers move to the data root where DataMigration.Decide upgrades them.
        var home = Path.Combine(_dir, "homedir");
        Directory.CreateDirectory(Path.Combine(home, "instances", "GTN"));
        Directory.CreateDirectory(Path.Combine(home, "cache", "icons"));
        Directory.CreateDirectory(Path.Combine(home, "meta"));
        Directory.CreateDirectory(Path.Combine(home, "assets", "indexes"));
        Directory.CreateDirectory(Path.Combine(home, "libraries"));
        Directory.CreateDirectory(Path.Combine(home, "icons"));
        Directory.CreateDirectory(Path.Combine(home, "skins"));

        File.WriteAllText(Path.Combine(home, "flint.cfg"), "Language=zh");
        File.WriteAllText(Path.Combine(home, "instances", "GTN", "instance.cfg"), "InstanceType=OneSix\nname=GTN\n");
        File.WriteAllText(Path.Combine(home, "accounts.json"), "[]");
        File.WriteAllText(Path.Combine(home, "instgroups.json"), "{}");
        File.WriteAllText(Path.Combine(home, "api-token"), "tok");
        File.WriteAllText(Path.Combine(home, "prism_nomigrate.txt"), "");
        File.WriteAllText(Path.Combine(home, "metacache"), "prism-ledger");
        File.WriteAllText(Path.Combine(home, "cache", "icons", "i.png"), "png");
        File.WriteAllText(Path.Combine(home, "meta", "index.json"), "{}");
        File.WriteAllText(Path.Combine(home, "assets", "indexes", "5.json"), "{}");
        File.WriteAllText(Path.Combine(home, "libraries", "x.jar"), "jar");
        File.WriteAllText(Path.Combine(home, "icons", "default.png"), "png");
        File.WriteAllText(Path.Combine(home, "skins", "steve.png"), "png");
        File.WriteAllText(Path.Combine(home, "prismlauncher.cfg"), "k=v");

        var paths = FlintPaths.FromHomeDir(home);
        Assert.Equal(home, paths.HomeRoot);
        var decisions = new MigrationDecisionStore(Path.Combine(paths.StateRoot, "migration"),
            FlintJsonContext.Default.MigrationDecisionRecord);

        var result = FlintLegacyMigration.ExecuteIfNeeded(paths, decisions);

        Assert.True(result.Ran);
        Assert.Null(result.Error);
        Assert.True(result.LegacyLedgerCleaned);
        Assert.False(File.Exists(Path.Combine(home, "flint.cfg")));
        Assert.True(File.Exists(Path.Combine(paths.ConfigRoot, "flint.cfg")));
        Assert.True(File.Exists(Path.Combine(paths.DataRoot, "instances", "GTN", "instance.cfg")));
        Assert.True(File.Exists(Path.Combine(paths.DataRoot, "accounts.json")));
        Assert.True(File.Exists(Path.Combine(paths.DataRoot, "instgroups.json")));
        Assert.True(File.Exists(Path.Combine(paths.DataRoot, "icons", "default.png")));
        Assert.True(File.Exists(Path.Combine(paths.DataRoot, "skins", "steve.png")));
        Assert.True(File.Exists(Path.Combine(paths.ConfigRoot, "prismlauncher.cfg")));
        Assert.True(File.Exists(Path.Combine(paths.DataRoot, "prism_nomigrate.txt")));
        Assert.False(File.Exists(Path.Combine(home, "api-token")));  // S2: dead stock, deleted
        Assert.False(File.Exists(Path.Combine(home, "metacache")));  // E8: bare ledger, deleted
        // under a home source the old cache root IS the new cache root — children stay
        Assert.True(File.Exists(Path.Combine(paths.CacheRoot, "icons", "i.png")));
        Assert.True(File.Exists(Path.Combine(paths.CacheRoot, "meta", "index.json")));
        Assert.True(File.Exists(Path.Combine(paths.StoreRoot, "assets", "indexes", "5.json")));
        Assert.True(File.Exists(Path.Combine(paths.StoreRoot, "libraries", "x.jar")));

        // the pass sealed itself with the S3 record — a second run is a no-op
        var record = decisions.Read(FlintLegacyMigrationProfile.ProfileName);
        Assert.NotNull(record);
        Assert.Equal(MigrationDecisionRecord.Imported, record!.Decision);
        Assert.Equal(1, record.Instances);
        Assert.False(FlintLegacyMigration.ExecuteIfNeeded(paths, decisions).Ran);
    }

    [Fact]
    public async Task FlintLegacy_Execute_Remounts_Imported_Content_Even_When_The_Startup_Record_Is_Sealed()
    {
        // the upgraded-user steady state: the startup pass already ran and sealed the S3
        // record — the record gates the STARTUP auto-run only, never the remount itself
        var home = Path.Combine(_dir, "sealed");
        var data = Path.Combine(home, "data");
        var store = Path.Combine(home, "store");
        var paths = FlintPaths.FromRoots(Path.Combine(home, "config"), data, store,
            Path.Combine(home, "cache"), Path.Combine(home, "state"));
        foreach (var root in new[] { paths.ConfigRoot, paths.DataRoot, paths.StoreRoot,
                     paths.CacheRoot, paths.StateRoot })
        {
            Directory.CreateDirectory(root);
        }
        var decisions = new MigrationDecisionStore(Path.Combine(paths.StateRoot, "migration"),
            FlintJsonContext.Default.MigrationDecisionRecord);
        Directory.CreateDirectory(Path.Combine(data, "assets", "indexes"));
        await File.WriteAllTextAsync(Path.Combine(data, "assets", "indexes", "5.json"), "{}");

        Assert.True(FlintLegacyMigration.ExecuteIfNeeded(paths, decisions).Ran);
        var sealedRecord = decisions.Read(FlintLegacyMigrationProfile.ProfileName);
        Assert.NotNull(sealedRecord);

        // the gated startup entry now skips (the :244 pin), even with fresh data-root
        // content waiting — which is exactly why the hook must not call it
        Directory.CreateDirectory(Path.Combine(data, "libraries", "com", "mojang"));
        await File.WriteAllTextAsync(Path.Combine(data, "libraries", "com", "mojang", "x.jar"), "j");
        Assert.False(FlintLegacyMigration.ExecuteIfNeeded(paths, decisions).Ran);
        Assert.False(File.Exists(Path.Combine(store, "libraries", "com", "mojang", "x.jar")));

        // the record-less entry (the prism import-finished hook) remounts what just landed
        var remount = FlintLegacyMigration.Execute(paths);
        Assert.True(remount.Ran);
        Assert.Null(remount.Error);
        Assert.True(File.Exists(Path.Combine(store, "libraries", "com", "mojang", "x.jar")));
        Assert.False(Directory.Exists(Path.Combine(data, "libraries")));

        // and it never touches the decision record — the seal is the startup gate's business
        var after = decisions.Read(FlintLegacyMigrationProfile.ProfileName);
        Assert.Equal(sealedRecord!.At, after!.At);
        Assert.Equal(sealedRecord.Bytes, after.Bytes);
    }

    [Fact]
    public async Task FlintLegacy_Execute_Writes_No_Record_Of_Its_Own()
    {
        // a first-prism-import user with NO flint-legacy record: the hook's remount does
        // the transfer but must not fabricate the startup seal over work it did not gate
        var home = Path.Combine(_dir, "unsealed");
        var data = Path.Combine(home, "data");
        var paths = FlintPaths.FromRoots(Path.Combine(home, "config"), data,
            Path.Combine(home, "store"), Path.Combine(home, "cache"), Path.Combine(home, "state"));
        foreach (var root in new[] { paths.ConfigRoot, paths.DataRoot, paths.StoreRoot,
                     paths.CacheRoot, paths.StateRoot })
        {
            Directory.CreateDirectory(root);
        }
        Directory.CreateDirectory(Path.Combine(data, "assets", "indexes"));
        await File.WriteAllTextAsync(Path.Combine(data, "assets", "indexes", "5.json"), "{}");

        var remount = FlintLegacyMigration.Execute(paths);

        Assert.True(remount.Ran);
        Assert.True(File.Exists(Path.Combine(paths.StoreRoot, "assets", "indexes", "5.json")));
        Assert.False(File.Exists(Path.Combine(paths.StateRoot, "migration",
            FlintLegacyMigrationProfile.ProfileName + ".json")));
    }

    [Fact]
    public void FlintLegacy_Eager_Pass_Leaves_The_Record_Unsealed_On_Transfer_Failure()
    {
        var home = Path.Combine(_dir, "failing");
        var data = Path.Combine(home, "data");
        var store = Path.Combine(home, "store");
        var paths = FlintPaths.FromRoots(Path.Combine(home, "config"), data, store,
            Path.Combine(home, "cache"), Path.Combine(home, "state"));
        // NOTE: the store root is deliberately NOT created — a FILE occupies its path, so
        // every directory-creation attempt for the assets destination fails
        foreach (var root in new[] { paths.ConfigRoot, paths.DataRoot, paths.CacheRoot, paths.StateRoot })
        {
            Directory.CreateDirectory(root);
        }
        File.WriteAllText(store, "a file blocks the store assets root");
        var decisions = new MigrationDecisionStore(Path.Combine(paths.StateRoot, "migration"),
            FlintJsonContext.Default.MigrationDecisionRecord);

        // a transfer that cannot succeed: the assets mount wants <store>/assets but the
        // store path is a file — both the whole-rename and the per-file walk fail, which
        // the pass must report instead of sealing over
        Directory.CreateDirectory(Path.Combine(data, "assets"));
        File.WriteAllText(Path.Combine(data, "assets", "5.json"), "x");

        var result = FlintLegacyMigration.ExecuteIfNeeded(paths, decisions);

        // honest failure: reported, NOT sealed — the next startup resumes the merge
        Assert.True(result.Ran);
        Assert.NotNull(result.Error);
        Assert.Null(decisions.Read(FlintLegacyMigrationProfile.ProfileName));
    }

    // B3: an existing destination DIRECTORY must resume the merge file by file — the old
    // whole-item skip stranded every not-yet-transferred source file forever, so a pass
    // that failed halfway could never complete on a rerun
    [Fact]
    public void FlintLegacy_DirectoryDestinationExists_ResumesTheMergeFileByFile()
    {
        var home = Path.Combine(_dir, "resume");
        var data = Path.Combine(home, "data");
        var store = Path.Combine(home, "store");
        var paths = FlintPaths.FromRoots(Path.Combine(home, "config"), data, store,
            Path.Combine(home, "cache"), Path.Combine(home, "state"));
        Directory.CreateDirectory(paths.ConfigRoot);
        Directory.CreateDirectory(paths.DataRoot);
        Directory.CreateDirectory(paths.CacheRoot);
        Directory.CreateDirectory(paths.StateRoot);

        // source content: one file lands, one is blocked — the remount fails halfway
        Directory.CreateDirectory(Path.Combine(data, "assets", "sub"));
        File.WriteAllText(Path.Combine(data, "assets", "first.txt"), "first");
        File.WriteAllText(Path.Combine(data, "assets", "sub", "late.txt"), "late");
        // a FILE occupies the <store>/assets/sub path, so sub/late.txt cannot transfer —
        // the exact shape of a half-done earlier pass (some files in, some stranded)
        Directory.CreateDirectory(Path.Combine(store, "assets"));
        File.WriteAllText(Path.Combine(store, "assets", "sub"), "a file blocks the subtree");

        var first = FlintLegacyMigration.Execute(paths, data);

        // honest half-failure: the pass reports incomplete, first.txt is in, late.txt is not
        Assert.True(first.Ran);
        Assert.NotNull(first.Error);
        Assert.Equal("first", File.ReadAllText(Path.Combine(store, "assets", "first.txt")));
        Assert.False(File.Exists(Path.Combine(store, "assets", "sub", "late.txt")));
        Assert.True(File.Exists(Path.Combine(data, "assets", "sub", "late.txt"))); // source intact

        // the blocker clears (operator or a later pass) — the rerun must now complete the
        // leftovers THROUGH the existing destination directory
        File.Delete(Path.Combine(store, "assets", "sub"));
        var second = FlintLegacyMigration.Execute(paths, data);

        Assert.True(second.Ran);
        Assert.Null(second.Error);
        Assert.Equal("late", File.ReadAllText(Path.Combine(store, "assets", "sub", "late.txt")));
        Assert.Equal("first", File.ReadAllText(Path.Combine(store, "assets", "first.txt"))); // kept
    }

    [Fact]
    public void FlintLegacy_Eager_Pass_Is_A_Noop_For_A_Fresh_Install()
    {
        var home = Path.Combine(_dir, "fresh");
        var paths = FlintPaths.FromRoots(
            Path.Combine(home, "config"), Path.Combine(home, "data"), Path.Combine(home, "store"),
            Path.Combine(home, "cache"), Path.Combine(home, "state"));
        foreach (var root in new[] { paths.ConfigRoot, paths.DataRoot, paths.StoreRoot,
                     paths.CacheRoot, paths.StateRoot })
        {
            Directory.CreateDirectory(root);
        }

        // no legacy anchor (a bare instances/ directory is NOT one — discovery creates it
        // on every install) → skipped, and no decision record is written
        Directory.CreateDirectory(Path.Combine(paths.DataRoot, "instances"));
        var result = FlintLegacyMigration.ExecuteIfNeeded(paths, null);

        Assert.False(result.Ran);
        Assert.False(File.Exists(Path.Combine(paths.StateRoot, "migration",
            FlintLegacyMigrationProfile.ProfileName + ".json")));
    }

    // a failing decision-store write must not escape the eager pass: the seal is
    // best-effort (the remount itself already tolerated IO failures), so the completed
    // remount still reports success and the next startup resumes as a no-op probe
    [Fact]
    public async Task FlintLegacy_Eager_Pass_Tolerates_A_Failing_Decision_Store()
    {
        var home = Path.Combine(_dir, "blockedstore");
        var data = Path.Combine(home, "data");
        var paths = FlintPaths.FromRoots(Path.Combine(home, "config"), data,
            Path.Combine(home, "store"), Path.Combine(home, "cache"), Path.Combine(home, "state"));
        foreach (var root in new[] { paths.ConfigRoot, paths.DataRoot, paths.StoreRoot,
                     paths.CacheRoot, paths.StateRoot })
        {
            Directory.CreateDirectory(root);
        }
        // a FILE occupies the migration records path, so the seal write throws
        var blocker = Path.Combine(paths.StateRoot, "migration");
        File.WriteAllText(blocker, "a file blocks the migration records directory");
        var decisions = new MigrationDecisionStore(blocker, FlintJsonContext.Default.MigrationDecisionRecord);
        Directory.CreateDirectory(Path.Combine(data, "assets", "indexes"));
        await File.WriteAllTextAsync(Path.Combine(data, "assets", "indexes", "5.json"), "{}");

        var result = FlintLegacyMigration.ExecuteIfNeeded(paths, decisions);

        // the remount itself completed and reported success — the failed seal was swallowed
        Assert.True(result.Ran);
        Assert.Null(result.Error);
        Assert.True(File.Exists(Path.Combine(paths.StoreRoot, "assets", "indexes", "5.json")));
    }

    // the double-legacy state: BOTH roots carry old flat-layout anchors (a prism import
    // landed shared content at the data root and the process died before the
    // OnImportFinished remount, while the home root is itself a pre-reform total directory).
    // One eager pass must remount both and seal exactly ONE profile-level record.
    [Fact]
    public void FlintLegacy_Dual_Legacy_Roots_Both_Remount_Under_One_Seal()
    {
        var home = Path.Combine(_dir, "dual");
        Directory.CreateDirectory(Path.Combine(home, "instances", "GTN"));
        Directory.CreateDirectory(Path.Combine(home, "assets", "indexes"));
        File.WriteAllText(Path.Combine(home, "flint.cfg"), "Language=zh");
        File.WriteAllText(Path.Combine(home, "instances", "GTN", "instance.cfg"),
            "InstanceType=OneSix\nname=GTN\n");
        File.WriteAllText(Path.Combine(home, "assets", "indexes", "5.json"), "{}");

        var paths = FlintPaths.FromHomeDir(home);
        foreach (var root in new[] { paths.ConfigRoot, paths.DataRoot, paths.StoreRoot,
                     paths.CacheRoot, paths.StateRoot })
        {
            Directory.CreateDirectory(root);
        }
        // the import-landed half sits at the data root: shared content the remount hook
        // never moved (the process died before OnImportFinished ran)
        Directory.CreateDirectory(Path.Combine(paths.DataRoot, "assets", "indexes"));
        File.WriteAllText(Path.Combine(paths.DataRoot, "assets", "indexes", "17.json"), "{}");
        var decisions = new MigrationDecisionStore(Path.Combine(paths.StateRoot, "migration"),
            FlintJsonContext.Default.MigrationDecisionRecord);

        var result = FlintLegacyMigration.ExecuteIfNeeded(paths, decisions);

        Assert.True(result.Ran);
        Assert.Null(result.Error);
        // BOTH roots remounted: the data-root assets onto the store root AND the home
        // root's instances/GTN into the fresh data directory, flint.cfg on the config side
        Assert.True(File.Exists(Path.Combine(paths.StoreRoot, "assets", "indexes", "17.json")));
        Assert.True(File.Exists(Path.Combine(paths.StoreRoot, "assets", "indexes", "5.json")));
        Assert.True(File.Exists(Path.Combine(paths.DataRoot, "instances", "GTN", "instance.cfg")));
        Assert.True(File.Exists(Path.Combine(paths.ConfigRoot, "flint.cfg")));
        // exactly ONE profile-level record seals the whole pass
        Assert.NotNull(decisions.Read(FlintLegacyMigrationProfile.ProfileName));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(paths.StateRoot, "migration")));
        // and the seal gates the next startup probe again
        Assert.False(FlintLegacyMigration.ExecuteIfNeeded(paths, decisions).Ran);
    }

    // the failure variant of the dual-root pass: the home root's remount fails (a FILE
    // occupies the config root, so flint.cfg cannot land) while the data-root half already
    // completed — the aggregate reports the error, seals NO record, and the completed
    // data-root remount stays done (the next startup resumes the idempotent merge)
    [Fact]
    public void FlintLegacy_Dual_Root_Failure_Seals_No_Record_And_Keeps_The_Done_Root_Done()
    {
        var home = Path.Combine(_dir, "dualfail");
        Directory.CreateDirectory(Path.Combine(home, "instances", "GTN"));
        File.WriteAllText(Path.Combine(home, "flint.cfg"), "Language=zh");
        File.WriteAllText(Path.Combine(home, "instances", "GTN", "instance.cfg"),
            "InstanceType=OneSix\nname=GTN\n");

        var paths = FlintPaths.FromHomeDir(home);
        foreach (var root in new[] { paths.DataRoot, paths.StoreRoot, paths.CacheRoot, paths.StateRoot })
        {
            Directory.CreateDirectory(root);
        }
        // the placeholder-file method: a FILE occupies the config root so the home pass's
        // flint.cfg mount cannot transfer
        File.WriteAllText(paths.ConfigRoot, "a file blocks the config root");
        // the data-root half that must complete regardless: import-landed shared content
        Directory.CreateDirectory(Path.Combine(paths.DataRoot, "assets", "indexes"));
        File.WriteAllText(Path.Combine(paths.DataRoot, "assets", "indexes", "17.json"), "{}");
        var decisions = new MigrationDecisionStore(Path.Combine(paths.StateRoot, "migration"),
            FlintJsonContext.Default.MigrationDecisionRecord);

        var result = FlintLegacyMigration.ExecuteIfNeeded(paths, decisions);

        // honest aggregate failure: reported, NOT sealed
        Assert.True(result.Ran);
        Assert.NotNull(result.Error);
        Assert.Null(decisions.Read(FlintLegacyMigrationProfile.ProfileName));
        // the data-root half already completed; the home half's flint.cfg is still waiting
        Assert.True(File.Exists(Path.Combine(paths.StoreRoot, "assets", "indexes", "17.json")));
        Assert.True(File.Exists(Path.Combine(home, "flint.cfg")));
    }
}
