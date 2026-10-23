// SPDX-License-Identifier: GPL-3.0-only
// Tests for Worlds/World.cs + Worlds/WorldList.cs: synthesize a minimal gzipped
// level.dat in-test, parse it, rename the world, re-read it, plus zip-world handling,
// the world_gen_settings seed fallback, icon handling and the WorldList directory ops.

using System.IO.Compression;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Minecraft.Worlds;
using FlintLauncher.Nbt;
using TruePath;
using Xunit;

namespace FlintLauncher.Tests;

public class WorldTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "flint-worlds-" + Guid.NewGuid().ToString("N"));

    public WorldTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    /// <summary>Builds a minimal level.dat NBT structure like a real one's Data compound.</summary>
    private static NbtCompound MakeLevelDat(string name = "Test World", long lastPlayed = 1700000000000,
        int gameType = 1, long randomSeed = 12345)
    {
        var root = new NbtCompound();
        var data = new NbtCompound();
        data.Put("LevelName", new NbtString(name));
        data.Put("LastPlayed", new NbtLong(lastPlayed));
        data.Put("GameType", new NbtInt(gameType));
        if (randomSeed != 0)
        {
            data.Put("RandomSeed", new NbtLong(randomSeed));
        }

        data.Put("Difficulty", new NbtInt(2));
        root.Put("Data", data);
        return root;
    }

    /// <summary>Creates a folder world: &lt;base&gt;/&lt;folderName&gt;/level.dat (+icon.png).</summary>
    private string MakeFolderWorld(string folderName, string name = "Test World", bool withIcon = true)
    {
        var worldDir = Path.Combine(_tempDir, folderName);
        Directory.CreateDirectory(worldDir);
        File.WriteAllBytes(Path.Combine(worldDir, "level.dat"), NbtIo.WriteCompoundGzip(MakeLevelDat(name)));
        if (withIcon)
        {
            File.WriteAllBytes(Path.Combine(worldDir, "icon.png"), [0x89, 0x50, 0x4E, 0x47]);
        }

        return worldDir;
    }

    [Fact]
    public void FolderWorld_ParsesLevelDat()
    {
        var dir = MakeFolderWorld("TestWorld");

        var world = new World(dir);
        world.LoadMetadata();
        Assert.True(world.IsValid);
        Assert.True(world.IsOnFs);
        Assert.Equal("TestWorld", world.FolderName);
        Assert.Equal("Test World", world.Name);
        Assert.Equal(GameTypeKind.Creative, world.GameType.Kind);
        Assert.Equal(1, world.GameType.Original);
        Assert.Equal(12345, world.Seed);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000), world.LastPlayed);
        Assert.EndsWith("icon.png", world.IconFile);
    }

    [Fact]
    public void RawLevelDat_IsAlsoAccepted()
    {
        // deviation from the C++ (which requires gzip): raw level.dat parse fine
        var dir = Path.Combine(_tempDir, "RawWorld");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "level.dat"), NbtIo.WriteCompoundRaw(MakeLevelDat(name: "Raw")));

        var world = new World(dir);
        Assert.True(world.IsValid);
        world.LoadMetadata();
        Assert.Equal("Raw", world.Name);
    }

    [Fact]
    public void MissingLevelDat_IsInvalid()
    {
        var dir = Path.Combine(_tempDir, "Empty");
        Directory.CreateDirectory(dir);
        var world = new World(dir);
        Assert.False(world.IsValid);
        Assert.True(world.IsOnFs); // the (invalid) world folder still exists
        world.LoadMetadata();      // must be a no-op-ish and not throw
        Assert.False(world.IsValid);
    }

    [Fact]
    public void CorruptLevelDat_IsInvalid()
    {
        var dir = Path.Combine(_tempDir, "Corrupt");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "level.dat"), [0x0A, 0x00, 0x07]); // truncated
        var world = new World(dir);
        Assert.True(world.IsValid); // repath only checks existence, like the C++
        world.LoadMetadata();
        Assert.False(world.IsValid); // metadata loading fails
    }

    [Fact]
    public void Rename_UpdatesLevelName_PreservesRest_AndRenamesFolder()
    {
        var dir = MakeFolderWorld("TestWorld");
        var world = new World(dir);
        world.LoadMetadata();

        Assert.True(world.Rename("Renamed World"));

        // folder is renamed to the sanitized new name
        var newDir = Path.Combine(_tempDir, "Renamed World");
        Assert.True(Directory.Exists(newDir));
        Assert.False(Directory.Exists(dir));

        // re-read: LevelName updated, other fields preserved
        var reloaded = new World(newDir);
        reloaded.LoadMetadata();
        Assert.True(reloaded.IsValid);
        Assert.Equal("Renamed World", reloaded.Name);
        Assert.Equal(GameTypeKind.Creative, reloaded.GameType.Kind);
        Assert.Equal(12345, reloaded.Seed);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000), reloaded.LastPlayed);
        var root = NbtIo.TryReadCompound(File.ReadAllBytes(Path.Combine(newDir, "level.dat")));
        Assert.NotNull(root);
        var data = Assert.IsType<NbtCompound>(root["Data"]);
        Assert.Equal(2, Assert.IsType<NbtInt>(data["Difficulty"]).Value);
    }

    // the folder rename derives a NON-clashing name (FS::DirNameFromString): against a
    // taken folder name it lands on "name(N)" — it never merges into or duplicates the
    // existing world
    [Fact]
    public void Rename_OntoATakenFolderName_LandsOnTheNextFreeName()
    {
        var dir = MakeFolderWorld("Alpha");
        var world = new World(dir);
        world.LoadMetadata();

        var otherDir = MakeFolderWorld("Beta");
        var otherEntries = Directory.GetFileSystemEntries(otherDir, "*", SearchOption.AllDirectories).Length;

        Assert.True(world.Rename("Beta"));

        var movedDir = Path.Combine(_tempDir, "Beta(1)");
        Assert.True(Directory.Exists(movedDir)); // landed on the next free name
        Assert.False(Directory.Exists(dir));
        Assert.True(Directory.Exists(otherDir)); // the existing world is untouched
        Assert.Equal(otherEntries,
            Directory.GetFileSystemEntries(otherDir, "*", SearchOption.AllDirectories).Length);

        var reloaded = new World(movedDir);
        reloaded.LoadMetadata();
        Assert.True(reloaded.IsValid);
        Assert.Equal("Beta", reloaded.Name);
    }

    [Fact]
    public void Rename_FailsForZipWorlds()
    {
        var zipPath = Path.Combine(_tempDir, "zipped.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("TestWorld/level.dat");
            using var s = entry.Open();
            var bytes = NbtIo.WriteCompoundGzip(MakeLevelDat());
            s.Write(bytes, 0, bytes.Length);
        }

        var world = new World(zipPath);
        Assert.True(world.IsValid);
        Assert.False(world.Rename("Nope"));
    }

    [Fact]
    public void ZipWorld_LoadsMetadataFromArchive()
    {
        var zipPath = Path.Combine(_tempDir, "zipped.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("SomePrefix/level.dat");
            using var s = entry.Open();
            var bytes = NbtIo.WriteCompoundGzip(MakeLevelDat(name: "Zipped World"));
            s.Write(bytes, 0, bytes.Length);
        }

        var world = new World(zipPath);
        Assert.True(world.IsValid);
        world.LoadMetadata();
        Assert.True(world.IsMetadataLoaded);
        Assert.Equal("Zipped World", world.Name);
        Assert.Equal("SomePrefix/", world.ContainerOffsetPath);
    }

    [Fact]
    public void Install_FromZip_ExtractsAndRenames()
    {
        var zipPath = Path.Combine(_tempDir, "zipped.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("Inner/level.dat");
            using var s = entry.Open();
            var bytes = NbtIo.WriteCompoundGzip(MakeLevelDat(name: "Zipped World"));
            s.Write(bytes, 0, bytes.Length);
        }

        var target = Path.Combine(_tempDir, "saves");
        var world = new World(zipPath);
        Assert.True(world.Install(new AbsolutePath(target), "Installed World"));

        // a folder named "Installed World" appears (renamed during install)
        var installed = new World(Path.Combine(target, "Installed World"));
        installed.LoadMetadata();
        Assert.True(installed.IsValid);
        Assert.Equal("Installed World", installed.Name);
    }

    [Fact]
    public void Seed_FallsBackToWorldGenSettings()
    {
        var dir = Path.Combine(_tempDir, "WgWorld");
        Directory.CreateDirectory(Path.Combine(dir, "data", "minecraft"));

        var levelDat = MakeLevelDat(randomSeed: 0); // no RandomSeed in level.dat
        File.WriteAllBytes(Path.Combine(dir, "level.dat"), NbtIo.WriteCompoundGzip(levelDat));

        var wgRoot = new NbtCompound();
        var data = new NbtCompound();
        data.Put("seed", new NbtLong(987654321));
        wgRoot.Put("data", data);
        File.WriteAllBytes(Path.Combine(dir, "data", "minecraft", "world_gen_settings.dat"),
            NbtIo.WriteCompoundGzip(wgRoot));

        var world = new World(dir);
        world.LoadMetadata();
        Assert.Equal(987654321, world.Seed);
    }

    [Fact]
    public void ResetIcon_RemovesIconFile()
    {
        var dir = MakeFolderWorld("IconWorld");
        var world = new World(dir);
        Assert.NotEqual("", world.IconFile);
        Assert.True(world.ResetIcon());
        Assert.Equal("", world.IconFile);
        Assert.False(File.Exists(Path.Combine(dir, "icon.png")));
        Assert.False(world.ResetIcon()); // nothing left to remove
    }

    [Fact]
    public void Destroy_RemovesWorldFolder()
    {
        var dir = MakeFolderWorld("Doomed");
        var world = new World(dir);
        Assert.True(world.Destroy());
        Assert.False(Directory.Exists(dir));
        // deleting a missing path is a success (like std::filesystem::remove_all),
        // and the C++ destroy() only bails when the world was never valid
        Assert.True(world.Destroy());
    }

    [Fact]
    public void WeakEquality_ComparesValidityAndFolderName()
    {
        var dir = MakeFolderWorld("Same");
        var a = new World(dir);
        var b = new World(dir);
        Assert.Equal(a, b);

        var other = MakeFolderWorld("Other", name: "Different Name");
        Assert.NotEqual(a, new World(other));
    }

    // ----------------------------------------------------------------- WorldList

    private string MakeSavesDir()
    {
        var saves = Path.Combine(_tempDir, "saves");
        Directory.CreateDirectory(saves);
        return saves;
    }

    private void WriteWorldInto(string parent, string folder, string name)
    {
        var dir = Path.Combine(parent, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "level.dat"), NbtIo.WriteCompoundGzip(MakeLevelDat(name)));
    }

    [Fact]
    public void WorldList_Update_ListsOnlyValidWorldDirs()
    {
        var saves = MakeSavesDir();
        WriteWorldInto(saves, "alpha", "Alpha");
        WriteWorldInto(saves, "beta", "Beta");
        Directory.CreateDirectory(Path.Combine(saves, "not_a_world")); // no level.dat
        File.WriteAllBytes(Path.Combine(saves, "stray.zip"), []);      // files are skipped

        var list = new WorldList(MaterializedDirectory.Ensure(new AbsolutePath(saves))!);
        Assert.True(list.Update());
        Assert.Equal(2, list.Count);
        Assert.Equal("alpha", list[0].FolderName); // name sorted
        Assert.Equal("beta", list[1].FolderName);
        Assert.True(list.IsValid());
    }

    [Fact]
    public async Task WorldList_LoadWorldsAsync_FillsMetadataAndSize()
    {
        var saves = MakeSavesDir();
        WriteWorldInto(saves, "alpha", "Alpha");
        WriteWorldInto(saves, "beta", "Beta");

        var list = new WorldList(MaterializedDirectory.Ensure(new AbsolutePath(saves))!);
        Assert.True(list.Update());
        await list.LoadWorldsAsync();

        Assert.Equal("Alpha", list[0].Name);
        Assert.Equal("Beta", list[1].Name);
        Assert.True(list[0].Size > 0);
        Assert.True(list[0].IsMetadataLoaded);
    }

    [Fact]
    public async Task WorldList_CopyWorldAsync_InstallWorldAsync_DeleteWorldAsync()
    {
        var saves = MakeSavesDir();
        WriteWorldInto(saves, "alpha", "Alpha");

        var list = new WorldList(MaterializedDirectory.Ensure(new AbsolutePath(saves))!);
        list.Update();

        // copy with a new display name (CopyWorldTask)
        Assert.True(await list.CopyWorldAsync(0, "Alpha Copy"));
        Assert.Equal(2, list.Count);
        var copy = list.Worlds.Single(w => w.FolderName == "Alpha Copy");
        Assert.Equal("Alpha Copy", copy.Name); // rename inside the copy

        // install from outside (InstallWorldTask)
        var external = MakeFolderWorld("external_world", name: "External");
        Assert.True(await list.InstallWorldAsync(external));
        Assert.Equal(3, list.Count);

        // delete (DeleteWorldTask)
        var victimIndex = list.Worlds.ToList().FindIndex(w => w.FolderName == "alpha");
        Assert.True(await list.DeleteWorldAsync(victimIndex));
        Assert.Equal(2, list.Count);
        Assert.False(Directory.Exists(Path.Combine(saves, "alpha")));
    }

    [Fact]
    public void WorldList_DeleteWorlds_RemovesRange()
    {
        var saves = MakeSavesDir();
        WriteWorldInto(saves, "one", "One");
        WriteWorldInto(saves, "two", "Two");
        WriteWorldInto(saves, "three", "Three");

        var list = new WorldList(MaterializedDirectory.Ensure(new AbsolutePath(saves))!);
        list.Update();
        Assert.True(list.DeleteWorlds(0, 1));
        Assert.Equal(1, list.Count);
        // culture-sorted order is one, three, two — so "two" survives
        Assert.Equal("two", list[0].FolderName);
        Assert.False(Directory.Exists(Path.Combine(saves, "one")));
        Assert.False(Directory.Exists(Path.Combine(saves, "three")));
        Assert.True(Directory.Exists(Path.Combine(saves, "two")));
    }

    [Fact]
    public void WorldList_CalculateWorldSize()
    {
        var saves = MakeSavesDir();
        WriteWorldInto(saves, "alpha", "Alpha");

        var dir = Path.Combine(saves, "alpha");
        var expected = Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true })
            .Sum(f => new FileInfo(f).Length);
        Assert.Equal(expected, WorldList.CalculateWorldSize(dir));
        Assert.Equal(-1, WorldList.CalculateWorldSize(Path.Combine(saves, "missing")));
        Assert.True(WorldList.CalculateWorldSize(saves) > 0); // the saves dir itself contains world files
    }

    [Fact]
    public void WorldList_InvalidDir_UpdateFails()
    {
        var list = new WorldList(MaterializedDirectory.Ensure(new AbsolutePath(Path.Combine(_tempDir, "never-created")))!);
        Directory.Delete(Path.Combine(_tempDir, "never-created")); // ctor created it; remove again
        Assert.False(list.IsValid());
        Assert.False(list.Update());
    }
}
