// SPDX-License-Identifier: GPL-3.0-only
// Port of tests/Packwiz_test.cpp (fixtures: tests/testdata/Packwiz -> TestData/Packwiz),
// plus coverage for the create/update/delete/by-id paths and the MiniToml subset parser.

using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.ModPlatform;
using FlintLauncher.Core.ModPlatform.Packwiz;
using TruePath;
using Xunit;

namespace FlintLauncher.Tests;

public class PackwizTests
{
    private static string TestDataDir()
    {
        // QFINDTESTDATA("testdata/Packwiz") equivalent (TestData/** is copied to the output dir)
        var dir = Path.Combine(AppContext.BaseDirectory, "TestData", "Packwiz");
        Assert.True(Directory.Exists(dir), $"Missing test data: {dir}");
        return dir;
    }

    // ------------------------------------------------- Packwiz_test.cpp ports

    [Fact]
    public void LoadFromFile_Modrinth()
    {
        var indexDir = TestDataDir();
        var slugMod = "borderless-mining";
        var fileName = slugMod + ".pw.toml";
        Assert.Contains(fileName, Directory.GetFiles(indexDir).Select(Path.GetFileName));

        var metadata = Packwiz.GetIndexForMod(new AbsolutePath(indexDir), slugMod);

        Assert.True(metadata.IsValid);

        Assert.Equal("Borderless Mining", metadata.Name);
        Assert.Equal("borderless-mining-1.1.1+1.18.jar", metadata.Filename);
        Assert.Equal(new SideType(SideTypeValue.ClientSide), metadata.Side);

        Assert.Equal("https://cdn.modrinth.com/data/kYq5qkSL/versions/1.1.1+1.18/borderless-mining-1.1.1+1.18.jar",
            metadata.Url);
        Assert.Equal("sha512", metadata.HashFormat);
        Assert.Equal("c8fe6e15ddea32668822dddb26e1851e5f03834be4bcb2eff9c0da7fdc086a9b6cead78e31a44d3bc66335cba11144ee0337c6d5346f1ba6362306449" +
                     "9b3188d", metadata.Hash);

        Assert.Equal(ResourceProvider.Modrinth, metadata.Provider);
        Assert.Equal("ug2qKTPR", metadata.Version());
        Assert.Equal("kYq5qkSL", metadata.ModId());
    }

    [Fact]
    public void LoadFromFile_Curseforge()
    {
        var indexDir = TestDataDir();
        var nameMod = "screenshot-to-clipboard-fabric.pw.toml";
        Assert.Contains(nameMod, Directory.GetFiles(indexDir).Select(Path.GetFileName));

        // Try without the .pw.toml at the end
        nameMod = nameMod[..^8];

        var metadata = Packwiz.GetIndexForMod(new AbsolutePath(indexDir), nameMod);

        Assert.True(metadata.IsValid);

        Assert.Equal("Screenshot to Clipboard (Fabric)", metadata.Name);
        Assert.Equal("screenshot-to-clipboard-1.0.7-fabric.jar", metadata.Filename);
        Assert.Equal(new SideType(SideTypeValue.UniversalSide), metadata.Side);

        Assert.Equal("https://edge.forgecdn.net/files/3509/43/screenshot-to-clipboard-1.0.7-fabric.jar", metadata.Url);
        Assert.Equal("murmur2", metadata.HashFormat);
        Assert.Equal("1781245820", metadata.Hash);

        Assert.Equal(ResourceProvider.Flame, metadata.Provider);
        Assert.Equal("3509043", metadata.FileId);
        Assert.Equal("327154", metadata.ProjectId);
    }

    // ---------------------------------------------------------- write/read paths

    private static (IndexedPack Pack, IndexedVersion Version) ModrinthSample()
    {
        var pack = new IndexedPack
        {
            AddonId = "kYq5qkSL",
            Provider = ResourceProvider.Modrinth,
            Name = "Borderless Mining",
            Slug = "borderless-mining",
        };
        var version = new IndexedVersion
        {
            AddonId = "kYq5qkSL",
            FileId = "ug2qKTPR",
            FileName = "borderless-mining-1.1.1+1.18.jar",
            DownloadUrl = "https://cdn.modrinth.com/data/kYq5qkSL/versions/1.1.1+1.18/borderless-mining-1.1.1+1.18.jar",
            HashType = "sha512",
            Hash = "abc123",
            VersionNumber = "1.1.1+1.18",
            McVersion = ["1.18", "1.18.1"],
            Loaders = ModLoaderType.Fabric,
            VersionType = new IndexedVersionType(IndexedVersionTypeValue.Release),
        };
        return (pack, version);
    }

