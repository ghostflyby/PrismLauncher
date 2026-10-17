// SPDX-License-Identifier: GPL-3.0-only
// The instance group model (GLOBAL G4): the SOLE OWNER of both halves the C++ kept together
// in instgroups.json —
//   config side (syncable): the group definitions, group name → member set, in the C8
//     groups file (<config-root>/groups.json — see FlintPaths.GroupsFile); members are
//     instance UUID references (DATA D3), with unresolvable legacy members (dirnames of
//     instances that are gone or not yet discovered) kept verbatim and converted whenever
//     a discovery resolves them;
//   state side (machine-local): the collapsed/"hidden" flags, keyed by group NAME (no group
//     IDs — G4: the name is the explicit user identifier and follows a rename), persisted
//     into launcher-state.json's ui section (STATE S5).
//
// Atomicity contract (G4): RenameGroup/DeleteGroup write the CONFIG side first and only
// then re-point the state side; a failed config write rolls the in-memory change back so
// memory and disk never split. (Both files are small — rollback costs nothing.)
//
// Port lineage: this is the group half of InstanceList (C++ InstanceList::groupByGroup /
// m_instanceGroupIndex / on_GroupStateChanged), extracted once the C8/S5 split made the two
// halves heterogeneous (config syncs, state does not).

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.State;

namespace FlintLauncher.Core.Instances;

public sealed class InstanceGroupModel
{
    private readonly Func<string> _canonicalFile;
    private readonly Func<IReadOnlyList<string>> _legacyFiles;
    private readonly Func<LauncherStateStore?> _stateProvider;
    private readonly Func<bool>? _canPersist;

    /// <summary>The state side's store, resolved per use — the owner stamps it after
    /// construction (InstanceList.LauncherState is a settable property).</summary>
    private LauncherStateStore? State => _stateProvider();

    /// <summary>member reference → group (C++ m_instanceGroupIndex). The key is the
    /// instance UUID (D3: the only persistent reference form); a member that could not be
    /// resolved at load/commit time keeps its raw string (a legacy dirname) until a
    /// discovery resolves it.</summary>
    private readonly Dictionary<string, string> _memberGroups = new(StringComparer.Ordinal);

    /// <summary>group name → member count (C++ m_groupNameCache); "" is never counted.</summary>
    private readonly Dictionary<string, int> _groupCounts = new(StringComparer.Ordinal);

