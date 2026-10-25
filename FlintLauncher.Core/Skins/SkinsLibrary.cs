// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// The local skin library: a minimal local store of skin
// textures backed by the SkinsDir SETTING (default "skins", data-root-relative — the
// setting is honored now that a model actually lives there; the directory was dead weight
// before). The persistence half is the existing <see cref="SkinListStore"/> index.json
// (the C++ SkinList format, kept verbatim); what is new here is the model around it: list /
// install / delete, plus the apply flow that composes the existing account upload
// (SkinUpload — the Mojang "minecraft/profile/skins" call the account endpoints already
// make). The account skin surface (Mojang-direct /accounts/{profileId}/skin|cape) is
// untouched.
//
// Not ported from the C++ SkinList: the QAbstractListModel surface and the QImage preview
// generation. The file watching IS ported — as an opt-in DirectorySpace tenancy
// (AttachWatcher): the library becomes a resident model kept fresh by coalesced watcher
// events; unattached it stays demand-driven (loads on access).

using FlintLauncher.Core.IO;
using TruePath;
using TruePath.SystemIo;

namespace FlintLauncher.Core.Skins;

public sealed class SkinsLibrary
{
    // injected, re-pointable: the SkinsDir setting change arrives as SetSkinsDir from the
    // composition root, not as per-access re-resolution
    private MaterializedDirectory _skinsDir;

    private DirectorySpace? _watchSpace;
    private List<SkinModel> _skins = [];
    private AbsolutePath _watchedDir;

    /// <summary>The injected library directory (created-and-verified at the composition
    /// root; the SkinsDir setting change arrives through <see cref="SetSkinsDir"/>).</summary>
    public SkinsLibrary(MaterializedDirectory skinsDir)
    {
        _skinsDir = skinsDir;
        _watchedDir = skinsDir.Path;
    }

    /// <summary>The library root (the injected directory's path).</summary>
    public AbsolutePath SkinsDir => _skinsDir.Path;

    /// <summary>Re-points the library (the SkinsDir setting changed at runtime): swaps the
    /// directory, reloads the index and re-points the watch. The new directory is created
    /// on demand.</summary>
    public void SetSkinsDir(AbsolutePath dir)
    {
        if (dir == _skinsDir.Path)
        {
            return;
        }

        _skinsDir = MaterializedDirectory.Ensure(dir)
                    ?? throw new InvalidOperationException(
                        $"cannot create the skins directory '{dir.Value}'");
        if (_watchSpace is { } space && _watchedDir is { } watched)
        {
            space.SyncTenantRoots([watched], [_skinsDir.Path], Reload);
            _watchedDir = _skinsDir.Path;
        }

        Reload();
    }

    /// <summary>Opts the library into the watcher-driven live model (the C++ SkinList was
    /// kept fresh by a QFileSystemWatcher): the skin list becomes resident and external
    /// changes to the library directory reload it. Default (no attachment) keeps the
    /// demand-driven load-per-access.</summary>
    public void AttachWatcher(DirectorySpace space, Action<AbsolutePath, AbsolutePath>? moved = null)
    {
        _watchSpace = space;
        Reload();
        space.Register(_skinsDir, Reload, moved);
    }

    /// <summary>Re-reads the index from the store into the resident list.</summary>
    private void Reload() => _skins = SkinListStore.Load(_skinsDir.Path);

    /// <summary>Refresh path for both modes: unwatched libraries reload per access; watched
    /// libraries serve the resident list (the watch keeps it current).</summary>
    private void EnsureFresh()
    {
        if (_watchSpace is null)
        {
            Reload();
        }
    }

    /// <summary>The stored skins (the index.json order). Entries whose texture file went
    /// missing stay listed with the caller checking <see cref="SkinModel.Path"/> — the C++
    /// model kept such entries visible in the manage dialog too.</summary>
    public List<SkinModel> List()
    {
        EnsureFresh();
        return [.. _skins];
    }

    /// <summary>The stored skin with the given name, or null.</summary>
    public SkinModel? Get(string name)
    {
        EnsureFresh();
        foreach (var skin in _skins)
        {
            if (skin.Name() == name)
            {
                return skin;
            }
        }

        return null;
    }

    /// <summary>The texture file of a stored skin, when it exists on disk ("" otherwise).</summary>
    public AbsolutePath TextureFileOf(SkinModel skin) => skin.Path.ExistsFile() ? skin.Path : default;

    /// <summary>Store one skin texture: copies <paramref name="sourceFile"/> into the
    /// library as &lt;name&gt;.png and upserts the index entry. Returns false when the
    /// source is unreadable, the name is not a plain file name, or an entry with the name
    /// already exists (no overwrite — consistent with the icon install semantics; DELETE
    /// first to replace).</summary>
    public bool Install(AbsolutePath sourceFile, string name, SkinModelKind model)
    {
        if (!IsPlainName(name) || !sourceFile.ExistsFile())
        {
            return false;
        }

        var dir = _skinsDir.Path;
        if (!FileOps.EnsureFolderPathExists(new AbsolutePath(dir)))
        {
            return false;
        }

        string a1 = name + ".png";
        var target = dir / a1;
        if (target.ExistsFile())
        {
            return false;
        }

        try
        {
            sourceFile.Copy(target, overwrite: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }

        var skins = SkinListStore.Load(dir);
        skins.RemoveAll(skin => skin.Name() == name);
        skins.Add(new SkinModel(dir, name, model: model));
        SkinListStore.Save(dir, skins);
        Reload(); // refresh the resident list (watched or not)
        return true;
    }

    /// <summary>Delete a stored skin: drops the index entry and removes the texture file.
    /// Returns false when no such entry exists.</summary>
    public bool Delete(string name)
    {
        if (!IsPlainName(name))
        {
            return false;
        }

        var dir = _skinsDir.Path;
        var skins = SkinListStore.Load(dir);
        if (skins.RemoveAll(skin => skin.Name() == name) == 0)
        {
            return false;
        }

        SkinListStore.Save(dir, skins);
        string a1 = name + ".png";
        var texture = dir / a1;
        if (texture.ExistsFile())
        {
            try
            {
                texture.Delete();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // the index entry is gone; a locked texture is reported by the caller's
                // success anyway (matches the C++ remove-then-pragmatic-file-cleanup)
            }
        }

        Reload(); // refresh the resident list (watched or not)
        return true;
    }

    /// <summary>A plain file name (no separators, no traversal, not empty) — the guard for
    /// everything derived from a user-supplied skin name.</summary>
    private static bool IsPlainName(string name) =>
        name.Length != 0 && !name.Contains('/') && !name.Contains('\\') &&
        !name.Contains("..", StringComparison.Ordinal) && name != "." &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
}
