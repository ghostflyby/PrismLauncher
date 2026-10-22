// SPDX-License-Identifier: GPL-3.0-only
// The instance-side INI import (CONFIG C5 read-compat + C7 gate folding, ask item 2): a
// Prism/PolyMC/MultiMC instance.cfg is parsed into the typed instance layer. Flint never
// writes the INI back — the import is the READ half of the dual-track format determination:
//
//   instance directory carries instance.json  -> Flint format (typed reads, JSON writes)
//   otherwise                                 -> instance.cfg is READ-ONLY; the payload below
//                                                is materialized in memory and only written
//                                                to instance.json when the instance's first
//                                                write happens ("写时转换").
//
// C7 gate folding: a layered value imports ONLY when its gate (OverrideMemory & co) is
// present AND true — a gate set to false, or absent (the registered default is false), means
// the C++ OverrideSetting resolved through to the global setting, so the value is treated as
// unset and dropped into inheritance. Gate values are collected ONLY from the Override* keys
// themselves (InstanceConfigKeys.GateIniSpellings) over a FULL file scan: QSettings writes
// instance.cfg keys in ordinal order, so member keys (JavaPath, MaxMemAlloc, …) always sort
// BEFORE their Override* gate — the position must never decide the gate, and a member value
// ("8192" → AsBool=false) must never BE the gate.
//
// The passthrough pair (ConsoleMaxLines/ConsoleOverflowStop) and the java probe quintet are
// non-layered (C7/C9): instance values are dropped — the console pair stays global-only and
// the probe cache is launcher-state.json's path-keyed map.

using FlintLauncher.Core.Settings;
using TruePath;
using TruePath.SystemIo;

namespace FlintLauncher.Core.Config;

public static class InstanceIniImporter
{
    /// <summary>The legacy instance.cfg name (read side only — Flint never writes it).</summary>
    public const string LegacyFileName = "instance.cfg";

    /// <summary>Import one instance.cfg into the typed instance layer. A missing/unreadable
    /// file yields an empty payload (the record defaults take over through the fold).</summary>
    public static InstanceConfigFile Import(AbsolutePath iniPath)
    {
        var result = new InstanceConfigFile();
        if (!iniPath.ExistsFile())
        {
            return result;
        }

        var ini = new IniFile();
        if (!ini.LoadFile(iniPath))
        {
            return result;
        }

        // pass 1: collect the gate values — from the Override* keys ONLY, scanning the whole
        // file (INI keys are unique, so a full scan has no first/last ambiguity; the key
        // position relative to its family's value keys is irrelevant by construction).
        // Synonym gate spellings (OverrideCommands/OverrideLaunchCmd both map to the
        // OverrideCommands family) are settled LAST-WINS by file order here, while the C++
        // retrieveValue walks configKeys() primary-first (INISettingsObject.cpp:123 —
        // BaseInstance.cpp:110 declares OverrideCommands first). Only a hand-edited cfg
        // carrying both spellings with conflicting values can tell the difference: the C++
        // write path never produces one (changeSetting writes the primary and removes the
        // synonyms), so Prism-produced data stays unambiguous.
        var gates = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var key in ini.Keys)
        {
            if (InstanceConfigKeys.GateIniSpellings.TryGetValue(key, out var family))
            {
                gates[family] = Variant.AsBool(Variant.FromIniValue(ini.Get(key) ?? IniValue.Invalid));
            }
        }

        // pass 2: fold the known keys; a layered value survives only behind an explicit
        // gate=true (C7: gate=false AND gate-absent both mean "resolved through to global")
        foreach (var key in ini.Keys)
        {
            if (key == QSettingsIniCodec.ConfigVersionKey)
            {
                continue;
            }

            if (InstanceConfigKeys.Resolve(key) is not { } entry)
            {
                continue; // unknown INI keys stay unread in the cfg (GUI geometry, themes, …)
            }

            if (entry.GateKey is { } family &&
                (!gates.TryGetValue(family, out var enabled) || !enabled))
            {
                continue; // C7: the instance value is treated as unset — inherit
            }

            var value = ini.Get(key);
            if (value is null || value.ValueKind == IniValue.Kind.Invalid)
            {
                continue;
            }

            result = entry.Apply(result, Variant.FromIniValue(value));
        }

        return result;
    }
}
