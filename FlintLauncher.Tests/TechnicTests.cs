// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Tests for the Technic platform: the Platform API URL building and search/metadata
// parsing (including the url-vs-solder detection), the SingleZipPackInstallTask over the
// three pack flavors (bin/version.json, bin/modpack.jar, vanilla), the Solder pack install
// (build JSON -> md5-verified mod downloads -> extraction -> postProcess) and the pack
// processor's component writing — all against the stub Technic/Solder server. Each test
// drives its own TechnicApi client pointed at the stub (the former process-global
// BaseUrl/ApiBuild hooks are per-instance state now).

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.ModPlatform.Technic;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Tasks;
using Xunit;

namespace FlintLauncher.Tests;

public class TechnicTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"flint-technic-{Guid.NewGuid():N}");
    private readonly TestGlobalSettings _global = new();
    private readonly StubTechnicServer _server = new();
    private readonly TechnicApi _api;

    public TechnicTests()
    {
        Directory.CreateDirectory(_root);
        _api = new TechnicApi { BaseUrl = _server.Url + "/", ApiBuild = "testbuild" };
    }

    public void Dispose()
    {
        _server.Dispose();
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

    private async Task<SingleZipPackInstallTask> RunZipInstall(TechnicZipPackBuilder pack, string minecraftVersion = "",
        string name = "Technic Imported")
    {
        var zipName = $"pack-{Guid.NewGuid():N}.zip";
        var zipPath = Path.Combine(_root, zipName);
        Assert.True(pack.WriteZip(zipPath));
        // the zip is served by the stub (like the Platform "url" field would point to)
        _server.Downloads[zipName] = await File.ReadAllBytesAsync(zipPath);

        var list = CreateList();
        var task = new SingleZipPackInstallTask(new Uri($"{_server.Url}/{zipName}"), minecraftVersion);
        task.SetName(name);
        var wrapped = list.WrapInstanceTask(task);
        await wrapped.StartAsync();
        return task;
    }

    // ---------------------------------------------------------------- API surface

    [Fact]
    public void SearchUrl_BuildsPlatformUrls()
    {
        // empty term -> trending, plain term -> search, #slug / modpack urls -> single
        var (trending, trendingMode) = _api.SearchUrl("");
        Assert.Equal($"{_api.BaseUrl}trending?build=testbuild", trending);
        Assert.Equal(TechnicSearchMode.List, trendingMode);

        var (search, searchMode) = _api.SearchUrl("bevo");
        Assert.Equal($"{_api.BaseUrl}search?build=testbuild&q=bevo", search);
        Assert.Equal(TechnicSearchMode.List, searchMode);

        var (hashed, hashedMode) = _api.SearchUrl("#bevo");
        Assert.Equal("https://api.technicpack.net/modpack/bevo?build=testbuild", hashed);
        Assert.Equal(TechnicSearchMode.Single, hashedMode);

        var (httpsUrl, httpsMode) = _api.SearchUrl("https://api.technicpack.net/modpack/bevo");
        Assert.Equal("https://api.technicpack.net/modpack/bevo?build=testbuild", httpsUrl);
        Assert.Equal(TechnicSearchMode.Single, httpsMode);

        var (httpUrl, _) = _api.SearchUrl("http://api.technicpack.net/modpack/bevo");
        Assert.Equal("https://api.technicpack.net/modpack/bevo?build=testbuild", httpUrl);
    }

    [Fact]
    public async Task Search_ListParsing_SkipsVanillaAndMapsLogos()
    {
        _server.SearchListJson = """
                                 {"modpacks":[
                                     {"name":"Bevo's Tech Pack","slug":"bevo","iconUrl":"http://cdn.technicpack.net/bevo.png"},
                                     {"name":"Vanilla","slug":"vanilla","iconUrl":"null"},
                                     {"name":"No Icon","slug":"noicon"}
                                 ]}
                                 """;
        var (url, mode) = _api.SearchUrl("");
        var request = NetRequest.MakeByteArray(new Uri(url));
        var job = new NetJob("search");
        job.AddNetRequest(request);
        await job.StartAsync();
        Assert.True(job.WasSuccessful, job.FailReason);
        Assert.Equal("testbuild", _server.LastQuery["build"]);

        var packs = TechnicApi.LoadSearchResults(request.BodyAsString, mode);

        Assert.Equal(2, packs.Count);
        Assert.Equal("bevo", packs[0].Slug);
        Assert.Equal("Bevo's Tech Pack", packs[0].Name);
        Assert.Equal("http://cdn.technicpack.net/bevo.png", packs[0].LogoUrl);
        Assert.Equal("bevo.png", packs[0].LogoName);
        Assert.False(packs[0].Broken);
        Assert.Equal("noicon", packs[1].Slug);
        Assert.Equal("null", packs[1].LogoName);
    }

    [Fact]
    public void Search_SingleParsing_AndErrorPayload()
    {
        var results = TechnicApi.LoadSearchResults("""
                                                   {"displayName":"Hexxit Updated","name":"hexxit-updated","icon":{"url":"http://cdn.technicpack.net/hexxit.jpg"}}
                                                   """, TechnicSearchMode.Single);

        var pack = Assert.Single(results);
        Assert.Equal("Hexxit Updated", pack.Name);
        Assert.Equal("hexxit-updated", pack.Slug);
        Assert.Equal("hexxit-updated.jpg", pack.LogoName);

        Assert.Empty(TechnicApi.LoadSearchResults("""{"error":"invalid id"}""", TechnicSearchMode.Single));
        // a malformed payload parses to nothing (the C++ logs and returns)
        Assert.Empty(TechnicApi.LoadSearchResults("""{"modpacks":}""", TechnicSearchMode.List));
    }

    [Fact]
    public void Search_NonObjectPayloads_ParseToNothing()
    {
        // the requireObject(doc) path: a non-object root is a parse failure, never a crash
        Assert.Empty(TechnicApi.LoadSearchResults("[]", TechnicSearchMode.List));
        Assert.Empty(TechnicApi.LoadSearchResults("\"text\"", TechnicSearchMode.Single));
        Assert.False(TechnicApi.LoadPackMetadata("[]", new TechnicModpack()));
        Assert.Null(TechnicApi.LoadSolderPack("[]"));
    }

    [Fact]
    public void Search_NullAndNonStringMembers_ReadAsTheMissingDefaults()
    {
        // {"error":null} still marks the invalid-API-url payload (contains(), not the value)
        Assert.Empty(TechnicApi.LoadSearchResults("""{"error":null}""", TechnicSearchMode.Single));

        // JSON null / non-string members read like QJsonValue's toString(default) fallbacks
        var packs = TechnicApi.LoadSearchResults("""
                                                 {"modpacks":[
                                                     {"name":"Null Icon","slug":"nullicon","iconUrl":null},
                                                     {"name":"Number Icon","slug":"numicon","iconUrl":5}
                                                 ]}
                                                 """, TechnicSearchMode.List);
        Assert.Equal(2, packs.Count);
        Assert.Equal("null", packs[0].LogoUrl);
        Assert.Equal("null", packs[0].LogoName);
        Assert.Equal("null", packs[1].LogoUrl);

        var zipPack = new TechnicModpack { Slug = "zip" };
        Assert.True(TechnicApi.LoadPackMetadata("""{"url":"http://x","minecraft":null,"user":7}""", zipPack));
        Assert.Equal("", zipPack.MinecraftVersion);
        Assert.Equal("", zipPack.Author);
    }

    [Fact]
    public void PackMetadata_DetectsSolderByUrlOrSolderField()
    {
        var zipPack = new TechnicModpack { Slug = "zip-pack" };
        Assert.True(TechnicApi.LoadPackMetadata("""
                                                {"url":"http://www.technicpack.net/download/zip-pack","minecraft":"1.12.2","platformUrl":"http://technicpack.net","user":"sarge","description":"A zip pack","version":"1.2.3"}
                                                """, zipPack));
        Assert.False(zipPack.IsSolder);
        Assert.Equal("http://www.technicpack.net/download/zip-pack", zipPack.Url);
        Assert.Equal("1.12.2", zipPack.MinecraftVersion);
        Assert.Equal("sarge", zipPack.Author);
        Assert.Equal("1.2.3", zipPack.CurrentVersion);
        Assert.True(zipPack.MetadataLoaded);

        var solderPack = new TechnicModpack { Slug = "solder-pack" };
        Assert.True(TechnicApi.LoadPackMetadata(
            """{"solder":"http://solder.example.com/","minecraft":"1.7.10"}""", solderPack));
        Assert.True(solderPack.IsSolder);
        Assert.Equal("http://solder.example.com/", solderPack.Url);
        Assert.Equal("1.7.10", solderPack.MinecraftVersion);

        // neither key -> the metadata step bails (upstream warns and stays)
        Assert.False(TechnicApi.LoadPackMetadata("""{"minecraft":"1.7.10"}""", new TechnicModpack()));
    }

    [Fact]
    public void SolderPackListing_ParsesBuilds()
    {
        var pack = TechnicApi.LoadSolderPack(
            """{"recommended":"1.1.0","latest":"1.2.0","builds":["1.0.0","1.1.0","1.2.0"]}""");

        Assert.NotNull(pack);
        Assert.Equal("1.1.0", pack.Recommended);
        Assert.Equal("1.2.0", pack.Latest);
        Assert.Equal(["1.0.0", "1.1.0", "1.2.0"], pack.Builds);

        // a malformed listing parses to null (the C++ falls back to the current version)
        Assert.Null(TechnicApi.LoadSolderPack("""{"recommended":}"""));
    }

    // ------------------------------------------------------ SingleZip install flavors

    [Fact]
    public async Task SingleZip_VersionJsonFlavor_SetsComponentsAndLayout()
    {
        var task = await RunZipInstall(new TechnicZipPackBuilder());
        Assert.True(task.WasSuccessful, task.FailReason);

        var staging = Path.Combine(_root, "instances", "Technic Imported");
        // the extracted pack landed in minecraft/
        Assert.Equal("technic=1\n", await File.ReadAllTextAsync(Path.Combine(staging, "minecraft", "modpack", "options.txt")));
        Assert.True(File.Exists(Path.Combine(staging, "minecraft", "bin", "version.json")));
        // instance.json identity
        var cfg = await File.ReadAllTextAsync(Path.Combine(staging, "instance.json"));
        Assert.Contains("Technic Imported", cfg);
        // the forge library name was translated into the component version
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(staging, "mmc-pack.json")));
        var components = doc.RootElement.GetProperty("components").EnumerateArray()
            .Select(c => (Uid: c.GetProperty("uid").GetString(), Version: c.TryGetProperty("version", out var v) ? v.GetString() : null))
            .ToList();
        Assert.Contains(components, c => c is { Uid: "net.minecraft", Version: "1.20.1" });
        Assert.Contains(components, c => c is { Uid: "net.minecraftforge", Version: "42.0.0" });
    }

    [Fact]
    public async Task SingleZip_Forge1710Library_FiltersBuildNumber()
    {
        // 1.7.10 versions look like 1.7.10-10.13.4.1614-1.7.10; the component keeps 10.13.4.1614
        var pack = new TechnicZipPackBuilder
        {
            VersionJson = """{"inheritsFrom":"1.7.10","id":"legacy","libraries":[{"name":"net.minecraftforge:forge:1.7.10-10.13.4.1614-1.7.10"}]}""",
        };
        var task = await RunZipInstall(pack, name: "Technic 1710");
        Assert.True(task.WasSuccessful, task.FailReason);

        var staging = Path.Combine(_root, "instances", "Technic 1710");
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(staging, "mmc-pack.json")));
        Assert.Contains(doc.RootElement.GetProperty("components").EnumerateArray(),
            c => c.GetProperty("uid").GetString() == "net.minecraftforge"
                 && c.GetProperty("version").GetString() == "10.13.4.1614");
    }

    [Fact]
    public async Task SingleZip_FabricAndQuiltLibraries_MapToComponents()
    {
        var fabricPack = new TechnicZipPackBuilder
        {
            VersionJson = """{"inheritsFrom":"1.20.1","id":"fabricpack","libraries":[{"name":"net.fabricmc:fabric-loader:0.14.21"}]}""",
        };
        var task = await RunZipInstall(fabricPack, name: "Technic Fabric");
        Assert.True(task.WasSuccessful, task.FailReason);
        var staging = Path.Combine(_root, "instances", "Technic Fabric");
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(staging, "mmc-pack.json")));
        Assert.Contains(doc.RootElement.GetProperty("components").EnumerateArray(),
            c => c.GetProperty("uid").GetString() == "net.fabricmc.fabric-loader"
                 && c.GetProperty("version").GetString() == "0.14.21");

        var quiltPack = new TechnicZipPackBuilder
        {
            VersionJson = """{"inheritsFrom":"1.20.1","id":"quiltpack","libraries":[{"name":"org.quiltmc:quilt-loader:0.19.0"}]}""",
        };
        var task2 = await RunZipInstall(quiltPack, name: "Technic Quilt");
        Assert.True(task2.WasSuccessful, task2.FailReason);
        var staging2 = Path.Combine(_root, "instances", "Technic Quilt");
        using var doc2 = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(staging2, "mmc-pack.json")));
        Assert.Contains(doc2.RootElement.GetProperty("components").EnumerateArray(),
            c => c.GetProperty("uid").GetString() == "org.quiltmc.quilt-loader"
                 && c.GetProperty("version").GetString() == "0.19.0");
    }

    [Fact]
    public async Task SingleZip_ModpackJarWithoutVersionJson_InstallsJarModAndForgeProperties()
    {
        var pack = new TechnicZipPackBuilder
        {
            WithModpackJar = true,
            ForgeVersionProperties = "forge.major.number=1\nforge.minor.number=4\nforge.revision.number=7\nforge.build.number=5\n",
        };
        // the minecraft version must be known (as from the Platform metadata)
        var task = await RunZipInstall(pack, minecraftVersion: "1.4.7", name: "Technic JarForge");
        Assert.True(task.WasSuccessful, task.FailReason);

        var staging = Path.Combine(_root, "instances", "Technic JarForge");
        Assert.True(File.Exists(Path.Combine(staging, "minecraft", "bin", "modpack.jar")));
        // the jar was installed as a jar mod
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(staging, "jarmods")));
        Assert.Contains(Directory.GetFiles(Path.Combine(staging, "patches")),
            f => Path.GetFileName(f).StartsWith("custom.jarmod."));
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(staging, "mmc-pack.json")));
        var components = doc.RootElement.GetProperty("components").EnumerateArray()
            .Select(c => (Uid: c.GetProperty("uid").GetString(), Version: c.TryGetProperty("version", out var v) ? v.GetString() : null))
            .ToList();
        Assert.Contains(components, c => c is { Uid: "net.minecraft", Version: "1.4.7" });
        Assert.Contains(components, c => c is { Uid: "net.minecraftforge", Version: "1.4.7.5" });
    }

    [Fact]
    public async Task SingleZip_ModpackJarWithoutVersionJsonAndUnknownMc_Fails()
    {
        var task = await RunZipInstall(new TechnicZipPackBuilder { WithModpackJar = true }, name: "Technic UnknownMc");
        Assert.Equal(TaskState.Failed, task.State);
        Assert.Contains("Minecraft version is unknown", task.FailReason);
    }

    [Fact]
    public async Task SingleZip_ModpackJarEmbeddingVersionJson_ParsesIt()
    {
        var pack = new TechnicZipPackBuilder
        {
            WithModpackJar = true,
            JarEmbedsVersionJson = true,
            VersionJson = """{"inheritsFrom":"1.6.4","id":"embedded","libraries":[{"name":"net.minecraftforge:minecraftforge:9.11.1.964"}]}""",
        };
        var task = await RunZipInstall(pack, name: "Technic Embedded");
        Assert.True(task.WasSuccessful, task.FailReason);

        var staging = Path.Combine(_root, "instances", "Technic Embedded");
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(staging, "mmc-pack.json")));
        var components = doc.RootElement.GetProperty("components").EnumerateArray()
            .Select(c => (Uid: c.GetProperty("uid").GetString(), Version: c.TryGetProperty("version", out var v) ? v.GetString() : null))
            .ToList();
        Assert.Contains(components, c => c is { Uid: "net.minecraft", Version: "1.6.4" });
        Assert.Contains(components, c => c is { Uid: "net.minecraftforge", Version: "9.11.1.964" });
    }

    [Fact]
    public async Task SingleZip_ModpackJarFmlProperties_FallbackMcVersion()
    {
        // version.json without inheritsFrom falls back to fmlbuild.mcversion
        var pack = new TechnicZipPackBuilder
        {
            WithModpackJar = true,
            JarEmbedsVersionJson = true,
            VersionJson = """{"id":"fml-only","libraries":[]}""",
            FmlVersionProperties = "fmlbuild.mcversion=1.6.4\nfmlbuild.deobfuscated_artifact=yes\n",
        };
        var task = await RunZipInstall(pack, name: "Technic Fml");
        Assert.True(task.WasSuccessful, task.FailReason);

        var staging = Path.Combine(_root, "instances", "Technic Fml");
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(staging, "mmc-pack.json")));
        Assert.Contains(doc.RootElement.GetProperty("components").EnumerateArray(),
            c => c.GetProperty("uid").GetString() == "net.minecraft"
                 && c.GetProperty("version").GetString() == "1.6.4");
    }

    [Fact]
    public async Task SingleZip_ModpackJarFmlMissing_FailsWithInheritsFromMessage()
    {
        var pack = new TechnicZipPackBuilder
        {
            WithModpackJar = true,
            JarEmbedsVersionJson = true,
            VersionJson = """{"id":"no-inherits","libraries":[]}""",
        };
        var task = await RunZipInstall(pack, name: "Technic NoInherits");
        Assert.Equal(TaskState.Failed, task.State);
        Assert.Contains("inheritsFrom is missing", task.FailReason);
    }

    [Fact]
    public async Task SingleZip_VanillaFlavor_UsesPassedMinecraftVersion()
    {
        // no bin/modpack.jar and no bin/version.json: the "vanilla" flavor
        var task = await RunZipInstall(new TechnicZipPackBuilder { WithVersionJson = false },
            minecraftVersion: "1.19.2", name: "Technic Vanilla");
        Assert.True(task.WasSuccessful, task.FailReason);

        var staging = Path.Combine(_root, "instances", "Technic Vanilla");
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(staging, "mmc-pack.json")));
        var components = doc.RootElement.GetProperty("components").EnumerateArray().ToList();
        Assert.Single(components);
        Assert.Equal("net.minecraft", components[0].GetProperty("uid").GetString());
        Assert.Equal("1.19.2", components[0].GetProperty("version").GetString());
    }

    [Fact]
    public async Task SingleZip_MalformedVersionJson_FailsWithParseMessage()
    {
        // the surfaced parse error keeps the wrapper prefix; the STJ detail text follows it
        // (the former requireDocument "Error parsing JSON" wording is not the contract)
        var task = await RunZipInstall(new TechnicZipPackBuilder { VersionJson = """{"inheritsFrom":}""" },
            name: "Technic BadVersionJson");
        Assert.Equal(TaskState.Failed, task.State);
        Assert.Contains("Could not understand \"version.json\":", task.FailReason);
    }

    // --------------------------------------------------------------- Solder install

    [Fact]
    public async Task SolderInstall_EndToEnd_DownloadsAndExtractsMods()
    {
        var modBytes = TechnicZipPackBuilder.BuildSolderModZip("mods/soldermod.jar", "solder mod payload"u8.ToArray());
        _server.SolderBuildJson["testpack/1.0.0"] = new JsonObject
        {
            ["minecraft"] = "1.20.1",
            ["mods"] = new JsonArray(
                new JsonObject
                {
                    ["name"] = "Solder Mod",
                    ["version"] = "1.0",
                    ["md5"] = Convert.ToHexString(MD5.HashData(modBytes)).ToLowerInvariant(),
                    ["url"] = $"{_server.Url}/files/soldermod.zip",
                }),
        }.ToJsonString();
        _server.Downloads["soldermod.zip"] = modBytes;

        var list = CreateList();
        var task = new SolderPackInstallTask(new Uri(_server.Url), "testpack", "1.0.0", minecraftVersion: "");
        task.SetName("Solder Imported");
        var wrapped = list.WrapInstanceTask(task);
        await wrapped.StartAsync();

        Assert.True(task.WasSuccessful, task.FailReason);

        var staging = Path.Combine(_root, "instances", "Solder Imported");
        // the mod zip was downloaded (md5-verified) and extracted into minecraft/
        Assert.Equal("solder mod payload",
            await File.ReadAllTextAsync(Path.Combine(staging, "minecraft", "mods", "soldermod.jar")));
        // the postProcess used the build's minecraft version (vanilla flavor)
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(staging, "mmc-pack.json")));
        var components = doc.RootElement.GetProperty("components").EnumerateArray().ToList();
        Assert.Single(components);
        Assert.Equal("net.minecraft", components[0].GetProperty("uid").GetString());
        Assert.Equal("1.20.1", components[0].GetProperty("version").GetString());
    }

    [Fact]
    public async Task SolderInstall_Md5Mismatch_Fails()
    {
        var modBytes = TechnicZipPackBuilder.BuildSolderModZip("mods/soldermod.jar", "solder mod payload"u8.ToArray());
        _server.SolderBuildJson["testpack/1.0.0"] = new JsonObject
        {
            ["minecraft"] = "1.20.1",
            ["mods"] = new JsonArray(
                new JsonObject
                {
                    ["name"] = "Solder Mod",
                    ["md5"] = new string('0', 32),
                    ["url"] = $"{_server.Url}/files/soldermod.zip",
                }),
        }.ToJsonString();
        _server.Downloads["soldermod.zip"] = modBytes;

        var list = CreateList();
        var task = new SolderPackInstallTask(new Uri(_server.Url), "testpack", "1.0.0", minecraftVersion: "");
        task.SetName("Solder BadMd5");
        var wrapped = list.WrapInstanceTask(task);
        await wrapped.StartAsync();

        Assert.Equal(TaskState.Failed, task.State);
    }

    [Fact]
    public async Task SolderInstall_MalformedManifest_FailsWithParseMessage()
    {
        // the surfaced parse error keeps the requireDocument wrapping around the node parse
        _server.SolderBuildJson["testpack/1.0.0"] = """{"minecraft":}""";
        var list = CreateList();
        var task = new SolderPackInstallTask(new Uri(_server.Url), "testpack", "1.0.0", minecraftVersion: "");
        task.SetName("Solder BadManifest");
        var wrapped = list.WrapInstanceTask(task);
        await wrapped.StartAsync();

        Assert.Equal(TaskState.Failed, task.State);
        Assert.Contains("Could not understand pack manifest:", task.FailReason);
        Assert.Contains("Error parsing JSON", task.FailReason);
    }

    [Fact]
    public async Task SolderInstall_MinecraftFromBuild_OverridesFallback()
    {
        // when the build declares a minecraft version it wins over the fallback
        _server.SolderBuildJson["vanillapack/2.0"] = new JsonObject
        {
            ["minecraft"] = "1.16.5",
            ["mods"] = new JsonArray(),
        }.ToJsonString();

        var list = CreateList();
        var task = new SolderPackInstallTask(new Uri(_server.Url), "vanillapack", "2.0", minecraftVersion: "1.12.2");
        task.SetName("Solder Vanilla");
        var wrapped = list.WrapInstanceTask(task);
        await wrapped.StartAsync();

        Assert.True(task.WasSuccessful, task.FailReason);
        var staging = Path.Combine(_root, "instances", "Solder Vanilla");
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(staging, "mmc-pack.json")));
        Assert.Contains(doc.RootElement.GetProperty("components").EnumerateArray(),
            c => c.GetProperty("uid").GetString() == "net.minecraft"
                 && c.GetProperty("version").GetString() == "1.16.5");
    }
}
