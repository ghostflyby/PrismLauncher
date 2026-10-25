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
// Port of launcher/InstanceList.{h,cpp} (the data/logic halves).
//
// Dropped (Qt UI/infrastructure plumbing):
//  - the QAbstractListModel surface (index/rowCount/data/setData/flags, the drag&drop
//    mime handling and the AdditionalRoles): the instancesChanged() model-reset signal
//    becomes INotifyCollectionChanged (Reset-only — the update model is reconcile-and-
//    swap, never positional deltas) on the list itself, and the per-instance
//    propertiesChanged()->dataChanged() glue becomes INotifyPropertyChanged on
//    BaseInstance;
//  - the QFileSystemWatcher glue (suspendWatch/resumeWatch/instanceDirContentsChanged and
//    the m_watchLevel/m_dirty bookkeeping) is RESTORED as an opt-in tenancy of
//    <see cref="FlintLauncher.Core.IO.DirectorySpace"/> (<see cref="AttachWatcher"/>):
//    external changes coalesce into a debounced LoadList() re-run. Where the C++ watcher
//    died silently when a watched root was renamed away, the port reports the loss to the
//    tenants and re-binds with bounded backoff when the root returns. Default off —
//    demand-driven LoadList() (callers re-run it themselves) stays the fallback.
//  - the trash flow (trashInstance/trashedSomething/undoTrashInstance and the
//    TrashHistoryItem stack): FS::trash needs desktop-shell integration and is not ported.
//    DeleteInstance (hard delete) is ported;
//  - on_GroupStateChanged stays as <see cref="SetGroupCollapsed"/> because it persists the
//    group view state — since the launcher-state.json split ("hidden" is machine-
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
//    fallback inside the primary instance dir. The canonical file is the
//    config-side groups.json with both legacy instgroups.json
//    anchors as read-compat fallbacks, and the whole group half lives in the
//    InstanceGroupModel — the location can still be pinned through
//    <see cref="GroupFilePath"/> (tests/embedding).
//  - instance-dir canonicalization uses Path.GetFullPath (QDir::canonicalPath resolves
//    symlinks; there is no cheap portable equivalent and the watcher dedup use is dropped).
//  - on_InstFolderChanged becomes <see cref="SetInstanceDirs"/> and the signal chain it
//    triggered (instancesChanged -> providerUpdated -> loadList) is collapsed into a direct
//    LoadList() call.
//  - migrateTotalPlayTime()'s APPLICATION->playtimeSettings() becomes the injectable
//    <see cref="LauncherState"/> (the former settings keys moved to
//    launcher-state.json); when it is null the migration is skipped.
//  - instance order inside a group of instgroups.json: QSet iteration is unordered upstream,
//    the port writes ids sorted (ordinal) for deterministic output.
//  - CommitStagedInstance's override path re-implements FS::overrideFolder as an
//    overwrite-copy of the staging folder over the destination.

using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using FlintLauncher.Core.Config;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Launch;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Settings;
using FlintLauncher.Core.State;
using FlintLauncher.Core.Tasks;
using TruePath;
using TruePath.SystemIo;

namespace FlintLauncher.Core.Instances;

/// <summary>Port of the InstListError enum.</summary>
public enum InstListError : byte
{
    NoError = 0,
    UnknownError,
}

public class InstanceList : INotifyCollectionChanged, INotifyPropertyChanged
{
    private const int MinBackoff = 1;
    private const int MaxBackoff = 16;

    private readonly List<AbsolutePath> _instDirs = [];

    private readonly List<BaseInstance> _instances = [];

    // C++ m_instanceRootDirMap: id -> instance dir that provided it
    private readonly Dictionary<string, AbsolutePath> _instanceRootDirMap = new(StringComparer.Ordinal);

    // inode index: instance uuid <-> the identity of its instance dir. Rebuilt on
    // every LoadList; empty on platforms without identity
    // support (Windows StatInode = 0 — the addressing chain skips the identity link).
    private readonly Dictionary<string, DirectoryIdentity> _inodeByUuid = new(StringComparer.Ordinal);
    private readonly Dictionary<DirectoryIdentity, string> _uuidByInode = new();

    // the group model is the sole owner of the config-side group file (uuid members)
    // and the state-side collapse flags; the list only translates instance handles.
    private readonly InstanceGroupModel _groups;

