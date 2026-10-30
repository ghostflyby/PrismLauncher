// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Tests for the FTB App import: the FTB App root/instances-path discovery, the
// instance.json parsing (fields, modLoader string, legacy version.json fallback, icon
// detection) and the end-to-end import of a synthetic FTB App instance folder (minecraft/
// copy, InstanceType/totalTimePlayed/JvmArgs settings, net.minecraft + loader components)
// through an FtbAppPaths instance whose RootPath points at the synthetic tree (per-instance,
// nothing process-global to restore).

using System.Text.Json;
using FlintLauncher.Core.Config;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.ModPlatform.FtbApp;
using FlintLauncher.Core.Settings;
using TruePath;
using Xunit;

namespace FlintLauncher.Tests;

public class FtbAppTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"flint-ftbapp-{Guid.NewGuid():N}");
    private readonly TestGlobalSettings _global = new();
    private string _instancesPath = "";

    public FtbAppTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException) { }
    }

    private InstanceList CreateList()
    {
        var instancesDir = Path.Combine(_root, "instances");
        Directory.CreateDirectory(instancesDir);
        return _global.BareInstanceList(instancesDir);
    }

    private static List<(string? Uid, string? Version)> Components(string staging)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(staging, "mmc-pack.json")));
        return doc.RootElement.GetProperty("components").EnumerateArray()
            .Select(c => (Uid: c.GetProperty("uid").GetString(),
                Version: c.TryGetProperty("version", out var v) ? v.GetString() : null))
            .ToList();
    }

    /// <summary>A synthetic FTB App root: storage/settings.json pointing at an instances
    /// folder that holds one modpack folder (built by <paramref name="populate"/>). The
    /// matching <see cref="FtbAppPaths"/> instance lands in <see cref="_paths"/>.</summary>
    private string BuildFtbAppTree(Action<string> populate)
    {
        var ftbRoot = Path.Combine(_root, ".ftba");
        _instancesPath = Path.Combine(_root, "ftb-instances");
        Directory.CreateDirectory(Path.Combine(ftbRoot, "storage"));
        Directory.CreateDirectory(_instancesPath);
        File.WriteAllText(Path.Combine(ftbRoot, "storage", "settings.json"),
            $$"""{"instanceLocation": "{{_instancesPath.Replace("\\", "\\\\")}}"}""");

        var packPath = Path.Combine(_instancesPath, "my-ftb-pack");
        Directory.CreateDirectory(packPath);
        populate(packPath);
        _paths = new FtbAppPaths(new AbsolutePath(ftbRoot));
        return packPath;
    }

    private FtbAppPaths _paths = new();

    private static void WriteInstanceJson(string packPath, string modLoader)
    {
        File.WriteAllText(Path.Combine(packPath, "instance.json"),
            $$"""
              {
                  "uuid": "0af88df9-6d80-4e34-9a3a-9d51cef93a0b",
                  "id": 5593,
                  "versionId": 81362,
                  "name": "My FTB Pack",
                  "version": "1.4.2",
                  "mcVersion": "1.18.2",
                  "modLoader": "{{modLoader}}",
                  "jvmArgs": "-Xmx2G -Dtest=1",
                  "totalPlayTime": 95514000
              }
              """);
    }

    // ----------------------------------------------------------------------- paths

    [Fact]
    public void GetDynamicInstancesPath_ReadsStorageSettings()
    {
        var packPath = BuildFtbAppTree(_ => { });

        var packDir = Directory.GetParent(packPath);
        Assert.NotNull(packDir);
        Assert.Equal(packDir.FullName, _paths.GetDynamicInstancesPath());
    }

    [Fact]
    public void GetDynamicInstancesPath_FallsBackToBinSettings()
    {
        var ftbRoot = Path.Combine(_root, ".ftba-bin");
        var binInstancesPath = Path.Combine(_root, "bin-instances");
        Directory.CreateDirectory(Path.Combine(ftbRoot, "bin"));
        File.WriteAllText(Path.Combine(ftbRoot, "bin", "settings.json"),
            $$"""{"instanceLocation": "{{binInstancesPath.Replace("\\", "\\\\")}}"}""");
        var binPaths = new FtbAppPaths(new AbsolutePath(ftbRoot));

        Assert.Equal(binInstancesPath, binPaths.GetDynamicInstancesPath());
    }

    [Fact]
    public void GetDynamicInstancesPath_MissingSettings_ReturnsEmpty()
    {
        var binPaths = new FtbAppPaths(new AbsolutePath(Path.Combine(_root, "nonexistent-ftba")));
        Assert.Equal("", binPaths.GetDynamicInstancesPath());
    }

    // ----------------------------------------------------------------- parseDirectory

    [Fact]
    public void ParseDirectory_ReadsInstanceJsonModLoaderAndIcon()
    {
        var packPath = BuildFtbAppTree(pack =>
        {
            WriteInstanceJson(pack, "forge-14.23.5.2860");
            // the minecraft folder with some content
            Directory.CreateDirectory(Path.Combine(pack, "minecraft", "mods"));
            File.WriteAllText(Path.Combine(pack, "minecraft", "mods", "testmod.jar"), "mod");
            // folder.jpg wins over the .ftbapp logo
            Directory.CreateDirectory(Path.Combine(pack, ".ftbapp"));
            File.WriteAllBytes(Path.Combine(pack, ".ftbapp", "logo"), new byte[] { 0x01, 0xAA, 0xBB });
        });

        var modpack = FtbAppPackParse.ParseDirectory(packPath);

        Assert.Equal(packPath, modpack.Path);
        Assert.Equal("0af88df9-6d80-4e34-9a3a-9d51cef93a0b", modpack.Uuid);
        Assert.Equal(5593, modpack.Id);
        Assert.Equal(81362, modpack.VersionId);
        Assert.Equal("My FTB Pack", modpack.Name);
        Assert.Equal("1.4.2", modpack.Version);
        Assert.Equal("1.18.2", modpack.McVersion);
        Assert.Equal(95514000, modpack.TotalPlayTime);
        Assert.Equal("-Xmx2G -Dtest=1", modpack.JvmArgs);
        Assert.Equal(ModLoaderType.Forge, modpack.LoaderType);
        Assert.Equal("14.23.5.2860", modpack.LoaderVersion);
        // folder.jpg does not exist -> the .ftbapp logo with its jpg type byte
        Assert.Equal(FtbAppIcon.Kind.Logo, modpack.Icon.IconKind);
        Assert.Equal("jpg", modpack.Icon.ImageType);
    }

    [Fact]
    public void ParseDirectory_LegacyVersionJsonFallback()
    {
        var packPath = BuildFtbAppTree(pack =>
        {
            // no usable modLoader in instance.json: the loader comes from the legacy
            // version.json (an empty modLoader keeps the parse alive like the C++)
            File.WriteAllText(Path.Combine(pack, "instance.json"),
                """
                {
                    "uuid": "u", "id": 1, "versionId": 2, "name": "Legacy Pack",
                    "version": "3.0", "mcVersion": "1.12.2", "totalPlayTime": 0,
                    "modLoader": ""
                }
                """);
            Directory.CreateDirectory(Path.Combine(pack, ".ftbapp"));
            File.WriteAllText(Path.Combine(pack, ".ftbapp", "version.json"),
                """
                {"targets":[{"name":"minecraft","version":"1.12.2"},{"name":"fabric","version":"0.14.21"}]}
                """);
        });

        var modpack = FtbAppPackParse.ParseDirectory(packPath);

        Assert.Equal(ModLoaderType.Fabric, modpack.LoaderType);
        Assert.Equal("0.14.21", modpack.LoaderVersion);
    }

    [Fact]
    public void ParseDirectory_FolderJpgIconWins()
    {
        var packPath = BuildFtbAppTree(pack =>
        {
            WriteInstanceJson(pack, "forge-14.23.5.2860");
            File.WriteAllBytes(Path.Combine(pack, "folder.jpg"), new byte[] { 0xFF, 0xD8, 0xFF });
        });

        var modpack = FtbAppPackParse.ParseDirectory(packPath);

        Assert.Equal(FtbAppIcon.Kind.FolderJpg, modpack.Icon.IconKind);
        Assert.Equal(Path.Combine(packPath, "folder.jpg"), modpack.Icon.FilePath);
    }

    [Fact]
    public void ParseDirectory_MissingInstanceJson_YieldsInvalid()
    {
        var packPath = BuildFtbAppTree(pack => { Directory.CreateDirectory(pack); });

        var modpack = FtbAppPackParse.ParseDirectory(packPath);

        Assert.Equal("", modpack.Path);
    }

    [Fact]
    public void ParseInstancesDirectory_ListsValidPacks()
    {
        BuildFtbAppTree(pack => WriteInstanceJson(pack, "forge-14.23.5.2860"));
        // a sibling folder without an instance.json is ignored
        Directory.CreateDirectory(Path.Combine(_instancesPath, "not-a-pack"));

        var modpacks = FtbAppPackParse.ParseInstancesDirectory(_instancesPath);

        var pack = Assert.Single(modpacks);
        Assert.Equal("My FTB Pack", pack.Name);
        Assert.Equal(Path.Combine(_instancesPath, "my-ftb-pack"), pack.Path);

        Assert.Empty(FtbAppPackParse.ParseInstancesDirectory(Path.Combine(_root, "missing")));
    }

    // ----------------------------------------------------------------------- install

    [Fact]
    public async Task Install_CopiesFolderAndRebuildsSettings()
    {
        var packPath = BuildFtbAppTree(pack =>
        {
            WriteInstanceJson(pack, "fabric-0.14.21");
            Directory.CreateDirectory(Path.Combine(pack, "minecraft", "mods"));
            File.WriteAllText(Path.Combine(pack, "minecraft", "mods", "testmod.jar"), "mod bytes");
            File.WriteAllText(Path.Combine(pack, "minecraft", "options.txt"), "opt:1\n");
            Directory.CreateDirectory(Path.Combine(pack, "config"));
            File.WriteAllText(Path.Combine(pack, "config", "extra.cfg"), "extra=1\n");
        });
        var modpack = FtbAppPackParse.ParseDirectory(packPath);

        var list = CreateList();
        var task = new FtbAppPackInstallTask(modpack);
        task.SetName("FTB App Imported");
        var wrapped = list.WrapInstanceTask(task);
        await wrapped(TestContext.Current.CancellationToken);


        var staging = Path.Combine(_root, "instances", "FTB App Imported");
        // the whole modpack folder was copied below minecraft/
        Assert.Equal("mod bytes",
            await File.ReadAllTextAsync(Path.Combine(staging, "minecraft", "minecraft", "mods", "testmod.jar")));
        Assert.Equal("extra=1\n",
            await File.ReadAllTextAsync(Path.Combine(staging, "minecraft", "config", "extra.cfg")));

        // the settings of the C++ copySettings, through the typed layer: the time trio
        // lands in instance-state.json and the jvm args in instance.json (the derived
        // OverrideJavaArgs gate reads true because JvmArgs is set)
        Assert.Equal(95514, // ms -> s
            Variant.AsLong(new MinecraftInstance(
                new InstanceJsonSettingsObject(new AbsolutePath(staging), _global.Environment),
                new AbsolutePath(staging)).Settings.Get("totalTimePlayed")));
        var instanceJson = await File.ReadAllTextAsync(Path.Combine(staging, "instance.json"));
        Assert.Contains("jvmArgs", instanceJson); // camelCase wire shape
        Assert.Contains("-Xmx2G -Dtest=1", instanceJson);

        var components = Components(staging);
        Assert.Contains(components, c => c is { Uid: "net.minecraft", Version: "1.18.2" });
        Assert.Contains(components, c => c is { Uid: "net.fabricmc.fabric-loader", Version: "0.14.21" });
    }

    [Fact]
    public async Task Install_NeoForgeLoader_UsesNeoForgeUid()
    {
        var packPath = BuildFtbAppTree(pack => { WriteInstanceJson(pack, "neoforge-20.4.237"); });
        var modpack = FtbAppPackParse.ParseDirectory(packPath);

        var list = CreateList();
        var task = new FtbAppPackInstallTask(modpack);
        task.SetName("FTB App NeoForge");
        var wrapped = list.WrapInstanceTask(task);
        await wrapped(TestContext.Current.CancellationToken);

        var components = Components(Path.Combine(_root, "instances", "FTB App NeoForge"));
        Assert.Contains(components, c => c is { Uid: "net.neoforged", Version: "20.4.237" });
    }
}
