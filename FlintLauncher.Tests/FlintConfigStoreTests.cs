// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Tests: the typed layer-stack fold (upper non-null wins, null
// inherits, record property defaults single-source), the flint.json store (load-or-default,
// atomic save, resolved-shape round-trip), the one-shot INI import pipeline (chain probing
// with the MultiMC dual-name rider, the secret trio → the store, the state keys →
// launcher-state.json, the rest → the record domains) and the wire rerouting (PUT/GET of
// the secret trio through secrets.json, never flint.json).

using System.Net;
using System.Text.Json;
using FlintLauncher.Core;
using FlintLauncher.Core.Config;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Secrets;
using FlintLauncher.Core.Settings;
using FlintLauncher.Core.State;
using FlintLauncher.Web;
using TruePath;
using TruePath.SystemIo;
using Xunit;

namespace FlintLauncher.Tests;

public class FlintConfigStoreTests : IDisposable
{
    private readonly AbsolutePath _dir = Temporary.SystemTempDirectory() / $"flint-config-{Guid.NewGuid():N}";

    private AbsolutePath ConfigPath => _dir / "flint.json";

    public FlintConfigStoreTests() => _dir.CreateDirectory();

    public void Dispose()
    {
        try
        {
            _dir.DeleteDirectoryRecursively();
        }
        catch (DirectoryNotFoundException) { }
    }

    private FlintConfigStore NewStore() =>
        new(ConfigPath, FlintJsonContext.Default.FlintConfigFile);

    // ------------------------------------------------------------------------ fold

    [Fact]
    public void Fold_Defaults_Come_From_The_Default_Table()
    {
        // an all-null layer stack resolves to exactly the Default() table (single source —
        // the old double table is gone; the domain records are blank)
        var resolved = FlintConfigFold.MergeOverDefault(new FlintConfigFile());
        Assert.Equal(10, resolved.Launcher.NumberOfConcurrentTasks);
        Assert.Equal(6, resolved.Launcher.NumberOfConcurrentDownloads);
        Assert.Equal("Name", resolved.Launcher.InstSortMode);
        Assert.True(resolved.Console.ShowConsoleOnError);
        Assert.Equal(100000, resolved.Console.ConsoleMaxLines);
        Assert.Equal(512, resolved.Memory.MinMemAlloc);
        Assert.Equal(MemoryConfig.DefaultMaxMemAlloc, resolved.Memory.MaxMemAlloc);
        Assert.Equal(854, resolved.Window.MinecraftWinWidth);
        Assert.Equal("None", resolved.Proxy.ProxyType);
        Assert.Equal("127.0.0.1", resolved.Proxy.ProxyAddr);
        Assert.Equal(8080, resolved.Proxy.ProxyPort);
        Assert.Equal((int)PasteUpload.PasteType.Mclogs, resolved.Api.PastebinType);
        Assert.NotNull(resolved.MinecraftMisc.Env);
        Assert.Empty(resolved.MinecraftMisc.Env);
        Assert.Equal("[]", resolved.ModDownload.ModUpdateReleaseTypes);
    }

    [Fact]
    public void Fold_Upper_Layer_Wins_Where_NonNull_Null_Inherits()
    {
        // Layers are partial payloads (the shape deserialization produces — absent scalars
        // are null and inherit; the materializing constructors re-create absent DOMAINS as
        // blank records, so a domain's presence never masks the layer below — only its set
        // scalars win):
        // user layer sets memory + java, the instance layer (typed payloads arrive)
        // overrides only MinMemAlloc.
        var user = JsonSerializer.Deserialize(
            """{"memory":{"minMemAlloc":1024},"java":{"javaPath":"/opt/jdk-21"}}""",
            FlintJsonContext.Default.FlintConfigFile);
        var instance = JsonSerializer.Deserialize(
            """{"memory":{"minMemAlloc":2048}}""",
            FlintJsonContext.Default.FlintConfigFile);

        Assert.NotNull(user);
        Assert.NotNull(instance);
        var resolved = FlintConfigFold.MergeOverDefault(FlintConfigFold.Merge(user, instance));
        Assert.Equal(2048, resolved.Memory.MinMemAlloc);                            // instance wins
        Assert.Equal("/opt/jdk-21", resolved.Java.JavaPath);                        // inherited from user
        Assert.Equal(MemoryConfig.DefaultMaxMemAlloc, resolved.Memory.MaxMemAlloc); // default
        Assert.Equal(FlintConfigFile.Default().Dirs.IconsDir, resolved.Dirs.IconsDir);
    }

