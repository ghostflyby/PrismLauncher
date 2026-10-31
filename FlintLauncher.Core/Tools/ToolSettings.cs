// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// The typed user store seen through the settings surface the external tools were written
// against. A tool's factory registers its keys and reads them back; the keys resolve through
// FlintConfigKeys to the flint.json members, so no value is duplicated here.

using FlintLauncher.Core.Config;
using FlintLauncher.Core.Settings;

namespace FlintLauncher.Core.Tools;

/// <summary>The global tool settings (JProfilerPath, JVisualVMPath, ...) as a
/// <see cref="SettingsObject"/>. Reads and writes go straight to the store; a key the store
/// does not know is only visible through its registered default.</summary>
public sealed class ToolSettings(FlintConfigStore store) : SettingsObject
{
    /// <summary>The store this object reads through.</summary>
    public FlintConfigStore Store { get; } = store;

    protected override void ChangeSetting(Setting setting, object? value) =>
        Store.Set(setting.Id, value);

    protected override void ResetSetting(Setting setting) =>
        Store.Reset(setting.Id);

    internal override object? RetrieveValue(Setting setting) =>
        Store.Get(setting.Id);

    public override void SuspendSave() { }

    public override void ResumeSave() { }
}
