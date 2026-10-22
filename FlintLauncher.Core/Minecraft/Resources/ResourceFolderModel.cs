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

using FlintLauncher.Core.IO;
using TruePath;

namespace FlintLauncher.Core.Minecraft.Resources;

/// <summary>
/// Port of ResourceFolderModel: a basic model for external resources. As such, external
/// users of such resources do not own them, and the resource's lifetime is contingent on
/// the model's lifetime.
/// </summary>
public class ResourceFolderModel
{
    private readonly bool _isIndexed;
    private bool _firstFolderLoad = true;

    private readonly List<Resource> _resources = [];

    // Represents the relationship between a resource's internal ID and its row position on the model.
    private readonly Dictionary<string, int> _resourcesIndex = new();

    public ResourceFolderModel(AbsolutePath dir, bool isIndexed, bool createDir)
    {
        Dir = dir;
        _isIndexed = isIndexed;
        if (createDir)
        {
            FileOps.EnsureFolderPathExists(new AbsolutePath(dir));
        }
    }

    public virtual string Id => "resource";

    public AbsolutePath Dir { get; }

    /// <summary>Port of ResourceFolderModel::indexDir.</summary>
    public virtual AbsolutePath IndexDir => Dir / ".index";

    public int Size => _resources.Count;

    public bool Empty => Size == 0;

    public Resource At(int index) => _resources[index];

    public IReadOnlyList<Resource> Resources => _resources;

    public List<Resource> AllResources() => [.. _resources];

    /// <summary>Port of ResourceFolderModel::find.</summary>
    public Resource? Find(string id)
    {
        return _resources.Find(r => r.InternalId == id);
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

        return false;
    }

    /// <summary>
    /// Port of ResourceFolderModel::uninstallResource: uninstall (i.e. remove all data
    /// about) a resource, given its file name.
    /// </summary>
    public virtual bool UninstallResource(string fileName, bool preserveMetadata = false)
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

    /// <summary>
    /// Port of ResourceFolderModel::update (plus the ResourceFolderLoadTask scan it
    /// schedules): scan the folder and merge the differences into the resource list.
    /// Runs synchronously; returns whether an update was performed.
    /// </summary>
    public virtual bool Update()
    {
        var newResources = RunLoadTask();
        _firstFolderLoad = false;

        var currentSet = new HashSet<string>(_resourcesIndex.Keys);
        var newSet = new HashSet<string>(newResources.Keys);

        ApplyUpdates(currentSet, newSet, newResources);
        return true;
    }

    // ------------------------------------------------------------------ internals

    /// <summary>Whether the (unported) watcher would be running; always false here.</summary>
    protected bool IsWatching => false;

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
    /// </summary>
    private void ApplyUpdates(HashSet<string> currentSet, HashSet<string> newSet,
        SortedDictionary<string, Resource> newResources)
    {
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
            }
        }

        // add new resources to the end
        {
            var addedSet = newSet.Except(currentSet).ToHashSet();

            foreach (var added in addedSet.OrderBy(id => id, StringComparer.Ordinal))
            {
                _resources.Add(newResources[added]);
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
    }
}
