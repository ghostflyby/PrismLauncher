// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// This file incorporates work covered by the following copyright and
// permission notice:
//
//     Copyright 2013-2021 MultiMC Contributors
//
//     Licensed under the Apache License, Version 2.0 (the "License");
//     you may not use this file except in compliance with the License.
//     You may obtain a copy of the License at
//
//         http://www.apache.org/licenses/LICENSE-2.0
//
//     Unless required by applicable law or agreed to in writing, software
//     distributed under the License is distributed on an "AS IS" BASIS,
//     WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//     See the License for the specific language governing permissions and
//     limitations under the License.
//
// Port of launcher/settings/Setting.{h,cpp}. QVariant becomes object? with Variant helpers.

namespace FlintLauncher.Core.Settings;

public class Setting(IReadOnlyList<string> synonyms, object? defVal = null)
{
    /// <summary>All config-file keys this setting answers to, in order of preference.</summary>
    public IReadOnlyList<string> ConfigKeys => synonyms;

    public string Id => synonyms[0];

    public virtual object? Default { get; } = defVal;

    internal SettingsObject? Storage { get; init; }

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