    /// <summary>The collapsed group names ("" = the ungrouped pseudo-group) — the state
    /// side; owned by launcher-state.json since C8/S5.</summary>
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);

    private bool loaded;

    /// <summary><paramref name="canPersist"/> is the upstream "group saving prevented
    /// because we don't know the full list of instances yet" gate (true = writes allowed);
    /// while it reports false, Save() is a legitimate no-op that leaves nothing stale.</summary>
    public InstanceGroupModel(Func<string> canonicalFile, Func<IReadOnlyList<string>> legacyFiles,
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

    // ------------------------------------------------------- atomic group ops (G4 owner)

    /// <summary>Rename a group: members (config side) move, and the collapse state follows
    /// the NAME (G4 — no group IDs). Config side is written FIRST; a failed write rolls the
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
    /// Same G4 ordering/rollback as <see cref="RenameGroup"/>. Returns the members that
    /// were ungrouped.</summary>
    public IReadOnlyList<string> DeleteGroup(string name)
    {
        var cachedCount = _groupCounts.TryGetValue(name, out var count) ? count : 0;
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
    /// group file carries definitions only since C8/S5), so there is no config write to
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
    /// promote-and-strip (C8/S5). Member references are converted to UUIDs through
    /// <paramref name="uuidResolver"/> (D3 import conversion: legacy files and Prism
    /// imports carry dirnames); a reference that resolves to nothing is kept verbatim.</summary>
    public bool Load(Func<string, string?> uuidResolver)
    {
        if (loaded)
        {
            return true;
        }

        // the collapse state seeds from launcher-state.json on EVERY attempt (C8/S5: it is
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

        // canonical file first; when absent, the legacy anchors (the pre-C8 data-root
        // instgroups.json — the Prism import target — then the primary-instance-dir copy
        // upstream used before there was a launcher data dir)
        var groupFileName = _canonicalFile();
        if (!File.Exists(groupFileName))
        {
            foreach (var legacy in _legacyFiles())
            {
                if (File.Exists(legacy))
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
            jsonData = FileSystem.Read(groupFileName);
        }
        catch (FileSystemException)
        {
            // qCritical: failed to read instance group file
            return false;
        }

        JsonDocument jsonDoc;
        try
        {
            jsonDoc = JsonDocument.Parse(jsonData);
        }
        catch (JsonException)
        {
            // qCritical: failed to parse instance group file
            return false;
        }

        using (jsonDoc)
        {
            var rootObj = jsonDoc.RootElement;

            // if the root of the json wasn't an object, fail
            if (rootObj.ValueKind != JsonValueKind.Object)
            {
                // qWarning: invalid group file. Root entry should be an object.
                return false;
            }

            // Make sure the format version matches, otherwise fail.
            // (QJsonValue::toVariant().toInt() accepts both numbers and numeric strings)
            // toVariant().toInt() never throws upstream — an unrepresentable number simply
            // fails the version match (a decimal like 1.5 rounds to 2 and is rejected there
            // too). TryGetInt32 keeps that silent-reject shape; a raw GetInt32 would unwind
            // a FormatException through Load into a group-endpoint 500.
            if (!TryGetFormatVersion(rootObj, out var formatVersion) || formatVersion != GroupFormatVersion)
            {
                return false;
            }

            // Get the groups. if it's not an object, fail
            if (!rootObj.TryGetProperty("groups", out var groupsValue) || groupsValue.ValueKind != JsonValueKind.Object)
            {
                // qWarning: invalid group list JSON: 'groups' should be an object.
                return false;
            }

            // the file is real: rebuild the member maps from it (upstream cleared its maps
            // on every LoadGroupList run — here only the successful-parse path clears, so
            // a transient absence never erases live assignments)
            _memberGroups.Clear();
            _groupCounts.Clear();

            foreach (var groupProp in groupsValue.EnumerateObject())
            {
                var groupName = groupProp.Name;
                if (groupName.Length == 0)
                {
                    // qWarning: redundant empty group found
                    continue;
                }
                if (groupProp.Value.ValueKind != JsonValueKind.Object)
                {
                    // qWarning: group 'X' in the group list should be an object
                    continue;
                }

                var groupObj = groupProp.Value;
                if (!groupObj.TryGetProperty("instances", out var instancesValue) ||
                    instancesValue.ValueKind != JsonValueKind.Array)
                {
                    // qWarning: group 'X' is invalid, it should contain an array called 'instances'
                    continue;
                }

                // QJsonValue::toBool(false): only a real JSON true counts
                var hidden = groupObj.TryGetProperty("hidden", out var hiddenValue) &&
                             hiddenValue.ValueKind == JsonValueKind.True;
                if (hidden)
                {
                    fileCarriedHidden = true;
                    if (_collapsed.Add(groupName))
                    {
                        stateSeededHidden = true;
                    }
                }

                foreach (var value in instancesValue.EnumerateArray())
                {
                    // QJsonValue::toString() yields "" for non-strings
                    var member = value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
                    // D3 import conversion: a dirname member becomes the instance's uuid
                    // when the current discovery can resolve it; unknown members persist
                    // verbatim (the conversion retries on the next load)
                    member = uuidResolver(member) is { Length: > 0 } uuid ? uuid : member;
                    _memberGroups[member] = groupName;
                    IncreaseGroupCount(groupName);
                }
            }

            var ungroupedHidden = false;
            if (rootObj.TryGetProperty("ungrouped", out var ungroupedValue) &&
                ungroupedValue.ValueKind == JsonValueKind.Object &&
                ungroupedValue.TryGetProperty("hidden", out var ungroupedHiddenValue))
            {
                ungroupedHidden = ungroupedHiddenValue.ValueKind == JsonValueKind.True;
            }
            if (ungroupedHidden)
            {
                fileCarriedHidden = true;
                if (_collapsed.Add(""))
                {
                    // empty string represents ungrouped "group"
                    stateSeededHidden = true;
                }
            }
            loaded = true;
        }

        if (stateSeededHidden)
        {
            // newly-learned file flags promote into launcher-state.json
            PersistCollapsed();
        }

        if (fileCarriedHidden && State is not null && !migratingLegacyGroups)
        {
            // C8/S5 one-shot completion: the state side owns the collapse flags now, so the
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

    /// <summary>D3: re-resolve members kept verbatim at load (dirnames of instances not yet
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

    /// <summary>The group file format version (g_GROUP_FILE_FORMAT_VERSION — the STRING
    /// "1" on disk, like upstream).</summary>
    public const int GroupFormatVersion = 1;

    /// <summary>Write the group-definition file (config side). Returns false when a write
    /// was attempted and failed — the G4 ordering (config side first, state side after)
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

        var groupFileName = _canonicalFile();

        // C++ QMap<QString, QSet<QString>> reverseGroupMap; sorted containers for
        // deterministic output. Members are uuid references (D3) — unresolvable legacy
        // members (dirnames kept verbatim at load) round-trip as they are.
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

        var toplevel = new JsonObject();
        toplevel["formatVersion"] = JsonValue.Create("1"); // QJsonValue(QString("1")) — a string!
        var groupsArr = new JsonObject();
        foreach (var (name, list) in reverseGroupMap)
        {
            var groupObj = new JsonObject();
            var instanceArr = new JsonArray();
            // NOTE: the collapse state ("hidden") is NOT written here — it lives in
            // launcher-state.json (CONFIG C8 split / STATE S5); the group file carries the
            // group definitions only
            foreach (var item in list)
            {
                instanceArr.Add((JsonNode)JsonValue.Create(item)!); // non-generic Add: AOT/trimming clean
            }
            groupObj["instances"] = instanceArr;
            groupsArr[name] = groupObj;
        }
        toplevel["groups"] = groupsArr;
        try
        {
            FileSystem.Write(groupFileName,
                Encoding.UTF8.GetBytes(QtJson.ToIndentedString(toplevel) + "\n"));
            return true;
        }
        catch (Exception e) when (e is FileSystemException or IOException or UnauthorizedAccessException
                                  or NotSupportedException)
        {
            // qCritical: failed to write instance group file — reported to the G4 ordering
            return false;
        }
    }

    /// <summary>Rewrite the group file without its "hidden" flags (C8/S5: the file carries
    /// the group DEFINITIONS only once the state side owns the collapse state). Best-effort:
    /// a failed rewrite leaves the flags in place and the next load re-seeds them (the
    /// pre-fix behavior) — it must not fail the load.</summary>
    private static void StripHiddenFromGroupFile(string groupFileName)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(groupFileName)) is not JsonObject topLevel)
            {
                return;
            }
            if (topLevel["groups"] is JsonObject groups)
            {
                foreach (var group in groups)
                {
                    (group.Value as JsonObject)?.Remove("hidden");
                }
            }
            if (topLevel["ungrouped"] is JsonObject ungrouped)
            {
                ungrouped.Remove("hidden");
                if (ungrouped.Count == 0)
                {
                    topLevel.Remove("ungrouped");
                }
            }
            FileSystem.Write(groupFileName,
                Encoding.UTF8.GetBytes(QtJson.ToIndentedString(topLevel) + "\n"));
        }
        catch (Exception e) when (e is JsonException or FileSystemException or IOException
                                  or UnauthorizedAccessException or NotSupportedException)
        {
            // best-effort — see the summary
        }
    }

    private static bool TryGetFormatVersion(JsonElement rootObj, out int version)
    {
        version = 0;
        if (!rootObj.TryGetProperty("formatVersion", out var value))
        {
            return false;
        }
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                if (!value.TryGetInt32(out version))
                {
                    return false;
                }
                return true;
            case JsonValueKind.String:
                return int.TryParse(value.GetString(), out version);
            default:
                return false;
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