    // ------------------------------------------------------------------------ store

    [Fact]
    public void Store_Missing_File_Yields_Defaults_And_First_Write_Seals_The_File()
    {
        Assert.False(ConfigPath.ExistsFile());
        var store = NewStore();
        Assert.Equal(512, store.Current.Memory.MinMemAlloc);
        Assert.False(ConfigPath.ExistsFile()); // a load without writes creates nothing

        store.Set("MinMemAlloc", 3072);
        Assert.True(ConfigPath.ExistsFile());
        Assert.Equal(3072, store.Current.Memory.MinMemAlloc);

        // reload from disk: the resolved shape round-trips (Flint-only write side)
        var reloaded = NewStore();
        Assert.Equal(3072, reloaded.Current.Memory.MinMemAlloc);
    }

    [Fact]
    public void Store_Set_Coerces_Like_The_QVariant_Reads_Did()
    {
        var store = NewStore();
        // INI-era boxed values arrive as strings/longs; the registry's Apply coerces
        store.Set("ProxyPort", "3128");
        Assert.Equal(3128, store.Current.Proxy.ProxyPort);
        store.Set("ProxyPort", 9999L);
        Assert.Equal(9999, store.Current.Proxy.ProxyPort);
        store.Set("LaunchMaximized", "true");
        Assert.True(store.Current.Window.LaunchMaximized);
    }

    [Fact]
    public void Store_Partial_Payload_Folds_Over_The_Current_Defaults()
    {
        // a payload written by an older schema (fewer members) keeps its stored values and
        // takes the record defaults for the absent ones — no nulls reach the resolved view
        ConfigPath.WriteAllText(
            """{"launcher":{"numberOfConcurrentTasks":3},"memory":{"minMemAlloc":768}}""");
        var store = NewStore();
        Assert.Equal(3, store.Current.Launcher.NumberOfConcurrentTasks);
        Assert.Equal(768, store.Current.Memory.MinMemAlloc);
        Assert.Equal(6, store.Current.Launcher.NumberOfConcurrentDownloads);
    }

    [Fact]
    public void Store_Resolves_Every_Registry_Key_And_Persists_Through_The_Store()
    {
        var store = NewStore();
        foreach (var entry in FlintConfigKeys.All)
        {
            if (entry.Secret)
            {
                // the secret trio is never a config member — the store serves an empty
                // placeholder (the real values live in the ISecretStore)
                Assert.Equal("", Variant.AsString(store.Get(entry.Key)));
            }
            else
            {
                // the resolved fold materializes every key's default
                Assert.NotNull(store.Get(entry.Key));
            }
        }

        store.Set("JavaPath", "/usr/bin/java");
        Assert.Equal("/usr/bin/java", Variant.AsString(store.Get("JavaPath")));
        store.Reset("JavaPath");
        Assert.Equal("", Variant.AsString(store.Get("JavaPath")));
        Assert.True(ConfigPath.ExistsFile()); // every mutation persisted (flint.json, not INI)
    }

    // ------------------------------------------------------------------------ importer

    /// <summary>Secrets + state over the fixture roots (the import's two side destinations).</summary>
    private (FileSecretStore Secrets, LauncherStateStore State) NewSideStores()
    {
        var secrets = new FileSecretStore(_dir / "secrets.json",
            FlintJsonContext.Default.SecretsFile);
        var state = new LauncherStateStore(_dir / "launcher-state.json",
            FlintJsonContext.Default.LauncherState);
        return (secrets, state);
    }

    private FlintPaths Paths
    {
        get
        {
            AbsolutePath dataRoot = _dir / "data";
            AbsolutePath storeRoot = _dir / "store";
            AbsolutePath cacheRoot = _dir / "cache";
            AbsolutePath stateRoot = _dir / "state";
            return new FlintPaths()
            {
                ConfigRoot = _dir,
                DataRoot = dataRoot,
                StoreRoot = storeRoot,
                CacheRoot = cacheRoot,
                StateRoot = stateRoot,
            };
        }
    }