    private bool _groupsLoaded;
    private bool _instancesProbed;

    // the restored watcher tenancy (see the header note): null = demand-driven LoadList
    private DirectorySpace? _watchSpace;
    private int _reconcileRunning;

    private LaunchServices? _launchServices;

    /// <summary>Constructs the list over its materialized instance roots. The list owns
    /// discovery, lifecycle and watching — it deliberately knows nothing about the wider
    /// data layout (it stays with the composition root and the instances):
    /// the group persistence arrives as its two config objects, and the launch-pipeline
    /// half reaches the instances through <paramref name="launchServices"/>.</summary>
    /// <param name="instanceDirs">The materialized instance roots (Ensure semantics —
    /// missing roots are created; the first one is the primary dir).</param>
    /// <param name="environment">The typed instance-layer environment. Required:
    /// discovery cannot build instances without it.</param>
    /// <param name="groupsFile">The canonical groups file (the write target).</param>
    /// <param name="legacyGroupsFiles">The read-compat instgroups probes, in order (the
    /// data-root copy, then the primary-dir copy upstream used).</param>
    /// <param name="launchServices">Optional at construction (the host logger factory
    /// lives in DI); the composition root completes it through the
    /// <see cref="LaunchServices"/> property, which re-stamps discovered instances.</param>
    /// <param name="metaCache">Optional: stamped onto discovered instances (library and
    /// asset-index download caches, metadata entities).</param>
    /// <param name="launcherState">Optional: launcher-state.json — the TotalPlayTime
    /// migration and the group collapse persistence; null skips both.</param>
    public InstanceList(IEnumerable<MaterializedDirectory> instanceDirs,
        InstanceEnvironment environment, AbsolutePath groupsFile,
        IReadOnlyList<AbsolutePath> legacyGroupsFiles,
        LaunchServices? launchServices = null, HttpMetaCache? metaCache = null,
        LauncherStateStore? launcherState = null)
    {
        Environment = environment;
        _launchServices = launchServices;
        MetaCache = metaCache;
        LauncherState = launcherState;

        foreach (var dir in instanceDirs)
        {
            if (!_instDirs.Contains(dir.Path))
            {
                _instDirs.Add(dir.Path);
            }
        }

        _groups = new InstanceGroupModel(groupsFile, legacyGroupsFiles,
            () => LauncherState,
            canPersist: () => _instancesProbed);
    }

    /// <summary>The typed instance-layer environment: the user-layer flint.json store
    /// plus the instance.json / instance-state.json type infos. The C++ passed the global
    /// SettingsObject here; the typed stack replaced it.</summary>
    public InstanceEnvironment Environment { get; }

    /// <summary>Injected metadata cache — stamped onto discovered instances (library and
    /// asset-index download caches, metadata entities).</summary>
    public HttpMetaCache? MetaCache { get; }

    /// <summary>Injected launch-pipeline dependencies — stamped onto discovered instances
    /// (the base LaunchServices their CreateLaunchTask builds from). The composition root
    /// completes it after the host build (its logger factory lives in DI); setting it
    /// re-stamps the already-discovered residents.</summary>
    public LaunchServices? LaunchServices
    {
        get => _launchServices;
        set
        {
            _launchServices = value;
            foreach (var resident in _instances.OfType<MinecraftInstance>())
            {
                resident.LaunchServices = value;
            }
        }
    }

    /// <summary>The launcher runtime state (launcher-state.json) — the one-shot
    /// TotalPlayTime migration and the group collapse ("hidden") persistence land here;
    /// null skips both (no state root in a bare fixture).</summary>
    public LauncherStateStore? LauncherState { get; }

    // ------------------------------------------------------------- events (BCL notifications)

    /// <summary>INCC with Reset-only semantics: the update model is reconcile-and-swap (the
    /// watcher coalesces external changes into a full reload), never positional deltas —
    /// Reset carries that contract to every standard .NET consumer. Raised at most once per
    /// mutating pass, on the reconcile thread; consumers marshal.</summary>
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    /// <summary>INPC for the aggregate view ("Count"/"Instances"); per-instance data
    /// changes surface through the instance's own INotifyPropertyChanged.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Port of the instanceSelectRequest() signal (a UI command, not a change
    /// notification — outside the INCC/INPC scope).</summary>
    public event Action<string>? InstanceSelectRequest;

