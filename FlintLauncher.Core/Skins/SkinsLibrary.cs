// SPDX-License-Identifier: GPL-3.0-only
// The local skin library (DATA D6, user-picked plan B): a minimal local store of skin
// textures backed by the SkinsDir SETTING (default "skins", data-root-relative — the
// setting is honored now that a model actually lives there; the directory was dead weight
// before D6). The persistence half is the existing <see cref="SkinListStore"/> index.json
// (the C++ SkinList format, kept verbatim); what D6 adds is the model around it: list /
// install / delete, plus the apply flow that composes the existing account upload
// (SkinUpload — the Mojang "minecraft/profile/skins" call the account endpoints already
// make). The account skin surface (Mojang-direct /accounts/{profileId}/skin|cape) is
// untouched, per D6.
//
// Not ported from the C++ SkinList: the QAbstractListModel surface, the QImage preview
// generation and the file watching — the API serves the stored data and the texture files.

using System.Text.Json.Nodes;
using FlintLauncher.Core.IO;

namespace FlintLauncher.Core.Skins;

public sealed class SkinsLibrary
{
    private readonly Func<string> _skinsDir;

    /// <summary><paramref name="skinsDir"/> is a provider, not a value: the SkinsDir
    /// setting can change at runtime (PUT /settings) and the library follows it.</summary>
    public SkinsLibrary(Func<string> skinsDir)
    {
        _skinsDir = skinsDir;
    }

    /// <summary>The library root the provider currently resolves to.</summary>
    public string SkinsDir => _skinsDir();

    /// <summary>The stored skins (the index.json order). Entries whose texture file went
    /// missing stay listed with the caller checking <see cref="SkinModel.Path"/> — the C++
    /// model kept such entries visible in the manage dialog too.</summary>
    public List<SkinModel> List() => SkinListStore.Load(_skinsDir());

    /// <summary>The stored skin with the given name, or null.</summary>
    public SkinModel? Get(string name)
    {
        foreach (var skin in List())
        {
            if (skin.Name() == name)
            {
                return skin;
            }
        }
        return null;
    }

    /// <summary>The texture file of a stored skin, when it exists on disk ("" otherwise).</summary>
    public string TextureFileOf(SkinModel skin) => File.Exists(skin.Path) ? skin.Path : "";

    /// <summary>Store one skin texture: copies <paramref name="sourceFile"/> into the
    /// library as &lt;name&gt;.png and upserts the index entry. Returns false when the
    /// source is unreadable, the name is not a plain file name, or an entry with the name
    /// already exists (no overwrite — consistent with the icon install semantics; DELETE
    /// first to replace).</summary>
    public bool Install(string sourceFile, string name, SkinModelKind model)
    {
        if (!IsPlainName(name) || !File.Exists(sourceFile))
        {
            return false;
        }
        var dir = _skinsDir();
        if (!FileSystem.EnsureFolderPathExists(dir))
        {
            return false;
        }
        var target = Path.Combine(dir, name + ".png");
        if (File.Exists(target))
        {
            return false;
        }
        try
        {
            File.Copy(sourceFile, target, overwrite: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }

        var skins = SkinListStore.Load(dir);
        skins.RemoveAll(skin => skin.Name() == name);
        skins.Add(new SkinModel(dir, new JsonObject
        {
            ["name"] = name,
            ["capeId"] = "",
            ["url"] = "",
            ["model"] = model == SkinModelKind.Slim ? "SLIM" : "CLASSIC",
        }));
        SkinListStore.Save(dir, skins);
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
        var dir = _skinsDir();
        var skins = SkinListStore.Load(dir);
        if (skins.RemoveAll(skin => skin.Name() == name) == 0)
        {
            return false;
        }
        SkinListStore.Save(dir, skins);
        var texture = Path.Combine(dir, name + ".png");
        if (File.Exists(texture))
        {
            try
            {
                File.Delete(texture);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // the index entry is gone; a locked texture is reported by the caller's
                // success anyway (matches the C++ remove-then-pragmatic-file-cleanup)
            }
        }
        return true;
    }

    /// <summary>A plain file name (no separators, no traversal, not empty) — the guard for
    /// everything derived from a user-supplied skin name.</summary>
    private static bool IsPlainName(string name) =>
        name.Length != 0 && !name.Contains('/') && !name.Contains('\\') &&
        !name.Contains("..", StringComparison.Ordinal) && name != "." &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
}