    [Fact]
    public void CreateModFormat_ProducesPackwizMod()
    {
        var (pack, version) = ModrinthSample();
        var mod = Packwiz.CreateModFormat(pack, version);

        Assert.True(mod.IsValid);
        Assert.Equal("borderless-mining", mod.Slug);
        Assert.Equal("Borderless Mining", mod.Name);
        Assert.Equal("borderless-mining-1.1.1+1.18.jar", mod.Filename);
        Assert.Equal("url", mod.Mode);
        Assert.Equal(version.DownloadUrl, mod.Url);
        Assert.Equal("sha512", mod.HashFormat);
        Assert.Equal(ResourceProvider.Modrinth, mod.Provider);
        Assert.Equal("kYq5qkSL", mod.ModId());
        Assert.Equal("ug2qKTPR", mod.Version());
        Assert.Equal("1.1.1+1.18", mod.VersionNumber);
        // sorted with the Version comparator and de-duplicated
        Assert.Equal(["1.18", "1.18.1"], mod.McVersions);
        Assert.Equal(ModLoaderType.Fabric, mod.Loaders);
    }

    [Fact]
    public void CreateModFormat_FlameUsesMetadataMode()
    {
        var pack = new IndexedPack
        {
            AddonId = "327154",
            Provider = ResourceProvider.Flame,
            Name = "Screenshot to Clipboard",
            Slug = "screenshot-to-clipboard",
        };
        var version = new IndexedVersion
        {
            AddonId = "327154",
            FileId = "3509043",
            FileName = "screenshot-to-clipboard-1.0.7-fabric.jar",
            HashType = "murmur2",
            Hash = "1781245820",
            Version = "1.0.7",
        };
        var mod = Packwiz.CreateModFormat(pack, version);

        Assert.Equal("metadata:curseforge", mod.Mode);
        Assert.Equal("", mod.Url);
        // on CurseForge, there is only a version name - not a version number
        Assert.Equal("1.0.7", mod.VersionNumber);
    }

    [Fact]
    public void UpdateModIndex_AndReadBack()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"flint-packwiz-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        var (pack, version) = ModrinthSample();
        var mod = Packwiz.CreateModFormat(pack, version);
        mod.Dependencies.Add(new Dependency { AddonId = "P7dR8mSH", Type = DependencyType.FromString("REQUIRED") });

        Packwiz.UpdateModIndex(new AbsolutePath(dir), mod);

        var indexFile = Path.Combine(dir, "borderless-mining.pw.toml");
        Assert.True(File.Exists(indexFile));