    [Fact]
    public void Import_Maps_The_Ini_Chain_Into_Records_Secrets_And_State()
    {
        var paths = Paths;
        paths.DataRoot.CreateDirectory();
        (paths.DataRoot / "prismlauncher.cfg").WriteAllText("""
                                                                             [General]
                                                                             ConfigVersion=1.3
                                                                             NumberOfConcurrentTasks=4
                                                                             MaxMemAlloc=8192
                                                                             JavaPath=/opt/jdk
                                                                             ProxyType=HTTP
                                                                             ProxyAddr=proxy.corp
                                                                             ProxyPort=3128
                                                                             ProxyUser=corpus
                                                                             ProxyPass=hunter2
                                                                             ModrinthToken=mr-token
                                                                             TotalPlayTime=123456
                                                                             TotalPlayTimeMigrated=true
                                                                             LastOfflinePlayerName=OfflineOne
                                                                             UserAskedAboutAutomaticJavaDownload=true
                                                                             Language=zh_CN
                                                                             IconTheme=pebble
                                                                             MainWindowGeometry=@ByteArray(\0\0)
                                                                             """);

        var store = NewStore();
        var (secrets, state) = NewSideStores();
        var imported = FlintConfigImporter.Import(paths, store, secrets, state);

        // the chain file that won + the one-shot seal
        Assert.EndsWith("prismlauncher.cfg", imported.ImportedIni);
        Assert.True(ConfigPath.ExistsFile());

        // (3) record domains — the recognized keys landed, unknown keys did not
        Assert.Equal(4, store.Current.Launcher.NumberOfConcurrentTasks);
        Assert.Equal(8192, store.Current.Memory.MaxMemAlloc);
        Assert.Equal("/opt/jdk", store.Current.Java.JavaPath);
        Assert.Equal("HTTP", store.Current.Proxy.ProxyType);
        Assert.Equal("proxy.corp", store.Current.Proxy.ProxyAddr);
        Assert.Equal(3128, store.Current.Proxy.ProxyPort);
        Assert.Equal("corpus", store.Current.Proxy.ProxyUser);
        Assert.Equal("zh_CN", store.Current.Language.Language);
        // secrets must NOT be config members (the format carries no secret fields)
        Assert.DoesNotContain("hunter2", ConfigPath.ReadAllText());
        Assert.DoesNotContain("mr-token", ConfigPath.ReadAllText());

        // (1) the secret trio → the store
        Assert.Equal("hunter2", secrets.Get("ProxyPass"));
        Assert.Equal("mr-token", secrets.Get("ModrinthToken"));
        Assert.Null(secrets.Get("FlameKeyOverride"));

        // (2) the state keys → launcher-state.json
        Assert.Equal(123456, state.Current.Playtime.Total);
        Assert.True(state.Current.Playtime.Migrated);
        Assert.Equal("OfflineOne", state.Current.LastOfflinePlayerName);
        Assert.True(state.Current.UserAskedAboutAutomaticJavaDownload);
    }

    [Fact]
    public void Import_Probes_The_Chain_With_The_Multimc_Dual_Names()
    {
        // preference order: flint.cfg > prismlauncher.cfg > polymc.cfg > multimc.cfg | mmc.cfg
        Assert.Null(FlintConfigImporter.FindChainFile(Paths));
        var paths = Paths;
        paths.DataRoot.CreateDirectory();

        (paths.DataRoot / "mmc.cfg").WriteAllText("[General]\nLanguage=from-mmc\n");
        Assert.EndsWith("mmc.cfg", FlintConfigImporter.FindChainFile(paths)?.Value);

        (paths.DataRoot / "multimc.cfg").WriteAllText("[General]\nLanguage=from-multimc\n");
        Assert.EndsWith("multimc.cfg", FlintConfigImporter.FindChainFile(paths)?.Value);

        (paths.DataRoot / "polymc.cfg").WriteAllText("[General]\nLanguage=from-polymc\n");
        Assert.EndsWith("polymc.cfg", FlintConfigImporter.FindChainFile(paths)?.Value);

        (paths.DataRoot / "prismlauncher.cfg").WriteAllText("[General]\nLanguage=from-prism\n");
        Assert.EndsWith("prismlauncher.cfg", FlintConfigImporter.FindChainFile(paths)?.Value);

        // the config root outranks the flat data root for the same name
        (paths.ConfigRoot / "flint.cfg").WriteAllText("[General]\nLanguage=from-flint\n");
        Assert.EndsWith("flint.cfg", FlintConfigImporter.FindChainFile(paths)?.Value);

        var store = NewStore();
        var (secrets, state) = NewSideStores();
        var imported = FlintConfigImporter.Import(paths, store, secrets, state);
        Assert.EndsWith("flint.cfg", imported.ImportedIni);
        Assert.Equal("from-flint", store.Current.Language.Language);
    }

