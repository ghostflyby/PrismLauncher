// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// New tests locking the persistence behavior ported from launcher/minecraft/PackProfile.{h,cpp}:
// mmc-pack.json round trips (the typed write; values are the gate — docs/JSON-MIGRATION.md),
// patches/*.json handling and the jar mod / custom jar / agent installers.

using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.Meta;
using FlintLauncher.Core.Minecraft;
using TruePath;
using TruePath.SystemIo;
using Xunit;

namespace FlintLauncher.Tests;

public class PackProfileTests : IDisposable
{
    private readonly string _root;

    public PackProfileTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "flint-packprofile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException) { }
    }

    private static JsonNode ParseNode(string json)
    {
        var node = JsonNode.Parse(json);
        Assert.NotNull(node);
        return node;
    }

    private void WriteFile(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        var dir = Path.GetDirectoryName(path);
        Assert.NotNull(dir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, content);
    }

    private static string VersionFileJson(string uid, string version, string name, string? mainClass = null)
    {
        var obj = new JsonObject
        {
            ["formatVersion"] = 1,
            ["name"] = name,
            ["uid"] = uid,
            ["version"] = version,
        };
        if (mainClass is not null)
        {
            obj["mainClass"] = mainClass;
        }

        return obj.ToJsonString();
    }

    // ---------------------------------------------------------------- mmc-pack.json round trip

    [Fact]
    public void MmcPackRoundTrip()
    {
        WriteFile("mmc-pack.json",
            """
            {
                "formatVersion": 1,
                "components": [
                    {
                        "cachedName": "Minecraft",
                        "cachedVersion": "1.12.2",
                        "important": true,
                        "uid": "net.minecraft",
                        "version": "1.12.2"
                    },
                    {
                        "uid": "org.lwjgl3"
                    },
                    {
                        "dependencyOnly": true,
                        "uid": "org.quiltmc.hashed",
                        "version": "1.12.2"
                    }
                ]
            }
            """);
        // a custom patch file for one component must make it "custom" once loaded
        WriteFile("patches/org.lwjgl3.json", VersionFileJson("org.lwjgl3", "3.1.2", "LWJGL 3"));

        using var profile = new PackProfile(new AbsolutePath(_root));
        var result = profile.Load();
        Assert.True(result.Ok, result.ErrorMessage);

        Assert.Equal(3, profile.Components.Count);

        var minecraft = profile.Components[0];
        Assert.Equal("net.minecraft", minecraft.GetId());
        Assert.Equal("1.12.2", minecraft.Version);
        Assert.True(minecraft.Important);
        Assert.False(minecraft.DependencyOnly);
        Assert.Equal("1.12.2", minecraft.GetVersion());
        Assert.Equal("Minecraft", minecraft.GetName());
        Assert.False(minecraft.IsCustom());

        var lwjgl = profile.Components[1];
        Assert.Equal("org.lwjgl3", lwjgl.GetId());
        // the patch file is on disk and wins over metadata
        Assert.True(lwjgl.IsRemovable());

        var hashed = profile.Components[2];
        Assert.Equal("org.quiltmc.hashed", hashed.GetId());
        Assert.True(hashed.DependencyOnly);
        Assert.Equal("1.12.2", hashed.Version);
    }

    [Fact]
    public void MmcPack_Null_Component_Entry_Is_Bad_File_Format()
    {
        // a JSON null where a component object belongs fails the whole file (the former
        // "Component must be an object." throw), never a silently-shortened list
        WriteFile("mmc-pack.json", """{"formatVersion": 1, "components": [null, {"uid": "net.minecraft"}]}""");
        using var profile = new PackProfile(new AbsolutePath(_root));
        var result = profile.Load();
        Assert.False(result.Ok);
    }

    [Fact]
    public void SaveProducesQtCompatibleJson()
    {
        using var profile = new PackProfile(new AbsolutePath(_root));
        profile.BuildingFromScratch();

        var minecraft = new Component(profile, "net.minecraft")
        {
            Version = "1.20.1",
        };
        minecraft.SetImportant(true);
        var loader = new Component(profile, "net.fabricmc.fabric-loader")
        {
            Version = "0.14.21",
            CachedVersion = "0.14.21",
            CachedName = "Fabric Loader",
        };
        loader.CachedRequires.Add(new Require { Uid = "net.minecraft", EqualsVersion = "1.20.1" });
        profile.AppendComponent(minecraft);
        profile.AppendComponent(loader);

        profile.SaveNow();
        Assert.False(profile.SaveIsScheduled());

        // the typed mmc-pack write keeps the values, not the Qt byte shape — parse the
        // written file and assert the member values (then load it back identically)
        using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "mmc-pack.json"))))
        {
            var root = doc.RootElement;
            Assert.Equal(1, root.GetProperty("formatVersion").GetInt32());
            var components = root.GetProperty("components");
            Assert.Equal(2, components.GetArrayLength());

            var minecraftWire = components[0];
            Assert.Equal("net.minecraft", minecraftWire.GetProperty("uid").GetString());
            Assert.Equal("1.20.1", minecraftWire.GetProperty("version").GetString());
            Assert.True(minecraftWire.GetProperty("important").GetBoolean());

            var loaderWire = components[1];
            Assert.Equal("net.fabricmc.fabric-loader", loaderWire.GetProperty("uid").GetString());
            Assert.Equal("Fabric Loader", loaderWire.GetProperty("cachedName").GetString());
            Assert.Equal("0.14.21", loaderWire.GetProperty("cachedVersion").GetString());
            Assert.Equal("0.14.21", loaderWire.GetProperty("version").GetString());
            var cachedRequires = loaderWire.GetProperty("cachedRequires");
            Assert.Equal(1, cachedRequires.GetArrayLength());
            Assert.Equal("net.minecraft", cachedRequires[0].GetProperty("uid").GetString());
            Assert.Equal("1.20.1", cachedRequires[0].GetProperty("equals").GetString());
        }

        // and the written file loads back identically
        var result = profile.Load();
        Assert.True(result.Ok, result.ErrorMessage);
        Assert.Equal(2, profile.Components.Count);
        Assert.Equal("net.minecraft", profile.Components[0].GetId());
        Assert.Equal("0.14.21", profile.Components[1].Version);
        Assert.Contains(new Require { Uid = "net.minecraft", EqualsVersion = "1.20.1" },
            profile.Components[1].CachedRequires);
    }

    [Fact]
    public void LoadRejectsUnknownFormatVersion()
    {
        WriteFile("mmc-pack.json", """{ "formatVersion": 99, "components": [] }""");
        using var profile = new PackProfile(new AbsolutePath(_root));
        var result = profile.Load();
        Assert.False(result.Ok);
        Assert.Contains("bad file format", result.ErrorMessage);
    }

    [Fact]
    public void DuplicateComponentsAreIgnored()
    {
        WriteFile("mmc-pack.json",
            """
            {
                "formatVersion": 1,
                "components": [
                    { "uid": "net.minecraft", "version": "1.12.2" },
                    { "uid": "net.minecraft", "version": "1.20.1" }
                ]
            }
            """);
        using var profile = new PackProfile(new AbsolutePath(_root));
        var result = profile.Load();
        Assert.True(result.Ok, result.ErrorMessage);
        Assert.Single(profile.Components);
        Assert.Equal("1.12.2", profile.Components[0].Version);
    }

    // ---------------------------------------------------------------- component ops

    [Fact]
    public void MoveReordersComponents()
    {
        using var profile = new PackProfile(new AbsolutePath(_root));
        profile.BuildingFromScratch();
        var a = new Component(profile, "test.a");
        var b = new Component(profile, "test.b");
        var c = new Component(profile, "test.c");
        profile.AppendComponent(a);
        profile.AppendComponent(b);
        profile.AppendComponent(c);

        profile.Move(1, PackProfile.MoveDirection.MoveUp); // b moves to the front
        Assert.Equal(new[] { "test.b", "test.a", "test.c" }, profile.Components.Select(x => x.GetId()).ToArray());

        profile.Move(1, PackProfile.MoveDirection.MoveDown); // a moves behind c
        Assert.Equal(new[] { "test.b", "test.c", "test.a" }, profile.Components.Select(x => x.GetId()).ToArray());
    }

    [Fact]
    public void RemoveDropsPatchFileAndJarModFile()
    {
        var jarmodSource = Path.Combine(Path.GetTempPath(), "flint-jarmod-" + Guid.NewGuid().ToString("N") + ".jar");
        File.WriteAllText(jarmodSource, "not a real jar");
        try
        {
            using var profile = new PackProfile(new AbsolutePath(_root));
            profile.BuildingFromScratch();
            Assert.True(profile.InstallJarMods([new AbsolutePath(jarmodSource)]));

            var jarmodComponent = profile.Components.Single(c => c.GetId().StartsWith("custom.jarmod."));
            var jarmodPatch = profile.PatchFilePathForUid(jarmodComponent.GetId());
            var jarmodFile = Directory.EnumerateFiles(Path.Combine(_root, "jarmods")).Single();
            Assert.True(jarmodPatch.ExistsFile());
            Assert.True(File.Exists(jarmodFile));

            // removing the jar mod component deletes both the patch record and the copied jar
            Assert.True(profile.Remove(jarmodComponent.GetId()));
            Assert.False(jarmodPatch.ExistsFile());
            Assert.False(File.Exists(jarmodFile));
            Assert.DoesNotContain(profile.Components, c => c.GetId() == jarmodComponent.GetId());
        }
        finally
        {
            File.Delete(jarmodSource);
        }
    }

    // ---------------------------------------------------------------- installers

    [Fact]
    public void InstallCustomJar()
    {
        var source = Path.Combine(Path.GetTempPath(), "flint-customjar-" + Guid.NewGuid().ToString("N") + ".zip");
        File.WriteAllText(source, "not a real jar");
        try
        {
            using var profile = new PackProfile(new AbsolutePath(_root));
            profile.BuildingFromScratch();
            Assert.True(profile.InstallCustomJar(new AbsolutePath(source)));

            var component = profile.Components.Single();
            Assert.Equal("customjar", component.GetId());
            Assert.True(component.IsCustom());
            Assert.EndsWith(" (custom jar)", component.GetName());

            // the jar is copied into <instance>/libraries/customjar-1.jar
            Assert.True(File.Exists(Path.Combine(_root, "libraries", "customjar-1.jar")));

            var patch = ParseNode(profile.PatchFilePathForUid("customjar").ReadAllText()).AsObject();
            Assert.Equal("customjar", patch["uid"]?.ToString());
            Assert.NotNull(patch["mainJar"]);

            profile.SaveNow();
            var pack = ParseNode(File.ReadAllText(Path.Combine(_root, "mmc-pack.json")));
            var components = pack["components"]?.AsArray();
            Assert.NotNull(components);
            Assert.Equal("customjar", components[0]?["uid"]?.ToString());
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Fact]
    public void InstallAgents()
    {
        var source = Path.Combine(Path.GetTempPath(), "flint-agent-" + Guid.NewGuid().ToString("N") + ".jar");
        File.WriteAllText(source, "not a real agent");
        try
        {
            using var profile = new PackProfile(new AbsolutePath(_root));
            profile.BuildingFromScratch();
            Assert.True(profile.InstallAgents([new AbsolutePath(source)]));

            var component = profile.Components.Single();
            Assert.StartsWith("custom.agent.", component.GetId());
            Assert.EndsWith(" (agent)", component.GetName());

            // the agent jar is copied into <instance>/libraries/<guid>.jar
            Assert.Single(Directory.EnumerateFiles(Path.Combine(_root, "libraries")));

            var patch = ParseNode(profile.PatchFilePathForUid(component.GetId()).ReadAllText()).AsObject();
            var agents = patch["+agents"]?.AsArray();
            Assert.NotNull(agents);
            Assert.Single(agents);
            Assert.StartsWith("custom.agents:", agents[0]?["name"]?.ToString());
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Fact]
    public void InstallEmpty()
    {
        using var profile = new PackProfile(new AbsolutePath(_root));
        profile.BuildingFromScratch();
        Assert.True(profile.InstallEmpty("org.quiltmc.quilt-loader", "Quilt Loader"));

        var component = profile.Components.Single();
        Assert.Equal("org.quiltmc.quilt-loader", component.GetId());
        Assert.Equal("Quilt Loader", component.GetName());
        Assert.True(component.IsCustom());

        var patch = ParseNode(profile.PatchFilePathForUid("org.quiltmc.quilt-loader").ReadAllText());
        Assert.Equal("org.quiltmc.quilt-loader", patch["uid"]?.ToString());
        Assert.Equal("1", patch["version"]?.ToString());
    }

    // ---------------------------------------------------------------- mod loaders

    [Fact]
    public void GetModLoaders()
    {
        using var profile = new PackProfile(new AbsolutePath(_root));
        profile.BuildingFromScratch();

        var minecraft = new Component(profile, "net.minecraft") { Version = "1.20.1" };
        minecraft.SetImportant(true);
        var fabric = new Component(profile, "net.fabricmc.fabric-loader") { Version = "0.14.21" };
        profile.AppendComponent(minecraft);
        profile.AppendComponent(fabric);

        Assert.Equal(ModLoaderType.Fabric, profile.GetModLoaders());

        profile.Remove("net.fabricmc.fabric-loader");
        Assert.Null(profile.GetModLoaders());

        // quilt implies fabric support; and fabric implies it is in the loader list
        var quilt = new Component(profile, "org.quiltmc.quilt-loader") { Version = "0.19.2" };
        profile.AppendComponent(quilt);
        Assert.Equal(ModLoaderType.Quilt | ModLoaderType.Fabric, profile.GetSupportedModLoaders());
        Assert.Equal(new[] { ModLoaderType.Quilt, ModLoaderType.Fabric }, profile.GetModLoadersList());
    }
}
