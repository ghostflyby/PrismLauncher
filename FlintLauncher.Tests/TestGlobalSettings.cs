// SPDX-License-Identifier: GPL-3.0-only
// Shared test double for the Application-global settings object (the same registration set
// as the GlobalSettingsStub nested in InstanceTests, which mirrors Application.cpp's set)
// plus a playtime-settings double for the TotalPlayTime migration.
using FlintLauncher.Core.Settings;

namespace Flint.Tests;

internal sealed class TestGlobalSettings : SettingsObject
{
    private readonly Dictionary<string, object?> _values = new();

    public TestGlobalSettings()
    {
        Register("ShowGameTime", true);
        Register("RecordGameTime", true);
        Register("PreLoadCommand", "");
        Register("PreLaunchCommand", "");
        Register("WrapperCommand", "");
        Register("PostExitCommand", "");
        Register("ShowConsole", false);
        Register("AutoCloseConsole", false);
        Register("ShowConsoleOnError", false);
        Register("LogPrePostOutput", true);
        Register("ConsoleMaxLines", 100000);
        Register("ConsoleOverflowStop", false);
        Register("JavaPath", "java");
        Register("JvmArgs", "");
        Register("IgnoreJavaCompatibility", false);
        Register("JavaSignature", "");
        Register("JavaArchitecture", "");
        Register("JavaRealArchitecture", "");
        Register("JavaVersion", "");
        Register("JavaVendor", "");
        Register("LaunchMaximized", false);
        Register("MinecraftWinWidth", 854);
        Register("MinecraftWinHeight", 480);
        Register("MinMemAlloc", 512);
        Register("MaxMemAlloc", 4096);
        Register("PermGen", 192);
        Register("LowMemWarning", true);
        Register("UseNativeOpenAL", false);
        Register("CustomOpenALPath", "");
        Register("UseNativeGLFW", false);
        Register("CustomGLFWPath", "");
        Register("UseNativeSDL", false);
        Register("CustomSDLPath", "");
        Register("EnableFeralGamemode", false);
        Register("EnableMangoHud", false);
        Register("UseDiscreteGpu", false);
        Register("UseZink", false);
        Register("CloseAfterLaunch", false);
        Register("QuitAfterGameStop", false);
        Register("OnlineFixes", false);
        Register("Env", "{}");
        Register("AutomaticJavaSwitch", false);
    }

    private void Register(string id, object? def) => RegisterSetting(id, def);

    protected override void ChangeSetting(Setting setting, object? value) => _values[setting.Id] = value;

    protected override void ResetSetting(Setting setting) => _values.Remove(setting.Id);

    internal override object? RetrieveValue(Setting setting) => _values.GetValueOrDefault(setting.Id);

    public override void SuspendSave() { }

    public override void ResumeSave() { }
}

/// <summary>Stand-in for APPLICATION->playtimeSettings() (TotalPlayTime keys).</summary>
internal sealed class TestPlaytimeSettings : SettingsObject
{
    private readonly Dictionary<string, object?> _values = new();

    public TestPlaytimeSettings()
    {
        RegisterSetting("TotalPlayTime", 0L);
        RegisterSetting("TotalPlayTimeMigrated", false);
    }

    public long TotalPlayTime => Variant.AsLong(_values.GetValueOrDefault("TotalPlayTime"));

    public bool TotalPlayTimeMigrated => Variant.AsBool(_values.GetValueOrDefault("TotalPlayTimeMigrated"));

    protected override void ChangeSetting(Setting setting, object? value) => _values[setting.Id] = value;

    protected override void ResetSetting(Setting setting) => _values.Remove(setting.Id);

    internal override object? RetrieveValue(Setting setting) => _values.GetValueOrDefault(setting.Id);

    public override void SuspendSave() { }

    public override void ResumeSave() { }
}