    /// <summary>Raises Reset plus the aggregate INPC pair. Called at most once per mutating
    /// pass (the C++ instancesChanged() signal, in standard clothing), outside the model's
    /// internal state transitions.</summary>
    private void RaiseReset()
    {
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Instances)));
    }

    // ------------------------------------------------------------------- list accessors

    public AbsolutePath PrimaryDir() => _instDirs.FirstOrDefault();

    /// <summary>Port of InstanceList::instanceDirs (all configured instance roots).</summary>
    public IReadOnlyList<AbsolutePath> InstanceDirs() => _instDirs;

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
                newList.Add(inst);
            }
        }

        if (existingIds.Count != 0)
        {
            // (the C++ removes the model rows in contiguous ranges; the plain list only
            // needs to drop the instances that disappeared, invalidated like upstream)
            foreach (var index in existingIds.Values.OrderByDescending(i => i))
            {
                // the directory is gone — the instance's folder-model watches would
                // otherwise retry against it forever
                _instances[index].ReleaseWatchTenancy();
                _instances[index].Invalidate();
                _instances.RemoveAt(index);
                removedAny = true;
            }
        }

        if (newList.Count != 0)
        {
            Add(newList);
        }

        // at most ONE reset per reconcile pass (the pre-interface version raised twice when
        // a pass both removed and added); a pass with no membership change is silent
        if (removedAny || newList.Count != 0)
        {
            RaiseReset();
        }

        RebuildInodeIndex();               // the inode index follows every (re)discovery
        ConvertLinkedInstanceReferences(); // persisted link references are uuids
        if (_groupsLoaded)
        {
            // discovery is complete — dirname members resolve now (the model was loaded
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
    /// The instance addressing chain (inode → uuid → dirname) — the SINGLE entry point
    /// through which every endpoint resolves an <c>{ref}</c> parameter (the direct
    /// GetInstanceById use is banned at the API boundary; this is its only caller shape
    /// there).
    /// Unresolved references yield null (the endpoint answers 404).
    /// </summary>
    /// <remarks>
    /// Layer semantics:
    ///  - <b>inode</b>: first link. Only a pure-numeric reference can name an inode; the
    ///    index is rebuilt with every LoadList and is empty on platforms without inode
    ///    support (Windows StatInode returns 0), so the chain transparently skips the link.
    ///    Documented boundary: a pure-numeric DIRECTORY name that collides with a live
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
            _uuidByInode.TryGetValue(new DirectoryIdentity(inode), out var inodeUuid) &&
            FindByUuidOrDirname(inodeUuid) is { } byInode)
        {
            return byInode;
        }

        return FindByUuidOrDirname(reference);
    }

    /// <summary>The instance directory's stat inode, 0 when unavailable (Windows, or
    /// the directory vanished since LoadList).</summary>
    public long InodeOf(BaseInstance inst) =>
        _inodeByUuid.GetValueOrDefault(inst.Uuid, DirectoryIdentity.Unavailable).Inode;

    /// <summary>Re-stats every instance dir; the index is local-only and never
    /// persisted, so a rebuild is always authoritative.</summary>
    private void RebuildInodeIndex()
    {
        _inodeByUuid.Clear();
        _uuidByInode.Clear();
        foreach (var inst in _instances)
        {
            var identity = DirectoryIdentity.Of(inst.InstanceRoot);
            if (!identity.IsAvailable)
            {
                continue; // platform without identity support (Windows) or a vanished dir
            }

            _inodeByUuid[inst.Uuid] = identity;
            _uuidByInode[identity] = inst.Uuid;
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

    /// <summary>The uuid/directname half of the chain: uuid first (the persistent
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
        // the persisted reference form is the uuid; an entry written before its target
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

    /// <summary>Import conversion for linkedInstances (the group-member precedent —
    /// the import lands dirnames, the discovery round converts them): a
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
    // InstanceGroupModel is the SOLE owner of both halves (config groups file + state
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
        // the member key is the uuid; the dirname fallback covers a staged commit's
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
            inst.NotifyChanged();
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
                inst.NotifyChanged();
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
                inst.NotifyChanged();
            }
        }
    }

    public bool IsGroupCollapsed(string group)
    {
        EnsureGroupsLoaded();
        return _groups.IsCollapsed(group);
    }

    /// <summary>Port of the on_GroupStateChanged slot. Only the state side changes (the
    /// collapse state is launcher-state.json's — the group file no longer
    /// carries "hidden"), so there is no config write to order against.</summary>
    public void SetGroupCollapsed(string group, bool collapsed)
    {
        EnsureGroupsLoaded();
        _groups.SetCollapsed(group, collapsed);
    }

    /// <summary>The group definitions load lazily on first group access (it used to hang
    /// off the first instance load, but the uuid conversion needs the COMPLETE
    /// discovery). Retries until a file was parsed — a missing file may still appear
    /// (e.g. a Prism import landing instgroups.json mid-session).</summary>
    private void EnsureGroupsLoaded()
    {
        if (!_groupsLoaded)
        {
            _groupsLoaded = _groups.Load(ResolveMemberUuid);
        }
    }

    /// <summary>Reference conversion, shared by both persisted-reference forms: a dirname
    /// (the legacy and Prism-import form — group-file members and linkedInstances entries)
    /// becomes the instance's uuid; unknown references return null and are kept verbatim
    /// (the group load retries; the linkedInstances pass retries at the next LoadList).
    /// Also stamped onto every discovered instance as <see cref="BaseInstance.UuidResolver"/>
    /// so the write path resolves dirname inputs. The addressing here is deliberately
    /// uuid/dirname only — a numeric member must not resolve through the inode link.</summary>
    private string? ResolveMemberUuid(string member) =>
        member.Length == 0 ? null : GetInstanceById(member)?.Uuid;

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

        // release the watcher tenancy BEFORE the delete: the folder models' watches
        // dispose now instead of retrying against a directory that stops existing
        inst.ReleaseWatchTenancy();

        if (!FileOps.DeletePath(new AbsolutePath(inst.InstanceRoot)))
        {
            // qWarning: deletion not completely successful
            return;
        }

        // (the C++ leaves the list update to the directory watcher; without one the port
        //  drops the instance from the list directly)
        _instances.Remove(inst);
        RaiseReset();
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
    public AbsolutePath? GetStagedInstancePath(AbsolutePath targetDir)
    {
        var root = PrimaryDir();
        if (root.Value is not { Length: > 0 })
            return null;
        if (targetDir.Value is { Length: > 0 })
        {
            if (!_instDirs.Contains(targetDir) || !targetDir.Exists())
            {
                // qCritical: requested instance directory is no longer configured or accessible
                return null;
            }

            root = targetDir;
        }

        var tempRoot = root / ".tmp";
        AbsolutePath result;
        var tries = 0;
        do
        {
            if (++tries > 256)
            {
                return null;
            }

            // QUuid::createUuid().toString(QUuid::Id128).left(6)
            var key = Guid.NewGuid().ToString("N")[..6];
            result = tempRoot / key;
        } while (result.Exists());

        if (!FileOps.EnsureFolderPathExists(result))
        {
            return null;
        }

        return result;
    }

    /// <summary>
    /// Commit the staging area given by <paramref name="path"/> — used when creation
    /// succeeds. The task's <c>ShouldOverride</c> merges the staging folder over an
    /// existing instance instead of moving it to a fresh id.
    /// </summary>
    public bool CommitStagedInstance(string path, InstanceTask instanceTask, string groupName)
    {
        string instId;

        var shouldOverride = instanceTask.ShouldOverride;

        var targetDir = instanceTask.TargetDir;
        if (targetDir.Value is { Length: > 0 } && !_instDirs.Contains(targetDir))
        {
            // qCritical: target directory for instance is no longer configured or accessible
            return false;
        }

        if (targetDir.Value is not { Length: > 0 })
        {
            targetDir = PrimaryDir();
        }

        if (shouldOverride)
        {
            instId = instanceTask.OriginalInstanceId;
        }
        else
        {
            instId = FileNaming.DirNameFromString(instanceTask.ModifiedName(), _instDirs.Select(dir => new AbsolutePath(dir)));
        }

        if (instId.Length == 0)
        {
            // Q_ASSERT(!instID.isEmpty())
            return false;
        }

        var destination = targetDir / instId;

        if (shouldOverride)
        {
            // FS::overrideFolder: merge-copy the staging folder over the destination
            if (!FileOps.EnsureFolderPathExists(new AbsolutePath(destination)))
            {
                return false;
            }

            if (!FileOps.Copy(new AbsolutePath(path), new AbsolutePath(destination), overwrite: true))
            {
                // qWarning() << "Failed to override" << path << "to" << destination
                return false;
            }
        }
        else
        {
            if (!FileOps.Move(new AbsolutePath(path), new AbsolutePath(destination)))
            {
                // qWarning() << "Failed to move" << path << "to" << destination
                return false;
            }

            _instanceRootDirMap[instId] = targetDir;
        }

        if (groupName.Length != 0)
        {
            // the group file stores UUID references, but the committed folder is not
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

        RaiseReset();
        InstanceSelectRequest?.Invoke(instId);

        return true;
    }

    // -------------------------------------------------------------- instance dirs change

    /// <summary>Opts the list into watcher-driven reloads (the restored C++ watcher glue,
    /// see the header note): <paramref name="space"/> watches every instance root and
    /// external changes coalesce into a debounced LoadList() re-run. A renamed/moved root
    /// re-points the configured root set in place (the discovery reads
    /// <see cref="InstanceDirs"/>, so a stale path would empty the list on the next
    /// reconcile). The space's dispatcher owns serialization with the host's instance
    /// lock. Default (no attachment) keeps the demand-driven reload.</summary>
    public void AttachWatcher(DirectorySpace space)
    {
        _watchSpace = space;
        foreach (var dir in _instDirs)
        {
            if (MaterializedDirectory.Wrap(dir) is { } materialized)
            {
                space.Register(materialized, ReconcileFromWatcher, moved: OnInstanceRootMoved);
            }
        }
    }

    /// <summary>The configured root set follows the watched root's rename — and the
    /// instances reload from scratch: the kept objects hold path-derived state
    /// (InstanceRoot and the settings stack's own root) that cannot be re-pointed in
    /// place, so a stale keep would keep reading the dead location. The reload is
    /// identity-stable: references hold via uuid, and the directory identities (and the
    /// inode index over them) survive the move untouched.</summary>
    private void OnInstanceRootMoved(AbsolutePath oldRoot, AbsolutePath newRoot)
    {
        var index = _instDirs.IndexOf(oldRoot);
        if (index < 0)
        {
            return;
        }

        _instDirs[index] = newRoot;
        foreach (var inst in _instances)
        {
            inst.ReleaseWatchTenancy();
        }

        _instances.Clear();
        LoadList();
    }

    /// <summary>The watcher-side reconcile: LoadList is idempotent, so the coalesced
    /// trailing re-run after the list's own mutations (staging commit, delete) is harmless;
    /// skip-if-busy collapses overlapping fires instead of re-entering a running pass.</summary>
    private void ReconcileFromWatcher()
    {
        if (Interlocked.Exchange(ref _reconcileRunning, 1) != 0)
        {
            return;
        }

        try
        {
            LoadList();
        }
        finally
        {
            Interlocked.Exchange(ref _reconcileRunning, 0);
        }
    }

    /// <summary>Port of the on_InstFolderChanged slot as an explicit method.</summary>
    public void SetInstanceDirs(IEnumerable<AbsolutePath> dirs)
    {
        var newDirs = new List<AbsolutePath>();
        foreach (var dir in dirs)
        {
            if (dir.Value is not { Length: > 0 })
            {
                continue;
            }

            FileOps.EnsureFolderPathExists(dir);
            // the string-era QDirCanonicalPath normalized (GetFullPath) WITHOUT resolving
            // symlinks, so the stored roots stay verbatim (/var/... on macOS is NOT
            // rewritten to /private/var/...) — TruePath's Canonicalize() would, so it must
            // not be used here
            if (!newDirs.Contains(dir))
            {
                newDirs.Add(dir);
            }
        }

        if (newDirs.SequenceEqual(_instDirs))
        {
            return;
        }

        if (_groupsLoaded)
        {
            _groups.Save();
        }

        var oldDirs = _instDirs.ToArray();
        _instDirs.Clear();
        _instDirs.AddRange(newDirs);
        _watchSpace?.SyncTenantRoots(oldDirs, newDirs, ReconcileFromWatcher);
        _groupsLoaded = false;
        // the old roots' instances leave the live list — their folder-model watches must
        // not outlive the directories they were registered on
        foreach (var inst in _instances)
        {
            inst.ReleaseWatchTenancy();
        }

        _instances.Clear();
        // (upstream emits instancesChanged; the connected providerUpdated reloads right away)
        // — the trailing LoadList owns the Reset now
        LoadList();
    }

    // ---------------------------------------------------------------------- discovery

    private List<string> DiscoverInstances()
    {
        var outList = new List<string>();
        _instanceRootDirMap.Clear();
        foreach (var rootDir in _instDirs)
        {
            var rootInfo = new DirectoryInfo(rootDir.Value);
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
                var instDir = new AbsolutePath(entry.FullName);
                // dual-track discovery: a Flint-format directory carries instance.json,
                // a Prism-format one instance.cfg — both are instances (the cfg stays
                // read-only until the instance's first write converts it)
                if (!(instDir / "instance.json").ExistsFile() &&
                    !(instDir / "instance.cfg").ExistsFile())
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
                        var canonicalRoot = QDirCanonicalPath(otherRoot.Value);
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

        _instancesProbed = true;
        return outList;
    }

    private AbsolutePath RootDirOf(string id)
    {
        return _instanceRootDirMap.GetValueOrDefault(id, PrimaryDir());
    }

    private BaseInstance LoadInstance(string id)
    {
        var environment = Environment;

        var instanceRoot = RootDirOf(id) / id;
        // the typed layer stack: instance.json when present (Flint format), otherwise
        // the read-only instance.cfg import in memory — no write happens on discovery
        var instanceSettings = new InstanceJsonSettingsObject(instanceRoot, environment);
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
                MetaCache = MetaCache,
                LaunchServices = LaunchServices,
                LauncherState = LauncherState,
                WatchSpace = _watchSpace,
            };
        }

        // the write-side uuid convention — a dirname handed to AddLinkedInstanceId
        // resolves to the uuid at write time; an unresolvable input appends verbatim and
        // converts at the next LoadList round (same deferral as the group members).
        inst.UuidResolver = ResolveMemberUuid;
        return inst;
    }

    private void Add(IEnumerable<BaseInstance> list)
    {
        // per-instance data changes surface through the instance's own INotifyPropertyChanged
        // (BaseInstance.PropertyChanged) — the list only notifies about membership (Reset)
        foreach (var inst in list)
        {
            _instances.Add(inst);
        }
    }

    // -------------------------------------------------------------------- groups file
    // (the file mechanics — load/save/strip/counts — live in InstanceGroupModel now;
    // this type keeps only the location anchors above and the uuid conversion delegate.)

    /// <summary>The uuid of a just-committed instance folder, read straight from its
    /// metadata (instance.json identity.uuid first, else the imported instance.cfg uuid
    /// key — the same precedence discovery uses). Empty = unresolvable: the group member
    /// is then written under the raw dirname and converted at the next group load.</summary>
    private static string ProbeInstanceUuid(AbsolutePath instanceRoot)
    {
        try
        {
            var jsonPath = instanceRoot / "instance.json";
            if (jsonPath.ExistsFile())
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(jsonPath.Value));
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

            var cfgPath = instanceRoot / InstanceIniImporter.LegacyFileName;
            if (cfgPath.ExistsFile())
            {
                var ini = new IniFile();
                return ini.LoadFile(cfgPath) ? ini.GetString("uuid", "") : "";
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
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
        // (the former TotalPlayTime/TotalPlayTimeMigrated settings keys)
        LauncherState.Update(state => state with
        {
            Playtime = new PlaytimeState
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
        private readonly AbsolutePath? _stagingPath = parent.GetStagedInstancePath(child.TargetDir);

        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            if (_stagingPath is not { } stagingPath)
            {
                EmitFailed("Could not create staging folder");
                return;
            }

            child.StagingPath = stagingPath;
            // the typed instance environment (user flint.json store + instance.json
            // type infos) replaces the former parent SettingsObject handle
            child.SetParentEnvironment(parent.Environment);

            await child.StartAsync(cancellationToken);

            if (child.State == TaskState.Succeeded)
            {
                var backoff = MinBackoff;
                while (true)
                {
                    if (parent.CommitStagedInstance(stagingPath.Value, child, child.Group))
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
                FileOps.DeletePath(stagingPath);
                EmitAborted();
                return;
            }

            FileOps.DeletePath(stagingPath);
            EmitFailed(child.FailReason);
        }

        public override void Abort()
        {
            child.Abort();
        }
    }
}