    [Fact]
    public void Import_Merges_Idempotently_And_Never_Downgrades_Written_Values()
    {
        var paths = Paths;
        paths.DataRoot.CreateDirectory();
        (paths.DataRoot / "flint.cfg").WriteAllText(
            "[General]\nMinMemAlloc=256\nProxyPass=first\n");
        var store = NewStore();
        var (secrets, state) = NewSideStores();

        Assert.NotNull(FlintConfigImporter.Import(paths, store, secrets, state).ImportedIni);
        Assert.Equal(256, store.Current.Memory.MinMemAlloc);

        // the merge rule: a member the user's INI already set (≠ its record default) is
        // customized — a later INI change lands nowhere, and a stored secret is never
        // overwritten (a value that coincidentally EQUALS the default is the heuristic's
        // known blind spot: only MaxMemAlloc is machine-derived, the rest are fixed)
        (paths.DataRoot / "flint.cfg").WriteAllText(
            "[General]\nMinMemAlloc=999\nProxyPass=second\n");
        Assert.NotNull(FlintConfigImporter.Import(paths, store, secrets, state).ImportedIni);
        Assert.Equal(256, store.Current.Memory.MinMemAlloc);
        Assert.Equal("first", secrets.Get("ProxyPass"));

        // the projection fills members still at default: an INI key the first pass missed
        (paths.DataRoot / "flint.cfg").WriteAllText(
            "[General]\nMinMemAlloc=999\nProxyPass=second\nRequestTimeout=90\n");
        FlintConfigImporter.Import(paths, store, secrets, state);
        Assert.Equal(256, store.Current.Memory.MinMemAlloc);
        Assert.Equal(90, store.Current.Launcher.RequestTimeout);
    }

    [Fact]
    public void Import_Merges_Over_An_Existing_FlintJson()
    {
        var paths = Paths;
        paths.DataRoot.CreateDirectory();
        (paths.DataRoot / "flint.cfg").WriteAllText("[General]\nMinMemAlloc=999\nProxyAddr=proxy.corp\n");
        var store = NewStore();
        var (secrets, state) = NewSideStores();

        // the user wrote a setting before running the migration: the customized value wins,
        // the member still at default that the INI carries is filled
        store.Set("MinMemAlloc", 256);
        var imported = FlintConfigImporter.Import(paths, store, secrets, state);
        Assert.EndsWith("flint.cfg", imported.ImportedIni);
        Assert.Equal(256, store.Current.Memory.MinMemAlloc);
        Assert.Equal("proxy.corp", store.Current.Proxy.ProxyAddr);
    }

    [Fact]
    public void Import_Drops_The_Java_Probe_Quintet_And_Needs_No_State_Keys()
    {
        var paths = Paths;
        paths.DataRoot.CreateDirectory();
        (paths.DataRoot / "flint.cfg").WriteAllText("""
                                                                     [General]
                                                                     JavaSignature=abc
                                                                     JavaArchitecture=64
                                                                     JavaRealArchitecture=x86_64
                                                                     JavaVersion=17.0.2
                                                                     JavaVendor=temurin
                                                                     NumberOfManualRetries=2
                                                                     """);
        var store = NewStore();
        var (secrets, state) = NewSideStores();

        Assert.NotNull(FlintConfigImporter.Import(paths, store, secrets, state).ImportedIni);
        // the quintet is state-side and path-keyed now — nothing importable without the path
        Assert.Empty(state.Current.Java.Detections);
        Assert.Equal("", state.Current.Java.LastHostname);
        // the ordinary config key still landed and the seal was written
        Assert.Equal(2, store.Current.Launcher.NumberOfManualRetries);
        Assert.True(ConfigPath.ExistsFile());
    }

    [Fact]
    public void Import_C9_Bool_Keys_Read_Qt_Truthy_Strings()
    {
        // the seals must judge the INI string the way QVariant(QString).toBool() did —
        // a hand-edited "True" reads true, so the playtime one-shot seal holds (no re-migration)
        // and the wizard question stays asked. The strict "true"/"1" match read these false.
        var paths = Paths;
        paths.DataRoot.CreateDirectory();
        (paths.DataRoot / "prismlauncher.cfg").WriteAllText("""
                                                                             [General]
                                                                             TotalPlayTime=999
                                                                             TotalPlayTimeMigrated=True
                                                                             UserAskedAboutAutomaticJavaDownload=True
                                                                             """);
        var store = NewStore();
        var (secrets, state) = NewSideStores();

        Assert.NotNull(FlintConfigImporter.Import(paths, store, secrets, state).ImportedIni);
        Assert.Equal(999, state.Current.Playtime.Total);
        Assert.True(state.Current.Playtime.Migrated);
        Assert.True(state.Current.UserAskedAboutAutomaticJavaDownload);
    }