        // the written file parses through the MiniToml subset and round-trips
        var loaded = Packwiz.GetIndexForMod(new AbsolutePath(dir), "borderless-mining");
        Assert.True(loaded.IsValid);
        Assert.Equal(mod.Name, loaded.Name);
        Assert.Equal(mod.Filename, loaded.Filename);
        Assert.Equal(mod.Url, loaded.Url);
        Assert.Equal(mod.HashFormat, loaded.HashFormat);
        Assert.Equal(mod.Hash, loaded.Hash);
        Assert.Equal("kYq5qkSL", loaded.ModId());
        Assert.Equal("ug2qKTPR", loaded.Version());
        Assert.Equal("1.1.1+1.18", loaded.VersionNumber);
        Assert.Equal(["1.18", "1.18.1"], loaded.McVersions);
        Assert.Equal(ModLoaderType.Fabric, loaded.Loaders);
        Assert.Equal(new IndexedVersionType(IndexedVersionTypeValue.Release), loaded.ReleaseType);
        var dep = Assert.Single(loaded.Dependencies);
        Assert.Equal("P7dR8mSH", dep.AddonId);
        Assert.Equal(new DependencyType(DependencyTypeValue.Required), dep.Type);
        // the update lock defaults to false and round-trips (x-prismlauncher-lock-update)
        Assert.False(loaded.LockUpdate);
    }

    [Fact]
    public void UpdateModIndex_LockUpdate_RoundTrips()
    {
        // #3404's update lock: x-prismlauncher-lock-update = true survives the TOML round trip
        var dir = Path.Combine(Path.GetTempPath(), $"flint-packwiz-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        var (pack, version) = ModrinthSample();
        var mod = Packwiz.CreateModFormat(pack, version);
        mod.LockUpdate = true;
        Packwiz.UpdateModIndex(new AbsolutePath(dir), mod);

        var toml = File.ReadAllText(Path.Combine(dir, "borderless-mining.pw.toml"));
        Assert.Contains("x-prismlauncher-lock-update = true", toml);

        var loaded = Packwiz.GetIndexForMod(new AbsolutePath(dir), "borderless-mining");
        Assert.True(loaded.IsValid);
        Assert.True(loaded.LockUpdate);

        // and unlocking it writes false again
        loaded.LockUpdate = false;
        Packwiz.UpdateModIndex(new AbsolutePath(dir), loaded);
        Assert.Contains("x-prismlauncher-lock-update = false",
            File.ReadAllText(Path.Combine(dir, "borderless-mining.pw.toml")));
    }

    [Fact]
    public void UpdateModIndex_Flame_WritesIntIds_AndReadsBack()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"flint-packwiz-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        var mod = new Packwiz.Mod
        {
            Slug = "screenshot-to-clipboard-fabric",
            Name = "Screenshot to Clipboard (Fabric)",
            Filename = "screenshot-to-clipboard-1.0.7-fabric.jar",
            Provider = ResourceProvider.Flame,
            FileId = "3509043",
            ProjectId = "327154",
            HashFormat = "murmur2",
            Hash = "1781245820",
            Url = "https://edge.forgecdn.net/files/3509/43/screenshot-to-clipboard-1.0.7-fabric.jar",
            Mode = "metadata:curseforge",
        };
        Packwiz.UpdateModIndex(new AbsolutePath(dir), mod);

        var loaded = Packwiz.GetIndexForMod(new AbsolutePath(dir), "screenshot-to-clipboard-fabric");
        Assert.True(loaded.IsValid);
        Assert.Equal(ResourceProvider.Flame, loaded.Provider);
        Assert.Equal("3509043", loaded.FileId);
        Assert.Equal("327154", loaded.ProjectId);
    }

    [Fact]
    public void UpdateModIndex_RejectsInvalidMod()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"flint-packwiz-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        Packwiz.UpdateModIndex(new AbsolutePath(dir), new Packwiz.Mod()); // no slug/project id: silently dropped

        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact]
    public void DeleteModIndex_RemovesFile_IgnoreMissing()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"flint-packwiz-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        var (pack, version) = ModrinthSample();
        Packwiz.UpdateModIndex(new AbsolutePath(dir), Packwiz.CreateModFormat(pack, version));
        Assert.True(File.Exists(Path.Combine(dir, "borderless-mining.pw.toml")));

        Packwiz.DeleteModIndex(new AbsolutePath(dir), "borderless-mining");
        Assert.False(File.Exists(Path.Combine(dir, "borderless-mining.pw.toml")));

        // deleting a non-existent index does nothing
        Packwiz.DeleteModIndex(new AbsolutePath(dir), "does-not-exist");
    }

    [Fact]
    public void GetIndexForModById_ScansIndex()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"flint-packwiz-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        var (pack, version) = ModrinthSample();
        Packwiz.UpdateModIndex(new AbsolutePath(dir), Packwiz.CreateModFormat(pack, version));

        var byId = Packwiz.GetIndexForModById(new AbsolutePath(dir), "kYq5qkSL");
        Assert.True(byId.IsValid);
        // (upstream quirk kept: the by-id scan passes the file name as the slug)
        Assert.Equal("kYq5qkSL", byId.ModId());
        Assert.Equal("Borderless Mining", byId.Name);

        Assert.False(Packwiz.GetIndexForModById(new AbsolutePath(dir), "unknown-id").IsValid);
    }

    [Fact]
    public void GetIndexForMod_MissingFile_ReturnsInvalid()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"flint-packwiz-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        Assert.False(Packwiz.GetIndexForMod(new AbsolutePath(dir), "nope").IsValid);
        Assert.False(Packwiz.GetIndexForMod(new AbsolutePath(dir), "alsonope.pw.toml").IsValid);
    }

    [Fact]
    public void IndexFileName_KeepsSuffix()
    {
        Assert.Equal("x.pw.toml", Packwiz.IndexFileName("x"));
        Assert.Equal("x.pw.toml", Packwiz.IndexFileName("x.pw.toml"));
    }
}
