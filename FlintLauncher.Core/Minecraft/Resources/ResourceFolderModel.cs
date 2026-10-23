// SPDX-License-Identifier: GPL-3.0-only
// Port of the non-Qt data logic of launcher/minecraft/mod/ResourceFolderModel.{h,cpp} and
// mod/tasks/ResourceFolderLoadTask.{h,cpp} (the update()/folder-scan semantics and the
// install/uninstall flows exercised by tests/ResourceFolderModel_test.cpp).
//
// Dropped (inseparable from the Qt model / launcher application layer):
//  - The QAbstractListModel surface: data()/headerData()/flags()/rowCount/columnCount,
//    mime drag & drop, rowBackground, save/loadColumns, createHeaderContextMenu and the
//    ProxyModel (QSortFilterProxyModel). The actual comparison and filtering logic the
//    proxy delegates to lives on Resource.Compare / Resource.ApplyFilter and is ported.
//  - QFileSystemWatcher watching (startWatching/stopWatching/directoryChanged); updates
//    are synchronous here. installResource() consequently always runs update() like the
//    C++ does when not watching.
//  - The background resolver (ConcurrentTask + QThread, resolveResource, parse tickets,
//    hasPendingParseTasks, onParseFailed row removal).
//  - installResourceWithFlameMetadata (network + modplatform layer).
//  - The issues system (updateIssues/hasIssues; needs MinecraftInstance).
//  - The column-to-SortType mapping table (m_columnSortKeys/columnToSortKey): pure
//    view-column bookkeeping.
//
// Deviations:
//  - The C++ update() runs a Task on a thread pool and merges on its signal; here the
//    same scan (ResourceFolderLoadTask::executeTask) + merge (applyUpdates) run
//    synchronously inside Update().
//  - QMap/QSet iteration order is unspecified for the added-resource set; this port
//    appends new resources sorted by internal id for determinism, and the scan result
//    uses ordinal key order like QMap.
//  - The metadata (.pw.toml index) system is not ported, so the indexed scan pass
//    (getFromMetadata), the orphan-metadata cleanup and the NotInstalled status never
//    trigger; m_isIndexed/firstFolderLoad are kept for structure.

using System.Collections.Specialized;
using System.ComponentModel;
using FlintLauncher.Core.IO;
using TruePath;

namespace FlintLauncher.Core.Minecraft.Resources;

/// <summary>
/// Port of ResourceFolderModel: a basic model for external resources. As such, external
/// users of such resources do not own them, and the resource's lifetime is contingent on
/// the model's lifetime.
/// </summary>
public class ResourceFolderModel : INotifyCollectionChanged, INotifyPropertyChanged
{
    private readonly bool _isIndexed;
    private bool _firstFolderLoad = true;

    private readonly List<Resource> _resources = [];

    // Represents the relationship between a resource's internal ID and its row position on the model.
    private readonly Dictionary<string, int> _resourcesIndex = new();

    // C++ m_lock parity: the model is mutated by Update()/install/uninstall AND, once the
    // watcher tenancy is attached, from watcher timer threads — every list touch goes
    // through this lock (Monitor is reentrant, so lock-nested calls are fine).
    private readonly Lock _lock = new();

    private DirectorySpace? _watchSpace;
    private int _reconcileRunning;

