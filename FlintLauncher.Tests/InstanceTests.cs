// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Tests for the BaseInstance/MinecraftInstance ports over the typed layer stack:
// instance.json (settings+identity) and instance-state.json (the runtime time trio) —
// everything synthetic (temp dirs). The dual-track guarantee is exercised directly: a
// Prism-format instance.cfg is READ-ONLY on load and only converts on the first write.

using FlintLauncher.Core.Config;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Settings;
using TruePath;
using Xunit;

namespace FlintLauncher.Tests;

public class InstanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"flint-inst-{Guid.NewGuid():N}");
    private readonly TestGlobalSettings _global = new();
    private readonly string _cfgPath;

    public InstanceTests()
    {
        Directory.CreateDirectory(_root);
        _cfgPath = Path.Combine(_root, "instance.cfg");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException) { }
    }

    private string JsonPath => Path.Combine(_root, "instance.json");
    private string StatePath => Path.Combine(_root, "instance-state.json");

    private InstanceJsonSettingsObject Settings() =>
        new(new AbsolutePath(_root), TestInstanceEnvironment.Default(_global.Store));

    private MinecraftInstance CreateInstance() => new(Settings(), new AbsolutePath(_root));

    [Fact]
    public void Defaults_MatchUpstream()
    {
        var instance = CreateInstance();
        Assert.Equal("Unnamed Instance", instance.Name());
        Assert.Equal("default", instance.IconKey());
        Assert.Equal("", instance.Notes());
        // uuid: QUuid::Id128 form = 32 hex digits
        Assert.Equal(32, instance.Uuid.Length);
        Assert.True(instance.Uuid.All(char.IsAsciiHexDigit));
        Assert.Equal(Path.GetFileName(_root), instance.Id());
    }

    [Fact]
    public void Identity_PersistsToInstanceJson()
    {
        var instance = CreateInstance();
        instance.SetName("My Test");
        instance.SetIconKey("custom-icon");
        instance.SetNotes("some notes\nwith a newline");

        // the write landed in instance.json (the INI write path is gone)
        Assert.True(File.Exists(JsonPath));
        Assert.False(File.Exists(_cfgPath)); // nothing ever wrote an instance.cfg
        Assert.Contains("My Test", File.ReadAllText(JsonPath));

        var reloaded = new MinecraftInstance(Settings(), new AbsolutePath(_root));
        Assert.Equal("My Test", reloaded.Name());
        Assert.Equal("custom-icon", reloaded.IconKey());
        Assert.Equal("some notes\nwith a newline", reloaded.Notes());
    }

    [Fact]
    public void InstanceType_DefaultsToOneSix()
    {
        var instance = CreateInstance();
        // loadSpecificSettings runs lazily through Settings(); the record default IS OneSix
        // — nothing force-writes the type anymore (a non-OneSix type never constructs a
        // MinecraftInstance; InstanceList yields NullInstance instead)
        _ = instance.Settings;
        Assert.Equal("OneSix", instance.InstanceType());
        // the only thing a fresh construction persists is the generated uuid identity —
        // no InstanceType write, no INI
        Assert.DoesNotContain("InstanceType", File.ReadAllText(JsonPath));
        Assert.False(File.Exists(_cfgPath));
    }

    [Fact]
    public void CommandOverrides_ResolveThroughTheFold()
    {
        _global.StubSet("PreLaunchCommand", "echo global");
        var instance = CreateInstance();

        // instance layer unset -> the user layer's value (the fold)
        Assert.Equal("echo global", instance.GetPreLaunchCommand());

        // instance-layer write -> the override (the gate DEGENERATED to member-set:
        // there is no separate OverrideCommands write; the derived gate reads true)
        instance.Settings.Set("PreLaunchCommand", "echo local");
        Assert.Equal("echo local", instance.GetPreLaunchCommand());
        Assert.True(Variant.AsBool(instance.Settings.Get("OverrideCommands")));

        // persisted to instance.json and reloaded
        var reloaded = new MinecraftInstance(Settings(), new AbsolutePath(_root));
        Assert.Equal("echo local", reloaded.GetPreLaunchCommand());

        // reset -> member null -> inherits the user layer again
        reloaded.Settings.Reset("PreLaunchCommand");
        Assert.Equal("echo global", reloaded.GetPreLaunchCommand());
        Assert.False(Variant.AsBool(reloaded.Settings.Get("OverrideCommands")));
    }

    [Fact]
    public void MemoryOverride_InstanceLayerWrite()
    {
        var instance = CreateInstance();
        // unset -> the user layer's seeded value
        Assert.Equal(4096, Variant.AsInt(instance.Settings.Get("MaxMemAlloc")));
        Assert.False(Variant.AsBool(instance.Settings.Get("OverrideMemory")));

        instance.Settings.Set("MaxMemAlloc", 8192);
        Assert.Equal(8192, Variant.AsInt(instance.Settings.Get("MaxMemAlloc")));
        // the derived gate: a set family member IS the override
        Assert.True(Variant.AsBool(instance.Settings.Get("OverrideMemory")));
        Assert.Contains("8192", File.ReadAllText(JsonPath));

        // gate-write false clears the family back to inheritance
        instance.Settings.Set("OverrideMemory", false);
        Assert.Equal(4096, Variant.AsInt(instance.Settings.Get("MaxMemAlloc")));
        Assert.False(Variant.AsBool(instance.Settings.Get("OverrideMemory")));
    }

    [Fact]
    public void GameRoot_PrefersMinecraftDir_UsesDotMinecraftFallback()
    {
        var instance = CreateInstance();
        // nothing exists yet -> planned "minecraft" layout
        Assert.Equal(FileSystemPathCombine(_root, "minecraft"), instance.GameRoot.Value);

        // legacy layout: only .minecraft exists
        Directory.CreateDirectory(Path.Combine(_root, ".minecraft"));
        Assert.Equal(FileSystemPathCombine(_root, ".minecraft"), instance.GameRoot.Value);
    }

    [Fact]
    public void PathAccessors_MatchUpstreamComposition()
    {
        var instance = CreateInstance();
        var gameRoot = instance.GameRoot;

        Assert.Equal(FileSystemPathCombine(gameRoot.Value, "mods"), instance.ModsRoot.Value);
        Assert.Equal(FileSystemPathCombine(gameRoot.Value, "coremods"), instance.CoreModsDir.Value);
        Assert.Equal(FileSystemPathCombine(gameRoot.Value, "resourcepacks"), instance.ResourcePacksDir.Value);
        Assert.Equal(FileSystemPathCombine(gameRoot.Value, "shaderpacks"), instance.ShaderPacksDir.Value);
        Assert.Equal(FileSystemPathCombine(gameRoot.Value, "bin"), instance.BinRoot().Value);
        // these live under the instance root, not gameRoot
        Assert.Equal(FileSystemPathCombine(_root, "jarmods"), instance.JarModsDir.Value);
        Assert.Equal(FileSystemPathCombine(_root, "natives"), instance.GetNativePath().Value);
        Assert.Equal(FileSystemPathCombine(_root, "libraries"), instance.GetLocalLibraryPath().Value);
    }

    [Fact]
    public void PackProfile_WiresUpComponents()
    {
        File.WriteAllText(
            Path.Combine(_root, "mmc-pack.json"),
            """
            {
                "formatVersion": 1,
                "components": [
                    {
                        "important": true,
                        "uid": "net.minecraft",
                        "version": "1.20.1"
                    },
                    {
                        "uid": "org.quiltmc.foo",
                        "version": "1.0.0"
                    }
                ]
            }
            """);

        var instance = CreateInstance();
        var profile = instance.GetPackProfile();
        profile.Load();

        var uids = profile.Components.Select(c => c.Uid).ToList();
        Assert.Contains("net.minecraft", uids);
        Assert.Contains("org.quiltmc.foo", uids);
    }

    [Fact]
    public void Playtime_AccumulatesIntoInstanceStateFile()
    {
        var instance = CreateInstance();
        Assert.Equal(0, instance.TotalTimePlayed());
        // construction persisted the fresh identity (uuid) — which converts the whole
        // instance, state side included (zeros)

        instance.SetMinecraftRunning(true);
        Assert.Equal(0, instance.TotalTimePlayed()); // reset base while running
        Assert.True(instance.LastLaunch() > 0);
        Assert.True(File.Exists(StatePath)); // the launch stamped instance-state.json

        instance.SetMinecraftRunning(false);
        // ~0 seconds elapsed, but the state file must carry the bookkeeping
        Assert.True(instance.TotalTimePlayed() >= 0);
        Assert.True(Variant.AsLong(instance.Settings.Get("lastTimePlayed")) >= 0);

        // the trio survives a reload (instance-state.json, not instance.json)
        var reloaded = new MinecraftInstance(Settings(), new AbsolutePath(_root));
        Assert.Equal(instance.TotalTimePlayed(), reloaded.TotalTimePlayed());
        Assert.Equal(instance.LastLaunch(), reloaded.LastLaunch());
    }

    [Fact]
    public void Playtime_SkippedWhenRecordingDisabled()
    {
        _global.StubSet("RecordGameTime", false);
        var instance = CreateInstance();
        instance.SetMinecraftRunning(true);
        instance.SetMinecraftRunning(false);
        // RecordGameTime=false: no bookkeeping at all (upstream returns early) — the state
        // file (materialized by the identity write) still carries zeros
        Assert.Equal(0, instance.LastLaunch());
        Assert.Equal(0, instance.TotalTimePlayed());
        Assert.Equal(0, Variant.AsLong(instance.Settings.Get("lastTimePlayed")));
    }

    [Fact]
    public void ManagedPack_SettersAndAutomaticJavaReset()
    {
        var instance = CreateInstance();
        instance.Settings.Set("AutomaticJava", true);
        // the gate is the member — an explicit JavaPath at the instance layer
        instance.Settings.Set("JavaPath", "/opt/pinned-jdk");
        Assert.True(Variant.AsBool(instance.Settings.Get("OverrideJavaLocation")));
        _global.StubSet("AutomaticJavaSwitch", true);

        instance.SetManagedPack("modrinth", "id1", "Pack", "vid", "1.0.0");

        Assert.True(instance.IsManagedPack());
        Assert.Equal("modrinth", instance.GetManagedPackType());
        Assert.Equal("id1", instance.GetManagedPackId());
        // managed-pack adoption cleared the instance java-location family: JavaPath resolves
        // through the fold to the user layer again
        Assert.False(Variant.AsBool(instance.Settings.Get("OverrideJavaLocation")));
        Assert.Equal("java", Variant.AsString(instance.Settings.Get("JavaPath")));
    }

    [Fact]
    public void Legacy_Cfg_Converts_Eagerly_The_Cfg_Stays_Unwritten()
    {
        // a Prism-format instance directory (instance.cfg, no instance.json)
        File.WriteAllLines(_cfgPath,
        [
            "[General]",
            "ConfigVersion=1.3",
            "name=Prism Inst",
            "iconKey=grass",
            "uuid=0123456789abcdef0123456789abcdef",
            "InstanceType=OneSix",
            "OverrideMemory=true",
            "MaxMemAlloc=2048",
            "lastLaunchTime=1700000000000",
            "totalTimePlayed=3600",
            "lastTimePlayed=120",
        ]);

        // the migration pass materializes BOTH halves before the store reads anything (the
        // trio's seed lands in instance-state.json, the gate-folded payload in instance.json)
        TestLegacyConversion.ConvertOne(new AbsolutePath(_root));
        Assert.True(File.Exists(JsonPath));
        Assert.True(File.Exists(StatePath));

        var instance = new MinecraftInstance(Settings(), new AbsolutePath(_root));
        // reads come from the converted layer (gate honored: gate=true keeps the value)
        Assert.Equal("Prism Inst", instance.Name());
        Assert.Equal("grass", instance.IconKey());
        Assert.Equal("0123456789abcdef0123456789abcdef", instance.Uuid);
        Assert.Equal(2048, Variant.AsInt(instance.Settings.Get("MaxMemAlloc")));
        Assert.True(Variant.AsBool(instance.Settings.Get("OverrideMemory")));
        Assert.Equal(1700000000000, instance.LastLaunch());
        Assert.Equal(3600, instance.TotalTimePlayed());
        // the cfg is never rewritten or deleted (read-compat), but instance.json wins
        Assert.Contains("name=Prism Inst", File.ReadAllText(_cfgPath));

        instance.SetNotes("converted");
        Assert.Contains("converted", File.ReadAllText(JsonPath));
        var reloaded = new MinecraftInstance(Settings(), new AbsolutePath(_root));
        Assert.Equal("converted", reloaded.Notes());
        Assert.Equal("0123456789abcdef0123456789abcdef", reloaded.Uuid);
        Assert.Equal(2048, Variant.AsInt(reloaded.Settings.Get("MaxMemAlloc")));
        Assert.Equal(3600, reloaded.TotalTimePlayed());
    }

    [Fact]
    public void PrismFormat_GateFalse_DropsIntoInheritance()
    {
        // gate=false values are treated as unset on import — the user layer's value wins
        File.WriteAllLines(_cfgPath,
        [
            "[General]",
            "name=Gate Inst",
            "OverrideMemory=false",
            "MaxMemAlloc=2048",
        ]);

        TestLegacyConversion.ConvertOne(new AbsolutePath(_root));
        var instance = new MinecraftInstance(Settings(), new AbsolutePath(_root));
        Assert.False(Variant.AsBool(instance.Settings.Get("OverrideMemory")));
        Assert.Equal(4096, Variant.AsInt(instance.Settings.Get("MaxMemAlloc"))); // user layer
    }

    [Fact]
    public void PrismFormat_HandEditedGate_QtTruthyForms_KeepTheInstanceValue()
    {
        // QVariant(QString).toBool() reads every string but ""/"0"/"false" as true, so a
        // hand-edited "True" gate must import like Qt's own read did. Under the strict
        // "true"/"1" match the gate read false and the instance value folded away to the
        // user layer (4096) — the override silently vanished on every load.
        File.WriteAllLines(_cfgPath,
        [
            "[General]",
            "name=Case Gate",
            "OverrideMemory=True",
            "MaxMemAlloc=2048",
        ]);

        TestLegacyConversion.ConvertOne(new AbsolutePath(_root));
        var instance = new MinecraftInstance(Settings(), new AbsolutePath(_root));
        Assert.True(Variant.AsBool(instance.Settings.Get("OverrideMemory")));
        Assert.Equal(2048, Variant.AsInt(instance.Settings.Get("MaxMemAlloc"))); // kept, not the user layer's 4096
    }

    [Fact]
    public void PrismFormat_QSettingsKeyOrder_ValuesBeforeGates_StillImport()
    {
        // QSettings writes instance.cfg keys in ORDINAL order, so the member keys (JavaPath,
        // JvmArgs, LowMemWarning, MaxMemAlloc, MinMemAlloc) all precede their Override* gate
        // keys. The gate must be decided by the Override* keys only — a member value like
        // "8192" (AsBool=false) must never disable its own family.
        File.WriteAllLines(_cfgPath,
        [
            "[General]",
            "name=Ordered Inst",
            "JavaPath=/opt/jdk-21/bin/java",
            "JvmArgs=-Xss1m",
            "LowMemWarning=false",
            "MaxMemAlloc=8192",
            "MinMemAlloc=1024",
            "OverrideJavaArgs=true",
            "OverrideJavaLocation=true",
            "OverrideMemory=true",
        ]);

        TestLegacyConversion.ConvertOne(new AbsolutePath(_root));
        var instance = new MinecraftInstance(Settings(), new AbsolutePath(_root));
        Assert.Equal("/opt/jdk-21/bin/java", Variant.AsString(instance.Settings.Get("JavaPath")));
        Assert.Equal("-Xss1m", Variant.AsString(instance.Settings.Get("JvmArgs")));
        Assert.Equal(8192, Variant.AsInt(instance.Settings.Get("MaxMemAlloc")));
        Assert.Equal(1024, Variant.AsInt(instance.Settings.Get("MinMemAlloc")));
        Assert.False(Variant.AsBool(instance.Settings.Get("LowMemWarning")));
        Assert.True(Variant.AsBool(instance.Settings.Get("OverrideMemory")));
        Assert.True(Variant.AsBool(instance.Settings.Get("OverrideJavaLocation")));
        Assert.True(Variant.AsBool(instance.Settings.Get("OverrideJavaArgs")));
    }

    [Fact]
    public void PrismFormat_ValueKeyCannotPolluteAnExplicitFalseGate()
    {
        // the reverse direction: a member key must never flip a gate the cfg set to false —
        // a Prism instance that explicitly does NOT override memory must not load as one
        File.WriteAllLines(_cfgPath,
        [
            "[General]",
            "name=No Override",
            "LowMemWarning=true",
            "OverrideMemory=false",
        ]);

        TestLegacyConversion.ConvertOne(new AbsolutePath(_root));
        var instance = new MinecraftInstance(Settings(), new AbsolutePath(_root));
        Assert.False(Variant.AsBool(instance.Settings.Get("OverrideMemory")));
        // the orphaned value was dropped (gate=false), the seeded user-layer default applies
        Assert.True(Variant.AsBool(instance.Settings.Get("LowMemWarning")));
    }

    [Fact]
    public void PrismFormat_AbsentGate_DropsIntoInheritance()
    {
        // a layered value whose gate key is absent resolves like the C++ OverrideSetting with
        // its registered default (false): through to the user layer
        File.WriteAllLines(_cfgPath,
        [
            "[General]",
            "name=No Gate Key",
            "MaxMemAlloc=8192",
        ]);

        TestLegacyConversion.ConvertOne(new AbsolutePath(_root));
        var instance = new MinecraftInstance(Settings(), new AbsolutePath(_root));
        Assert.False(Variant.AsBool(instance.Settings.Get("OverrideMemory")));
        Assert.Equal(4096, Variant.AsInt(instance.Settings.Get("MaxMemAlloc"))); // user layer
    }

    [Fact]
    public void LinkedInstances_RoundTripThroughTheTypedLayer()
    {
        var instance = CreateInstance();
        Assert.Empty(instance.GetLinkedInstances());

        instance.AddLinkedInstanceId("11111111111111111111111111111111");
        instance.AddLinkedInstanceId("22222222222222222222222222222222");
        Assert.Equal(2, instance.GetLinkedInstances().Count);
        Assert.True(instance.IsLinkedToInstanceId("22222222222222222222222222222222"));
        Assert.Contains("22222222222222222222222222222222", File.ReadAllText(JsonPath));

        var reloaded = new MinecraftInstance(Settings(), new AbsolutePath(_root));
        Assert.Equal(2, reloaded.GetLinkedInstances().Count);
        Assert.True(reloaded.RemoveLinkedInstanceId("11111111111111111111111111111111"));
        Assert.Single(reloaded.GetLinkedInstances());
    }

    [Fact]
    public void NullInstance_IsBrokenByDefault()
    {
        var instance = new NullInstance(Settings(), new AbsolutePath(_root));
        Assert.True(instance.HasVersionBroken);
        Assert.False(instance.CanLaunch());
    }

    private static string FileSystemPathCombine(string a, string b) =>
        Path.Combine(a, b).TrimEnd('/');
}
