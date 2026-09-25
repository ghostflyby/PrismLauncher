// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/InstanceList.{h,cpp} (the data/logic halves).
//
// Dropped (Qt UI/infrastructure plumbing):
//  - the QAbstractListModel surface (index/rowCount/data/setData/flags, the drag&drop
//    mime handling and the AdditionalRoles): the instancesChanged() model-reset signal
//    becomes the plain <see cref="InstanceListChanged"/> event and the per-instance
//    propertiesChanged()->dataChanged() glue becomes <see cref="InstanceDataChanged"/>;
//  - the QFileSystemWatcher glue (suspendWatch/resumeWatch/instanceDirContentsChanged and
//    the m_watchLevel/m_dirty bookkeeping) — callers re-run LoadList() themselves;
//  - the trash flow (trashInstance/trashedSomething/undoTrashInstance and the
//    TrashHistoryItem stack): FS::trash needs desktop-shell integration which FileSystem.cs
//    declares not ported. DeleteInstance (hard delete) is ported;
//  - on_GroupStateChanged stays as <see cref="SetGroupCollapsed"/> because it persists the
//    group view state into instgroups.json ("hidden").
//
// Deviations from the C++ original:
//  - the group of an instance lives in InstanceList's own id->group map upstream (C++
//    m_instanceGroupIndex); instances never store it. The port keeps the same map; access
//    is via GetInstanceGroup/SetInstanceGroup (there is no instance->group()).
//  - loadInstance(): upstream (this revision) *skips* folders whose instance.cfg carries an
//    unknown InstanceType (returns nullptr and the folder is not listed). Older MultiMC had
//    dedicated Legacy/OneSixOld instance classes there. The port instead yields a
//    NullInstance placeholder for every non-"OneSix" type so the folder stays visible as a
//    broken instance.
//  - the group file location: upstream hard-codes QDir::current().filePath("instgroups.json")
//    (the launcher data dir, where the application chdir'd at startup) with a legacy
//    fallback inside the primary instance dir. The port defaults to the same CWD path but
//    the location can be overridden through <see cref="GroupFilePath"/> (tests/embedding).
//  - instance-dir canonicalization uses Path.GetFullPath (QDir::canonicalPath resolves
//    symlinks; there is no cheap portable equivalent and the watcher dedup use is dropped).
//  - on_InstFolderChanged becomes <see cref="SetInstanceDirs"/> and the signal chain it
//    triggered (instancesChanged -> providerUpdated -> loadList) is collapsed into a direct
//    LoadList() call.
//  - migrateTotalPlayTime()'s APPLICATION->playtimeSettings() is the injectable
//    <see cref="PlaytimeSettings"/>; when it is null the migration is skipped.
//  - instance order inside a group of instgroups.json: QSet iteration is unordered upstream,
//    the port writes ids sorted (ordinal) for deterministic output.
//  - CommitStagedInstance's override path re-implements FS::overrideFolder as an
//    overwrite-copy of the staging folder over the destination.
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Settings;
using FlintLauncher.Core.Tasks;

namespace FlintLauncher.Core.Instances;

/// <summary>Port of the InstListError enum.</summary>
public enum InstListError : byte
{
    NoError = 0,
    UnknownError,
}

public class InstanceList
{
    /// <summary>g_GROUP_FILE_FORMAT_VERSION.</summary>
    public const int GroupFileFormatVersion = 1;

    private const int MinBackoff = 1;
    private const int MaxBackoff = 16;

    private readonly SettingsObject _globalSettings;
    private readonly List<string> _instDirs = [];

    private readonly List<BaseInstance> _instances = [];
    // id -> refs (C++ m_groupNameCache)
    private readonly Dictionary<string, int> _groupNameCache = new(StringComparer.Ordinal);
    // C++ m_instanceGroupIndex: id -> group
    private readonly Dictionary<string, string> _instanceGroupIndex = new(StringComparer.Ordinal);
    // C++ m_instanceRootDirMap: id -> instance dir that provided it
    private readonly Dictionary<string, string> _instanceRootDirMap = new(StringComparer.Ordinal);
    private readonly HashSet<string> _instanceSet = new(StringComparer.Ordinal);
    // "" represents the ungrouped "group" (C++ m_collapsedGroups)
    private readonly HashSet<string> _collapsedGroups = new(StringComparer.Ordinal);

