// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2023 Trial97 <alexandru.tripon97@gmail.com>
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
// Port of launcher/icons/IconList.{h,cpp} — the stateful half the port was missing (the
// stateless IconUtils/file-operations half already lives in <see cref="IconFiles"/>):
// a key→path index over the icon library directory, the directory scan that builds it,
// and install/delete. This is an injectable model (the C++ singleton
// ICONS := new IconList) that the icon endpoints consume instead of the static helpers;
// the IconsDir SETTING finally decides where the library lives (it resolves relative to
// the data root, like the C++ default "icons").
//
// Deviations from the C++ original:
//  - IconList is a live QAbstractListModel kept fresh by a QFileSystemWatcher; the port
//    keeps the live model through the DirectorySpace tenancy (AttachWatcher): the index
//    is resident and coalesced watcher events rebuild it. Without the tenancy the pool
//    stays demand-driven (every accessor rescans). The C++ watchers' one consumer
//    (iconUpdated → the instance view change) is carried by BaseInstance.IconUpdated
//    instead. MMCIcon's theme/mask bookkeeping is display-side and not ported (the
//    getIconFileSizeLimit/m reapIcon edge is display-only).
//  - installIcon() returned void upstream (failures silently ignored); the port returns
//    bool, like <see cref="IconFiles.InstallIcon"/> (QFile::copy semantics — no overwrite).

using FlintLauncher.Core.IO;
using TruePath;

namespace FlintLauncher.Core.Icons;

/// <summary>One indexed icon: its key (completeBaseName of the file, subdirectory icons
/// included), its file path and the display label ("dir » name" for nested icons).</summary>
public sealed record IconEntry(string Key, AbsolutePath Path, string DisplayName);

public sealed class IconPool
{
    // injected, re-pointable: the IconsDir setting change arrives as SetIconsDir from the
    // composition root, not as per-access re-resolution
    private MaterializedDirectory _iconsDir;
    private readonly Dictionary<string, AbsolutePath> _byKey = new(StringComparer.Ordinal);

    private DirectorySpace? _watchSpace;
    private AbsolutePath _watchedRoot;

    /// <summary>The injected library directory (created-and-verified at the composition
    /// root; the IconsDir setting change arrives through <see cref="SetIconsDir"/>).</summary>
    public IconPool(MaterializedDirectory iconsDir)
    {
        _iconsDir = iconsDir;
        _watchedRoot = iconsDir.Path;
    }

    /// <summary>The library root (the injected directory's path).</summary>
    public AbsolutePath IconsDir => _iconsDir.Path;

    /// <summary>Re-points the library (the IconsDir setting changed at runtime): swaps the
    /// directory, rebuilds the index and re-points the watch. The new directory is created
    /// on demand.</summary>
    public void SetIconsDir(AbsolutePath dir)
    {
        if (dir == _iconsDir.Path)
        {
            return;
        }

        _iconsDir = MaterializedDirectory.Ensure(dir)
                    ?? throw new InvalidOperationException(
                        $"cannot create the icons directory '{dir.Value}'");
        if (_watchSpace is { } space && _watchedRoot is { } watched)
        {
            space.SyncTenantRoots([watched], [_iconsDir.Path], Rescan);
            _watchedRoot = _iconsDir.Path;
        }

        Rescan();
    }

    // ------------------------------------------------------------------ lookups (index)

    /// <summary>The icon FILE for a key ("" when there is none). Like the upstream
    /// getIcon/getIconIndex the scan covers the whole tree (subdirectory icons carry their
    /// base-name key, a later duplicate replaces an earlier one) and "default" aliases to
    /// "grass". Watched: serves the resident index; unwatched: rescans first.</summary>
    public AbsolutePath? GetIconFile(string key)
    {
        EnsureFresh();
        var resolvedKey = key == "default" ? "grass" : key;
        return _byKey.TryGetValue(resolvedKey, out var file) ? file : null;
    }

    /// <summary>Port of IconList::iconFileExists.</summary>
    public bool IconFileExists(string key) => GetIconFile(key) is not null;

    /// <summary>The index (the listing source for GET /icons).</summary>
    public List<IconEntry> Entries()
    {
        EnsureFresh();
        var dir = _iconsDir.Path;
        return _byKey
            .Select(pair => new IconEntry(pair.Key, pair.Value, IconFiles.IconDisplayName(dir, pair.Value)))
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Rebuild the key→path index from the directory scan (IconList::directoryChanged
    /// → the re-add loop; breadth-first via IconFiles.GetIconFilePaths, later duplicates
    /// replace earlier entries like addIcon()).</summary>
    public void Rescan()
    {
        _byKey.Clear();
        var dir = _iconsDir.Path;
        foreach (var file in IconFiles.GetIconFilePaths(dir))
        {
            if (!IconFiles.IsIconSuffix(file.GetExtensionWithoutDot()))
            {
                continue;
            }

            _byKey[IconFiles.IconKeyForFile(dir, file)] = file;
        }
    }

    // ------------------------------------------------------------------- watcher tenancy

    /// <summary>Opts the pool into the watcher-driven live model (the C++ IconList was a
    /// QAbstractListModel kept fresh by a QFileSystemWatcher): the index becomes resident,
    /// accessors serve it without rescanning, and coalesced watcher events rebuild it. A
    /// changed IconsDir resolution re-points the watch. Default (no attachment) keeps the
    /// demand-driven rescan-per-access.</summary>
    public void AttachWatcher(DirectorySpace space, Action<AbsolutePath, AbsolutePath>? moved = null)
    {
        _watchSpace = space;
        _watchedRoot = _iconsDir.Path;
        Rescan();
        space.Register(_iconsDir, Rescan, moved);
    }

    /// <summary>Refresh path for both modes: unwatched pools rescan per access; watched
    /// pools follow the IconsDir setting across provider changes (re-pointing the watch)
    /// and otherwise serve the resident index.</summary>
    private void EnsureFresh()
    {
        // watched: the index is resident and the watch keeps it current; unwatched
        // (bare fixtures): the demand-driven rescan-per-access fallback
        if (_watchSpace is null)
        {
            Rescan();
        }
    }

    // ----------------------------------------------------------------------- mutations

    /// <summary>Install one icon file (multipart upload target): copies it into the library
    /// under <paramref name="name"/> (its own file name when empty) and refreshes the
    /// index. Returns false when the source is not a readable icon file, the suffix is
    /// unsupported, or the target exists (QFile::copy semantics).</summary>
    public bool Install(AbsolutePath file, string name = "")
    {
        if (!IconFiles.InstallIcon(_iconsDir.Path, file, name))
        {
            return false;
        }

        Rescan();
        return true;
    }

    /// <summary>Port of IconList::deleteIcon: removes the key's file and refreshes the
    /// index. Returns false when the key has no file or the delete failed.</summary>
    public bool Delete(string key)
    {
        if (!IconFiles.DeleteIcon(_iconsDir.Path, key))
        {
            return false;
        }

        Rescan();
        return true;
    }

    // ------------------------------------------------------------------------ helpers

    /// <summary>QFileInfo::suffix(): the part after the last dot ("" when there is none).</summary>
    private static string Suffix(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        return dot > 0 ? fileName[(dot + 1)..] : "";
    }
}
