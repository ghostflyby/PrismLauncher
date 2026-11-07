// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022 Sefa Eyeoglu <contact@scrumplex.net>
// SPDX-FileCopyrightText: 2022 TheKodeToad <TheKodeToad@proton.me>
//
// This file incorporates work covered by the following copyright and
// permission notice:
//
//     Copyright 2015-2021 MultiMC Contributors
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
// Port of launcher/minecraft/World.{h,cpp} (World + GameType).
//
// Deviations from the C++ original:
//  - level.dat parsing goes through FlintLauncher.Nbt with gzip/raw auto-detection; the
//    C++ always gunzips first and fails on raw data (parseLevelDat -> GZip::unzip). The
//    port therefore additionally accepts uncompressed level.dat files.
//  - read_string/read_long/read_int logged qWarning on missing/mistyped tags; the port
//    returns null silently (callers fall back exactly like the C++ did).
//  - World::install used MMCZip::extractSubDir as `ok = !extractSubDir(...)`, inverting
//    its success value (upstream quirk): the port treats a successful extraction as
//    success.
//  - trash-based deletion (FS::trash) is not ported (no desktop trash in the core);
//    destroy() deletes directly.
//  - rename() updates the in-memory container path after the folder rename succeeds;
//    the C++ left the stale path behind (callers re-listed the directory instead).
//  - QDateTime members are DateTimeOffset; there is no translation layer, so
//    GameType::toTranslatedString == toLogString here.
//  - Zip metadata comes from System.IO.Compression (like MMCZip.cs), not libarchive.
//
// Note on playtime: this checkout of World.{h,cpp} carries no playtime field — the
// level.dat keys read are exactly LevelName, LastPlayed, GameType, WorldGenSettings.seed
// and RandomSeed (plus data/seed from world_gen_settings.dat via loadSeed).

using System.IO.Compression;
using FlintLauncher.Core.IO;
using FlintLauncher.Nbt;
using TruePath;
using TruePath.SystemIo;

namespace FlintLauncher.Core.Minecraft.Worlds;

/// <summary>Port of the GameType struct (launcher/minecraft/World.h).</summary>
public sealed class GameType
{
    public GameType() { }

    public GameType(int? original)
    {
        Original = original;
        Kind = original switch
        {
            0 => GameTypeKind.Survival,
            1 => GameTypeKind.Creative,
            2 => GameTypeKind.Adventure,
            3 => GameTypeKind.Spectator,
            _ => GameTypeKind.Unknown,
        };
    }

    /// <summary>The recognized game mode; Unknown when the raw value is missing or unmapped.</summary>
    public GameTypeKind Kind { get; } = GameTypeKind.Unknown;

    /// <summary>The raw level.dat value, if any (GameType::original).</summary>
    public int? Original { get; }

    public string ToLogString()
    {
        return Kind switch
        {
            GameTypeKind.Survival => "Survival",
            GameTypeKind.Creative => "Creative",
            GameTypeKind.Adventure => "Adventure",
            GameTypeKind.Spectator => "Spectator",
            _ => Original is { } raw ? $"Unknown ({raw})" : "Undefined",
        };
    }

    /// <summary>The C++ version returns a QCoreApplication::translated string; this port has no i18n layer.</summary>
    public string ToTranslatedString() => ToLogString();
}

/// <summary>The enum part of the C++ GameType struct.</summary>
public enum GameTypeKind
{
    Unknown = -1,
    Survival = 0,
    Creative = 1,
    Adventure = 2,
    Spectator = 3,
}

/// <summary>Port of the World class (launcher/minecraft/World.h).</summary>
public sealed class World
{
    private bool _metadataLoaded;

    public World(string containerPath)
    {
        Repath(containerPath);
    }

    public string ContainerPath { get; private set; } = "";

    /// <summary>
    /// For zip worlds: the path prefix inside the archive that holds the world
    /// (everything before "level.dat" of the first matching entry), e.g. "world/".
    /// </summary>
    public string ContainerOffsetPath { get; private set; } = "";

    public string FolderName { get; private set; } = "";

