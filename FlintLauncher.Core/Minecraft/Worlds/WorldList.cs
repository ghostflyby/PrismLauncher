// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/WorldList.{h,cpp} — directory logic only.
//
// Dropped Qt model/view machinery (documented per the port plan):
//  - QAbstractListModel surface: rowCount/columnCount/data/headerData, the Columns enum
//    and all view Roles (ObjectRole..IconFileRole)
//  - drag & drop: mimeTypes/mimeData/dropMimeData/flags/supportedDragActions/
//    supportedDropActions (installWorld covers the useful part: installing a world file)
//  - change notification: the `changed` signal, dataChanged/beginResetModel calls
//  - QFileSystemWatcher: startWatching/stopWatching/directoryChanged
//  - the QThreadPool + QMetaObject::invokeMethod marshalling of loadWorldsAsync and the
//    WorldTasks.cpp worker tasks (replaced by plain async methods below that load,
//    install, copy or delete a world and then update the list, without GUI dispatch)
//  - BaseInstance coupling: the list is constructed from a directory path only
//    (instDirPath() was purely instance-model glue)

using FlintLauncher.Core.IO;
using TruePath;
using TruePath.SystemIo;

namespace FlintLauncher.Core.Minecraft.Worlds;

/// <summary>The worlds of one instance's "saves" directory (launcher/minecraft/WorldList.h).</summary>
public sealed class WorldList
{
    private readonly List<World> _worlds = [];

    public WorldList(MaterializedDirectory dir)
    {
        Dir = dir.Path;
    }

    /// <summary>The saves directory this list is rooted at (QDir m_dir). Re-pointed when
    /// the tenancy reports the root moved.</summary>
    public AbsolutePath Dir { get; private set; }

    /// <summary>The worlds of the last Update(), in directory order (m_worlds).</summary>
    public IReadOnlyList<World> Worlds => _worlds;

    public int Count => _worlds.Count;

    public bool IsEmpty => _worlds.Count == 0;

    public World this[int index] => _worlds[index];

    private DirectorySpace? _watchSpace;
    private int _reconcileRunning;

    /// <summary>Opts the list into watcher-driven refresh (the C++ WorldList watched its
    /// folder): external changes coalesce into Update() re-runs, and a rename/move of the
    /// root re-points <see cref="Dir"/> before the next reconcile. Default keeps the
    /// demand-driven Update().</summary>
    public void AttachWatcher(DirectorySpace space)
    {
        _watchSpace = space;
        Update();
        if (MaterializedDirectory.Wrap(Dir) is { } materialized)
        {
            space.Register(materialized, Reconcile,
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

    /// <summary>The watcher-side reconcile: Update() is idempotent, so overlapping fires
    /// collapse into the running one instead of re-entering it.</summary>
    private void Reconcile()
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

    /// <summary>Port of WorldList::update: rescan the directory and rebuild the world list.</summary>
    /// <returns>False when the directory does not exist or is unreadable.</returns>
    public bool Update()
    {
        if (!IsValid())
        {
            return false;
        }

        var newWorlds = new List<World>();
        IEnumerable<string> entries;
        try
        {
            // QDir filter Readable|NoDotAndDotDot|Files|Dirs, sorting Name|IgnoreCase|LocaleAware.
            entries = Directory.EnumerateDirectories(Dir.Value).OrderBy(e => e, StringComparer.CurrentCultureIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        foreach (var entry in entries)
        {
            var world = new World(entry);
            if (world.IsValid)
            {
                newWorlds.Add(world);
            }
        }

        _worlds.Clear();
        _worlds.AddRange(newWorlds);
        return true;
    }

    /// <summary>Port of WorldList::isValid.</summary>
    public bool IsValid()
    {
        return Dir.ExistsDirectory();
    }

    /// <summary>
    /// Port of WorldList::installWorld: install a world (folder or zip) from the given
    /// location into this list's directory.
    /// </summary>
    public bool InstallWorld(string filename)
    {
        var world = new World(filename);
        if (!world.IsValid)
        {
            return false;
        }

        return world.Install(Dir);
    }

    /// <summary>
    /// Port of WorldList::removeWorldFromModel: drop a world that was already deleted
    /// from the list.
    /// </summary>
    public bool RemoveWorld(AbsolutePath sourcePath)
    {
        var sourceFullPath = Path.GetFullPath(sourcePath.Value);
        for (var i = 0; i < _worlds.Count; i++)
        {
            if (!string.Equals(Path.GetFullPath(_worlds[i].ContainerPath), sourceFullPath, StringComparison.Ordinal))
            {
                continue;
            }

            _worlds.RemoveAt(i);
            return true;
        }

        return false;
    }

    /// <summary>Port of WorldList::deleteWorlds(first, last): destroy and remove a row range.</summary>
    public bool DeleteWorlds(int first, int last)
    {
        for (var i = first; i <= last; i++)
        {
            _ = _worlds[i].Destroy(); // individual failures ignored, like the C++
        }

        _worlds.RemoveRange(first, last - first + 1);
        return true;
    }

    /// <summary>Port of WorldList::resetIcon(index).</summary>
    public bool ResetIcon(int index)
    {
        if (index >= _worlds.Count || index < 0)
        {
            return false;
        }

        return _worlds[index].ResetIcon();
    }

    // ------------------------------------------------- ports of the WorldTasks workers

    /// <summary>Port of WorldList::loadWorldsAsync / the async metadata fill of update().</summary>
    public Task LoadWorldsAsync()
    {
        var snapshot = _worlds.ToList();
        return Task.WhenAll(snapshot.Select(world =>
        {
            world.LoadMetadata();
            world.SetSize(CalculateWorldSize(world.ContainerPath));
            return Task.CompletedTask;
        }));
    }

    /// <summary>Port of InstallWorldTask: import a world file/folder, then refresh the list.</summary>
    public async Task<bool> InstallWorldAsync(string sourceFile)
    {
        var world = new World(sourceFile);
        var ok = world.IsValid && world.Install(Dir);
        if (ok)
        {
            Update();
            await LoadWorldsAsync();
        }

        return ok;
    }

    /// <summary>Port of CopyWorldTask: copy the world at <paramref name="index"/>, renamed to <paramref name="name"/>.</summary>
    public async Task<bool> CopyWorldAsync(int index, string name)
    {
        if (index >= _worlds.Count || index < 0)
        {
            return false;
        }

        var world = _worlds[index];
        var ok = world.IsValid && world.Install(Dir, name);
        if (ok)
        {
            Update();
            await LoadWorldsAsync();
        }

        return ok;
    }

    /// <summary>Port of DeleteWorldTask: delete the world at <paramref name="index"/> from disk and list.</summary>
    public Task<bool> DeleteWorldAsync(int index)
    {
        if (index >= _worlds.Count || index < 0)
        {
            return Task.FromResult(false);
        }

        var world = _worlds[index];
        var ok = world.Destroy();
        if (ok)
        {
            RemoveWorld(new AbsolutePath(world.ContainerPath));
        }

        return Task.FromResult(ok);
    }

    /// <summary>Port of WorldList.cpp calculateWorldSize: zip file size or recursive folder size, -1 otherwise.</summary>
    public static long CalculateWorldSize(string file)
    {
        if (File.Exists(file) && Path.GetExtension(file) == ".zip")
        {
            return new FileInfo(file).Length;
        }

        if (Directory.Exists(file))
        {
            long total = 0;
            try
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
                foreach (var f in Directory.EnumerateFiles(file, "*", options))
                {
                    total += new FileInfo(f).Length;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return total; // partial count is the best effort
            }

            return total;
        }

        return -1;
    }
}
