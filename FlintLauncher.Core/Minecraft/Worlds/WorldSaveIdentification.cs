// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/mod/tasks/LocalWorldSaveParseTask.{h,cpp} together with the
// WorldSave model from launcher/minecraft/mod/WorldSave.h.
//
// Deviations:
//  - The WorldSave model drops the Resource base class and the QMutex: this port is a
//    plain mutable value holder, the parse functions are synchronous and side-effect
//    free with respect to shared state.
//  - LocalWorldSaveParseTask (the Task/abort plumbing) is not ported; modpack imports
//    can wrap the synchronous Process* calls in a FlintTask themselves.
//  - Zip inspection uses System.IO.Compression (like MMCZip.cs) instead of libarchive;
//    directory entries are represented by the "name/" entry convention.
//  - The C++ `zip.exists("/saves")` check (ArchiveReader::exists: exact entry match or
//    any entry under that directory) is mirrored semantically over the entry list.

using System.IO.Compression;

namespace FlintLauncher.Core.Minecraft.Worlds;

/// <summary>Port of WorldSaveFormat (launcher/minecraft/mod/WorldSave.h).</summary>
public enum WorldSaveFormat
{
    Single,
    Multi,
    Invalid,
}

/// <summary>Port of WorldSaveUtils::ProcessingLevel (LocalWorldSaveParseTask.h).</summary>
public enum WorldSaveProcessingLevel
{
    Full,
    BasicInfoOnly,
}

/// <summary>Port of the WorldSave model (launcher/minecraft/mod/WorldSave.h).</summary>
public sealed class WorldSave(string file)
{
    /// <summary>The save file or folder this instance refers to.</summary>
    public string File { get; } = file;

    public FileInfo FileInfo { get; } = new(file);

    /// <summary>Single (a bare world) or Multi (an archive/folder of saves).</summary>
    public WorldSaveFormat SaveFormat { get; set; } = WorldSaveFormat.Invalid;

    /// <summary>The name of the save dir (the first one found in multi mode).</summary>
    public string SaveDirName { get; set; } = "";

    /// <summary>Port of WorldSave::valid.</summary>
    public bool Valid() => SaveFormat != WorldSaveFormat.Invalid;
}

/// <summary>
/// Port of the WorldSaveUtils namespace: folder/zip world-save detection used by
/// modpack imports. Identifies whether a save is a single world (level.dat at the top
/// or one folder down) or a multi-save ("saves" layout), and the world's directory name.
/// </summary>
public static class WorldSaveIdentification
{
    /// <summary>Port of WorldSaveUtils::process (Resource::type maps to folder / ".zip" file).</summary>
    public static bool Process(WorldSave save, WorldSaveProcessingLevel level = WorldSaveProcessingLevel.Full)
    {
        if (save.FileInfo.Attributes.HasFlag(FileAttributes.Directory))
        {
            return ProcessFolder(save, level);
        }

        if (save.FileInfo.Exists && string.Equals(save.FileInfo.Extension, ".zip", StringComparison.Ordinal))
        {
            return ProcessZip(save, level);
        }

        return false; // invalid type for world save parse task
    }

    /// <summary>Port of WorldSaveUtils::processFolder.</summary>
    public static bool ProcessFolder(WorldSave save, WorldSaveProcessingLevel level = WorldSaveProcessingLevel.Full)
    {
        var (found, saveDirName, foundSavesDir) = FolderContainsLevelDat(save.FileInfo.FullName);

        if (!found)
        {
            return false;
        }

        save.SaveDirName = saveDirName;
        save.SaveFormat = foundSavesDir ? WorldSaveFormat.Multi : WorldSaveFormat.Single;

        if (level == WorldSaveProcessingLevel.BasicInfoOnly)
        {
            return true; // only need basic info already checked
        }

        // reserved for more intensive processing
        return true; // all tests passed
    }

    /// <summary>Port of WorldSaveUtils::processZIP.</summary>
    public static bool ProcessZip(WorldSave save, WorldSaveProcessingLevel level = WorldSaveProcessingLevel.Full)
    {
        var (found, saveDirName, foundSavesDir) = ZipContainsLevelDat(save.FileInfo.FullName);

        if (!found)
        {
            return false;
        }

        if (saveDirName.EndsWith('/'))
        {
            saveDirName = saveDirName[..^1];
        }

        save.SaveDirName = saveDirName;
        save.SaveFormat = foundSavesDir ? WorldSaveFormat.Multi : WorldSaveFormat.Single;

        if (level == WorldSaveProcessingLevel.BasicInfoOnly)
        {
            return true; // only need basic info already checked
        }

        // reserved for more intensive processing
        return true;
    }

    /// <summary>Port of WorldSaveUtils::validate.</summary>
    public static bool Validate(string file)
    {
        var save = new WorldSave(file);
        return Process(save, WorldSaveProcessingLevel.BasicInfoOnly) && save.Valid();
    }

    /// <summary>
    /// Port of contains_level_dat(QDir dir, bool saves): checks a folder structure for a
    /// direct "worldDir/level.dat" child, descending into a "saves" folder once.
    /// </summary>
    private static (bool Found, string SaveDirName, bool SavesDir) FolderContainsLevelDat(string dirPath,
        bool saves = false)
    {
        string[] entries;
        try
        {
            entries = Directory.GetDirectories(dirPath).OrderBy(e => e, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (false, "", saves);
        }

        foreach (var entry in entries)
        {
            var fileName = Path.GetFileName(entry);
            if (!saves && fileName == "saves")
            {
                return FolderContainsLevelDat(entry, true);
            }

            var levelDat = Path.Combine(entry, "level.dat");
            if (File.Exists(levelDat))
            {
                return (true, fileName, saves);
            }
        }

        return (false, "", saves);
    }

    /// <summary>
    /// Port of contains_level_dat(QString fileName): checks a zip structure for a
    /// "&lt;world&gt;/level.dat" entry, scoped to "saves/" when a saves folder exists.
    /// </summary>
    private static (bool Found, string SaveDirName, bool SavesDir) ZipContainsLevelDat(string fileName)
    {
        List<string> files;
        try
        {
            using var zip = ZipFile.OpenRead(fileName);
            files = zip.Entries.Select(e => e.FullName).ToList();
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            return (false, "", false); // collectFiles failure
        }

        // zip.exists("/saves"): exact entry or any entry below that directory.
        var saves = files.Any(f => f.Equals("saves", StringComparison.OrdinalIgnoreCase)
                                   || f.StartsWith("saves/", StringComparison.OrdinalIgnoreCase));

        foreach (var file in files)
        {
            var relativePath = file;
            if (saves)
            {
                if (!relativePath.StartsWith("saves/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                relativePath = relativePath["saves/".Length..];
            }

            if (!relativePath.EndsWith("/level.dat", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var slashIndex = relativePath.IndexOf('/');
            if (slashIndex == -1)
            {
                continue; // malformed: no slash between saves/ and level.dat
            }

            var worldName = relativePath[..slashIndex];
            var remaining = relativePath[(slashIndex + 1)..];

            // Check that there's nothing between worldName/ and level.dat
            if (remaining == "level.dat")
            {
                return (true, worldName, saves);
            }
        }

        return (false, "", saves);
    }
}
