// SPDX-License-Identifier: GPL-3.0-only
// Port of the non-Qt data logic of launcher/minecraft/mod/ModFolderModel.{h,cpp}: the
// Mod-backed resource factory (and the allMods accessor) used by the folder model tests.
// The Qt model surface (columns/data/headerData) and the dependency bookkeeping
// (m_requiredBy/m_requires, onParseSucceeded) are not ported — the latter is driven by
// the background parse tasks that were dropped with the resolver.
//
// P6-a metadata hook: upstream runs the indexed getFromMetadata() pass inside the folder
// load task (ResourceFolderLoadTask) and attaches a MetadataHandler::ModStruct to each Mod.
// The P3b port dropped both; this port wires the minimal get/set path instead: Update()
// loads the .pw.toml index after the folder scan, marks mods that have metadata as
// Installed and exposes the metadata through GetMetadata (the Mod object itself has no
// metadata field in this port). UninstallResource removes the index entry like the C++
// resource->destroy(index_dir, ...) call.
//
// Fix-1 metadata hook: upstream's background resolver (ResourceFolderModel::resolveResource
// + LocalModParseTask + onParseSucceeded) fills each Mod's ModDetails after the scan; this
// port runs the ModUtils parse synchronously at the end of Update() for every mod that
// still ShouldResolve (the freshly scanned / changed / added ones, exactly the set the C++
// resolve pass targets). The parse is best-effort: a failing jar keeps filename-only
// details, like an upstream parse task that finishes with an empty result.
using FlintLauncher.Core.ModPlatform;

namespace FlintLauncher.Core.Minecraft.Resources;

/// <summary>A legacy mod list, backed by a folder.</summary>
public class ModFolderModel : ResourceFolderModel
{
    private readonly Dictionary<string, MetadataHandler.ModStruct> _metadata = new(StringComparer.Ordinal);

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

    /// <summary>
    /// The packwiz metadata attached during Update (indexed scan); null when the mod has no
    /// metadata (the C++ Mod::metadata() null case).
    /// </summary>
    public MetadataHandler.ModStruct? GetMetadata(Resource resource)
    {
        return _metadata.TryGetValue(resource.InternalId, out var metadata) ? metadata : null;
    }

    public override bool Update()
    {
        var updated = base.Update();
        LoadMetadataIndex();
        ResolveMods();
        return updated;
    }

    /// <summary>
    /// Port of the ResourceFolderModel resolver (resolveResource + LocalModParseTask +
    /// onParseSucceeded): run the LocalModParseTask pass synchronously for every mod that
    /// still needs details, then finish the resolution with the parsed ModDetails (empty
    /// details on a parse failure — the task always succeeds, mirroring the C++ event flow).
    /// </summary>
    private void ResolveMods()
    {
        foreach (var mod in AllMods())
        {
            if (!mod.ShouldResolve)
            {
                continue;
            }

            mod.SetResolving(true, mod.ResolutionTicket);
            ModUtils.Process(mod);
            mod.FinishResolvingWithDetails(mod.Details);
        }
    }

    public override bool UninstallResource(string fileName, bool preserveMetadata = false)
    {
        // resolve the resource like the base class does, to know which index entry to drop
        MetadataHandler.ModStruct? metadata = null;
        foreach (var resource in Resources)
        {
            var resourceFileName = resource.Fileinfo.Name;
            if (!resource.Enabled && resourceFileName.EndsWith(".disabled", StringComparison.Ordinal))
            {
                resourceFileName = resourceFileName[..^9];
            }
            if (resourceFileName != fileName)
            {
                continue;
            }
            metadata = GetMetadata(resource);
            break;
        }

        if (metadata is not null && !preserveMetadata)
        {
            // Resource::destroy(index_dir, ...): remove the metadata for the mod
            MetadataHandler.Remove(IndexDir, metadata.Slug);
        }

        return base.UninstallResource(fileName, preserveMetadata);
    }

    /// <summary>
    /// Port of ResourceFolderLoadTask::getFromMetadata (the indexed scan pass): read every
    /// .pw.toml in the index directory and attach it to the mod whose file it describes.
    /// Index entries without a matching file are ignored (upstream adds a NotInstalled-only
    /// row; this port has no metadata field on Mod to hang it onto).
    /// </summary>
    private void LoadMetadataIndex()
    {
        _metadata.Clear();
        if (_metadata.Count != 0)
        {
            return;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(IndexDir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return;
        }

        foreach (var file in files)
        {
            if (!file.EndsWith(".pw.toml", StringComparison.Ordinal))
            {
                continue;
            }

            var metadata = MetadataHandler.Get(IndexDir, Path.GetFileName(file));
            if (!metadata.IsValid)
            {
                continue;
            }

            foreach (var resource in Resources)
            {
                if (resource.GetOriginalFileName() != metadata.Filename)
                {
                    continue;
                }

                // both the jar and the metadata are present
                resource.SetStatus(ResourceStatus.Installed);
                _metadata[resource.InternalId] = metadata;
                break;
            }
        }
    }
}
