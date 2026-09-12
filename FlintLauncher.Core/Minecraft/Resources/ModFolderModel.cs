// SPDX-License-Identifier: GPL-3.0-only
// Port of the non-Qt data logic of launcher/minecraft/mod/ModFolderModel.{h,cpp}: the
// Mod-backed resource factory (and the allMods accessor) used by the folder model tests.
// The Qt model surface (columns/data/headerData) and the dependency bookkeeping
// (m_requiredBy/m_requires, onParseSucceeded) are not ported — the latter is driven by
// the background parse tasks that were dropped with the resolver.
namespace FlintLauncher.Core.Minecraft.Resources;

/// <summary>A legacy mod list, backed by a folder.</summary>
public class ModFolderModel : ResourceFolderModel
{
    public ModFolderModel(string dir, bool isIndexed = false, bool createDir = false)
        : base(dir, isIndexed, createDir)
    {
    }

    public override string Id => "mods";

    protected override Resource CreateResource(FileInfo file) => new Mod(file);

    /// <summary>Port of the RESOURCE_HELPERS allMods accessor.</summary>
    public List<Mod> AllMods()
    {
        var result = new List<Mod>(Size);
        foreach (var resource in Resources)
        {
            result.Add((Mod)resource);
        }
        return result;
    }

    public new Mod At(int index) => (Mod)base.At(index);
}