    /// <summary>INCC with Reset-only semantics: the update model is reconcile-and-swap.
    /// Raised when a scan actually changed the resource set, while the model lock may still
    /// be held (C++ parity — Qt emitted dataChanged under m_lock too); handlers must not
    /// re-enter the model synchronously, and consumers marshal threads.</summary>
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    /// <summary>INPC for <see cref="Size"/> (raised together with Reset, only on real
    /// changes).</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    private void RaiseResetAndSize()
    {
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Size)));
    }

    public ResourceFolderModel(MaterializedDirectory dir, bool isIndexed)
    {
        Dir = dir.Path;
        _isIndexed = isIndexed;
    }

    public virtual string Id => "resource";

    /// <summary>The model's directory (the injected handle's path — the creation/
    /// verification happened at the injection site, which is why the old createDir
    /// constructor flag retired). Re-pointed when the tenancy reports the root moved.</summary>
    public AbsolutePath Dir { get; private set; }

    /// <summary>Port of ResourceFolderModel::indexDir.</summary>
    public virtual AbsolutePath IndexDir => Dir / ".index";

    public int Size
    {
        get
        {
            lock (_lock)
            {
                return _resources.Count;
            }
        }
    }

    public bool Empty => Size == 0;

    public Resource At(int index)
    {
        lock (_lock)
        {
            return _resources[index];
        }
    }

    public IReadOnlyList<Resource> Resources
    {
        get
        {
            lock (_lock)
            {
                return [.. _resources];
            }
        }
    }

    public List<Resource> AllResources()
    {
        lock (_lock)
        {
            return [.. _resources];
        }
    }

    /// <summary>Port of ResourceFolderModel::find.</summary>
    public Resource? Find(string id)
    {
        lock (_lock)
        {
            return _resources.Find(r => r.InternalId == id);
        }
    }

    /// <summary>
    /// Port of ResourceFolderModel::installResource (the GH-1178 trailing-slash fix
    /// included): install a resource from a path outside the model's directory.
    /// </summary>
    public virtual bool InstallResource(string originalPath)
    {
        // NOTE: fix for GH-1178: normalize so a trailing slash can't leave an empty file
        // name, and resolve against the current directory so the self-overwrite check below
        // compares like with like (FS::NormalizePath kept cwd-relative paths relative; the
        // comparison only needs both sides in one canonical form).
        originalPath = new AbsolutePath(originalPath).Value;
        var fileInfo = new FileInfo(originalPath);

        var isDir = Directory.Exists(originalPath) && !File.Exists(originalPath);
        var isFile = File.Exists(originalPath);
        if (!isDir && !isFile)
        {
            return false; // non-existing file or file-like object
        }

        // NOTE: like the C++, the validity/type check uses a plain Resource here — not the
        // createResource factory (whose products may not be valid() before a parse pass).
        var resource = new Resource(fileInfo);
        if (!resource.Valid)
        {
            return false; // not a valid resource. Ignoring it.
        }

        var newPath = (new AbsolutePath(Dir) / fileInfo.Name).Value;
        if (originalPath == newPath)
        {
            return false; // overwriting the resource with itself makes no sense
        }

        lock (_lock)
        {
            switch (resource.Type)
            {
                case ResourceType.SingleFile:
                case ResourceType.ZipFile:
                case ResourceType.LiteMod:
                {
                    if (File.Exists(newPath) || File.Exists(newPath + ".disabled"))
                    {
                        if (!FileOps.DeletePath(new AbsolutePath(newPath)))
                        {
                            return false; // cleaning up new location was unsuccessful
                        }
                    }

                    try
                    {
                        File.Copy(originalPath, newPath, overwrite: false);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                                  or NotSupportedException)
                    {
                        return false; // copy failed
                    }

                    FileOps.UpdateTimestamp(new AbsolutePath(newPath));

                    resource.Fileinfo = new FileInfo(newPath);

                    if (!IsWatching)
                    {
                        return Update();
                    }

                    return true;
                }
                case ResourceType.Folder:
                {
                    if (File.Exists(newPath) || Directory.Exists(newPath))
                    {
                        return false; // it would merge with the existing folder
                    }

                    if (!FileOps.Copy(new AbsolutePath(originalPath), new AbsolutePath(newPath)))
                    {
                        return false; // copy of folder (potentially partially) failed
                    }

                    resource.Fileinfo = new FileInfo(newPath);

                    if (!IsWatching)
                    {
                        return Update();
                    }

                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Port of ResourceFolderModel::uninstallResource: uninstall (i.e. remove all data
    /// about) a resource, given its file name.
    /// </summary>
    public virtual bool UninstallResource(string fileName, bool preserveMetadata = false)
    {
        lock (_lock)
        {
            foreach (var resource in _resources)
            {
                var resourceFileName = resource.Fileinfo.Name;
                if (!resource.Enabled && resourceFileName.EndsWith(".disabled", StringComparison.Ordinal))
                {
                    resourceFileName = resourceFileName[..^9];
                }

                if (resourceFileName == fileName)
                {
                    var res = resource.Destroy(attemptTrash: false);

                    Update();

                    return res;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Port of ResourceFolderModel::update (plus the ResourceFolderLoadTask scan it
    /// schedules): scan the folder and merge the differences into the resource list.
    /// Runs synchronously; returns whether an update was performed.
    /// </summary>
    public virtual bool Update()
    {
        lock (_lock)
        {
            var newResources = RunLoadTask();
            _firstFolderLoad = false;

            var currentSet = new HashSet<string>(_resourcesIndex.Keys);
            var newSet = new HashSet<string>(newResources.Keys);

            if (ApplyUpdates(currentSet, newSet, newResources))
            {
                RaiseResetAndSize();
            }

            return true;
        }
    }

    // ------------------------------------------------------------------ internals

    /// <summary>Whether the watcher tenancy is attached (the C++ startWatching state): when
    /// set, install/uninstall leave the list refresh to the coalesced watcher reconcile
    /// instead of running a synchronous Update().</summary>
    protected bool IsWatching => _watchSpace is not null;

    /// <summary>Opts the model into watcher-driven reloads (the C++ startWatching/
    /// stopWatching pair): external changes to <see cref="Dir"/> coalesce into a debounced
    /// Update() re-run, and a rename/move of the root re-points <see cref="Dir"/> before
    /// the next reconcile. Default (no attachment) keeps the demand-driven update.</summary>
    public void AttachWatcher(DirectorySpace space)
    {
        _watchSpace = space;
        if (MaterializedDirectory.Wrap(Dir) is { } materialized)
        {
            space.Register(materialized, ReconcileFromWatcher,
                moved: (oldRoot, newRoot) =>
                {
                    if (Dir == oldRoot)
                    {
                        Dir = newRoot;
                        Update();
                    }
                });
        }
    }

    /// <summary>The watcher-side reconcile: Update() is idempotent, so the coalesced
    /// trailing re-run after the model's own mutations (install/uninstall) is harmless;
    /// skip-if-busy collapses overlapping fires instead of re-entering a running pass.</summary>
    private void ReconcileFromWatcher()
    {
        if (Interlocked.Exchange(ref _reconcileRunning, 1) != 0)
        {
            return;
        }

        try
        {
            Update();
        }
        finally
        {
            Interlocked.Exchange(ref _reconcileRunning, 0);
        }
    }

    /// <summary>Port of ResourceFolderModel::createResource.</summary>
    protected virtual Resource CreateResource(FileInfo info) => new(info);

    /// <summary>
    /// Port of ResourceFolderLoadTask::executeTask: enumerate the model directory and build
    /// the id → resource map of what it contains.
    /// </summary>
    private SortedDictionary<string, Resource> RunLoadTask()
    {
        // QMap iterates in key order
        var result = new SortedDictionary<string, Resource>(StringComparer.Ordinal);

        if (_isIndexed)
        {
            // getFromMetadata(): the .pw.toml index layer is not ported; nothing to load.
        }

        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(Dir.Value);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            entries = [];
        }

        foreach (var filePath in entries)
        {
            var entryPath = filePath;
            // FS::getUniqueResourceName: rename x.disabled away when x exists
            var newFilePath = FileNaming.GetUniqueResourceName(new AbsolutePath(entryPath)).Value;
            if (newFilePath != entryPath)
            {
                FileOps.Move(new AbsolutePath(entryPath), new AbsolutePath(newFilePath));
                entryPath = newFilePath;
            }

            var resource = CreateResource(new FileInfo(entryPath));

            if (resource.Enabled)
            {
                if (result.ContainsKey(resource.InternalId))
                {
                    result[resource.InternalId].SetStatus(ResourceStatus.Installed);
                    // the object we just created is dropped, since a valid one is already in the list
                }
                else
                {
                    resource.SetStatus(ResourceStatus.NoMetadata);
                    result[resource.InternalId] = resource;
                }
            }
            else
            {
                var choppedId = resource.InternalId[..^9]; // remove ".disabled"
                if (result.ContainsKey(choppedId))
                {
                    result[resource.InternalId] = resource;

                    // The C++ would move the metadata from the enabled entry onto this one,
                    // mark it Installed and drop the enabled entry; without the metadata
                    // layer both the enabled and the disabled file stay listed.
                }
                else
                {
                    resource.SetStatus(ResourceStatus.NoMetadata);
                    result[resource.InternalId] = resource;
                }
            }
        }

        if (_firstFolderLoad)
        {
            // Remove orphan metadata to prevent issues (PolyMC issue 996). The metadata
            // layer is not ported, so there are no NotInstalled-only entries to clean.
        }

        return result;
    }

    /// <summary>
    /// Port of ResourceFolderModel::applyUpdates: use set operations to find differences
    /// between the current state and the updated state, acting only on those disparities.
    /// Returns whether anything actually changed (drives the change notifications).
    /// </summary>
    private bool ApplyUpdates(HashSet<string> currentSet, HashSet<string> newSet,
        SortedDictionary<string, Resource> newResources)
    {
        var changed = false;

        // see if the kept resources changed in some way
        {
            var keptSet = currentSet.Intersect(newSet).ToHashSet();

            foreach (var kept in keptSet)
            {
                var row = _resourcesIndex[kept];

                var newResource = newResources[kept];
                var currentResource = _resources[row];

                if (newResource.DateTimeChanged == currentResource.DateTimeChanged)
                {
                    // no significant change (updateIssues is not ported)
                    continue;
                }

                // If the resource is resolving, but something about it changed, we don't
                // want to continue the resolving (parse tasks are not ported).

                _resources[row] = newResource;
                changed = true;
            }
        }

        // remove resources no longer present
        {
            var removedSet = currentSet.Except(newSet).ToHashSet();

            var removedRows = removedSet.Select(removed => _resourcesIndex[removed]).ToList();

            removedRows.Sort((a, b) => b.CompareTo(a)); // descending

            foreach (var removedIndex in removedRows)
            {
                _resources.RemoveAt(removedIndex);
                changed = true;
            }
        }

        // add new resources to the end
        {
            var addedSet = newSet.Except(currentSet).ToHashSet();

            foreach (var added in addedSet.OrderBy(id => id, StringComparer.Ordinal))
            {
                _resources.Add(newResources[added]);
                changed = true;
            }
        }

        // update index
        {
            _resourcesIndex.Clear();
            var idx = 0;
            foreach (var mod in _resources)
            {
                _resourcesIndex[mod.InternalId] = idx;
                idx++;
            }
        }

        return changed;
    }
}