    /// <summary>The human readable name (level.dat Data.LevelName); falls back to FolderName.</summary>
    public string Name { get; private set; } = "";

    /// <summary>Path of the world's icon.png; empty when there is none.</summary>
    public string IconFile { get; private set; } = "";

    public DateTimeOffset LevelDatTime { get; private set; }

    public DateTimeOffset LastPlayed { get; private set; }

    /// <summary>Size in bytes; set externally via SetSize (WorldList::calculateWorldSize).</summary>
    public void SetSize(long size) => Size = size;

    public long Size { get; private set; }

    public long Seed { get; private set; }

    public GameType GameType { get; private set; } = new();

    public bool IsValid { get; private set; }

    public bool IsMetadataLoaded => _metadataLoaded;

    public bool IsOnFs => Directory.Exists(ContainerPath);

    /// <summary>Port of World::repath: change the world's filesystem path.</summary>
    public void Repath(string file)
    {
        var full = Path.GetFullPath(file);
        ContainerPath = full;
        FolderName = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        ContainerOffsetPath = "";
        _metadataLoaded = false;
        IconFile = "";
        if (File.Exists(full) && Path.GetExtension(full) == ".zip")
        {
            IconFile = "";
            IsValid = true;
        }
        else if (Directory.Exists(full))
        {
            var assumedIconPath = new AbsolutePath(full) / "icon.png";
            if (assumedIconPath.ExistsFile())
            {
                IconFile = Path.GetFullPath(assumedIconPath.Value);
            }

            IsValid = (new AbsolutePath(full) / "level.dat").ExistsFile();
        }
        else
        {
            IsValid = false;
        }
    }

    /// <summary>Port of World::loadMetadata: parse level.dat (from folder or zip) once.</summary>
    public void LoadMetadata()
    {
        if (_metadataLoaded)
        {
            return;
        }

        if (File.Exists(ContainerPath) && Path.GetExtension(ContainerPath) == ".zip")
        {
            ReadFromZip(ContainerPath);
        }
        else if (Directory.Exists(ContainerPath))
        {
            ReadFromFs(ContainerPath);
        }

        _metadataLoaded = true;
    }

