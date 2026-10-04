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
//    group view state — since the C8/S5 split into launcher-state.json ("hidden" is machine-
//    local state now; the group file carries the group definitions only).
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
//    fallback inside the primary instance dir. Since CONFIG C8 the canonical file is the
//    config-side groups.json (FlintPaths.GroupsFile) with both legacy instgroups.json
//    anchors as read-compat fallbacks, and the whole group half lives in the
//    InstanceGroupModel (GLOBAL G4) — the location can still be pinned through
//    <see cref="GroupFilePath"/> (tests/embedding).
//  - instance-dir canonicalization uses Path.GetFullPath (QDir::canonicalPath resolves
//    symlinks; there is no cheap portable equivalent and the watcher dedup use is dropped).
//  - on_InstFolderChanged becomes <see cref="SetInstanceDirs"/> and the signal chain it
//    triggered (instancesChanged -> providerUpdated -> loadList) is collapsed into a direct
//    LoadList() call.
//  - migrateTotalPlayTime()'s APPLICATION->playtimeSettings() becomes the injectable
//    <see cref="LauncherState"/> (launcher-state.json since S5 — CONFIG C9 retired the
//    settings keys); when it is null the migration is skipped.
//  - instance order inside a group of instgroups.json: QSet iteration is unordered upstream,
//    the port writes ids sorted (ordinal) for deterministic output.
//  - CommitStagedInstance's override path re-implements FS::overrideFolder as an
//    overwrite-copy of the staging folder over the destination.
using System.Globalization;
using System.Text.Json;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Launch;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Net;
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
    private const int MinBackoff = 1;
    private const int MaxBackoff = 16;

    private readonly List<string> _instDirs = [];

    private readonly List<BaseInstance> _instances = [];
    // C++ m_instanceRootDirMap: id -> instance dir that provided it
    private readonly Dictionary<string, string> _instanceRootDirMap = new(StringComparer.Ordinal);
    private readonly HashSet<string> _instanceSet = new(StringComparer.Ordinal);

    // D2 inode index: instance uuid <-> the stat(2) inode of its instance dir. Rebuilt on
    // every LoadList ("索引随 LoadList 重建"); empty on platforms without inode support
    // (Windows StatInode = 0 — the addressing chain skips the inode link).
    private readonly Dictionary<string, long> _inodeByUuid = new(StringComparer.Ordinal);
    private readonly Dictionary<long, string> _uuidByInode = new();

    // G4: the group model is the sole owner of the config-side group file (uuid members)
    // and the state-side collapse flags; the list only translates instance handles.
    private readonly InstanceGroupModel _groups;

    private bool _groupsLoaded;
    private bool _instancesProbed;

    public InstanceList(IEnumerable<string> instDirs)
    {
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

        _groups = new InstanceGroupModel(
            CanonicalGroupFile,
            LegacyGroupFiles,
            () => LauncherState,
            canPersist: () => _instancesProbed);
    }

    /// <summary>The typed instance-layer environment (D4): the user-layer flint.json store
    /// plus the instance.json / instance-state.json type infos. The C++ passed the global
    /// SettingsObject here; the typed stack replaced it (CONFIG C6/C7). Must be stamped
    /// before LoadList — discovery cannot build instances without it.</summary>
    public Core.Config.InstanceEnvironment? Environment { get; set; }

    /// <summary>Injected data-directory layout — stamped onto discovered instances (their
    /// asset/library/java anchors) and used for instgroups.json. Set by the composition root.</summary>
    public FlintPaths? Paths { get; set; }

    /// <summary>Injected metadata cache — stamped onto discovered instances (library and
    /// asset-index download caches, metadata entities).</summary>
    public HttpMetaCache? MetaCache { get; set; }

    /// <summary>Injected launch-pipeline dependencies — stamped onto discovered instances
    /// (the base LaunchServices their CreateLaunchTask builds from). Set by the composition
    /// root next to Paths/MetaCache.</summary>
    public LaunchServices? LaunchServices { get; set; }

    // ---------------------------------------------------------------- events (Qt signals)

    /// <summary>Port of the instancesChanged() signal (the model reset replacement).</summary>
    public event Action? InstanceListChanged;

    /// <summary>Merged port of the propertiesChanged()->dataChanged() per-instance glue.</summary>
    public event Action<BaseInstance>? InstanceDataChanged;

    /// <summary>Port of the instanceSelectRequest() signal.</summary>
    public event Action<string>? InstanceSelectRequest;

    // --------------------------------------------------------------------- configuration

    /// <summary>
    /// The launcher runtime state (launcher-state.json, S5) — the one-shot TotalPlayTime
    /// migration and the group collapse ("hidden") persistence land here; null skips both
    /// (no state root in a bare fixture).
    /// </summary>
    public Core.State.LauncherStateStore? LauncherState { get; set; }

    /// <summary>
    /// Overrides the C8 groups-file location (tests/embedding). When set, it IS the
    /// canonical file (only the primary-instance-dir legacy copy still falls back).
    /// Empty (default) uses the config-side anchors: &lt;config-root&gt;/groups.json
    /// (FlintPaths.GroupsFile, C8) with the legacy data-root instgroups.json and the
    /// primary-instance-dir instgroups.json as read-compat fallbacks.
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
        RebuildInodeIndex(); // D2: the inode index follows every (re)discovery
        ConvertLinkedInstanceReferences(); // D3: persisted link references are uuids
        if (_groupsLoaded)
        {
            // D3: discovery is complete — dirname members resolve now (the model was loaded
            // earlier in this session with some references unresolvable; without this the
            // conversion waits for the next process). Never forces the lazy group load.
            _groups.ConvertUnresolvedMembers(ResolveMemberUuid);
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

    /// <summary>
    /// The D2 instance addressing chain (inode → uuid → dirname) — the SINGLE entry point
    /// through which every endpoint resolves an <c>{ref}</c> parameter (GLOBAL §1 bans the
    /// direct GetInstanceById use at the API boundary; this is its only caller shape there).
    /// Unresolved references yield null (the endpoint answers 404).
    /// </summary>
    /// <remarks>
    /// Layer semantics (DATA D2):
    ///  - <b>inode</b>: first link. Only a pure-numeric reference can name an inode; the
    ///    index is rebuilt with every LoadList and is empty on platforms without inode
    ///    support (Windows StatInode returns 0), so the chain transparently skips the link.
    ///    DOCUMENTED BOUNDARY (D2): a pure-numeric DIRECTORY name that collides with a live
    ///    inode resolves to the INODE — the directory loses. Probability tends to zero and
    ///    the collision is resolved deterministically (inode wins) rather than left ambiguous.
    ///    The index is local-only, never persisted: a rename does not change the inode, so
    ///    references survive renames across restarts; only cross-filesystem/machine moves
    ///    and delete+recreate (inode reuse) invalidate it — persistent references are
    ///    therefore always UUIDs.
    ///  - <b>uuid</b>: the instance metadata's "uuid" key — the only form allowed in
    ///    persisted references (group file members, linkedInstances).
    ///  - <b>dirname</b>: the compatibility last link (and the display form); old clients
    ///    keep working through it.
    /// </remarks>
    public BaseInstance? ResolveInstance(string reference)
    {
        if (reference.Length == 0)
        {
            return null;
        }
        // inode link: a numeric miss falls through (the reference may be a numeric dirname
        // whose inode is not tracked, or the platform may not track inodes at all)
        if (long.TryParse(reference, NumberStyles.Integer, CultureInfo.InvariantCulture, out var inode) &&
            _uuidByInode.TryGetValue(inode, out var inodeUuid) &&
            FindByUuidOrDirname(inodeUuid) is { } byInode)
        {
            return byInode;
        }
        return FindByUuidOrDirname(reference);
    }

    /// <summary>The instance directory's stat inode (D2), 0 when unavailable (Windows, or
    /// the directory vanished since LoadList).</summary>
    public long InodeOf(BaseInstance inst) =>
        _inodeByUuid.GetValueOrDefault(inst.Uuid, 0);

    /// <summary>D2: re-stat every instance dir; the index is local-only and never
    /// persisted, so a rebuild is always authoritative.</summary>
    private void RebuildInodeIndex()
    {
        _inodeByUuid.Clear();
        _uuidByInode.Clear();
        foreach (var inst in _instances)
        {
            var inode = FileTransfer.StatInode(inst.InstanceRoot());
            if (inode == 0)
            {
                continue; // platform without inode support (Windows) or a vanished dir
            }
            _inodeByUuid[inst.Uuid] = inode;
            _uuidByInode[inode] = inst.Uuid;
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

    /// <summary>The uuid/directname half of the D2 chain: uuid first (the persistent
    /// handle), dirname second (compat).</summary>
    private BaseInstance? FindByUuidOrDirname(string reference)
    {
        foreach (var inst in _instances)
        {
            if (inst.Uuid == reference || inst.Id() == reference)
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
        // D3: the persisted reference form is the uuid; an entry written before its target
        // was discovered (or between LoadList rounds) may still carry the dirname until the
        // next conversion pass — match both forms, the same tolerance GroupOfInstance gives
        // the group members.
        var uuid = FindByUuidOrDirname(id)?.Uuid;
        var linkedInstances = new List<string>();
        foreach (var inst in _instances)
        {
            if (inst.GetLinkedInstances()
                    .Any(item => item == id || (uuid is { Length: > 0 } && item == uuid)))
            {
                linkedInstances.Add(inst.Id());
            }
        }
        return linkedInstances;
    }

    /// <summary>D3 import conversion for linkedInstances (the group-member precedent, D3:
    /// "导入时转换" — the import lands dirnames, the discovery round converts them): a
    /// dirname entry becomes the referenced instance's uuid when the now-complete discovery
    /// can resolve it; unknown entries are kept verbatim and retried on the next LoadList.
    /// A changed list persists through SetLinkedInstances — on a Prism-format instance that
    /// write is the sanctioned dual-track conversion (the in-memory cfg import materializes
    /// as instance.json); a converged list costs no write.</summary>
    private void ConvertLinkedInstanceReferences()
    {
        foreach (var inst in _instances)
        {
            var linked = inst.GetLinkedInstances();
            if (linked.Count == 0)
            {
                continue;
            }
            var converted = new List<string>(linked.Count);
            var changed = false;
            foreach (var entry in linked)
            {
                // uuid/dirname addressing only — a numeric entry must not resolve through
                // the inode link (same boundary as ResolveMemberUuid)
                var replacement = FindByUuidOrDirname(entry)?.Uuid;
                if (replacement is not { Length: > 0 })
                {
                    converted.Add(entry);
                    continue;
                }
                changed |= replacement != entry;
                // the uuid form may already sit in a half-converted list — keep one
                if (!converted.Contains(replacement))
                {
                    converted.Add(replacement);
                }
            }
            if (changed || converted.Count != linked.Count)
            {
                inst.SetLinkedInstances(converted);
            }
        }
    }

    // ---------------------------------------------------------------------- group logic
    // G4: InstanceGroupModel is the SOLE owner of both halves (config groups file + state
    // hidden); the list only translates instance handles (dirname/uuid) and raises the
    // per-instance change notifications around the model's answers.

    /// <summary>The group of the instance with the given id ("" when ungrouped).</summary>
    public string GetInstanceGroup(string id)
    {
        var inst = GetInstanceById(id);
        if (inst is null)
        {
            return "";
        }
        EnsureGroupsLoaded();
        // the member key is the uuid (D3); the dirname fallback covers a staged commit's
        // transient not-yet-converted member
        return _groups.GroupOfInstance(inst.Uuid, inst.Id());
    }

    public void SetInstanceGroup(string id, string name)
    {
        var inst = GetInstanceById(id);
        if (inst is null)
        {
            // qDebug() << "Attempt to set a null instance's group"
            return;
        }
        EnsureGroupsLoaded();
        if (_groups.SetMemberGroup(inst.Uuid, name))
        {
            InstanceDataChanged?.Invoke(inst);
        }
        // dedupe: a staged commit whose uuid was unprovable left a dirname-keyed member —
        // assigned under the uuid now, so the transient form goes (the uuid member keeps
        // the count above zero, so this cannot uncollapse the group)
        _groups.RemoveMember(inst.Id());
    }

    /// <summary>The non-empty groups that currently have instances (sorted, like QMap keys).</summary>
    public List<string> GetGroups()
    {
        EnsureGroupsLoaded();
        return _groups.GroupNames();
    }

    public void DeleteGroup(string name)
    {
        EnsureGroupsLoaded();
        var affected = _groups.DeleteGroup(name);
        foreach (var member in affected)
        {
            if (FindByUuidOrDirname(member) is { } inst)
            {
                InstanceDataChanged?.Invoke(inst);
            }
        }
    }

    public void RenameGroup(string src, string dst)
    {
        EnsureGroupsLoaded();
        var affected = _groups.RenameGroup(src, dst);
        foreach (var member in affected)
        {
            if (FindByUuidOrDirname(member) is { } inst)
            {
                InstanceDataChanged?.Invoke(inst);
            }
        }
    }

    public bool IsGroupCollapsed(string group)
    {
        EnsureGroupsLoaded();
        return _groups.IsCollapsed(group);
    }

    /// <summary>Port of the on_GroupStateChanged slot. Only the state side changes (the
    /// collapse state is launcher-state.json's since C8/S5 — the group file no longer
    /// carries "hidden"), so there is no config write to order against.</summary>
    public void SetGroupCollapsed(string group, bool collapsed)
    {
        EnsureGroupsLoaded();
        _groups.SetCollapsed(group, collapsed);
    }

    /// <summary>The group definitions load lazily on first group access (it used to hang
    /// off the first instance load, but the D3 uuid conversion needs the COMPLETE
    /// discovery). Retries until a file was parsed — a missing file may still appear
    /// (e.g. a Prism import landing instgroups.json mid-session).</summary>
    private void EnsureGroupsLoaded()
    {
        if (!_groupsLoaded)
        {
            _groupsLoaded = _groups.Load(ResolveMemberUuid);
        }
    }

    /// <summary>D3 reference conversion, shared by both persisted-reference forms: a dirname
    /// (the legacy and Prism-import form — group-file members and linkedInstances entries)
    /// becomes the instance's uuid; unknown references return null and are kept verbatim
    /// (the group load retries; the linkedInstances pass retries at the next LoadList).
    /// Also stamped onto every discovered instance as <see cref="BaseInstance.UuidResolver"/>
    /// so the write path resolves dirname inputs. The addressing here is deliberately
    /// uuid/dirname only — a numeric member must not resolve through the inode link.</summary>
    private string? ResolveMemberUuid(string member) =>
        member.Length == 0 ? null : GetInstanceById(member)?.Uuid;

    /// <summary>The canonical C8 groups file (the model's write target): the GroupFilePath
    /// override when pinned, else the config-side groups.json, else the legacy CWD anchor
    /// of bare fixtures.</summary>
    private string CanonicalGroupFile()
    {
        if (GroupFilePath.Length != 0)
        {
            return GroupFilePath;
        }
        if (Paths is { } paths)
        {
            return paths.GroupsFile;
        }
        // legacy CWD anchor — bare fixtures only (the process working directory is not
        // load-bearing anywhere else)
        return Path.Combine(Directory.GetCurrentDirectory(), "groups.json");
    }

    /// <summary>The read-compat instgroups.json anchors, probed in order when the canonical
    /// file is missing: the pre-C8 data-root copy (the Prism import target) and the
    /// primary-instance-dir copy upstream used before there was a launcher data dir.</summary>
    private IReadOnlyList<string> LegacyGroupFiles()
    {
        var legacy = new List<string>();
        if (Paths is { } paths)
        {
            legacy.Add(paths.InstgroupsFile);
        }
        legacy.Add(FileSystem.PathCombine(PrimaryDir(), "instgroups.json"));
        return legacy;
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

        // drop the group membership under BOTH reference forms (the uuid member plus a
        // possibly not-yet-converted dirname member from a staged commit)
        EnsureGroupsLoaded();
        _groups.RemoveMember(inst.Uuid);
        _groups.RemoveMember(inst.Id());

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
        return new InstanceStaging(this, task);
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

            _instanceRootDirMap[instId] = targetDir;
        }

        _instanceSet.Add(instId);

        if (groupName.Length != 0)
        {
            // D3: the group file stores UUID references, but the committed folder is not
            // discovered yet — probe its metadata for the uuid; when unreadable the member
            // goes in under the raw dirname and the next group load converts it.
            EnsureGroupsLoaded();
            var member = ProbeInstanceUuid(destination);
            if (member.Length == 0)
            {
                member = instId;
            }
            _groups.SetMemberGroup(member, groupName);
        }

        InstanceListChanged?.Invoke();
        InstanceSelectRequest?.Invoke(instId);

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
            _groups.Save();
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
                // D4 dual-track discovery: a Flint-format directory carries instance.json,
                // a Prism-format one instance.cfg — both are instances (the cfg stays
                // read-only until the instance's first write converts it)
                if (!File.Exists(FileSystem.PathCombine(subDir, "instance.json")) &&
                    !File.Exists(FileSystem.PathCombine(subDir, "instance.cfg")))
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
        var environment = Environment ??
            throw new InvalidOperationException(
                "InstanceList.Environment (the typed instance.json stack) must be stamped before LoadList.");

        var instanceRoot = FileSystem.PathCombine(RootDirOf(id), id);
        // the typed layer stack (D4): instance.json when present (Flint format), otherwise
        // the read-only instance.cfg import in memory — no write happens on discovery
        var instanceSettings = new Core.Config.InstanceJsonSettingsObject(instanceRoot, environment);
        // register the discriminator before dispatch (BaseInstance would only register it
        // inside the ctor, which the dispatch below decides)
        instanceSettings.RegisterSetting("InstanceType", "OneSix");

        // the format discriminator: instance.json Misc.InstanceType (default OneSix) or the
        // imported cfg value
        var instType = Variant.AsString(instanceSettings.Get("InstanceType"));
        BaseInstance inst;
        if (instType.Length != 0 && instType != "OneSix")
        {
            // Upstream (this revision) bails out with nullptr here: the folder is not listed
            // at all. Older MultiMC had dedicated Legacy/OneSixOld instance classes; the port
            // keeps such folders visible as broken instances instead (see the file header).
            inst = new NullInstance(instanceSettings, instanceRoot);
        }
        else
        {
            inst = new MinecraftInstance(instanceSettings, instanceRoot)
            {
                Paths = Paths,
                MetaCache = MetaCache,
                LaunchServices = LaunchServices,
                LauncherState = LauncherState,
            };
        }
        // D3: the write-side uuid convention — a dirname handed to AddLinkedInstanceId
        // resolves to the uuid at write time; an unresolvable input appends verbatim and
        // converts at the next LoadList round (same deferral as the group members).
        inst.UuidResolver = ResolveMemberUuid;
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
    // (G4: the file mechanics — load/save/strip/counts — live in InstanceGroupModel now;
    // this type keeps only the location anchors above and the uuid conversion delegate.)

    /// <summary>The uuid of a just-committed instance folder, read straight from its
    /// metadata (instance.json identity.uuid first, else the imported instance.cfg uuid
    /// key — the same precedence discovery uses). Empty = unresolvable: the group member
    /// is then written under the raw dirname and converted at the next group load.</summary>
    private static string ProbeInstanceUuid(string instanceRoot)
    {
        try
        {
            var jsonPath = FileSystem.PathCombine(instanceRoot, "instance.json");
            if (File.Exists(jsonPath))
            {
                using var doc = JsonDocument.Parse(FileSystem.Read(jsonPath));
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("identity", out var identity) &&
                    identity.ValueKind == JsonValueKind.Object &&
                    identity.TryGetProperty("uuid", out var uuid) &&
                    uuid.ValueKind == JsonValueKind.String)
                {
                    return uuid.GetString() ?? "";
                }
                return "";
            }
            var cfgPath = FileSystem.PathCombine(instanceRoot, Core.Config.InstanceIniImporter.LegacyFileName);
            if (File.Exists(cfgPath))
            {
                var ini = new Settings.IniFile();
                return ini.LoadFile(cfgPath) ? ini.GetString("uuid", "") : "";
            }
        }
        catch (Exception e) when (e is JsonException or FileSystemException or IOException
                                  or UnauthorizedAccessException)
        {
            // a broken commit payload simply loses its group assignment until the next load
        }
        return "";
    }

    // ------------------------------------------------------------------ playtime migration

    private void MigrateTotalPlayTime()
    {
        if (LauncherState is null)
        {
            return; // no state root to migrate into
        }
        if (LauncherState.Current.Playtime.Migrated)
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

        // the add happens inside the update so a concurrent writer cannot lose the totals
        // (S5: the former TotalPlayTime/TotalPlayTimeMigrated settings keys, CONFIG C9)
        LauncherState.Update(state => state with
        {
            Playtime = new State.PlaytimeState
            {
                Total = state.Playtime.Total + existingTotal,
                Migrated = true,
            },
        });
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
    private sealed class InstanceStaging(InstanceList parent, InstanceTask child) : FlintTask
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
            // D4: the typed instance environment (user flint.json store + instance.json
            // type infos) replaces the former parent SettingsObject handle
            child.SetParentEnvironment(parent.Environment ??
                throw new InvalidOperationException(
                    "InstanceList.Environment (the typed instance.json stack) must be stamped before staging instances."));

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