    // ------------------------------------------------- C++ setting synonyms

    [Fact]
    public void Registry_Folds_The_Cpp_Synonyms_Onto_The_Primary_Keys()
    {
        // the C++ registerSetting brace lists (launcher/Application.cpp:708-912): every
        // legacy INI spelling answers as its primary entry — and is never an entry of its own
        (string Alias, string Primary)[] pairs =
        [
            ("MCWindowMaximize", "LaunchMaximized"),
            ("MCWindowWidth", "MinecraftWinWidth"),
            ("MCWindowHeight", "MinecraftWinHeight"),
            ("ProxyHostName", "ProxyAddr"),
            ("ProxyUsername", "ProxyUser"),
            ("ProxyPassword", "ProxyPass"),
            ("MinMemoryAlloc", "MinMemAlloc"),
            ("MaxMemoryAlloc", "MaxMemAlloc"),
            ("PreLoadCmd", "PreLoadCommand"),
            ("PreLaunchCmd", "PreLaunchCommand"),
            ("PostExitCmd", "PostExitCommand"),
            ("ResourceURL", "ResourceURLOverride"),
            ("CFKeyOverride", "FlameKeyOverride"),
        ];
        foreach (var (alias, primary) in pairs)
        {
            Assert.Equal(primary, FlintConfigKeys.Canonical(alias));
            var resolved = FlintConfigKeys.Resolve(alias);
            Assert.NotNull(resolved);
            Assert.Equal(primary, resolved.Key);
            Assert.Equal(FlintConfigKeys.Default(primary), FlintConfigKeys.Default(alias));
            Assert.True(FlintConfigKeys.Resolve(alias) == FlintConfigKeys.Resolve(primary));
        }

        // the secret aliases read as secrets (the import redirects them with the primary)
        Assert.True(FlintConfigKeys.IsSecret("ProxyPassword"));
        Assert.True(FlintConfigKeys.IsSecret("CFKeyOverride"));
        // CentralModsDir|ModsDir stays dead: the alias of an unregistered key is
        // unregistered — a leftover alias in an imported INI stays unread in the file
        Assert.Null(FlintConfigKeys.Resolve("CentralModsDir"));
        Assert.Null(FlintConfigKeys.Resolve("ModsDir"));
    }

    [Fact]
    public void Import_Folds_The_Legacy_Synonym_Spellings_Onto_The_Primary_Keys()
    {
        // an old Prism cfg can carry the alias spellings — they must land under the
        // primary (record domains; secret aliases redirect with the primary), not drop
        // on the single pass's exact match
        var paths = Paths;
        paths.DataRoot.CreateDirectory();
        (paths.DataRoot / "prismlauncher.cfg").WriteAllText("""
                                                                             [General]
                                                                             ConfigVersion=1.3
                                                                             MCWindowMaximize=true
                                                                             MCWindowWidth=1024
                                                                             MCWindowHeight=768
                                                                             ProxyHostName=old.corp
                                                                             ProxyUsername=olduser
                                                                             ProxyPassword=oldpass
                                                                             MinMemoryAlloc=2048
                                                                             MaxMemoryAlloc=4096
                                                                             PreLoadCmd=echo preload
                                                                             PreLaunchCmd=echo prelaunch
                                                                             PostExitCmd=echo postexit
                                                                             ResourceURL=https://resources.example
                                                                             CFKeyOverride=cf-old
                                                                             """);
        var store = NewStore();
        var (secrets, state) = NewSideStores();

        Assert.NotNull(FlintConfigImporter.Import(paths, store, secrets, state).ImportedIni);

        Assert.True(store.Current.Window.LaunchMaximized);
        Assert.Equal(1024, store.Current.Window.MinecraftWinWidth);
        Assert.Equal(768, store.Current.Window.MinecraftWinHeight);
        Assert.Equal("old.corp", store.Current.Proxy.ProxyAddr);
        Assert.Equal("olduser", store.Current.Proxy.ProxyUser);
        Assert.Equal(2048, store.Current.Memory.MinMemAlloc);
        Assert.Equal(4096, store.Current.Memory.MaxMemAlloc);
        Assert.Equal("echo preload", store.Current.Commands.PreLoadCommand);
        Assert.Equal("echo prelaunch", store.Current.Commands.PreLaunchCommand);
        Assert.Equal("echo postexit", store.Current.Commands.PostExitCommand);
        Assert.Equal("https://resources.example", store.Current.Mirror.ResourceUrlOverride);

        // the secret aliases land under the PRIMARY names — nothing under the aliases
        Assert.Equal("oldpass", secrets.Get("ProxyPass"));
        Assert.Equal("cf-old", secrets.Get("FlameKeyOverride"));
        Assert.Null(secrets.Get("ProxyPassword"));
        Assert.Null(secrets.Get("CFKeyOverride"));
        // and never reach flint.json
        var flintJson = ConfigPath.ReadAllText();
        Assert.DoesNotContain("oldpass", flintJson);
        Assert.DoesNotContain("cf-old", flintJson);
    }

