// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// The instance settings object over the typed layer stack (the
// SettingsObject shape the instance/launch layer reads through, backed by
// flint.json -> instance.json instead of instance.cfg):
//
//  - READS resolve through the fold per key (InstanceConfigKeys): instance-layer member
//    when set, else the user layer (the live flint.json snapshot), else the registered
//    Setting default (the instance-only keys' defaults).
//  - WRITES land in the instance layer only and persist instance.json atomically; on a
//    Prism-format instance the first write performs the dual-track conversion (the in-memory
//    instance.cfg import materializes as instance.json — see InstanceConfigStore).
//  - The runtime time trio (lastLaunchTime/lastTimePlayed/totalTimePlayed) routes to
//    instance-state.json, never to the config.
//  - The Override* gates are DERIVED: reading computes "any family member set"; writing
//    false clears the family. The passthrough pair reads the user layer and drops
//    instance-side writes.
//
// This replaces the IniSettingsObject instance.cfg store (the INI write path is gone)
// and, together with the typed user stack, retires the FlintConfigSettings adapter.

using FlintLauncher.Core.Settings;
using Microsoft.Extensions.Options;
using TruePath;

namespace FlintLauncher.Core.Config;

public sealed class InstanceJsonSettingsObject(AbsolutePath instanceRoot, InstanceEnvironment environment) : SettingsObject
{
    private readonly IOptionsMonitor<FlintConfigFile>? _userConfig = environment.UserConfig;

    /// <summary>The typed instance.json store (dual-track marker + write target).</summary>
    public InstanceConfigStore ConfigStore { get; } = new(instanceRoot, environment.ConfigInfo);

    /// <summary>The typed instance-state.json store (the runtime time trio).</summary>
    public InstanceStateStore StateStore { get; } = new(instanceRoot, environment.StateInfo);

    /// <summary>The live user-layer read face (flint.json) when one is wired, for hosts that
    /// hand the same stack onward — null on bare fixtures.</summary>
    public IOptionsMonitor<FlintConfigFile>? UserConfig => _userConfig;

    /// <summary>The live user layer (flint.json), or the record defaults when the host has no
    /// config store (bare fixtures) — matching the former unset-global behavior.</summary>
    public FlintConfigFile UserLayer => _userConfig?.CurrentValue ?? FlintConfigFile.Default();

    protected override void ChangeSetting(Setting setting, object? value)
    {
        switch (setting.Id)
        {
            case "lastLaunchTime":
                StateStore.Update(s => s with { LastLaunchTime = Variant.AsLong(value) });
                break;
            case "lastTimePlayed":
                StateStore.Update(s => s with { LastTimePlayed = Variant.AsLong(value) });
                break;
            case "totalTimePlayed":
                StateStore.Update(s => s with { TotalTimePlayed = Variant.AsLong(value) });
                break;
            default:
                ConfigStore.Update(current => Apply(setting.Id, current, value));
                // the dual-track conversion is whole-instance: once the config side turned
                // Flint format, the trio's home materializes too (the legacy cfg seed must
                // not keep feeding runtime values)
                StateStore.EnsureMaterialized();
                break;
        }
    }

    protected override void ResetSetting(Setting setting)
    {
        if (IsStateKey(setting.Id))
        {
            // the trio's registered default is 0 — a reset zeroes the runtime state
            ChangeSetting(setting, 0L);
            return;
        }

        ConfigStore.Update(current => Apply(setting.Id, current, null));
        StateStore.EnsureMaterialized();
    }

    internal override object? RetrieveValue(Setting setting)
    {
        if (IsStateKey(setting.Id))
        {
            return setting.Id switch
            {
                "lastLaunchTime" => StateStore.Current.LastLaunchTime,
                "lastTimePlayed" => StateStore.Current.LastTimePlayed,
                _ => StateStore.Current.TotalTimePlayed,
            };
        }

        if (InstanceConfigKeys.Resolve(setting.Id) is not { } entry)
        {
            return null; // registered but unmapped would be a registry gap — Setting falls back
        }

        return entry.Get(UserLayer, ConfigStore.Current);
    }

    public override void SuspendSave() { }

    public override void ResumeSave() { }

    private static bool IsStateKey(string id) =>
        id is "lastLaunchTime" or "lastTimePlayed" or "totalTimePlayed";

    private static InstanceConfigFile Apply(string key, InstanceConfigFile current, object? value) =>
        InstanceConfigKeys.Resolve(key) is { } entry
            ? entry.Apply(current, value)
            : // unknown key with no registry entry: refuse loudly rather than silently dropping
            throw new InvalidOperationException($"unknown instance settings key '{key}'");
}
