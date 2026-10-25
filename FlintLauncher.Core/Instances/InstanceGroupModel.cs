// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2023 TheKodeToad <TheKodeToad@proton.me>
// SPDX-FileCopyrightText: 2022 Sefa Eyeoglu <contact@scrumplex.net>
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
// The instance group model: the SOLE OWNER of both halves the C++ kept together
// in instgroups.json —
//   config side (syncable): the group definitions, group name → member set, in the
//     canonical groups file (<config-root>/groups.json); members are
//     instance UUID references, with unresolvable legacy members (dirnames of
//     instances that are gone or not yet discovered) kept verbatim and converted whenever
//     a discovery resolves them;
//   state side (machine-local): the collapsed/"hidden" flags, keyed by group NAME (no group
//     IDs — the name is the explicit user identifier and follows a rename), persisted
//     into launcher-state.json's ui section.
//
// Atomicity contract: RenameGroup/DeleteGroup write the CONFIG side first and only
// then re-point the state side; a failed config write rolls the in-memory change back so
// memory and disk never split. (Both files are small — rollback costs nothing.)
//
// Port lineage: this is the group half of InstanceList (C++ InstanceList::groupByGroup /
// m_instanceGroupIndex / on_GroupStateChanged), extracted once the launcher-state.json split made the two
// halves heterogeneous (config syncs, state does not).

using System.Text.Json;
using System.Text.Json.Serialization;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.State;
using TruePath;
using TruePath.SystemIo;

namespace FlintLauncher.Core.Instances;

/// <summary>The instgroups.json wire shape. The group-name map is the dictionary modeling
/// of the opaque dynamic-key region; formatVersion keeps its dual-shape input compat
/// (number or numeric string — the file literally writes the string "1") through
/// GroupFormatVersionConverter.</summary>
public sealed record GroupFileJson(
    [property: JsonPropertyName("formatVersion"), JsonConverter(typeof(GroupFormatVersionConverter))]
    int Version,
    [property: JsonPropertyName("groups")] Dictionary<string, GroupBodyJson>? Groups,
    [property: JsonPropertyName("ungrouped")]
    GroupBodyJson? Ungrouped);

public sealed record GroupBodyJson(
    [property: JsonPropertyName("instances")]
    List<string?>? Instances,
    [property: JsonPropertyName("hidden")] bool? Hidden);

/// <summary>Input compat for the dual-shape formatVersion: a JSON number uses TryGetInt32
/// (silent reject on overflow), a numeric string parses, everything else fails.</summary>
public sealed class GroupFormatVersionConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            if (reader.TryGetInt32(out var number))
            {
                return number;
            }
        }
        else if (reader.TokenType == JsonTokenType.String && int.TryParse(reader.GetString(), out var parsed))
        {
            return parsed;
        }

        throw new JsonException("invalid group file format version");
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GroupFileJson))]
public sealed partial class GroupJsonContext : JsonSerializerContext;

public sealed class InstanceGroupModel
{
    private readonly AbsolutePath _canonicalFile;
    private readonly IReadOnlyList<AbsolutePath> _legacyFiles;
    private readonly Func<LauncherStateStore?> _stateProvider;
    private readonly Func<bool>? _canPersist;

    /// <summary>The state side's store, resolved per use — the owner stamps it after
    /// construction (InstanceList.LauncherState is a settable property).</summary>
    private LauncherStateStore? State => _stateProvider();

    /// <summary>member reference → group (C++ m_instanceGroupIndex). The key is the
    /// instance UUID (the only persistent reference form); a member that could not be
    /// resolved at load/commit time keeps its raw string (a legacy dirname) until a
    /// discovery resolves it.</summary>
    private readonly Dictionary<string, string> _memberGroups = new(StringComparer.Ordinal);

    /// <summary>group name → member count (C++ m_groupNameCache); "" is never counted.</summary>
    private readonly Dictionary<string, int> _groupCounts = new(StringComparer.Ordinal);

