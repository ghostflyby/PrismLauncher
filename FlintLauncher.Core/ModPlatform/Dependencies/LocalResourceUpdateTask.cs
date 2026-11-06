// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022 flowln <flowlnlnln@gmail.com>
// SPDX-FileCopyrightText: 2022 Sefa Eyeoglu <contact@scrumplex.net>
//
// Port of launcher/minecraft/mod/tasks/LocalResourceUpdateTask.{h,cpp}: write (or refresh)
// a downloaded resource's .pw.toml index entry, returning the previously installed file so
// the caller can delete it once the new download succeeded.
//
// Deviations:
//  - EnsureMetadataTask.cs inlines the same create/update tail for its own flow (the
//    hash-primed local-file pass); this class is the project/version-driven variant the
//    ResourceDownloadTask wires in, mirroring the C++ split.
//  - The C++ ctor performs the index-directory creation eagerly and the Q_OS_WIN32 hidden
//    attribute dance; here both the folder check and everything else run in the body
//    (the attribute call has no portable equivalent and only hides the folder on Windows).
//  - The hasOldResource signal becomes this method's return value.

using FlintLauncher.Core.IO;
using TruePath;

namespace FlintLauncher.Core.ModPlatform.Dependencies;

public static class LocalResourceUpdateTask
{
    /// <summary>Writes (or refreshes) the resource's index entry. Returns the previously
    /// installed file's (name, filename) when the index held an entry for this resource, so the
    /// caller can delete it once the new download succeeded.</summary>
    public static Task<(string Name, string Filename)?> RunAsync(AbsolutePath indexDir,
        IndexedPack project, IndexedVersion version, CancellationToken cancellationToken = default,
        bool lockUpdate = false)
    {
        _ = cancellationToken; // local file writes: nothing to observe

        // The C++ takes the project by value, so the slug rescue below must not leak into the
        // caller's pack: work on a shallow copy.
        var target = ClonePack(project);

        // Ensure a '.index' folder exists in the mods folder, and create it if it does not
        if (!FileOps.EnsureFolderPathExists(indexDir))
        {
            throw new InvalidOperationException($"Unable to create index directory at {indexDir}!");
        }

        // C++ Metadata::get(index_dir, addonId): the by-id lookup (cast forces the object
        // overload over the by-slug one, since AddonId is a plain string here)
        var oldMetadata = MetadataHandler.Get(indexDir, (object)target.AddonId);
        (string Name, string Filename)? previous = null;
        if (oldMetadata is { IsValid: true })
        {
            previous = (oldMetadata.Name, oldMetadata.Filename);
            if (target.Slug.Length == 0)
            {
                target.Slug = oldMetadata.Slug;
            }
        }

        var pwMod = MetadataHandler.Create(indexDir, target, version);
        if (pwMod is { IsValid: true })
        {
            pwMod.LockUpdate = lockUpdate;
            MetadataHandler.Update(indexDir, pwMod);
        }
        else
        {
            // "Tried to update an invalid resource!"
            throw new InvalidOperationException("Invalid metadata");
        }

        return Task.FromResult(previous);
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
