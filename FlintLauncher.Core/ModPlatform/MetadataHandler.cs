// SPDX-License-Identifier: GPL-3.0-only
// Port of minecraft/mod/MetadataHandler.h: the per-mod metadata layer facade. The storage
// backend is the packwiz index (Metadata::ModStruct == Packwiz::V1::Mod), like upstream.
//
// Call sites (C++ -> C#):
//  - LocalResourceUpdateTask::executeTask (metadata write after download/parse)
//  - ResourceFolderLoadTask::getFromMetadata (the indexed folder scan)
//  - Resource/Mod metadata accessors; in this port the Mod object keeps no metadata field
//    (P3b dropped it), so ModFolderModel carries a lookup hook instead (see
//    ModFolderModel.GetMetadata).
namespace FlintLauncher.Core.ModPlatform;

using PackwizModel = FlintLauncher.Core.ModPlatform.Packwiz.Packwiz;

public static class MetadataHandler
{
    public sealed class ModStruct : PackwizModel.Mod
    {
    }

    /// <summary>Port of Metadata::create.</summary>
    public static ModStruct Create(string indexDir, IndexedPack modPack, IndexedVersion modVersion)
    {
        return Copy(PackwizModel.CreateModFormat(modPack, modVersion));
    }

    /// <summary>Port of Metadata::update.</summary>
    public static void Update(string indexDir, ModStruct mod)
    {
        PackwizModel.UpdateModIndex(indexDir, mod);
    }

    /// <summary>Port of Metadata::remove.</summary>
    public static void Remove(string indexDir, string modSlug)
    {
        PackwizModel.DeleteModIndex(indexDir, modSlug);
    }

    /// <summary>Port of Metadata::get(index_dir, mod_slug).</summary>
    public static ModStruct Get(string indexDir, string modSlug)
    {
        return Copy(PackwizModel.GetIndexForMod(indexDir, modSlug));
    }

    /// <summary>Port of Metadata::get(index_dir, mod_id).</summary>
    public static ModStruct Get(string indexDir, object modId)
    {
        return Copy(PackwizModel.GetIndexForModById(indexDir, modId.ToString() ?? ""));
    }

    private static ModStruct Copy(PackwizModel.Mod mod)
    {
        var copy = new ModStruct
        {
            Slug = mod.Slug,
            Name = mod.Name,
            Filename = mod.Filename,
            Side = mod.Side,
            Loaders = mod.Loaders,
            ReleaseType = mod.ReleaseType,
            Mode = mod.Mode,
            Url = mod.Url,
            HashFormat = mod.HashFormat,
            Hash = mod.Hash,
            Provider = mod.Provider,
            FileId = mod.FileId,
            ProjectId = mod.ProjectId,
            VersionNumber = mod.VersionNumber,
        };
        copy.McVersions.AddRange(mod.McVersions);
        copy.Dependencies.AddRange(mod.Dependencies);
        return copy;
    }
}