    /// <summary>Port of World::resetIcon: remove the icon file, if any.</summary>
    public bool ResetIcon()
    {
        if (IconFile.Length == 0)
        {
            return false;
        }

        try
        {
            File.Delete(IconFile);
            IconFile = "";
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Port of World::rename: rewrite Data.LevelName in level.dat (leaving every other
    /// tag untouched), then rename the world's folder to a sanitized, non-clashing name
    /// derived from the new name.
    /// </summary>
    public bool Rename(string newName)
    {
        if (File.Exists(ContainerPath))
        {
            return false; // the C++ refuses to rename zipped worlds
        }

        var levelDatPath = new AbsolutePath(ContainerPath) / "level.dat";
        if (!levelDatPath.ExistsFile())
        {
            return false;
        }

        byte[] data;
        try
        {
            data = File.ReadAllBytes(levelDatPath.Value);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        var worldData = NbtIo.TryReadCompound(data);
        if (worldData is null)
        {
            return false;
        }

        if (!worldData.TryGetValue("Data", out var val) || val is not NbtCompound dataCompound)
        {
            return false;
        }

        dataCompound["LevelName"] = new NbtString(newName);

        try
        {
            // putLevelDatDataToFS: gzip the rewritten NBT and save atomically (PSaveFile).
            FileOps.Write(new AbsolutePath(levelDatPath), NbtIo.WriteCompoundGzip(worldData));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }

        Name = newName;

        var parentDir = Path.GetDirectoryName(ContainerPath) ?? ".";
        AbsolutePath inDir = new AbsolutePath(parentDir);
        var dirName = FileNaming.DirNameFromString(Name, [inDir]);
        var targetPath = new AbsolutePath(parentDir) / dirName;
        if (!string.Equals(Path.GetFullPath(targetPath.Value), Path.GetFullPath(ContainerPath), StringComparison.Ordinal))
        {
            try
            {
                // a plain same-folder directory rename. FileOps.Move is deliberately NOT
                // used: its copy+delete fallback is the import pipeline's merge semantics
                // and must not run for a rename — against an existing target it would
                // MERGE into that world, and over locked files it would duplicate this
                // one (PathIo has no directory move, hence the raw call)
                Directory.Move(ContainerPath, targetPath.Value);
                ContainerPath = Path.GetFullPath(targetPath.Value);
                FolderName = targetPath.FileName;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // QFile::rename failures were ignored upstream too; the level.dat edit won.
            }
        }

        return true;
    }

    /// <summary>
    /// Port of World::install: copy this world (folder or zip) into <paramref name="to"/>
    /// under a directory named after it, optionally renaming it afterwards.
    /// </summary>
    public bool Install(AbsolutePath to, string name = "")
    {
        // The C++ installed from unloaded World objects, deriving the target folder from
        // an empty name (FS::DirNameFromString("") -> "(1)"); loading the metadata here
        // gives the intended folder name instead.
        if (!_metadataLoaded)
        {
            LoadMetadata();
        }

        string a1 = FileNaming.DirNameFromString(Name, [to]);
        var finalPath = to / a1;
        if (!FileOps.EnsureFolderPathExists(finalPath))
        {
            return false;
        }

        bool ok;
        if (File.Exists(ContainerPath) && Path.GetExtension(ContainerPath) == ".zip")
        {
            // NOTE: upstream World.cpp writes `ok = !MMCZip::extractSubDir(...)`, which
            // inverts the result; this port uses the intended semantics.
            using var zip = ZipFile.OpenRead(ContainerPath);
            ok = MmcZip.ExtractSubDir(zip, ContainerOffsetPath, finalPath) != null;
        }
        else if (Directory.Exists(ContainerPath))
        {
            ok = FileOps.Copy(new AbsolutePath(ContainerPath), finalPath);
        }
        else
        {
            ok = false;
        }

        if (ok && name.Length > 0 && Name != name)
        {
            var newWorld = new World(finalPath.Value);
            if (newWorld.IsValid)
            {
                newWorld.Rename(name);
            }
        }

        return ok;
    }

    /// <summary>Port of World::replace: replace this world's files with a copy of another's.</summary>
    public bool Replace(World with)
    {
        if (!Destroy())
        {
            return false;
        }

        var targetDir = Path.GetDirectoryName(ContainerPath);
        if (targetDir is null)
        {
            return false;
        }

        var success = FileOps.Copy(new AbsolutePath(with.ContainerPath), new AbsolutePath(targetDir));
        if (success)
        {
            FolderName = with.FolderName;
        }

        return success;
    }

    /// <summary>Port of World::destroy: delete all the files of this world.</summary>
    public bool Destroy()
    {
        if (!IsValid)
        {
            return false;
        }

        // FS::trash is not available in the ported core; delete directly instead.
        return FileOps.DeletePath(new AbsolutePath(ContainerPath));
    }

    /// <summary>Port of World::setSize.</summary>
    /// <summary>Port of World::operator== — deliberately weak (validity + folder name).</summary>
    public override bool Equals(object? obj)
    {
        return obj is World other && IsValid == other.IsValid && FolderName == other.FolderName;
    }

    // the hash follows the deliberately weak equality (validity + folder name); both
    // members are mutable by design (readFromFS/rename flip them), same lifetime as the
    // C++ hash usage — two mutable members on the line, hence two disable-onces
    // ReSharper disable once NonReadonlyMemberInGetHashCode
    // ReSharper disable once NonReadonlyMemberInGetHashCode
    public override int GetHashCode() => HashCode.Combine(IsValid, FolderName);

    // ------------------------------------------------------------------ internals

    /// <summary>Port of World::readFromFS.</summary>
    private void ReadFromFs(string dir)
    {
        var bytes = GetDatDataFromFs(dir, "level.dat");
        if (bytes is null || bytes.Length == 0)
        {
            IsValid = false;
            return;
        }

        try
        {
            LevelDatTime = Directory.GetLastWriteTimeUtc(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LevelDatTime = DateTimeOffset.UnixEpoch;
        }

        LoadFromLevelDat(bytes);
        if (Seed == 0)
        {
            var worldGen = GetDatDataFromFs(dir, "data/minecraft/world_gen_settings.dat");
            if (worldGen is { Length: > 0 })
            {
                Seed = LoadSeed(worldGen);
            }
        }
    }

    /// <summary>Port of World::readFromZip: find the first "level.dat" entry (any depth).</summary>
    private void ReadFromZip(string zipPath)
    {
        IsValid = false;
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var entry in zip.Entries)
            {
                var filePath = entry.FullName;
                var fileName = Path.GetFileName(filePath);
                if (string.Equals(fileName, "level.dat", StringComparison.OrdinalIgnoreCase))
                {
                    ContainerOffsetPath = filePath[..^"level.dat".Length];
                    LevelDatTime = entry.LastWriteTime;
                    using var stream = entry.Open();
                    using var buffer = new MemoryStream();
                    stream.CopyTo(buffer);
                    LoadFromLevelDat(buffer.ToArray());
                    IsValid = true;
                    break;
                }
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            IsValid = false;
        }
    }

    /// <summary>Port of World::loadFromLevelDat.</summary>
    private void LoadFromLevelDat(byte[] data)
    {
        var levelData = NbtIo.TryReadCompound(data);
        if (levelData is null)
        {
            IsValid = false;
            return;
        }

        if (!levelData.TryGetValue("Data", out var val) || val is not NbtCompound dataCompound)
        {
            IsValid = false;
            return;
        }

        IsValid = true;

        var name = ReadString(dataCompound, "LevelName");
        Name = name ?? FolderName;

        var timestamp = ReadLong(dataCompound, "LastPlayed");
        LastPlayed = timestamp is { } ts ? FromUnixTimeMsSafe(ts) : LevelDatTime;

        GameType = new GameType(ReadInt(dataCompound, "GameType"));

        long? randomSeed = null;
        if (dataCompound.TryGetValue("WorldGenSettings", out var worldGenVal) && worldGenVal is NbtCompound worldGen)
        {
            randomSeed = ReadLong(worldGen, "seed");
        }

        randomSeed ??= ReadLong(dataCompound, "RandomSeed");
        Seed = randomSeed ?? 0;
    }

    /// <summary>Port of read_string: null when the tag is missing or not a string.</summary>
    private static string? ReadString(NbtCompound parent, string name)
    {
        return parent.TryGetValue(name, out var tag) && tag is NbtString value ? value.Value : null;
    }

    /// <summary>Port of read_long: null when the tag is missing or not a long.</summary>
    private static long? ReadLong(NbtCompound parent, string name)
    {
        return parent.TryGetValue(name, out var tag) && tag is NbtLong value ? value.Value : null;
    }

    /// <summary>Port of read_int: null when the tag is missing or not an int.</summary>
    private static int? ReadInt(NbtCompound parent, string name)
    {
        return parent.TryGetValue(name, out var tag) && tag is NbtInt value ? value.Value : null;
    }

    /// <summary>Port of loadSeed: the seed inside world_gen_settings.dat's "data" compound.</summary>
    private static long LoadSeed(byte[] data)
    {
        var levelData = NbtIo.TryReadCompound(data);
        if (levelData is null)
        {
            return 0;
        }

        if (!levelData.TryGetValue("data", out var val) || val is not NbtCompound dataCompound)
        {
            return 0;
        }

        return ReadLong(dataCompound, "seed") ?? 0;
    }

    /// <summary>QDateTime::fromMSecsSinceEpoch; out-of-range values fall back to the epoch.</summary>
    private static DateTimeOffset FromUnixTimeMsSafe(long milliseconds)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.UnixEpoch;
        }
    }

    /// <summary>Port of getDatDataFromFS: the contents of <paramref name="file"/> inside the world dir, or null.</summary>
    private static byte[]? GetDatDataFromFs(string root, string file)
    {
        var fullFilePath = new AbsolutePath(root) / file;
        if (!fullFilePath.ExistsFile())
        {
            return null;
        }

        try
        {
            return File.ReadAllBytes(fullFilePath.Value);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
