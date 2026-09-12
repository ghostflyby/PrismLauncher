// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/settings/Setting.{h,cpp}. QVariant becomes object? with Variant helpers.
using FlintLauncher.Core.Settings;

namespace FlintLauncher.Core.Settings;

public class Setting
{
    private readonly IReadOnlyList<string> _synonyms;

    public Setting(IReadOnlyList<string> synonyms, object? defVal = null)
    {
        _synonyms = synonyms;
        Default = defVal;
        Storage = null;
    }

    /// <summary>All config-file keys this setting answers to, in order of preference.</summary>
    public IReadOnlyList<string> ConfigKeys => _synonyms;

    public string Id => _synonyms[0];

    public virtual object? Default { get; }

    internal SettingsObject? Storage { get; set; }

    internal event Action<Setting, object?>? SettingChangedInternal;

    internal event Action<Setting>? SettingResetInternal;

    public virtual object? Get()
    {
        var storage = Storage;
        if (storage is null)
        {
            return Default;
        }
        var test = storage.RetrieveValue(this);
        return test is null ? Default : test;
    }

    public virtual void Set(object? value)
    {
        if (!Equals(Get(), value))
        {
            SettingChangedInternal?.Invoke(this, value);
        }
    }

    public virtual void Reset()
    {
        SettingResetInternal?.Invoke(this);
    }
}
