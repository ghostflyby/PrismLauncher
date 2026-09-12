// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/settings/INISettingsObject.{h,cpp}.
using FlintLauncher.Core.Settings;

namespace FlintLauncher.Core.Settings;

public class IniSettingsObject : SettingsObject
{
    private readonly IniFile _ini = new();

    /// <summary>'paths' is a list of INI files to try, in order, for fallback support.</summary>
    public IniSettingsObject(IReadOnlyList<string> paths)
    {
        var firstPath = paths[0];
        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                continue;
            }
            if (path != firstPath)
            {
                // Copy the fallback to the preferred path. Like QFile::copy this silently does
                // nothing when the preferred path already exists.
                try
                {
                    File.Copy(path, firstPath, overwrite: false);
                }
                catch (IOException)
                {
                }
                break;
            }
        }
        FilePath = firstPath;
        _ini.LoadFile(firstPath);
    }

    public IniSettingsObject(string path)
    {
        FilePath = path;
        _ini.LoadFile(path);
    }

    public string FilePath { get; set; }

    public override bool Reload()
    {
        if (!_ini.LoadFile(FilePath))
        {
            return false;
        }
        var suspendSavePrev = SuspendSaveFlag;
        var doSavePrev = DoSaveFlag;
        SuspendSaveFlag = true;
        var result = base.Reload();
        SuspendSaveFlag = suspendSavePrev;
        DoSaveFlag = doSavePrev;
        return result;
    }

    public override void SuspendSave() => SuspendSaveFlag = true;

    public override void ResumeSave()
    {
        SuspendSaveFlag = false;
        if (DoSaveFlag)
        {
            DoSave();
        }
    }

    protected override void ChangeSetting(Setting setting, object? value)
    {
        if (!Contains(setting.Id))
        {
            return;
        }
        if (value is not null)
        {
            // valid value -> set the main config key, remove all the synonyms
            var keys = setting.ConfigKeys.ToList();
            _ini.Set(keys[0], Variant.ToIniValue(value));
            foreach (var key in keys.Skip(1))
            {
                _ini.Remove(key);
            }
        }
        else
        {
            // invalid -> remove all (just like reset)
            foreach (var key in setting.ConfigKeys)
            {
                _ini.Remove(key);
            }
        }
        DoSave();
    }

    protected override void ResetSetting(Setting setting)
    {
        if (!Contains(setting.Id))
        {
            return;
        }
        foreach (var key in setting.ConfigKeys)
        {
            _ini.Remove(key);
        }
        DoSave();
    }

    internal override object? RetrieveValue(Setting setting)
    {
        if (!Contains(setting.Id))
        {
            return null;
        }
        foreach (var key in setting.ConfigKeys)
        {
            var value = _ini.Get(key);
            if (value is not null && value.ValueKind != IniValue.Kind.Invalid)
            {
                return Variant.FromIniValue(value);
            }
        }
        return null;
    }

    private void DoSave()
    {
        if (SuspendSaveFlag)
        {
            DoSaveFlag = true;
        }
        else
        {
            _ini.SaveFile(FilePath);
        }
    }
}