    [Fact]
    public void Import_Primary_Spelling_Wins_When_A_File_Carries_Both_Spellings()
    {
        var paths = Paths;
        paths.DataRoot.CreateDirectory();
        (paths.DataRoot / "flint.cfg").WriteAllText("""
                                                                     [General]
                                                                     MinMemAlloc=1111
                                                                     MinMemoryAlloc=2222
                                                                     ProxyPass=primary-secret
                                                                     ProxyPassword=alias-secret
                                                                     """);
        var store = NewStore();
        var (secrets, state) = NewSideStores();

        Assert.NotNull(FlintConfigImporter.Import(paths, store, secrets, state).ImportedIni);

        // INISettingsObject::retrieveValue walks configKeys() primary-first: the alias line
        // drops when the primary spelling is present, regardless of file order
        Assert.Equal(1111, store.Current.Memory.MinMemAlloc);
        Assert.Equal("primary-secret", secrets.Get("ProxyPass"));
    }

    // ------------------------------------------------------------ playtime.cfg probe

    [Fact]
    public void Import_Probes_The_DataRoot_PlaytimeCfg_For_The_TotalPlayTime_Pair()
    {
        // the C++ keeps the pair in <data-root>/playtime.cfg (Application.cpp:943-949 —
        // the relative name resolves against the chdir'd data root), never in the cfg chain;
        // a real Prism import takes the pair from the separate file
        var paths = Paths;
        paths.DataRoot.CreateDirectory();
        (paths.DataRoot / "prismlauncher.cfg").WriteAllText(
            "[General]\nLanguage=zh_CN\n");
        (paths.DataRoot / "playtime.cfg").WriteAllText("""
                                                                        [General]
                                                                        TotalPlayTime=654321
                                                                        TotalPlayTimeMigrated=true
                                                                        """);
        var store = NewStore();
        var (secrets, state) = NewSideStores();

        Assert.NotNull(FlintConfigImporter.Import(paths, store, secrets, state).ImportedIni);
        Assert.Equal(654321, state.Current.Playtime.Total);
        Assert.True(state.Current.Playtime.Migrated);
        // the ordinary chain keys landed as usual (the probe rides the same one-shot)
        Assert.Equal("zh_CN", store.Current.Language.Language);
    }

    [Fact]
    public void Import_PlaytimeCfg_Ouranks_The_In_Chain_Compat_Pair_Per_Member()
    {
        var paths = Paths;
        paths.DataRoot.CreateDirectory();
        (paths.DataRoot / "flint.cfg").WriteAllText("""
                                                                     [General]
                                                                     TotalPlayTime=111
                                                                     TotalPlayTimeMigrated=true
                                                                     """);
        // a half-populated playtime.cfg: only the total — the migrated flag falls back to
        // the in-chain branch, member by member
        (paths.DataRoot / "playtime.cfg").WriteAllText(
            "[General]\nTotalPlayTime=222\n");
        var store = NewStore();
        var (secrets, state) = NewSideStores();

        Assert.NotNull(FlintConfigImporter.Import(paths, store, secrets, state).ImportedIni);
        Assert.Equal(222, state.Current.Playtime.Total); // the file outranks the chain
        Assert.True(state.Current.Playtime.Migrated);    // the chain fills the absent member
    }

    // ------------------------------------------- automatic-java default derivation