    private bool _groupsLoaded;
    private bool _instancesProbed;

    public InstanceList(SettingsObject globalSettings, IEnumerable<string> instDirs)
    {
        _globalSettings = globalSettings;

        foreach (var dir in instDirs)
        {
            // Create and normalize path (QDir::current().mkpath(dir))
            FileSystem.EnsureFolderPathExists(dir);
            // NOTE: canonicalPath requires the path to exist. Do not move this above the creation block!
            var canonical = QDirCanonicalPath(dir);
            if (canonical.Length != 0 && !_instDirs.Contains(canonical))
            {
                _instDirs.Add(canonical);
            }
        }
    }

    // ---------------------------------------------------------------- events (Qt signals)

    /// <summary>Port of the instancesChanged() signal (the model reset replacement).</summary>
    public event Action? InstanceListChanged;

    /// <summary>Merged port of the propertiesChanged()->dataChanged() per-instance glue.</summary>
    public event Action<BaseInstance>? InstanceDataChanged;

    /// <summary>Port of the instanceSelectRequest() signal.</summary>
    public event Action<string>? InstanceSelectRequest;

    // --------------------------------------------------------------------- configuration

    /// <summary>
    /// C++ APPLICATION->playtimeSettings(), used by the one-shot TotalPlayTime migration;
    /// null skips the migration (no application singleton in the port).
    /// </summary>
    public SettingsObject? PlaytimeSettings { get; set; }

    /// <summary>
    /// Location of instgroups.json. Empty (default) reproduces upstream:
    /// &lt;current working directory&gt;/instgroups.json with a legacy fallback inside the
    /// primary instance dir.
    /// </summary>
    public string GroupFilePath { get; set; } = "";

    // ------------------------------------------------------------------- list accessors

    public string PrimaryDir() => _instDirs.Count == 0 ? "" : _instDirs[0];

    /// <summary>Port of InstanceList::instanceDirs (all configured instance roots).</summary>
    public IReadOnlyList<string> InstanceDirs() => _instDirs;

    /// <summary>C++ m_instances (in list order).</summary>
    public IReadOnlyList<BaseInstance> Instances => _instances;

    public int Count => _instances.Count;

    public InstListError LoadList()
    {
        var existingIds = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < _instances.Count; i++)
        {
            var id = _instances[i].Id();
            if (!existingIds.TryAdd(id, i))
            {
                // qWarning() << "Duplicate ID" << id << "in instance list"
            }
        }

        var newList = new List<BaseInstance>();
        var removedAny = false;

        foreach (var id in DiscoverInstances())
        {
            if (existingIds.Remove(id))
            {
                // keep and soft-reload
            }
            else
            {
                var inst = LoadInstance(id);
                if (inst is not null)
                {
                    newList.Add(inst);
                }
            }
        }

