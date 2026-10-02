// SPDX-License-Identifier: GPL-3.0-only
// Tests for the typed instance settings stack (D4 — the SettingsObject shape over
// flint.json -> instance.json): C7 fold resolution, the derived gates, the C7 passthrough
// pair, persistence through instance.json and the state-trio routing. The former
// INISettingsObject tests retired with the INI write path (CONFIG C5).
using FlintLauncher.Core.Config;
using FlintLauncher.Core.Settings;
using FlintLauncher.Web;
using Xunit;

namespace FlintLauncher.Tests;

public class SettingsObjectTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"flint-settings-{Guid.NewGuid():N}");

    public SettingsObjectTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private string JsonPath => Path.Combine(_root, "instance.json");

    private InstanceJsonSettingsObject Settings(FlintConfigStore? user = null) =>
        new(_root, new InstanceEnvironment(user, FlintJsonContext.Default.InstanceConfigFile,
            FlintJsonContext.Default.InstanceStateFile));

    /// <summary>A user layer with one non-default member (the fold's lower layer).</summary>
    private FlintConfigStore UserWith(string key, object? value)
    {
        var user = Flint.Tests.TestGlobalSettings.CreateStore();
        user.Set(key, value);
        return user;
    }

    [Fact]
    public void RegisterSetGetReset_PersistsToInstanceJson()
    {
        var settings = Settings();
        settings.RegisterSetting("MaxMemAlloc", 4096);

        Assert.Equal(4096, Variant.AsInt(settings.Get("MaxMemAlloc"))); // user-layer default

        Assert.True(settings.Set("MaxMemAlloc", 8192));
        Assert.Equal(8192, Variant.AsInt(settings.Get("MaxMemAlloc")));
        Assert.Contains("8192", File.ReadAllText(JsonPath)); // instance.json, not INI
        Assert.False(File.Exists(Path.Combine(_root, "instance.cfg")));

        settings.Reset("MaxMemAlloc");
        Assert.Equal(4096, Variant.AsInt(settings.Get("MaxMemAlloc")));
    }

    [Fact]
    public void Fold_UnsetMember_InheritsTheUserLayer()
    {
        var user = UserWith("MaxMemAlloc", 6144);
        var settings = Settings(user);
        settings.RegisterSetting("MaxMemAlloc", 4096);

        // instance layer unset -> the user layer's live value
        Assert.Equal(6144, Variant.AsInt(settings.Get("MaxMemAlloc")));

        // the user layer changing under the instance reflects immediately (live store)
        user.Set("MaxMemAlloc", 7168);
        Assert.Equal(7168, Variant.AsInt(settings.Get("MaxMemAlloc")));

        // instance layer set -> wins over the user layer
        settings.Set("MaxMemAlloc", 1024);
        Assert.Equal(1024, Variant.AsInt(settings.Get("MaxMemAlloc")));

        // reset -> member null -> inherits again
        settings.Reset("MaxMemAlloc");
        Assert.Equal(7168, Variant.AsInt(settings.Get("MaxMemAlloc")));
    }

    [Fact]
    public void Gates_AreDerivedFromSetMembers()
    {
        var settings = Settings();
        settings.RegisterSetting("OverrideMemory", false);
        settings.RegisterSetting("MaxMemAlloc", 4096);

        Assert.False(Variant.AsBool(settings.Get("OverrideMemory")));

        // the old GUI/task pattern (gate write then member write): the gate write is a no-op,
        // the member write IS the override
        settings.Set("OverrideMemory", true);
        Assert.False(Variant.AsBool(settings.Get("OverrideMemory")));
        settings.Set("MaxMemAlloc", 1024);
        Assert.True(Variant.AsBool(settings.Get("OverrideMemory")));

        // gate-write false clears the family back to inheritance
        settings.Set("OverrideMemory", false);
        Assert.False(Variant.AsBool(settings.Get("OverrideMemory")));
        Assert.Equal(4096, Variant.AsInt(settings.Get("MaxMemAlloc")));
    }

    [Fact]
    public void Passthrough_Pair_ReadsTheUserLayer_AndDropsInstanceWrites()
    {
        var user = UserWith("ConsoleMaxLines", 5000);
        var settings = Settings(user);
        settings.RegisterSetting("ConsoleMaxLines", 100000);
        settings.RegisterSetting("ConsoleOverflowStop", true);

        // C7: the pair is non-layered — the instance layer cannot carry it
        Assert.Equal(5000, Variant.AsInt(settings.Get("ConsoleMaxLines")));
        settings.Set("ConsoleMaxLines", 77);
        Assert.Equal(5000, Variant.AsInt(settings.Get("ConsoleMaxLines")));
    }

    [Fact]
    public void ChangeEvents_Fire()
    {
        var settings = Settings();
        settings.RegisterSetting("name", "Unnamed Instance");

        Setting? changed = null;
        object? newValue = null;
        settings.SettingChanged += (s, v) => { changed = s; newValue = v; };
        Setting? reset = null;
        settings.SettingReset += s => reset = s;

        settings.Set("name", "Renamed");
        Assert.Equal("name", changed?.Id);
        Assert.Equal("Renamed", newValue);

        settings.Reset("name");
        Assert.Equal("name", reset?.Id);
    }

    [Fact]
    public void StateTrio_RoutesToInstanceStateFile()
    {
        var settings = Settings();
        settings.RegisterSetting("lastLaunchTime", 0L);
        settings.RegisterSetting("lastTimePlayed", 0L);
        settings.RegisterSetting("totalTimePlayed", 0L);

        Assert.Equal(0L, Variant.AsLong(settings.Get("lastLaunchTime")));
        settings.Set("lastLaunchTime", 1700000000000L);
        settings.Set("totalTimePlayed", 3600L);
        settings.Set("lastTimePlayed", 120L);

        // the trio NEVER lands in instance.json — it is instance-state.json (D4)
        Assert.False(File.Exists(JsonPath));
        Assert.True(File.Exists(Path.Combine(_root, "instance-state.json")));

        // a fresh adapter reloads the trio from the state file
        var reloaded = Settings();
        reloaded.RegisterSetting("lastLaunchTime", 0L);
        reloaded.RegisterSetting("lastTimePlayed", 0L);
        reloaded.RegisterSetting("totalTimePlayed", 0L);
        Assert.Equal(1700000000000L, Variant.AsLong(reloaded.Get("lastLaunchTime")));
        Assert.Equal(3600L, Variant.AsLong(reloaded.Get("totalTimePlayed")));
        Assert.Equal(120L, Variant.AsLong(reloaded.Get("lastTimePlayed")));
    }

    [Fact]
    public void DualTrack_CfgImport_ConvertsOnFirstWrite()
    {
        File.WriteAllLines(Path.Combine(_root, "instance.cfg"),
        [
            "[General]",
            "name=Legacy",
            "uuid=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "OverrideWindow=true",
            "MinecraftWinWidth=1280",
        ]);

        // read-only: the import resolves in memory, nothing materializes
        var settings = Settings();
        settings.RegisterSetting("name", "Unnamed Instance");
        settings.RegisterSetting("notes", "");
        Assert.Equal("Legacy", Variant.AsString(settings.Get("name")));
        Assert.False(File.Exists(JsonPath));

        // the first write converts (uuid carried over verbatim — the D2 addressing chain)
        settings.Set("notes", "now flint");
        Assert.True(File.Exists(JsonPath));
        Assert.Contains("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", File.ReadAllText(JsonPath));
        Assert.Contains("1280", File.ReadAllText(JsonPath));
    }
}
