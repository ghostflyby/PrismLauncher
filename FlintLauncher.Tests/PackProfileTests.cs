// SPDX-License-Identifier: GPL-3.0-only
// New tests locking the persistence behavior ported from launcher/minecraft/PackProfile.{h,cpp}:
// mmc-pack.json round trips, the byte-exact Qt-style save format, patches/*.json handling and
// the jar mod / custom jar / agent installers.
using System.Text.Json.Nodes;
using FlintLauncher.Core.Minecraft;
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
        catch (IOException)
        {
        }
    }

    private string WriteFile(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
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

        using var profile = new PackProfile(_root);
        var result = profile.Load();
        Assert.True(result.Ok, result.ErrorMessage);

        Assert.Equal(3, profile.Components.Count);

        var minecraft = profile.Components[0];
        Assert.Equal("net.minecraft", minecraft.GetID());
        Assert.Equal("1.12.2", minecraft.Version);
        Assert.True(minecraft.Important);
        Assert.False(minecraft.DependencyOnly);
        Assert.Equal("1.12.2", minecraft.GetVersion());
        Assert.Equal("Minecraft", minecraft.GetName());
        Assert.False(minecraft.IsCustom());

        var lwjgl = profile.Components[1];
        Assert.Equal("org.lwjgl3", lwjgl.GetID());
        // the patch file is on disk and wins over metadata
        Assert.True(lwjgl.IsRemovable());

        var hashed = profile.Components[2];
        Assert.Equal("org.quiltmc.hashed", hashed.GetID());
        Assert.True(hashed.DependencyOnly);
        Assert.Equal("1.12.2", hashed.Version);
    }

    [Fact]
    public void SaveProducesQtCompatibleJson()
    {
        using var profile = new PackProfile(_root);
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
        loader.CachedRequires.Add(new FlintLauncher.Core.Meta.Require { Uid = "net.minecraft", EqualsVersion = "1.20.1" });
        profile.AppendComponent(minecraft);
        profile.AppendComponent(loader);

        profile.SaveNow();
        Assert.False(profile.SaveIsScheduled());

        var expected = """
            {
                "components": [
                    {
                        "important": true,
                        "uid": "net.minecraft",
                        "version": "1.20.1"
                    },
                    {
                        "cachedName": "Fabric Loader",
                        "cachedRequires": [
                            {
                                "equals": "1.20.1",
                                "uid": "net.minecraft"
                            }
                        ],
                        "cachedVersion": "0.14.21",
                        "uid": "net.fabricmc.fabric-loader",
                        "version": "0.14.21"
                    }
                ],
                "formatVersion": 1
            }
            """ + "\n"; // QJsonDocument::toJson() ends with a newline
        Assert.Equal(expected, File.ReadAllText(Path.Combine(_root, "mmc-pack.json")));

        // and the written file loads back identically
        var result = profile.Load();
        Assert.True(result.Ok, result.ErrorMessage);
        Assert.Equal(2, profile.Components.Count);
        Assert.Equal("net.minecraft", profile.Components[0].GetID());
        Assert.Equal("0.14.21", profile.Components[1].Version);
        Assert.True(profile.Components[1].CachedRequires.Contains(
            new FlintLauncher.Core.Meta.Require { Uid = "net.minecraft", EqualsVersion = "1.20.1" }));
    }

    [Fact]
    public void LoadRejectsUnknownFormatVersion()
    {
        WriteFile("mmc-pack.json", """{ "formatVersion": 99, "components": [] }""");
        using var profile = new PackProfile(_root);
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
        using var profile = new PackProfile(_root);
        var result = profile.Load();
        Assert.True(result.Ok, result.ErrorMessage);
        Assert.Single(profile.Components);
        Assert.Equal("1.12.2", profile.Components[0].Version);
    }

    // ---------------------------------------------------------------- component ops

    [Fact]
    public void MoveReordersComponents()
    {
        using var profile = new PackProfile(_root);
        profile.BuildingFromScratch();
        var a = new Component(profile, "test.a");
        var b = new Component(profile, "test.b");
        var c = new Component(profile, "test.c");
        profile.AppendComponent(a);
        profile.AppendComponent(b);
        profile.AppendComponent(c);

        profile.Move(1, PackProfile.MoveDirection.MoveUp); // b moves to the front
        Assert.Equal(new[] { "test.b", "test.a", "test.c" }, profile.Components.Select(x => x.GetID()).ToArray());

        profile.Move(1, PackProfile.MoveDirection.MoveDown); // a moves behind c
        Assert.Equal(new[] { "test.b", "test.c", "test.a" }, profile.Components.Select(x => x.GetID()).ToArray());
    }

    [Fact]
    public void RemoveDropsPatchFileAndJarModFile()
    {
        var jarmodSource = Path.Combine(Path.GetTempPath(), "flint-jarmod-" + Guid.NewGuid().ToString("N") + ".jar");
        File.WriteAllText(jarmodSource, "not a real jar");
        try
        {
            using var profile = new PackProfile(_root);
            profile.BuildingFromScratch();
            Assert.True(profile.InstallJarMods([jarmodSource]));

            var jarmodComponent = profile.Components.Single(c => c.GetID().StartsWith("custom.jarmod."));
            var jarmodPatch = profile.PatchFilePathForUid(jarmodComponent.GetID());
            var jarmodFile = Directory.EnumerateFiles(Path.Combine(_root, "jarmods")).Single();
            Assert.True(File.Exists(jarmodPatch));
            Assert.True(File.Exists(jarmodFile));

            // removing the jar mod component deletes both the patch record and the copied jar
            Assert.True(profile.Remove(jarmodComponent.GetID()));
            Assert.False(File.Exists(jarmodPatch));
            Assert.False(File.Exists(jarmodFile));
            Assert.DoesNotContain(profile.Components, c => c.GetID() == jarmodComponent.GetID());
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
            using var profile = new PackProfile(_root);
            profile.BuildingFromScratch();
            Assert.True(profile.InstallCustomJar(source));

            var component = profile.Components.Single();
            Assert.Equal("customjar", component.GetID());
            Assert.True(component.IsCustom());
            Assert.EndsWith(" (custom jar)", component.GetName());

            // the jar is copied into <instance>/libraries/customjar-1.jar
            Assert.True(File.Exists(Path.Combine(_root, "libraries", "customjar-1.jar")));

            var patch = JsonNode.Parse(File.ReadAllText(profile.PatchFilePathForUid("customjar")))!.AsObject();
            Assert.Equal("customjar", patch["uid"]!.ToString());
            Assert.NotNull(patch["mainJar"]);

            profile.SaveNow();
            var pack = JsonNode.Parse(File.ReadAllText(Path.Combine(_root, "mmc-pack.json")))!;
            Assert.Equal("customjar", pack["components"]!.AsArray()[0]!["uid"]!.ToString());
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
            using var profile = new PackProfile(_root);
            profile.BuildingFromScratch();
            Assert.True(profile.InstallAgents([source]));

            var component = profile.Components.Single();
            Assert.StartsWith("custom.agent.", component.GetID());
            Assert.EndsWith(" (agent)", component.GetName());

            // the agent jar is copied into <instance>/libraries/<guid>.jar
            Assert.Single(Directory.EnumerateFiles(Path.Combine(_root, "libraries")));

            var patch = JsonNode.Parse(File.ReadAllText(profile.PatchFilePathForUid(component.GetID())))!.AsObject();
            var agents = patch["+agents"]!.AsArray();
            Assert.Single(agents);
            Assert.StartsWith("custom.agents:", agents[0]!["name"]!.ToString());
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Fact]
    public void InstallEmpty()
    {
        using var profile = new PackProfile(_root);
        profile.BuildingFromScratch();
        Assert.True(profile.InstallEmpty("org.quiltmc.quilt-loader", "Quilt Loader"));

        var component = profile.Components.Single();
        Assert.Equal("org.quiltmc.quilt-loader", component.GetID());
        Assert.Equal("Quilt Loader", component.GetName());
        Assert.True(component.IsCustom());

        var patch = JsonNode.Parse(File.ReadAllText(profile.PatchFilePathForUid("org.quiltmc.quilt-loader")))!;
        Assert.Equal("org.quiltmc.quilt-loader", patch["uid"]!.ToString());
        Assert.Equal("1", patch["version"]!.ToString());
    }

    // ---------------------------------------------------------------- mod loaders

    [Fact]
    public void GetModLoaders()
    {
        using var profile = new PackProfile(_root);
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
