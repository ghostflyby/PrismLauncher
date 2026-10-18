// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/settings/SettingsObject.{h,cpp} (the registration/Get/Set surface).
//
// Since CONFIG C5/D4 the only production backing store is the typed layer stack
// (Core.Config.InstanceJsonSettingsObject: flint.json -> instance.json). The C++
// RegisterOverride/RegisterPassthrough machinery (OverrideSetting/PassthroughSetting) is
// retired with it — C7 degenerates the gates to "instance-layer member is non-null".

namespace FlintLauncher.Core.Settings;

public abstract class SettingsObject
{
    private readonly Dictionary<string, Setting> _settings = new(StringComparer.Ordinal);

    public event Action<Setting, object?>? SettingChanged;

    public event Action<Setting>? SettingReset;

    protected bool SuspendSaveFlag { get; set; }

    protected bool DoSaveFlag { get; set; }

    public Setting? RegisterSetting(IReadOnlyList<string> synonyms, object? defVal = null)
    {
        if (synonyms.Count == 0 || Contains(synonyms[0]))
        {
            return null;
        }

        var setting = new Setting(synonyms, defVal) { Storage = this };
        _settings[setting.Id] = setting;
        OnSettingRegistered(setting);
        return setting;
    }

    public Setting? RegisterSetting(string id, object? defVal = null) => RegisterSetting([id], defVal);

    public Setting? GetSetting(string id) => _settings.GetValueOrDefault(id);

    public Setting? GetOrRegisterSetting(string id, object? defVal = null) =>
        Contains(id) ? GetSetting(id) : RegisterSetting(id, defVal);

    public object? Get(string id) => GetSetting(id)?.Get();

    public bool Set(string id, object? value)
    {
        var setting = GetSetting(id);
        if (setting is null)
        {
            return false;
        }

        setting.Set(value);
        return true;
    }

    public void Reset(string id) => GetSetting(id)?.Reset();

    public bool Contains(string id) => _settings.ContainsKey(id);

    public virtual bool Reload()
    {
        foreach (var setting in _settings.Values)
        {
            setting.Set(setting.Get());
        }

        return true;
    }

    private void OnSettingRegistered(Setting setting)
    {
        // mirror SettingsObject::connectSignals
        setting.SettingChangedInternal += ChangeSetting;
        setting.SettingChangedInternal += (s, v) => SettingChanged?.Invoke(s, v);
        setting.SettingResetInternal += ResetSetting;
        setting.SettingResetInternal += s => SettingReset?.Invoke(s);
    }

    // Plumbing replacing Qt signals between Setting and this object.
    internal void EmitSettingChanged(Setting setting, object? value) => SettingChanged?.Invoke(setting, value);

    protected abstract void ChangeSetting(Setting setting, object? value);
    protected abstract void ResetSetting(Setting setting);

    internal abstract object? RetrieveValue(Setting setting);

    public abstract void SuspendSave();

    public abstract void ResumeSave();
}