    [Fact]
    public void Import_Derives_The_Automatic_Java_Pair_From_The_Imported_JavaPath()
    {
        // Application.cpp:768-770 registers the pair with the default "JavaPath is
        // empty", evaluated AFTER the INI loads — a cfg that pins JavaPath while silent on
        // the pair must land false, or the auto-java machinery would override the pinned
        // JVM on the next launch (AutoInstallJava / BaseInstance consult this value)
        var paths = Paths;
        paths.DataRoot.CreateDirectory();
        (paths.DataRoot / "prismlauncher.cfg").WriteAllText(
            "[General]\nJavaPath=/opt/pinned-jdk\n");
        var store = NewStore();
        var (secrets, state) = NewSideStores();

        Assert.NotNull(FlintConfigImporter.Import(paths, store, secrets, state).ImportedIni);
        Assert.False(store.Current.Java.AutomaticJavaSwitch);
        Assert.False(store.Current.Java.AutomaticJavaDownload);
        // the derived pair materialized into flint.json (the one-shot seal), from where the
        // launch path's UserLayer reads it
        var written = JsonDocument.Parse(ConfigPath.ReadAllText()).RootElement;
        Assert.False(written.GetProperty("java").GetProperty("automaticJavaSwitch").GetBoolean());
        Assert.False(written.GetProperty("java").GetProperty("automaticJavaDownload").GetBoolean());
    }

    [Fact]
    public void Import_Explicit_Automatic_Java_Keys_Win_Over_The_Derived_Default_Per_Key()
    {
        var paths = Paths;
        paths.DataRoot.CreateDirectory();
        (paths.DataRoot / "prismlauncher.cfg").WriteAllText("""
                                                                             [General]
                                                                             JavaPath=/opt/pinned-jdk
                                                                             AutomaticJavaSwitch=true
                                                                             """);
        var store = NewStore();
        var (secrets, state) = NewSideStores();

        Assert.NotNull(FlintConfigImporter.Import(paths, store, secrets, state).ImportedIni);
        Assert.True(store.Current.Java.AutomaticJavaSwitch);    // explicit — untouched
        Assert.False(store.Current.Java.AutomaticJavaDownload); // derived from the pin
    }

    [Fact]
    public void Import_Empty_JavaPath_Keeps_The_Automatic_Java_Pair_True()
    {
        var paths = Paths;
        paths.DataRoot.CreateDirectory();
        (paths.DataRoot / "prismlauncher.cfg").WriteAllText(
            "[General]\nLanguage=en\n");
        var store = NewStore();
        var (secrets, state) = NewSideStores();

        Assert.NotNull(FlintConfigImporter.Import(paths, store, secrets, state).ImportedIni);
        Assert.True(store.Current.Java.AutomaticJavaSwitch);
        Assert.True(store.Current.Java.AutomaticJavaDownload);
    }

    [Fact]
    public void Import_Is_Idle_Only_Without_A_Chain_File()
    {
        // no chain file at any root → Idle (neither imported nor unreadable); the projection
        // itself has no guard — a chain file always reports, it just fills nothing on a
        // re-run
        var paths = Paths;
        paths.DataRoot.CreateDirectory();
        var store = NewStore();
        var (secrets, state) = NewSideStores();
        var idle = FlintConfigImporter.Import(paths, store, secrets, state);
        Assert.Null(idle.ImportedIni);
        Assert.Null(idle.UnreadableIni);
        Assert.False(store.FilePath.ExistsFile());

        (paths.DataRoot / "flint.cfg").WriteAllText("[General]\nLanguage=x\n");
        Assert.NotNull(FlintConfigImporter.Import(paths, store, secrets, state).ImportedIni);
        Assert.NotNull(FlintConfigImporter.Import(paths, store, secrets, state).ImportedIni);
    }
}

// ------------------------------------------------------------- wire rerouting (API level)

[Collection("LauncherJars")]
public class SettingsSecretReroutingApiTests
{
    private static async Task<JsonElement> GetGroups(FlintApiHost host)
    {
        var response = await host.GetAsync("/api/v1/settings");
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement
            .GetProperty("groups").Clone();
    }