        if (existingIds.Count != 0)
        {
            // (the C++ removes the model rows in contiguous ranges; the plain list only
            // needs to drop the instances that disappeared, invalidated like upstream)
            foreach (var index in existingIds.Values.OrderByDescending(i => i))
            {
                _instances[index].Invalidate();
                _instances.RemoveAt(index);
                removedAny = true;
            }
        }
        if (removedAny)
        {
            InstanceListChanged?.Invoke();
        }
        if (newList.Count != 0)
        {
            Add(newList);
        }
        MigrateTotalPlayTime();
        return InstListError.NoError;
    }

    public void SaveNow()
    {
        foreach (var inst in _instances)
        {
            inst.SaveNow();
        }
    }

    /// <summary>O(n) lookup by folder id or instance uuid.</summary>
    public BaseInstance? GetInstanceById(string id)
    {
        if (id.Length == 0)
        {
            return null;
        }
        foreach (var inst in _instances)
        {
            if (inst.Id() == id || inst.Uuid == id)
            {
                return inst;
            }
        }
        return null;
    }

    /// <summary>O(n) lookup by managed-pack name.</summary>
    public BaseInstance? GetInstanceByManagedName(string managedName)
    {
        if (managedName.Length == 0)
        {
            return null;
        }
        foreach (var inst in _instances)
        {
            if (inst.GetManagedPackName() == managedName)
            {
                return inst;
            }
        }
        return null;
    }

    public List<string> GetLinkedInstancesById(string id)
    {
        var linkedInstances = new List<string>();
        foreach (var inst in _instances)
        {
            if (inst.IsLinkedToInstanceId(id))
            {
                linkedInstances.Add(inst.Id());
            }
        }
        return linkedInstances;
    }

    // ---------------------------------------------------------------------- group logic

    /// <summary>The group of the instance with the given id ("" when ungrouped).</summary>
    public string GetInstanceGroup(string id)
    {
        var inst = GetInstanceById(id);
        if (inst is null)
        {
            return "";
        }
        return _instanceGroupIndex.TryGetValue(inst.Id(), out var group) ? group : "";
    }

    public void SetInstanceGroup(string id, string name)
    {
        // (upstream normalizes an empty non-null QString to null; the port has one empty value)
        if (name.Length == 0)
        {
            name = "";
        }

        var inst = GetInstanceById(id);
        if (inst is null)
        {
            // qDebug() << "Attempt to set a null instance's group"
            return;
        }

        var changed = false;
        if (_instanceGroupIndex.TryGetValue(inst.Id(), out var current))
        {
            if (current != name)
            {
                DecreaseGroupCount(current);
                _instanceGroupIndex[inst.Id()] = name;
                changed = true;
            }
        }
        else
        {
            changed = true;
            _instanceGroupIndex[id] = name;
        }

        if (changed)
        {
            IncreaseGroupCount(name);
            SaveGroupList();
        }
    }

    /// <summary>The non-empty groups that currently have instances (sorted, like QMap keys).</summary>
    public List<string> GetGroups()
    {
        return _groupNameCache.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    public void DeleteGroup(string name)
    {
        _groupNameCache.Remove(name);
        _collapsedGroups.Remove(name);

        var removed = false;
        foreach (var instance in _instances.ToList())
        {
            var instId = instance.Id();
            if (GetInstanceGroup(instId) == name)
            {
                _instanceGroupIndex.Remove(instId);
                removed = true;
                InstanceDataChanged?.Invoke(instance);
            }
        }
        if (removed)
        {
            SaveGroupList();
        }
    }

    public void RenameGroup(string src, string dst)
    {
        _groupNameCache.Remove(src);
        if (_collapsedGroups.Remove(src))
        {
            _collapsedGroups.Add(dst);
        }

        var modified = false;
        foreach (var instance in _instances)
        {
            var instId = instance.Id();
            if (GetInstanceGroup(instId) == src)
            {
                _instanceGroupIndex[instId] = dst;
                IncreaseGroupCount(dst);
                modified = true;
                InstanceDataChanged?.Invoke(instance);
            }
        }
        if (modified)
        {
            SaveGroupList();
        }
    }

    public bool IsGroupCollapsed(string group) => _collapsedGroups.Contains(group);

    /// <summary>Port of the on_GroupStateChanged slot.</summary>
    public void SetGroupCollapsed(string group, bool collapsed)
    {
        if (collapsed)
        {
            _collapsedGroups.Add(group);
        }
        else
        {
            _collapsedGroups.Remove(group);
        }
        SaveGroupList();
    }

    // ---------------------------------------------------------------- instance deletion

    public void DeleteInstance(string id)
    {
        var inst = GetInstanceById(id);
        if (inst is null)
        {
            // qWarning: no such instance present (deleted externally?)
            return;
        }

        var cachedGroupId = _instanceGroupIndex.GetValueOrDefault(id, "");

        if (_instanceGroupIndex.Remove(id))
        {
            DecreaseGroupCount(cachedGroupId);
            SaveGroupList();
        }

        if (!FileSystem.DeletePath(inst.InstanceRoot()))
        {
            // qWarning: deletion not completely successful
            return;
        }

        // (the C++ leaves the list update to the directory watcher; without one the port
        //  drops the instance from the list directly)
        _instances.Remove(inst);
        InstanceListChanged?.Invoke();
    }

    // ---------------------------------------------------------- staging / instance tasks

    /// <summary>
    /// Wrap an instance creation task in the staging machinery: the task runs inside a
    /// temporary folder that is committed into the instance dir on success (with the C++
    /// exponential backoff around the commit, defending against antivirus file locks).
    /// </summary>
    public FlintTask WrapInstanceTask(InstanceTask task)
    {
        return new InstanceStaging(this, task, _globalSettings);
    }

    /// <summary>
    /// Create a new empty staging area for instance creation and return its path
    /// (null when no staging folder could be created).
    /// </summary>
    public string? GetStagedInstancePath(string targetDir)
    {
        var root = PrimaryDir();
        if (targetDir.Length != 0)
        {
            if (!_instDirs.Contains(targetDir) || !Directory.Exists(targetDir))
            {
                // qCritical: requested instance directory is no longer configured or accessible
                return null;
            }
            root = targetDir;
        }
        var tempRoot = FileSystem.PathCombine(root, ".tmp");

        string result;
        var tries = 0;
        do
        {
            if (++tries > 256)
            {
                return null;
            }
            // QUuid::createUuid().toString(QUuid::Id128).left(6)
            var key = Guid.NewGuid().ToString("N")[..6];
            result = FileSystem.PathCombine(tempRoot, key);
        } while (Directory.Exists(result) || File.Exists(result));

        if (!FileSystem.EnsureFolderPathExists(result))
        {
            return null;
        }
        return result;
    }

    /// <summary>
    /// Commit the staging area given by <paramref name="path"/> — used when creation
    /// succeeds. <paramref name="shouldOverride"/> (from the task) merges the staging folder
    /// over an existing instance instead of moving it to a fresh id.
    /// </summary>
    public bool CommitStagedInstance(string path, InstanceTask instanceTask, string groupName)
    {
        string instId;

        var shouldOverride = instanceTask.ShouldOverride;

        var targetDir = instanceTask.TargetDir;
        if (targetDir.Length != 0 && !_instDirs.Contains(targetDir))
        {
            // qCritical: target directory for instance is no longer configured or accessible
            return false;
        }
        if (targetDir.Length == 0)
        {
            targetDir = PrimaryDir();
        }

        if (shouldOverride)
        {
            instId = instanceTask.OriginalInstanceID;
        }
        else
        {
            instId = FileSystem.DirNameFromString(instanceTask.ModifiedName(), _instDirs);
        }

        if (instId.Length == 0)
        {
            // Q_ASSERT(!instID.isEmpty())
            return false;
        }

        var destination = FileSystem.PathCombine(targetDir, instId);

        if (shouldOverride)
        {
            // FS::overrideFolder: merge-copy the staging folder over the destination
            if (!FileSystem.EnsureFolderPathExists(destination))
            {
                return false;
            }
            if (!FileSystem.Copy(path, destination, overwrite: true))
            {
                // qWarning() << "Failed to override" << path << "to" << destination
                return false;
            }
        }
        else
        {
            if (!FileSystem.Move(path, destination))
            {
                // qWarning() << "Failed to move" << path << "to" << destination
                return false;
            }

            _instanceGroupIndex[instId] = groupName;
            IncreaseGroupCount(groupName);
            _instanceRootDirMap[instId] = targetDir;
        }

        _instanceSet.Add(instId);

        InstanceListChanged?.Invoke();
        InstanceSelectRequest?.Invoke(instId);

        SaveGroupList();
        return true;
    }

    // -------------------------------------------------------------- instance dirs change

    /// <summary>Port of the on_InstFolderChanged slot as an explicit method.</summary>
    public void SetInstanceDirs(IEnumerable<string> dirs)
    {
        var newDirs = new List<string>();
        foreach (var dir in dirs)
        {
            if (dir.Length == 0)
            {
                continue;
            }
            FileSystem.EnsureFolderPathExists(dir);
            var canonical = QDirCanonicalPath(dir);
            if (canonical.Length != 0 && !newDirs.Contains(canonical))
            {
                newDirs.Add(canonical);
            }
        }

        if (newDirs.SequenceEqual(_instDirs, StringComparer.Ordinal))
        {
            return;
        }

        if (_groupsLoaded)
        {
            SaveGroupList();
        }
        _instDirs.Clear();
        _instDirs.AddRange(newDirs);
        _groupsLoaded = false;
        _instances.Clear();
        // (upstream emits instancesChanged; the connected providerUpdated reloads right away)
        InstanceListChanged?.Invoke();
        LoadList();
    }

    // ---------------------------------------------------------------------- discovery

    private List<string> DiscoverInstances()
    {
        var outList = new List<string>();
        _instanceRootDirMap.Clear();
        foreach (var rootDir in _instDirs)
        {
            var rootInfo = new DirectoryInfo(rootDir);
            if (!rootInfo.Exists)
            {
                continue;
            }
            // QDirIterator(rootDir, Dirs | NoDot | NoDotDot | Readable | Hidden, FollowSymlinks)
            var options = new EnumerationOptions
            {
                AttributesToSkip = 0,
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
            };
            foreach (var entry in rootInfo.EnumerateDirectories("*", options))
            {
                var subDir = entry.FullName;
                if (!File.Exists(FileSystem.PathCombine(subDir, "instance.cfg")))
                {
                    continue;
                }
                // if it is a symlink, ignore it if it goes to ANY configured instance
                var finalTarget = entry.ResolveLinkTarget(returnFinalTarget: true);
                if (finalTarget is not null)
                {
                    var targetCanonical = QDirCanonicalPath(finalTarget.FullName);
                    var pointsIntoAnyRoot = _instDirs.Any(otherRoot =>
                    {
                        var canonicalRoot = QDirCanonicalPath(otherRoot);
                        return canonicalRoot.Length != 0 &&
                               targetCanonical.StartsWith(canonicalRoot, StringComparison.Ordinal);
                    });
                    if (pointsIntoAnyRoot)
                    {
                        // qDebug: ignoring symlink that leads into a configured instance root
                        continue;
                    }
                }
                var id = entry.Name;
                if (_instanceRootDirMap.ContainsKey(id))
                {
                    // qWarning: duplicate instance ID found - already claimed, skipping
                    continue;
                }
                _instanceRootDirMap[id] = rootDir;
                outList.Add(id);
            }
        }
        _instanceSet.Clear();
        foreach (var id in outList)
        {
            _instanceSet.Add(id);
        }
        _instancesProbed = true;
        return outList;
    }

    private string RootDirOf(string id)
    {
        return _instanceRootDirMap.GetValueOrDefault(id, PrimaryDir());
    }

    private BaseInstance? LoadInstance(string id)
    {
        if (!_groupsLoaded)
        {
            LoadGroupList();
        }

        var instanceRoot = FileSystem.PathCombine(RootDirOf(id), id);
        var instanceSettings = new IniSettingsObject(FileSystem.PathCombine(instanceRoot, "instance.cfg"));

        instanceSettings.RegisterSetting("InstanceType", "");

        var instType = Variant.AsString(instanceSettings.Get("InstanceType"));
        if (instType.Length != 0 && instType != "OneSix")
        {
            // Upstream (this revision) bails out with nullptr here: the folder is not listed
            // at all. Older MultiMC had dedicated Legacy/OneSixOld instance classes; the port
            // keeps such folders visible as broken instances instead (see the file header).
            return new NullInstance(_globalSettings, instanceSettings, instanceRoot);
        }

        var inst = new MinecraftInstance(_globalSettings, instanceSettings, instanceRoot);
        return inst;
    }

    private void Add(IEnumerable<BaseInstance> list)
    {
        foreach (var inst in list)
        {
            inst.PropertiesChanged += () => InstanceDataChanged?.Invoke(inst);
            _instances.Add(inst);
        }
        InstanceListChanged?.Invoke();
    }

    // -------------------------------------------------------------------- groups file

    private string GroupFileLocation()
    {
        if (GroupFilePath.Length != 0)
        {
            return GroupFilePath;
        }
        // QDir::current().filePath("instgroups.json")
        return Path.Combine(Directory.GetCurrentDirectory(), "instgroups.json");
    }

    private void SaveGroupList()
    {
        if (!_instancesProbed)
        {
            // qDebug: group saving prevented because we don't know the full list of instances yet
            return;
        }

        var groupFileName = GroupFileLocation();

        // C++ QMap<QString, QSet<QString>> reverseGroupMap; the port uses sorted containers
        // for deterministic output
        var reverseGroupMap = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var (id, group) in _instanceGroupIndex)
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
            set.Add(id);
        }

        var toplevel = new JsonObject();
        toplevel["formatVersion"] = JsonValue.Create("1"); // QJsonValue(QString("1")) — a string!
        var groupsArr = new JsonObject();
        foreach (var (name, list) in reverseGroupMap)
        {
            var groupObj = new JsonObject();
            var instanceArr = new JsonArray();
            groupObj["hidden"] = _collapsedGroups.Contains(name);
            foreach (var item in list)
            {
                instanceArr.Add((JsonNode)JsonValue.Create(item)!); // non-generic Add: AOT/trimming clean
            }
            groupObj["instances"] = instanceArr;
            groupsArr[name] = groupObj;
        }
        toplevel["groups"] = groupsArr;
        // empty string represents ungrouped "group"
        if (_collapsedGroups.Contains(""))
        {
            var ungrouped = new JsonObject();
            ungrouped["hidden"] = true;
            toplevel["ungrouped"] = ungrouped;
        }
        try
        {
            FileSystem.Write(groupFileName, Encoding.UTF8.GetBytes(QtJson.ToIndentedString(toplevel) + "\n"));
        }
        catch (Exception e) when (e is FileSystemException or IOException or UnauthorizedAccessException
                                  or NotSupportedException)
        {
            // qCritical: failed to write instance group file
        }
    }

    private void LoadGroupList()
    {
        _instanceGroupIndex.Clear();
        _groupNameCache.Clear();
        _collapsedGroups.Clear();

        var migratingLegacyGroups = false;

        // If there's no group file, try the legacy location.
        var groupFileName = GroupFileLocation();
        if (!File.Exists(groupFileName))
        {
            var legacyGroupFileName = FileSystem.PathCombine(PrimaryDir(), "instgroups.json");
            if (!File.Exists(legacyGroupFileName))
            {
                return;
            }
            groupFileName = legacyGroupFileName;
            migratingLegacyGroups = true;
        }

        byte[] jsonData;
        try
        {
            jsonData = FileSystem.Read(groupFileName);
        }
        catch (FileSystemException)
        {
            // qCritical: failed to read instance group file
            return;
        }

        JsonDocument jsonDoc;
        try
        {
            jsonDoc = JsonDocument.Parse(jsonData);
        }
        catch (JsonException)
        {
            // qCritical: failed to parse instance group file
            return;
        }

        using (jsonDoc)
        {
            var rootObj = jsonDoc.RootElement;

            // if the root of the json wasn't an object, fail
            if (rootObj.ValueKind != JsonValueKind.Object)
            {
                // qWarning: invalid group file. Root entry should be an object.
                return;
            }

            // Make sure the format version matches, otherwise fail.
            // (QJsonValue::toVariant().toInt() accepts both numbers and numeric strings)
            if (!TryGetFormatVersion(rootObj, out var formatVersion) || formatVersion != GroupFileFormatVersion)
            {
                return;
            }

            // Get the groups. if it's not an object, fail
            if (!rootObj.TryGetProperty("groups", out var groupsValue) || groupsValue.ValueKind != JsonValueKind.Object)
            {
                // qWarning: invalid group list JSON: 'groups' should be an object.
                return;
            }

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
                    _collapsedGroups.Add(groupName);
                }

                foreach (var value in instancesValue.EnumerateArray())
                {
                    // QJsonValue::toString() yields "" for non-strings
                    var instanceId = value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
                    _instanceGroupIndex[instanceId] = groupName;
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
                // empty string represents ungrouped "group"
                _collapsedGroups.Add("");
            }
            _groupsLoaded = true;
        }

        if (migratingLegacyGroups)
        {
            SaveGroupList();
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
                version = value.GetInt32();
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
        _groupNameCache.TryGetValue(group, out var count);
        _groupNameCache[group] = count + 1;
    }

    private void DecreaseGroupCount(string group)
    {
        if (group.Length == 0)
        {
            return;
        }
        _groupNameCache.TryGetValue(group, out var count);
        count--;
        if (count < 1)
        {
            _groupNameCache.Remove(group);
            _collapsedGroups.Remove(group);
        }
        else
        {
            _groupNameCache[group] = count;
        }
    }

    // ------------------------------------------------------------------ playtime migration

    private void MigrateTotalPlayTime()
    {
        if (PlaytimeSettings is null)
        {
            return; // no APPLICATION->playtimeSettings() to migrate into
        }
        if (Variant.AsBool(PlaytimeSettings.Get("TotalPlayTimeMigrated")))
        {
            return;
        }

        long existingTotal = 0;
        foreach (var inst in _instances)
        {
            if (inst.CountTimePlayed())
            {
                existingTotal += inst.TotalTimePlayed();
            }
        }

        var current = Variant.AsLong(PlaytimeSettings.Get("TotalPlayTime"));
        PlaytimeSettings.Set("TotalPlayTime", current + existingTotal);
        PlaytimeSettings.Set("TotalPlayTimeMigrated", true);
    }

    // ------------------------------------------------------------------------ helpers

    /// <summary>QDir(path).canonicalPath(): "" when the path does not exist.</summary>
    private static string QDirCanonicalPath(string dir)
    {
        if (!Directory.Exists(dir) && !File.Exists(dir))
        {
            return "";
        }
        try
        {
            var full = Path.GetFullPath(dir);
            return full.Length > 1 ? full.TrimEnd('/', Path.DirectorySeparatorChar) : full;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                  or NotSupportedException)
        {
            return "";
        }
    }

    // ------------------------------------------------------------------ staging task

    /// <summary>
    /// Port of the InstanceStaging helper: runs the child task inside a staging folder and
    /// commits it with the C++ exponential backoff (1..16, in 500ms steps) around the commit
    /// — the defense against Windows antivirus locking freshly written files.
    /// </summary>
    private sealed class InstanceStaging(InstanceList parent, InstanceTask child, SettingsObject settings) : FlintTask
    {
        private readonly string _stagingPath = parent.GetStagedInstancePath(child.TargetDir) ?? "";

        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            if (_stagingPath.Length == 0)
            {
                EmitFailed("Could not create staging folder");
                return;
            }

            child.StagingPath = _stagingPath;
            child.SetParentSettings(settings);

            await child.StartAsync(cancellationToken);

            if (child.State == TaskState.Succeeded)
            {
                var backoff = MinBackoff;
                while (true)
                {
                    if (parent.CommitStagedInstance(_stagingPath, child, child.Group))
                    {
                        EmitSucceeded();
                        return;
                    }
                    if (backoff == MaxBackoff)
                    {
                        EmitFailed("Failed to commit instance, even after multiple retries. It is being blocked by something.");
                        return;
                    }
                    await Task.Delay(backoff * 500, cancellationToken);
                    backoff = Math.Min(backoff * 2, MaxBackoff);
                }
            }
            if (child.State == TaskState.AbortedByUser)
            {
                FileSystem.DeletePath(_stagingPath);
                EmitAborted();
                return;
            }
            FileSystem.DeletePath(_stagingPath);
            EmitFailed(child.FailReason);
        }

        public override void Abort()
        {
            child.Abort();
        }
    }
}
