// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/icons/IconList.{h,cpp} — the stateful half the port was missing (the
// stateless IconUtils/file-operations half already lives in <see cref="IconFiles"/>):
// a key→path index over the icon library directory, the directory scan that builds it,
// and install/delete. DATA D5 turns this into an injectable model (the C++ singleton
// ICONS := new IconList) that the icon endpoints consume instead of the static helpers;
// the IconsDir SETTING finally decides where the library lives (it resolves relative to
// the data root, like the C++ default "icons").
//
// Deviations from the C++ original:
//  - IconList is a live QAbstractListModel kept fresh by a QFileSystemWatcher; the port
//    rebuilds the index per read — icon trees are tens of files, and the watchers' one
//    consumer (iconUpdated → PropertiesChanged) is carried by BaseInstance.IconUpdated
//    instead. MMCIcon's theme/mask bookkeeping is display-side and not ported (the
//    getIconFileSizeLimit/m reapIcon edge is display-only).
//  - installIcon() returned void upstream (failures silently ignored); the port returns
//    bool, like <see cref="IconFiles.InstallIcon"/> (QFile::copy semantics — no overwrite).

namespace FlintLauncher.Core.Icons;

/// <summary>One indexed icon: its key (completeBaseName of the file, subdirectory icons
/// included), its file path and the display label ("dir » name" for nested icons).</summary>
public sealed record IconEntry(string Key, string Path, string DisplayName);

public sealed class IconPool
{
    private readonly Func<string> _iconsDir;
    private readonly Dictionary<string, string> _byKey = new(StringComparer.Ordinal);

    /// <summary><paramref name="iconsDir"/> is a provider, not a value: the IconsDir
    /// setting can change at runtime (PUT /settings) and the pool follows it.</summary>
    public IconPool(Func<string> iconsDir)
    {
        _iconsDir = iconsDir;
    }

    /// <summary>The library root the provider currently resolves to.</summary>
    public string IconsDir => _iconsDir();

    // ------------------------------------------------------------------ lookups (index)

    /// <summary>The icon FILE for a key ("" when there is none). Like the upstream
    /// getIcon/getIconIndex the scan covers the whole tree (subdirectory icons carry their
    /// base-name key, a later duplicate replaces an earlier one) and "default" aliases to
    /// "grass". Rescans first — the C++ refreshed its model through the file watcher.</summary>
    public string GetIconFile(string key)
    {
        Rescan();
        var resolvedKey = key == "default" ? "grass" : key;
        return _byKey.GetValueOrDefault(resolvedKey, "");
    }

    /// <summary>Port of IconList::iconFileExists.</summary>
    public bool IconFileExists(string key) => GetIconFile(key).Length != 0;

    /// <summary>The index, freshly scanned (the listing source for GET /icons).</summary>
    public List<IconEntry> Entries()
    {
        Rescan();
        var dir = _iconsDir();
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
        var dir = _iconsDir();
        foreach (var file in IconFiles.GetIconFilePaths(dir))
        {
            if (!IconFiles.IsIconSuffix(Suffix(Path.GetFileName(file))))
            {
                continue;
            }

            _byKey[IconFiles.IconKeyForFile(dir, file)] = file;
        }
    }

    // ----------------------------------------------------------------------- mutations

    /// <summary>Install one icon file (multipart upload target): copies it into the library
    /// under <paramref name="name"/> (its own file name when empty) and refreshes the
    /// index. Returns false when the source is not a readable icon file, the suffix is
    /// unsupported, or the target exists (QFile::copy semantics).</summary>
    public bool Install(string file, string name = "")
    {
        if (!IconFiles.InstallIcon(_iconsDir(), file, name))
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
        if (!IconFiles.DeleteIcon(_iconsDir(), key))
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
