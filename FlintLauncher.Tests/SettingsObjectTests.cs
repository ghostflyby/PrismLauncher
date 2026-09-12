// SPDX-License-Identifier: GPL-3.0-only
// Tests for the SettingsObject/Setting/OverrideSetting framework (port of the MultiMC
// settings semantics exercised by the launcher itself).
using FlintLauncher.Core.Settings;
using Xunit;

namespace FlintLauncher.Tests;

public class SettingsObjectTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"flint-settings-{Guid.NewGuid():N}.ini");

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void RegisterSetGetReset()
    {
        var settings = new IniSettingsObject(_path);
        settings.RegisterSetting(["MaxMemAlloc", "MaxMemoryAlloc"], 4096);
        settings.RegisterSetting("Language", "en_US");

        Assert.Equal(4096, Variant.AsInt(settings.Get("MaxMemAlloc")));
        Assert.Equal("en_US", Variant.AsString(settings.Get("Language")));

        Assert.True(settings.Set("MaxMemAlloc", 8192));
        Assert.Equal(8192, Variant.AsInt(settings.Get("MaxMemAlloc")));
        Assert.Equal("8192", File.ReadAllText(_path).Split('\n').Single(l => l.StartsWith("MaxMemAlloc=")).Split('=')[1]);

        settings.Reset("MaxMemAlloc");
        Assert.Equal(4096, Variant.AsInt(settings.Get("MaxMemAlloc")));
        Assert.DoesNotContain(File.ReadAllText(_path).Split('\n'), l => l.StartsWith("MaxMemAlloc="));
    }

    [Fact]
    public void Synonyms_ReadFirst_WritePrimary_RemoveRest()
    {
        // a config with the legacy synonym present
        File.WriteAllLines(_path, ["[General]", "CurrentJavaPath=/old/path"]);
        var settings = new IniSettingsObject(_path);
        settings.RegisterSetting(["JavaPath", "CurrentJavaPath"], "java");

        Assert.Equal("/old/path", Variant.AsString(settings.Get("JavaPath")));

        settings.Set("JavaPath", "/new/path");
        var text = File.ReadAllText(_path);
        Assert.Contains("JavaPath=/new/path", text);
        Assert.DoesNotContain("CurrentJavaPath", text);
    }

    [Fact]
    public void OverrideSetting_GateControls_Resolution()
    {
        var settings = new IniSettingsObject(_path);
        var global = new SettingsStub();
        var globalMaxMem = global.RegisterSetting("MaxMemAlloc", 4096);

        var gate = settings.RegisterSetting("OverrideMemory", false);
        settings.RegisterOverride(globalMaxMem!, gate!);

        // gate off -> falls back to the overridden setting's live value
        Assert.Equal(4096, Variant.AsInt(settings.Get("MaxMemAlloc")));

        global.Set("MaxMemAlloc", 6144);
        Assert.Equal(6144, Variant.AsInt(settings.Get("MaxMemAlloc")));

        // gate on -> local value (default = the overridden value at registration time)
        settings.Set("OverrideMemory", true);
        Assert.Equal(6144, Variant.AsInt(settings.Get("MaxMemAlloc")));
        settings.Set("MaxMemAlloc", 1024);
        Assert.Equal(1024, Variant.AsInt(settings.Get("MaxMemAlloc")));

        // reset returns to the overridden value while gated
        settings.Reset("MaxMemAlloc");
        Assert.Equal(6144, Variant.AsInt(settings.Get("MaxMemAlloc")));
    }

    [Fact]
    public void PassthroughSetting_ForwardsWrites()
    {
        var other = new SettingsStub();
        var otherSetting = other.RegisterSetting("JavaPath", "java");

        var settings = new IniSettingsObject(_path);
        var gate = settings.RegisterSetting("OverrideJavaLocation", false);
        var passthrough = settings.RegisterPassthrough(otherSetting!, gate!);

        // gate off: reads go through, writes only forward to the other object
        settings.Set("JavaPath", "/usr/bin/java");
        Assert.Equal("/usr/bin/java", Variant.AsString(other.Get("JavaPath")));

        // gate on: writes also land in the local storage
        settings.Set("OverrideJavaLocation", true);
        settings.Set("JavaPath", "/custom/java");
        Assert.Contains("JavaPath=/custom/java", File.ReadAllText(_path));
    }

    [Fact]
    public void SuspendSave_DefersWrites()
    {
        var settings = new IniSettingsObject(_path);
        settings.RegisterSetting("Language", "en_US");
        settings.Set("Language", "zh");
        Assert.True(File.Exists(_path));

        var before = File.ReadAllText(_path);
        settings.SuspendSave();
        settings.Set("Language", "ja");
        Assert.Equal(before, File.ReadAllText(_path));
        settings.ResumeSave();
        Assert.Contains("Language=ja", File.ReadAllText(_path));
    }

    [Fact]
    public void ChangeEvents_Fire()
    {
        var settings = new IniSettingsObject(_path);
        settings.RegisterSetting("Language", "en_US");

        Setting? changed = null;
        object? newValue = null;
        settings.SettingChanged += (s, v) => { changed = s; newValue = v; };
        Setting? reset = null;
        settings.SettingReset += s => reset = s;

        settings.Set("Language", "zh");
        Assert.Equal("Language", changed?.Id);
        Assert.Equal("zh", newValue);

        settings.Reset("Language");
        Assert.Equal("Language", reset?.Id);
    }

    private sealed class SettingsStub : SettingsObject
    {
        private readonly Dictionary<string, object?> _values = new();

        protected override void ChangeSetting(Setting setting, object? value) => _values[setting.Id] = value;

        protected override void ResetSetting(Setting setting) => _values.Remove(setting.Id);

        internal override object? RetrieveValue(Setting setting) => _values.GetValueOrDefault(setting.Id);

        public override void SuspendSave() { }

        public override void ResumeSave() { }
    }
}
