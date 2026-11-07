// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Tests for the new state-cluster infrastructure: the FileSecretStore (
// data/secrets.json, 0600, named get/set/delete) and the migration engine (profiles +
// auto-run candidates + the flint-legacy eager remount pass with its bare-ledger cleanup).

using System.Security.Cryptography;
using FlintLauncher.Core;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Migration;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Secrets;
using FlintLauncher.Web;
using TruePath;
using TruePath.SystemIo;
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
        catch (DirectoryNotFoundException) { }
    }

    private FileSecretStore Store() =>
        new(new AbsolutePath(Path.Combine(_dir, "secrets.json")), FlintJsonContext.Default.SecretsFile);

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
    private readonly AbsolutePath _dir = Temporary.CreateTempFolder();

    public void Dispose()
    {
        try
        {
            _dir.DeleteDirectoryRecursively();
        }
        catch (DirectoryNotFoundException) { }
    }

    private MigrationDecisionStore Decisions() =>
        new(_dir / "state" / "migration", FlintJsonContext.Default.MigrationDecisionRecord);

    [Fact]
    public void Default_Engine_Carries_Both_G2_Profiles()
    {
        var engine = MigrationEngine.CreateDefault();

        Assert.Equal(["prism-family", "flint-legacy"], engine.Profiles.Select(p => p.Name).ToList());
        var prism = engine.Find("prism-family");
        var legacy = engine.Find("flint-legacy");
        Assert.NotNull(prism);
        Assert.NotNull(legacy);
        Assert.NotNull(engine.Find("FLINT-LEGACY")); // lookup is case-insensitive
        Assert.Null(engine.Find("unknown"));
    }

    [Fact]
    public void PrismFamily_Detects_Platform_Candidates_And_Plans()
    {
        var engine = new MigrationEngine([new PrismFamilyMigrationProfile([_dir])]);
        var prismDir = (_dir / "PrismLauncher");
        (prismDir / "instances" / "one").CreateDirectory();
        (prismDir / "prismlauncher.cfg").WriteAllText("x");
        AbsolutePath dataRoot = (_dir / "data");
        AbsolutePath storeRoot = (_dir / "store");
        AbsolutePath cacheRoot = (_dir / "cache");
        AbsolutePath stateRoot = (_dir / "state");
        var paths = new FlintPaths()
        {
            ConfigRoot = _dir,
            DataRoot = dataRoot,
            StoreRoot = storeRoot,
            CacheRoot = cacheRoot,
            StateRoot = stateRoot,
        };

        var profile = engine.Find("prism-family");
        Assert.NotNull(profile);
        var candidates = profile.Detect(paths, Decisions());

        var hit = Assert.Single(candidates);
        Assert.Equal(prismDir, hit.Path);
        Assert.Equal("prism", hit.SourceId);
        Assert.False(hit.DecisionRecorded);

        var plan = profile.Plan(prismDir, paths, ImportMode.Copy);
        Assert.NotNull(plan);
        Assert.Contains(plan, item => item.Name == "instances");
    }

    // the prism-family profile surfaces a MultiMC source under BOTH config names —
    // detection delegates to the dual-name probe (upstream multimc.cfg plus
    // the historical mmc.cfg) under the same launcher id
    [Fact]
    public void PrismFamily_Detects_MultiMC_Under_Both_Config_Names()
    {
        var parent1 = _dir / "p1";
        var parent2 = _dir / "p2";
        parent1.CreateDirectory();
        parent2.CreateDirectory();
        var dirName = PrismSourceDetector.NameToDirectory("multimc");
        (parent1 / dirName).CreateDirectory();
        (parent1 / dirName / "multimc.cfg").WriteAllText("x");
        (parent2 / dirName).CreateDirectory();
        (parent2 / dirName / "mmc.cfg").WriteAllText("x");
        var engine = new MigrationEngine([new PrismFamilyMigrationProfile([parent1, parent2])]);
        AbsolutePath dataRoot = _dir / "data";
        AbsolutePath storeRoot = _dir / "store";
        AbsolutePath cacheRoot = _dir / "cache";
        AbsolutePath stateRoot = _dir / "state";
        var paths = new FlintPaths()
        {
            ConfigRoot = _dir,
            DataRoot = dataRoot,
            StoreRoot = storeRoot,
            CacheRoot = cacheRoot,
            StateRoot = stateRoot,
        };

        var profile = engine.Find("prism-family");
        Assert.NotNull(profile);
        var candidates = profile.Detect(paths, Decisions());

        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, candidate =>
        {
            Assert.Equal("multimc", candidate.SourceId);
            Assert.Equal(PrismSourceDetector.NameToDirectory("multimc"),
                candidate.Path.FileName);
        });
    }

    [Fact]
    public async Task FlintLegacy_Detects_Root_Anchors_And_AutoRuns_Once()
    {
        var engine = MigrationEngine.CreateDefault();
        var data = _dir / "data";
        data.CreateDirectory();
        AbsolutePath storeRoot = _dir / "store";
        AbsolutePath cacheRoot = _dir / "cache";
        AbsolutePath stateRoot = _dir / "state";
        var paths = new FlintPaths()
        {
            ConfigRoot = _dir,
            DataRoot = data,
            StoreRoot = storeRoot,
            CacheRoot = cacheRoot,
            StateRoot = stateRoot,
        };

        var legacy = engine.Find("flint-legacy");
        Assert.NotNull(legacy);

        // no legacy anchor → nothing
        Assert.Empty(legacy.Detect(paths, Decisions()));

        // the old flat layout appears (flint.cfg at the root layer)
        await (data / "flint.cfg").WriteAllTextAsync("k=v");

        var decisions = Decisions();
        var candidates = legacy.Detect(paths, decisions);
        var hit = Assert.Single(candidates);
        Assert.False(hit.DecisionRecorded);

        // the decision record settles it — the engine never proposes twice
        decisions.Write(new MigrationDecisionRecord
        {
            Source = FlintLegacyMigrationProfile.ProfileName,
            Decision = MigrationDecisionRecord.Imported,
            At = DateTimeOffset.Now.ToString("o"),
        });
        Assert.True(legacy.Detect(paths, decisions).Single().DecisionRecorded);

        // the flat→five-root whitelist mapping: flint.cfg plans onto the config root
        var plan = legacy.Plan(data, paths, ImportMode.Move);
        Assert.NotNull(plan);
        var cfgItem = Assert.Single(plan, item => item.Name == "flint.cfg");
        Assert.False(cfgItem.IsDirectory);
        Assert.Equal(PlannedMethod.RenameDirectory, cfgItem.Method);

        // an old in-data cache area plans its children 1:1 under the cache root
        (data / "cache" / "icons").CreateDirectory();
        await (data / "cache" / "icons" / "i.png").WriteAllTextAsync("x");
        var planWithCache = legacy.Plan(data, paths, ImportMode.Move);
        Assert.NotNull(planWithCache);
        Assert.Contains(planWithCache, item => item is { Name: "icons", IsDirectory: true });
    }

    [Fact]
    public async Task FlintLegacy_Eager_Pass_Remounts_Once_And_Cleans_The_Bare_Ledger()
    {
        var home = _dir / "roots";
        var data = home / "data";
        var config = home / "config";
        var store = home / "store";
        var cache = home / "cache";
        var state = home / "state";
        var paths = new FlintPaths()
        {
            ConfigRoot = config,
            DataRoot = data,
            StoreRoot = store,
            CacheRoot = cache,
            StateRoot = state,
        };
        foreach (var root in new[] { config, data, store, cache, state })
        {
            root.CreateDirectory();
        }

        var decisions = new MigrationDecisionStore(state / "migration",
            FlintJsonContext.Default.MigrationDecisionRecord);

        // the old flat layout: config file, a ledger WITH a real entry, meta content, store
        // content, the old in-data cache area, and the Prism bare ledger (dead stock)
        await (data / "flint.cfg").WriteAllTextAsync("JavaPath=old");
        var metaFile = data / "meta" / "net.minecraft" / "index.json";
        var metaDir = Assert.NotNull(metaFile.Parent);
        metaDir.CreateDirectory();
        const string metaBody = "{}";
        await metaFile.WriteAllTextAsync(metaBody);
        var md5 = Convert.ToHexString(MD5.HashData("{}"u8.ToArray()))
            .ToLowerInvariant();
        var ledgerJson = """
                         {"version":"1","entries":[{"base":"meta","path":"net.minecraft/index.json","md5sum":"MD5","etag":"","last_changed_timestamp":0,"eternal":true}]}
                         """.Replace("MD5", md5);
        await (data / "metacache.json").WriteAllTextAsync(ledgerJson);
        (data / "assets" / "indexes").CreateDirectory();
        await (data / "assets" / "indexes" / "5.json").WriteAllTextAsync("{}");
        (data / "cache" / "fmllibs").CreateDirectory();
        await (data / "cache" / "fmllibs" / "lib.jar").WriteAllTextAsync("j");
        await (data / "metacache").WriteAllTextAsync("prism-ledger");

        var result = FlintLegacyMigration.ExecuteIfNeeded(paths, decisions);

        // everything landed on its five-root destination
        Assert.True(result.Ran);
        Assert.Null(result.Error);
        Assert.True(result.LegacyLedgerCleaned);
        Assert.False((data / "metacache").ExistsFile());      // bare ledger deleted
        Assert.True((config / "flint.cfg").ExistsFile());     // config root
        Assert.True((cache / "metacache.json").ExistsFile()); // cache root
        Assert.True((cache / "meta" / "net.minecraft" / "index.json").ExistsFile());
        Assert.True((store / "assets" / "indexes" / "5.json").ExistsFile()); // store
        Assert.True((cache / "fmllibs" / "lib.jar").ExistsFile());           // cache

        // the pass sealed itself with the decision record — a second run is a no-op
        var record = decisions.Read(FlintLegacyMigrationProfile.ProfileName);
        Assert.NotNull(record);
        Assert.Equal(MigrationDecisionRecord.Imported, record.Decision);
        Assert.Equal("move", record.Mode);
        Assert.Equal(0, record.Instances); // no instances directory in this layout

        var second = FlintLegacyMigration.ExecuteIfNeeded(paths, decisions);
        Assert.False(second.Ran);

        // the REMOUNTED ledger is live for the cache: the entry loads (GetEntry) and
        // resolves against the moved content file (ResolveEntry verifies existence + md5 —
        // this pins the assembly-order guarantee: the eager pass runs before the runtime's
        // MetaCache.Load(), so the first ledger read sees the remounted file)
        using var ledger = new HttpMetaCache(paths.MetacacheFile);
        ledger.AddBase("meta", paths.MetaDir);
        ledger.Load();
        var loaded = ledger.GetEntry("meta", "net.minecraft/index.json");
        Assert.NotNull(loaded);
        Assert.False(loaded.IsStale);
        var resolved = ledger.ResolveEntry("meta", "net.minecraft/index.json");
        Assert.False(resolved.IsStale);
        Assert.Equal(metaBody, await resolved.GetFullPath().ReadAllTextAsync());
    }

    [Fact]
    public void FlintLegacy_HomeDir_Source_Migrates_The_Total_Directory()
    {
        // --home-dir laid on a pre-reform total directory: the flat files sit at the
        // HOME root — X/data is a fresh child that sees none of them. This pins the
        // home-root probe plus the home-only mounts: the data-root anchors
        // (instances/accounts/instgroups/icons/skins) move per child into X/data, the INI
        // chain fallbacks land on the config side, the token file is deleted, and the
        // nomigrate markers move to the data root where DataMigration.Decide upgrades them.
        var home = _dir / "homedir";
        (home / "instances" / "GTN").CreateDirectory();
        (home / "cache" / "icons").CreateDirectory();
        (home / "meta").CreateDirectory();
        (home / "assets" / "indexes").CreateDirectory();
        (home / "libraries").CreateDirectory();
        (home / "icons").CreateDirectory();
        (home / "skins").CreateDirectory();

        (home / "flint.cfg").WriteAllText("Language=zh");
        (home / "instances" / "GTN" / "instance.cfg").WriteAllText("InstanceType=OneSix\nname=GTN\n");
        (home / "accounts.json").WriteAllText("[]");
        (home / "instgroups.json").WriteAllText("{}");
        (home / "api-token").WriteAllText("tok");
        (home / "prism_nomigrate.txt").WriteAllText("");
        (home / "metacache").WriteAllText("prism-ledger");
        (home / "cache" / "icons" / "i.png").WriteAllText("png");
        (home / "meta" / "index.json").WriteAllText("{}");
        (home / "assets" / "indexes" / "5.json").WriteAllText("{}");
        (home / "libraries" / "x.jar").WriteAllText("jar");
        (home / "icons" / "default.png").WriteAllText("png");
        (home / "skins" / "steve.png").WriteAllText("png");
        (home / "prismlauncher.cfg").WriteAllText("k=v");

        var paths = FlintPaths.FromHomeDir(home);
        Assert.Equal(home, paths.HomeRoot);
        var decisions = new MigrationDecisionStore(paths.StateRoot / "migration",
            FlintJsonContext.Default.MigrationDecisionRecord);

        var result = FlintLegacyMigration.ExecuteIfNeeded(paths, decisions);

        Assert.True(result.Ran);
        Assert.Null(result.Error);
        Assert.True(result.LegacyLedgerCleaned);
        Assert.False((home / "flint.cfg").ExistsFile());
        Assert.True((paths.ConfigRoot / "flint.cfg").ExistsFile());
        Assert.True((paths.DataRoot / "instances" / "GTN" / "instance.cfg").ExistsFile());
        Assert.True((paths.DataRoot / "accounts.json").ExistsFile());
        Assert.True((paths.DataRoot / "instgroups.json").ExistsFile());
        Assert.True((paths.DataRoot / "icons" / "default.png").ExistsFile());
        Assert.True((paths.DataRoot / "skins" / "steve.png").ExistsFile());
        Assert.True((paths.ConfigRoot / "prismlauncher.cfg").ExistsFile());
        Assert.True((paths.DataRoot / "prism_nomigrate.txt").ExistsFile());
        Assert.False((home / "api-token").ExistsFile()); // dead stock, deleted
        Assert.False((home / "metacache").ExistsFile()); // bare ledger, deleted
        // under a home source the old cache root IS the new cache root — children stay
        Assert.True((paths.CacheRoot / "icons" / "i.png").ExistsFile());
        Assert.True((paths.CacheRoot / "meta" / "index.json").ExistsFile());
        Assert.True((paths.StoreRoot / "assets" / "indexes" / "5.json").ExistsFile());
        Assert.True((paths.StoreRoot / "libraries" / "x.jar").ExistsFile());

        // the pass sealed itself with the decision record — a second run is a no-op
        var record = decisions.Read(FlintLegacyMigrationProfile.ProfileName);
        Assert.NotNull(record);
        Assert.Equal(MigrationDecisionRecord.Imported, record.Decision);
        Assert.Equal(1, record.Instances);
        Assert.False(FlintLegacyMigration.ExecuteIfNeeded(paths, decisions).Ran);
    }

    [Fact]
    public async Task FlintLegacy_Execute_Remounts_Imported_Content_Even_When_The_Startup_Record_Is_Sealed()
    {
        // the upgraded-user steady state: the startup pass already ran and sealed the
        // record — the record gates the STARTUP auto-run only, never the remount itself
        var home = _dir / "sealed";
        var data = home / "data";
        var store = home / "store";
        AbsolutePath configRoot = home / "config";
        AbsolutePath cacheRoot = home / "cache";
        AbsolutePath stateRoot = home / "state";
        var paths = new FlintPaths()
        {
            ConfigRoot = configRoot,
            DataRoot = data,
            StoreRoot = store,
            CacheRoot = cacheRoot,
            StateRoot = stateRoot,
        };
        foreach (var root in new[]
                 {
                     paths.ConfigRoot, paths.DataRoot, paths.StoreRoot,
                     paths.CacheRoot, paths.StateRoot,
                 })
        {
            root.CreateDirectory();
        }

        var decisions = new MigrationDecisionStore(paths.StateRoot / "migration",
            FlintJsonContext.Default.MigrationDecisionRecord);
        (data / "assets" / "indexes").CreateDirectory();
        await (data / "assets" / "indexes" / "5.json").WriteAllTextAsync("{}");

        Assert.True(FlintLegacyMigration.ExecuteIfNeeded(paths, decisions).Ran);
        var sealedRecord = decisions.Read(FlintLegacyMigrationProfile.ProfileName);
        Assert.NotNull(sealedRecord);

        // the gated startup entry now skips (the :244 pin), even with fresh data-root
        // content waiting — which is exactly why the hook must not call it
        (data / "libraries" / "com" / "mojang").CreateDirectory();
        await (data / "libraries" / "com" / "mojang" / "x.jar").WriteAllTextAsync("j");
        Assert.False(FlintLegacyMigration.ExecuteIfNeeded(paths, decisions).Ran);
        Assert.False((store / "libraries" / "com" / "mojang" / "x.jar").ExistsFile());

        // the record-less entry (the prism import-finished hook) remounts what just landed
        var remount = FlintLegacyMigration.Execute(paths);
        Assert.True(remount.Ran);
        Assert.Null(remount.Error);
        Assert.True((store / "libraries" / "com" / "mojang" / "x.jar").ExistsFile());
        Assert.False((data / "libraries").ExistsDirectory());

        // and it never touches the decision record — the seal is the startup gate's business
        var after = decisions.Read(FlintLegacyMigrationProfile.ProfileName);
        Assert.NotNull(after);
        Assert.Equal(sealedRecord.At, after.At);
        Assert.Equal(sealedRecord.Bytes, after.Bytes);
    }

    [Fact]
    public async Task FlintLegacy_Execute_Writes_No_Record_Of_Its_Own()
    {
        // a first-prism-import user with NO flint-legacy record: the hook's remount does
        // the transfer but must not fabricate the startup seal over work it did not gate
        var home = _dir / "unsealed";
        var data = home / "data";
        AbsolutePath configRoot = home / "config";
        AbsolutePath storeRoot = home / "store";
        AbsolutePath cacheRoot = home / "cache";
        AbsolutePath stateRoot = home / "state";
        var paths = new FlintPaths()
        {
            ConfigRoot = configRoot,
            DataRoot = data,
            StoreRoot = storeRoot,
            CacheRoot = cacheRoot,
            StateRoot = stateRoot,
        };
        foreach (var root in new[]
                 {
                     paths.ConfigRoot, paths.DataRoot, paths.StoreRoot,
                     paths.CacheRoot, paths.StateRoot,
                 })
        {
            root.CreateDirectory();
        }

        (data / "assets" / "indexes").CreateDirectory();
        await (data / "assets" / "indexes" / "5.json").WriteAllTextAsync("{}");

        var remount = FlintLegacyMigration.Execute(paths);

        Assert.True(remount.Ran);
        Assert.True((paths.StoreRoot / "assets" / "indexes" / "5.json").ExistsFile());
        Assert.False((paths.StateRoot / "migration" / (FlintLegacyMigrationProfile.ProfileName + ".json")).ExistsFile());
    }

    [Fact]
    public void FlintLegacy_Eager_Pass_Leaves_The_Record_Unsealed_On_Transfer_Failure()
    {
        var home = _dir / "failing";
        var data = home / "data";
        var store = home / "store";
        AbsolutePath configRoot = home / "config";
        AbsolutePath cacheRoot = home / "cache";
        AbsolutePath stateRoot = home / "state";
        var paths = new FlintPaths()
        {
            ConfigRoot = configRoot,
            DataRoot = data,
            StoreRoot = store,
            CacheRoot = cacheRoot,
            StateRoot = stateRoot,
        };
        // NOTE: the store root is deliberately NOT created — a FILE occupies its path, so
        // every directory-creation attempt for the assets destination fails
        foreach (var root in new[] { paths.ConfigRoot, paths.DataRoot, paths.CacheRoot, paths.StateRoot })
        {
            root.CreateDirectory();
        }

        store.WriteAllText("a file blocks the store assets root");
        var decisions = new MigrationDecisionStore(paths.StateRoot / "migration",
            FlintJsonContext.Default.MigrationDecisionRecord);

        // a transfer that cannot succeed: the assets mount wants <store>/assets but the
        // store path is a file — both the whole-rename and the per-file walk fail, which
        // the pass must report instead of sealing over
        (data / "assets").CreateDirectory();
        (data / "assets" / "5.json").WriteAllText("x");

        var result = FlintLegacyMigration.ExecuteIfNeeded(paths, decisions);

        // honest failure: reported, NOT sealed — the next startup resumes the merge
        Assert.True(result.Ran);
        Assert.NotNull(result.Error);
        Assert.Null(decisions.Read(FlintLegacyMigrationProfile.ProfileName));
    }

    // an existing destination DIRECTORY must resume the merge file by file — the old
    // whole-item skip stranded every not-yet-transferred source file forever, so a pass
    // that failed halfway could never complete on a rerun
    [Fact]
    public void FlintLegacy_DirectoryDestinationExists_ResumesTheMergeFileByFile()
    {
        var home = _dir / "resume";
        var data = home / "data";
        var store = home / "store";
        AbsolutePath configRoot = home / "config";
        AbsolutePath cacheRoot = home / "cache";
        AbsolutePath stateRoot = home / "state";
        var paths = new FlintPaths()
        {
            ConfigRoot = configRoot,
            DataRoot = data,
            StoreRoot = store,
            CacheRoot = cacheRoot,
            StateRoot = stateRoot,
        };
        paths.ConfigRoot.CreateDirectory();
        paths.DataRoot.CreateDirectory();
        paths.CacheRoot.CreateDirectory();
        paths.StateRoot.CreateDirectory();

        // source content: one file lands, one is blocked — the remount fails halfway
        (data / "assets" / "sub").CreateDirectory();
        (data / "assets" / "first.txt").WriteAllText("first");
        (data / "assets" / "sub" / "late.txt").WriteAllText("late");
        // a FILE occupies the <store>/assets/sub path, so sub/late.txt cannot transfer —
        // the exact shape of a half-done earlier pass (some files in, some stranded)
        (store / "assets").CreateDirectory();
        (store / "assets" / "sub").WriteAllText("a file blocks the subtree");

        var first = FlintLegacyMigration.Execute(paths, data);

        // honest half-failure: the pass reports incomplete, first.txt is in, late.txt is not
        Assert.True(first.Ran);
        Assert.NotNull(first.Error);
        Assert.Equal("first", (store / "assets" / "first.txt").ReadAllText());
        Assert.False((store / "assets" / "sub" / "late.txt").ExistsFile());
        Assert.True((data / "assets" / "sub" / "late.txt").ExistsFile()); // source intact

        // the blocker clears (operator or a later pass) — the rerun must now complete the
        // leftovers THROUGH the existing destination directory
        (store / "assets" / "sub").Delete();
        var second = FlintLegacyMigration.Execute(paths, data);

        Assert.True(second.Ran);
        Assert.Null(second.Error);
        Assert.Equal("late", (store / "assets" / "sub" / "late.txt").ReadAllText());
        Assert.Equal("first", (store / "assets" / "first.txt").ReadAllText()); // kept
    }

    [Fact]
    public void FlintLegacy_Eager_Pass_Is_A_Noop_For_A_Fresh_Install()
    {
        var home = _dir / "fresh";
        AbsolutePath configRoot = home / "config";
        AbsolutePath dataRoot = home / "data";
        AbsolutePath storeRoot = home / "store";
        AbsolutePath cacheRoot = home / "cache";
        AbsolutePath stateRoot = home / "state";
        var paths = new FlintPaths()
        {
            ConfigRoot = configRoot,
            DataRoot = dataRoot,
            StoreRoot = storeRoot,
            CacheRoot = cacheRoot,
            StateRoot = stateRoot,
        };
        foreach (var root in new[]
                 {
                     paths.ConfigRoot, paths.DataRoot, paths.StoreRoot,
                     paths.CacheRoot, paths.StateRoot,
                 })
        {
            root.CreateDirectory();
        }

        // no legacy anchor (a bare instances/ directory is NOT one — discovery creates it
        // on every install) → skipped, and no decision record is written
        (paths.DataRoot / "instances").CreateDirectory();
        var result = FlintLegacyMigration.ExecuteIfNeeded(paths, null);

        Assert.False(result.Ran);
        Assert.False((paths.StateRoot / "migration" / (FlintLegacyMigrationProfile.ProfileName + ".json")).ExistsFile());
    }

    // a failing decision-store write must not escape the eager pass: the seal is
    // best-effort (the remount itself already tolerated IO failures), so the completed
    // remount still reports success and the next startup resumes as a no-op probe
    [Fact]
    public async Task FlintLegacy_Eager_Pass_Tolerates_A_Failing_Decision_Store()
    {
        var home = _dir / "blockedstore";
        var data = home / "data";
        AbsolutePath configRoot = home / "config";
        AbsolutePath storeRoot = home / "store";
        AbsolutePath cacheRoot = home / "cache";
        AbsolutePath stateRoot = home / "state";
        var paths = new FlintPaths()
        {
            ConfigRoot = configRoot,
            DataRoot = data,
            StoreRoot = storeRoot,
            CacheRoot = cacheRoot,
            StateRoot = stateRoot,
        };
        foreach (var root in new[]
                 {
                     paths.ConfigRoot, paths.DataRoot, paths.StoreRoot,
                     paths.CacheRoot, paths.StateRoot,
                 })
        {
            root.CreateDirectory();
        }

        // a FILE occupies the migration records path, so the seal write throws
        var blocker = paths.StateRoot / "migration";
        await blocker.WriteAllTextAsync("a file blocks the migration records directory");
        var decisions = new MigrationDecisionStore(blocker, FlintJsonContext.Default.MigrationDecisionRecord);
        (data / "assets" / "indexes").CreateDirectory();
        await (data / "assets" / "indexes" / "5.json").WriteAllTextAsync("{}");

        var result = FlintLegacyMigration.ExecuteIfNeeded(paths, decisions);

        // the remount itself completed and reported success — the failed seal was swallowed
        Assert.True(result.Ran);
        Assert.Null(result.Error);
        Assert.True((paths.StoreRoot / "assets" / "indexes" / "5.json").ExistsFile());
    }

    // the double-legacy state: BOTH roots carry old flat-layout anchors (a prism import
    // landed shared content at the data root and the process died before the
    // OnImportFinished remount, while the home root is itself a pre-reform total directory).
    // One eager pass must remount both and seal exactly ONE profile-level record.
    [Fact]
    public void FlintLegacy_Dual_Legacy_Roots_Both_Remount_Under_One_Seal()
    {
        var home = _dir / "dual";
        (home / "instances" / "GTN").CreateDirectory();
        (home / "assets" / "indexes").CreateDirectory();
        (home / "flint.cfg").WriteAllText("Language=zh");
        (home / "instances" / "GTN" / "instance.cfg").WriteAllText("InstanceType=OneSix\nname=GTN\n");
        (home / "assets" / "indexes" / "5.json").WriteAllText("{}");

        var paths = FlintPaths.FromHomeDir(home);
        foreach (var root in new[]
                 {
                     paths.ConfigRoot, paths.DataRoot, paths.StoreRoot,
                     paths.CacheRoot, paths.StateRoot,
                 })
        {
            root.CreateDirectory();
        }

        // the import-landed half sits at the data root: shared content the remount hook
        // never moved (the process died before OnImportFinished ran)
        (paths.DataRoot / "assets" / "indexes").CreateDirectory();
        (paths.DataRoot / "assets" / "indexes" / "17.json").WriteAllText("{}");
        var decisions = new MigrationDecisionStore(paths.StateRoot / "migration",
            FlintJsonContext.Default.MigrationDecisionRecord);

        var result = FlintLegacyMigration.ExecuteIfNeeded(paths, decisions);

        Assert.True(result.Ran);
        Assert.Null(result.Error);
        // BOTH roots remounted: the data-root assets onto the store root AND the home
        // root's instances/GTN into the fresh data directory, flint.cfg on the config side
        Assert.True((paths.StoreRoot / "assets" / "indexes" / "17.json").ExistsFile());
        Assert.True((paths.StoreRoot / "assets" / "indexes" / "5.json").ExistsFile());
        Assert.True((paths.DataRoot / "instances" / "GTN" / "instance.cfg").ExistsFile());
        Assert.True((paths.ConfigRoot / "flint.cfg").ExistsFile());
        // exactly ONE profile-level record seals the whole pass
        Assert.NotNull(decisions.Read(FlintLegacyMigrationProfile.ProfileName));
        Assert.Single((paths.StateRoot / "migration").EnumerateFiles());
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
        var home = _dir / "dualfail";
        (home / "instances" / "GTN").CreateDirectory();
        (home / "flint.cfg").WriteAllText("Language=zh");
        (home / "instances" / "GTN" / "instance.cfg").WriteAllText("InstanceType=OneSix\nname=GTN\n");

        var paths = FlintPaths.FromHomeDir(home);
        foreach (var root in new[] { paths.DataRoot, paths.StoreRoot, paths.CacheRoot, paths.StateRoot })
        {
            root.CreateDirectory();
        }

        // the placeholder-file method: a FILE occupies the config root so the home pass's
        // flint.cfg mount cannot transfer
        paths.ConfigRoot.WriteAllText("a file blocks the config root");
        // the data-root half that must complete regardless: import-landed shared content
        (paths.DataRoot / "assets" / "indexes").CreateDirectory();
        (paths.DataRoot / "assets" / "indexes" / "17.json").WriteAllText("{}");
        var decisions = new MigrationDecisionStore(paths.StateRoot / "migration",
            FlintJsonContext.Default.MigrationDecisionRecord);

        var result = FlintLegacyMigration.ExecuteIfNeeded(paths, decisions);

        // honest aggregate failure: reported, NOT sealed
        Assert.True(result.Ran);
        Assert.NotNull(result.Error);
        Assert.Null(decisions.Read(FlintLegacyMigrationProfile.ProfileName));
        // the data-root half already completed; the home half's flint.cfg is still waiting
        Assert.True((paths.StoreRoot / "assets" / "indexes" / "17.json").ExistsFile());
        Assert.True((home / "flint.cfg").ExistsFile());
    }
}