    [Fact]
    public async Task Secret_Trio_Round_Trips_Through_The_Store_Never_Through_FlintJson()
    {
        await using var host = await FlintApiHost.StartAsync();
        // the home layout (--home-dir): flint.json lives under <home>/config, the secret
        // store under <home>/data
        var configPath = Path.Combine(host.HomeDir, "config", "flint.json");
        var secretsPath = Path.Combine(host.HomeDir, "data", "secrets.json");

        var put = await host.PutAsync("/api/v1/settings", new
        {
            group = "proxy",
            values = new Dictionary<string, object>
            {
                ["ProxyType"] = "HTTP",
                ["ProxyUser"] = "corpus",
                ["ProxyPass"] = "hunter2",
            },
        });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        var apiPut = await host.PutAsync("/api/v1/settings", new
        {
            group = "api",
            values = new Dictionary<string, object>
            {
                ["ModrinthToken"] = "mr-token",
                ["FlameKeyOverride"] = "flame-key",
            },
        });
        Assert.Equal(HttpStatusCode.NoContent, apiPut.StatusCode);

        // GET serves the stored secrets (the wire contract is unchanged for the host)
        var groups = await GetGroups(host);
        Assert.Equal("hunter2", groups.GetProperty("proxy").GetProperty("ProxyPass").GetString());
        Assert.Equal("mr-token", groups.GetProperty("api").GetProperty("ModrinthToken").GetString());
        Assert.Equal("flame-key", groups.GetProperty("api").GetProperty("FlameKeyOverride").GetString());

        // the secrets live ONLY in secrets.json — flint.json carries no secret fields
        var flintJson = File.Exists(configPath) ? await File.ReadAllTextAsync(configPath) : "";
        Assert.DoesNotContain("hunter2", flintJson);
        Assert.DoesNotContain("mr-token", flintJson);
        Assert.DoesNotContain("flame-key", flintJson);
        var secretsJson = await File.ReadAllTextAsync(secretsPath);
        Assert.Contains("hunter2", secretsJson);
        Assert.Contains("mr-token", secretsJson);
        Assert.Contains("flame-key", secretsJson);
        // the non-secret proxy keys are the typed stack's business (flint.json)
        Assert.Contains("proxy", flintJson);

        // clearing: an empty PUT deletes the stored secret and GET reports ""
        var clear = await host.PutAsync("/api/v1/settings", new
        {
            group = "proxy",
            values = new Dictionary<string, object> { ["ProxyPass"] = "" },
        });
        Assert.Equal(HttpStatusCode.NoContent, clear.StatusCode);
        // the stored secret is gone from secrets.json too
        Assert.DoesNotContain("hunter2", await File.ReadAllTextAsync(secretsPath));
        groups = await GetGroups(host);
        Assert.Equal("", groups.GetProperty("proxy").GetProperty("ProxyPass").GetString());
    }

    [Fact]
    public async Task Wire_Shape_Is_Unchanged_Group_Keys_And_Values()
    {
        await using var host = await FlintApiHost.StartAsync();
        var groups = await GetGroups(host);

        // the nine groups survive with the same membership (the wire keys stay)
        var expectedGroups = new[]
        {
            "launcher", "minecraft", "java", "dirs", "language", "meta", "proxy", "api", "tools",
        };
        foreach (var group in expectedGroups)
        {
            Assert.True(groups.TryGetProperty(group, out _), $"missing group '{group}'");
        }

        // spot defaults (the record property defaults are the single source)
        Assert.Equal(1, groups.GetProperty("launcher").GetProperty("NumberOfManualRetries").GetInt32());
        Assert.True(groups.GetProperty("launcher").GetProperty("ConsoleOverflowStop").GetBoolean());
        Assert.Equal("Name", groups.GetProperty("launcher").GetProperty("InstSortMode").GetString());
        Assert.Equal(512, groups.GetProperty("minecraft").GetProperty("MinMemAlloc").GetInt32());
        Assert.Equal("", groups.GetProperty("java").GetProperty("JavaPath").GetString());
        Assert.True(groups.GetProperty("java").GetProperty("AutomaticJava").GetBoolean());
        // CentralModsDir is unregistered and gone from the wire
        Assert.False(groups.GetProperty("dirs").TryGetProperty("CentralModsDir", out _));
        Assert.Equal("", groups.GetProperty("proxy").GetProperty("ProxyPass").GetString());
        Assert.Equal(3, groups.GetProperty("api").GetProperty("PastebinType").GetInt32());

        // an unknown key is still rejected
        var unknown = await host.PutAsync("/api/v1/settings", new
        {
            group = "launcher",
            values = new Dictionary<string, object> { ["NotAKey"] = 1 },
        });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal("unknown_setting", JsonDocument.Parse(await unknown.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}