    /// <summary>The collapsed group names ("" = the ungrouped pseudo-group) — the state
    /// side; owned by launcher-state.json.</summary>
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);

    private bool _loaded;

    /// <summary><paramref name="canPersist"/> is the upstream "group saving prevented
    /// because we don't know the full list of instances yet" gate (true = writes allowed);
    /// while it reports false, Save() is a legitimate no-op that leaves nothing stale.</summary>
    public InstanceGroupModel(AbsolutePath canonicalFile, IReadOnlyList<AbsolutePath> legacyFiles,
        Func<LauncherStateStore?> stateProvider, Func<bool>? canPersist = null)
    {
        _canonicalFile = canonicalFile;
        _legacyFiles = legacyFiles;
        _stateProvider = stateProvider;
        _canPersist = canPersist;
    }

    // ------------------------------------------------------------------- read accessors

    /// <summary>The group of a member reference ("" when ungrouped/unknown).</summary>
    public string GroupOf(string member) =>
        _memberGroups.GetValueOrDefault(member, "");

    /// <summary>The group of an instance addressed by either reference form: the uuid first
    /// (the persistent form), then the raw member string (a not-yet-converted dirname, e.g.
    /// written by a staged commit before the instance was discovered).</summary>
    public string GroupOfInstance(string uuid, string dirname)
    {
        var group = _memberGroups.GetValueOrDefault(uuid, "");
        return group.Length != 0 ? group : _memberGroups.GetValueOrDefault(dirname, "");
    }

    /// <summary>The non-empty groups that currently have members (sorted, like QMap keys).</summary>
    public List<string> GroupNames() =>
        _groupCounts.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

    public bool IsCollapsed(string group) => _collapsed.Contains(group);

    // ------------------------------------------------------------------ member mutation

    /// <summary>Assign a member to a group ("" = ungroup). Returns true when the map
    /// changed (and the file was rewritten).</summary>
    public bool SetMemberGroup(string member, string name)
    {
        if (name.Length == 0)
        {
            name = ""; // (upstream normalizes an empty non-null QString to null)
        }

        var changed = false;
        if (_memberGroups.TryGetValue(member, out var current))
        {
            if (current != name)
            {
                DecreaseGroupCount(current);
                _memberGroups[member] = name;
                changed = true;
            }
        }
        else
        {
            changed = true;
            _memberGroups[member] = name;
        }

        if (changed)
        {
            IncreaseGroupCount(name);
            Save();
        }

        return changed;
    }

    /// <summary>Drop a member (its instance is going away). Returns true when the map
    /// changed (and the file was rewritten).</summary>
    public bool RemoveMember(string member)
    {
        if (!_memberGroups.Remove(member, out var group))
        {
            return false;
        }

        DecreaseGroupCount(group);
        Save();
        return true;
    }

    // ------------------------------------------------------- atomic group ops

    /// <summary>Rename a group: members (config side) move, and the collapse state follows
    /// the NAME (no group IDs). Config side is written FIRST; a failed write rolls the
    /// in-memory rename back and leaves the state side untouched. Returns the members that
    /// moved (for the caller's change notifications), in no particular order.</summary>
    public IReadOnlyList<string> RenameGroup(string src, string dst)
    {
        var affected = RenameInMemory(src, dst, out var collapsedMoved);

        if (affected.Count != 0 && !Save())
        {
            // the config write failed → roll the rename back (state side never re-pointed)
            RenameInMemory(dst, src, out _);
            return [];
        }

        if (collapsedMoved)
        {
            PersistCollapsed();
        }

        return affected;
    }

    /// <summary>Delete a group: its members become ungrouped and its collapse flag goes.
    /// Same ordering/rollback as <see cref="RenameGroup"/>. Returns the members that
    /// were ungrouped.</summary>
    public IReadOnlyList<string> DeleteGroup(string name)
    {
        var cachedCount = _groupCounts.GetValueOrDefault(name, 0);
        _groupCounts.Remove(name);
        var collapsedRemoved = _collapsed.Remove(name);

        var affected = new List<string>();
        foreach (var member in _memberGroups.Keys.ToList())
        {
            if (_memberGroups[member] == name)
            {
                affected.Add(member);
                _memberGroups.Remove(member);
            }
        }

        var removed = affected.Count > 0;

        if (removed && !Save())
        {
            foreach (var member in affected)
            {
                _memberGroups[member] = name;
            }

            if (cachedCount > 0)
            {
                _groupCounts[name] = cachedCount;
            }

            if (collapsedRemoved)
            {
                _collapsed.Add(name);
            }

            return [];
        }

        if (collapsedRemoved)
        {
            PersistCollapsed();
        }

        return affected;
    }

    /// <summary>The in-memory half of a rename: members and the collapse set move together;
    /// the caller owns write ordering/rollback around it.</summary>
    private List<string> RenameInMemory(string src, string dst, out bool collapsedMoved)
    {
        _groupCounts.Remove(src);
        collapsedMoved = false;
        if (_collapsed.Remove(src))
        {
            _collapsed.Add(dst);
            collapsedMoved = true;
        }

        var affected = new List<string>();
        foreach (var member in _memberGroups.Keys.ToList())
        {
            if (_memberGroups[member] == src)
            {
                _memberGroups[member] = dst;
                IncreaseGroupCount(dst);
                affected.Add(member);
            }
        }

        return affected;
    }

    // -------------------------------------------------------------- collapse (state side)

    /// <summary>Port of the on_GroupStateChanged slot. Only the state side changes (the
    /// group file carries definitions only), so there is no config write to
    /// order against.</summary>
    public void SetCollapsed(string group, bool collapsed)
    {
        if (collapsed)
        {
            _collapsed.Add(group);
        }
        else
        {
            _collapsed.Remove(group);
        }

        PersistCollapsed();
    }

    /// <summary>Persist the in-memory collapse set into launcher-state.json (empty string =
    /// the ungrouped pseudo-group, represented by UiState.UngroupedHidden).</summary>
    private void PersistCollapsed()
    {
        State?.Update(state => state with
        {
            Ui = new UiState
            {
                HiddenGroups = _collapsed
                    .Where(group => group.Length != 0)
                    .OrderBy(group => group, StringComparer.Ordinal)
                    .ToList(),
                UngroupedHidden = _collapsed.Contains(""),
            },
        });
    }

    // ------------------------------------------------------------------------ load/save

    /// <summary>Load the group definitions (retried until a file was actually parsed — the
    /// upstream semantics: a missing file may still appear, e.g. a Prism import landing
    /// instgroups.json mid-session). Returns true when a group file was read and parsed.
    /// The collapse state comes from launcher-state.json first (the owner); a legacy/Prism
    /// group file may still carry "hidden" keys — those seed in as a one-shot read-compat
    /// promote-and-strip. Member references are converted to UUIDs through
    /// <paramref name="uuidResolver"/> (import conversion: legacy files and Prism
    /// imports carry dirnames); a reference that resolves to nothing is kept verbatim.</summary>
    public bool Load(Func<string, string?> uuidResolver)
    {
        if (_loaded)
        {
            return true;
        }

        // the collapse state seeds from launcher-state.json on EVERY attempt (it is
        // the owner; idempotent). The member maps are only reset when a file actually
        // parses — a retry (file not there yet / not parseable) must not wipe member
        // assignments made between attempts.
        if (State is { } state)
        {
            foreach (var group in state.Current.Ui.HiddenGroups)
            {
                if (group.Length != 0)
                {
                    _collapsed.Add(group);
                }
            }

            if (state.Current.Ui.UngroupedHidden)
            {
                // empty string represents ungrouped "group"
                _collapsed.Add("");
            }
        }

        var migratingLegacyGroups = false;
        var stateSeededHidden = false;
        var fileCarriedHidden = false;

        // canonical file first; when absent, the legacy anchors (the data-root
        // instgroups.json — the Prism import target — then the primary-instance-dir copy
        // upstream used before there was a launcher data dir)
        var groupFileName = _canonicalFile;
        if (!groupFileName.ExistsFile())
        {
            foreach (var legacy in _legacyFiles)
            {
                if (legacy.ExistsFile())
                {
                    groupFileName = legacy;
                    migratingLegacyGroups = true;
                    break;
                }
            }

            if (!migratingLegacyGroups)
            {
                return false;
            }
        }

        byte[] jsonData;
        try
        {
            jsonData = groupFileName.ReadAllBytes();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // qCritical: failed to read instance group file
            return false;
        }

        GroupFileJson? file;
        try
        {
            file = JsonSerializer.Deserialize(jsonData, GroupJsonContext.Default.GroupFileJson);
        }
        catch (JsonException)
        {
            // qCritical: failed to parse instance group file
            return false;
        }

        if (file is null || file.Groups is null)
        {
            // qWarning: invalid group file. Root entry should be an object carrying 'groups'.
            return false;
        }

        // the file is real: rebuild the member maps from it (upstream cleared its maps
        // on every LoadGroupList run — here only the successful-parse path clears, so
        // a transient absence never erases live assignments)
        _memberGroups.Clear();
        _groupCounts.Clear();

        foreach (var (groupName, groupValue) in file.Groups)
        {
            if (groupName.Length == 0)
            {
                // qWarning: redundant empty group found
                continue;
            }

            if (groupValue is null)
            {
                // qWarning: group 'X' in the group list should be an object
                continue;
            }

            if (groupValue.Instances is not { } instances)
            {
                // qWarning: group 'X' is invalid, it should contain an array called 'instances'
                continue;
            }

            // QJsonValue::toBool(false): only a real JSON true counts
            var hidden = groupValue.Hidden == true;
            if (hidden)
            {
                fileCarriedHidden = true;
                if (_collapsed.Add(groupName))
                {
                    stateSeededHidden = true;
                }
            }

            foreach (var value in instances)
            {
                // QJsonValue::toString() yields "" for non-strings
                var member = value ?? "";
                // import conversion: a dirname member becomes the instance's uuid
                // when the current discovery can resolve it; unknown members persist
                // verbatim (the conversion retries on the next load)
                member = uuidResolver(member) is { Length: > 0 } uuid ? uuid : member;
                _memberGroups[member] = groupName;
                IncreaseGroupCount(groupName);
            }
        }

        var ungroupedHidden = file.Ungrouped?.Hidden == true;

        if (ungroupedHidden)
        {
            fileCarriedHidden = true;
            if (_collapsed.Add(""))
            {
                // empty string represents ungrouped "group"
                stateSeededHidden = true;
            }
        }

        _loaded = true;

        if (stateSeededHidden)
        {
            // newly-learned file flags promote into launcher-state.json
            PersistCollapsed();
        }

        if (fileCarriedHidden && State is not null && !migratingLegacyGroups)
        {
            // one-shot completion: the state side owns the collapse flags now, so the
            // file must stop carrying them. Runs even when the flag only DUPLICATED the
            // state (Add returned false) — that is exactly the leftover of a pre-fix
            // resurrect cycle and would keep re-seeding otherwise. The legacy-group
            // migration path skips the strip: its Save rewrite below already writes the
            // canonical file without hidden keys.
            StripHiddenFromGroupFile(groupFileName);
        }

        if (migratingLegacyGroups)
        {
            Save();
        }

        return true;
    }

    /// <summary>Re-resolve members kept verbatim at load (dirnames of instances not yet
    /// discovered — the promise the load-time comment makes). Called after every discovery
    /// round; a conversion keeps the member's group so counts are untouched, and the file is
    /// rewritten once when something actually resolved.</summary>
    public bool ConvertUnresolvedMembers(Func<string, string?> uuidResolver)
    {
        var changed = false;
        foreach (var member in _memberGroups.Keys.ToList())
        {
            if (uuidResolver(member) is { Length: > 0 } uuid && uuid != member)
            {
                var group = _memberGroups[member];
                _memberGroups.Remove(member);
                _memberGroups[uuid] = group;
                changed = true;
            }
        }

        if (changed)
        {
            Save();
        }

        return changed;
    }

    /// <summary>The group file format version (g_GROUP_FILE_FORMAT_VERSION). Upstream
    /// literally wrote the STRING "1"; the typed write emits the number 1 — the read
    /// converter accepts both shapes.</summary>
    public const int GroupFormatVersion = 1;

    /// <summary>Write the group-definition file (config side). Returns false when a write
    /// was attempted and failed — the ordering (config side first, state side after)
    /// gates on it; the C++ swallowed this failure (qCritical) and had no second side to
    /// protect.</summary>
    public bool Save()
    {
        if (_canPersist is { } gate && !gate())
        {
            // qDebug: group saving prevented because we don't know the full list of
            // instances yet — legitimately skipped, nothing stale left behind
            return true;
        }

        var groupFileName = _canonicalFile;

        // C++ QMap<QString, QSet<QString>> reverseGroupMap; sorted containers for
        // deterministic QMap-ordered output. The write side maps onto the same GroupFileJson
        // wire the read side probes (the formatVersion writes as a number now — the read
        // converter accepts both shapes). Members are uuid references — unresolvable
        // legacy members (dirnames kept verbatim at load) round-trip as they are.
        var reverseGroupMap = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var (member, group) in _memberGroups)
        {
            if (group.Length == 0)
            {
                continue;
            }

            if (!reverseGroupMap.TryGetValue(group, out var set))
            {
                set = new SortedSet<string>(StringComparer.Ordinal);
                reverseGroupMap[group] = set;
            }

            set.Add(member);
        }

        var groups = new Dictionary<string, GroupBodyJson>(reverseGroupMap.Count, StringComparer.Ordinal);
        foreach (var (name, list) in reverseGroupMap)
        {
            // NOTE: the collapse state ("hidden") is NOT written here — it lives in
            // launcher-state.json; the group file carries the
            // group definitions only
            groups[name] = new GroupBodyJson([.. list], null);
        }

        var toplevel = new GroupFileJson(GroupFormatVersion, groups, null);
        try
        {
            FileOps.Write(new AbsolutePath(groupFileName),
                JsonSerializer.SerializeToUtf8Bytes(toplevel, GroupJsonContext.Default.GroupFileJson));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or NotSupportedException)
        {
            // qCritical: failed to write instance group file — reported to the ordering
            return false;
        }
    }

    /// <summary>Rewrite the group file without its "hidden" flags (the file carries
    /// the group DEFINITIONS only once the state side owns the collapse state). Best-effort:
    /// a failed rewrite leaves the flags in place and the next load re-seeds them (the
    /// pre-fix behavior) — it must not fail the load.</summary>
    private static void StripHiddenFromGroupFile(AbsolutePath groupFileName)
    {
        try
        {
            var file = JsonSerializer.Deserialize(
                groupFileName.ReadAllText(), GroupJsonContext.Default.GroupFileJson);
            if (file is null)
            {
                return;
            }

            GroupBodyJson? ungrouped = null;
            if (file.Ungrouped is { } body)
            {
                body = body with { Hidden = null };
                if (body.Instances is { Count: > 0 })
                {
                    ungrouped = body;
                }
                // else: the stripped body is empty — dropped from the file (the former DOM
                // strip removed the empty "ungrouped" object)
            }

            var stripped = new GroupFileJson(
                file.Version,
                file.Groups?.ToDictionary(
                    group => group.Key,
                    group => group.Value with { Hidden = null }, StringComparer.Ordinal),
                ungrouped);
            FileOps.Write(new AbsolutePath(groupFileName),
                JsonSerializer.SerializeToUtf8Bytes(stripped, GroupJsonContext.Default.GroupFileJson));
        }
        catch (Exception e) when (e is JsonException or IOException
                                      or UnauthorizedAccessException or NotSupportedException)
        {
            // best-effort — see the summary
        }
    }

    private void IncreaseGroupCount(string group)
    {
        if (group.Length == 0)
        {
            return;
        }

        _groupCounts.TryGetValue(group, out var count);
        _groupCounts[group] = count + 1;
    }

    private void DecreaseGroupCount(string group)
    {
        if (group.Length == 0)
        {
            return;
        }

        _groupCounts.TryGetValue(group, out var count);
        count--;
        if (count < 1)
        {
            _groupCounts.Remove(group);
            if (_collapsed.Remove(group))
            {
                PersistCollapsed();
            }
        }
        else
        {
            _groupCounts[group] = count;
        }
    }
}
