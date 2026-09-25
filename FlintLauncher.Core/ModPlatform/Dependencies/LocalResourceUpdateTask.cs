// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/mod/tasks/LocalResourceUpdateTask.{h,cpp}: write (or refresh)
// a downloaded resource's .pw.toml index entry, reporting the previously installed file
// through the hasOldResource signal so the caller can delete it once the new download
// succeeded.
//
// Deviations:
//  - EnsureMetadataTask.cs inlines the same create/update tail for its own flow (the
//    hash-primed local-file pass); this class is the project/version-driven variant the
//    ResourceDownloadTask wires in, mirroring the C++ split.
//  - The C++ ctor performs the index-directory creation eagerly and the Q_OS_WIN32 hidden
//    attribute dance; here both the folder check and everything else run in ExecuteAsync
//    (the attribute call has no portable equivalent and only hides the folder on Windows).
//  - abort() -> the base Abort() cancellation (the C++ just emits aborted()).
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Tasks;

namespace FlintLauncher.Core.ModPlatform.Dependencies;

public sealed class LocalResourceUpdateTask : FlintTask
{
    private readonly string _indexDir;
    private readonly IndexedPack _project;
    private readonly IndexedVersion _version;
    private readonly bool _lockUpdate;

    public LocalResourceUpdateTask(string indexDir, IndexedPack project, IndexedVersion version,
        bool lockUpdate = false)
    {
        _indexDir = indexDir;
        // The C++ takes the project by value, so the slug rescue below must not leak into
        // the caller's pack: work on a shallow copy.
        _project = ClonePack(project);
        _version = version;
        _lockUpdate = lockUpdate;
    }

    /// <summary>Port of the hasOldResource(name, filename) signal.</summary>
    public event Action<string, string>? HasOldResource;

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        // Ensure a '.index' folder exists in the mods folder, and create it if it does not
        if (!FileSystem.EnsureFolderPathExists(_indexDir))
        {
            EmitFailed($"Unable to create index directory at {_indexDir}!");
            return Task.CompletedTask;
        }

        EmitStatus($"Updating index for resource:\n{_project.Name}");

        // C++ Metadata::get(index_dir, addonId): the by-id lookup (cast forces the object
        // overload over the by-slug one, since AddonId is a plain string here)
        var oldMetadata = MetadataHandler.Get(_indexDir, (object)_project.AddonId);
        if (oldMetadata.IsValid)
        {
            HasOldResource?.Invoke(oldMetadata.Name, oldMetadata.Filename);
            if (_project.Slug.Length == 0)
            {
                _project.Slug = oldMetadata.Slug;
            }
        }

        var pwMod = MetadataHandler.Create(_indexDir, _project, _version);
        if (pwMod.IsValid)
        {
            pwMod.LockUpdate = _lockUpdate;
            MetadataHandler.Update(_indexDir, pwMod);
            EmitSucceeded();
        }
        else
        {
            // "Tried to update an invalid resource!"
            EmitFailed("Invalid metadata");
        }
        return Task.CompletedTask;
    }

    private static IndexedPack ClonePack(IndexedPack pack)
    {
        var copy = new IndexedPack
        {
            AddonId = pack.AddonId,
            Provider = pack.Provider,
            Name = pack.Name,
            Slug = pack.Slug,
            Description = pack.Description,
            Authors = pack.Authors,
            LogoName = pack.LogoName,
            LogoUrl = pack.LogoUrl,
            WebsiteUrl = pack.WebsiteUrl,
            Side = pack.Side,
            VersionsLoaded = pack.VersionsLoaded,
            ExtraDataLoaded = pack.ExtraDataLoaded,
            ResourceType = pack.ResourceType,
        };
        copy.Versions.AddRange(pack.Versions);
        return copy;
    }
}
