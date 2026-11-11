// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Tests for the ATLauncher platform: packsnew.json index parsing, share-code responses,
// and the end-to-end pack install (Configs.json fetch -> Configs.zip download+extraction
// with sha1 verification -> md5-verified mod downloads -> instance creation with the
// net.minecraft/loader components, jar mods and the optional-mod selection honored) —
// all against the stub ATL server. Each test drives its own AtLauncherClient pointed
// at the stub (the former process-global DownloadServerUrl/ApiBaseUrl hooks are
// per-instance state now).

using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.ModPlatform.AtLauncher;
using FlintLauncher.Core.Net;
using FlintLauncher.ExternalApi;
using FlintLauncher.ExternalApi.AtLauncher;
using TruePath;
using Xunit;

namespace FlintLauncher.Tests;

public class AtLauncherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"flint-atl-{Guid.NewGuid():N}");
    private readonly TestGlobalSettings _global = new();
    private readonly StubAtlServer _server = new();
    private readonly AtLauncherClient _api;

    public AtLauncherTests()
    {
        Directory.CreateDirectory(_root);
        // both URL hooks point at the stub (note the trailing slash like the C++ defaults)
        _api = new AtLauncherClient(new AtLauncherClientOptions
        {
            DownloadServerUrl = _server.Url + "/",
            ApiBaseUrl = _server.Url + "/",
        });
    }

    public void Dispose()
    {
        // (the meta lookup hooks are per-task state now — nothing process-global to reset)
        _server.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException) { }
    }

    private static JsonObject ParseObject(string json)
    {
        var node = JsonNode.Parse(json)?.AsObject();
        Assert.NotNull(node);
        return node;
    }

    private static JsonObject ChildObject(JsonObject parent, string property)
    {
        var node = parent[property]?.AsObject();
        Assert.NotNull(node);
        return node;
    }

    private InstanceList CreateList()
    {
        var instancesDir = Path.Combine(_root, "instances");
        Directory.CreateDirectory(instancesDir);
        return _global.BareInstanceList(instancesDir);
    }

    /// <summary>The (uid, version) component list of the created instance's mmc-pack.json;
    /// the version is null for components without one (e.g. jar mods).</summary>
    private static List<(string? Uid, string? Version)> Components(string staging)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(staging, "mmc-pack.json")));
        return doc.RootElement.GetProperty("components").EnumerateArray()
            .Select(c => (Uid: c.GetProperty("uid").GetString(),
                Version: c.TryGetProperty("version", out var v) ? v.GetString() : null))
            .ToList();
    }

    /// <summary>Runs the install and returns the reason it failed, or null when it succeeded.</summary>
    private async Task<(AtLauncherPackInstallTask Task, string? Failure)> RunInstall(AtlPackFixture fixture,
        IReadOnlyDictionary<string, bool>? optionalSelection = null, string name = "ATL Imported",
        Func<string, string, string, string?>? recommendedLookup = null)
    {
        fixture.Register(_server);
        var list = CreateList();
        var task = new AtLauncherPackInstallTask(fixture.PackName, fixture.VersionName, api: _api, http: TestNet.Http)
        {
            OptionalModsSelection = optionalSelection ?? new Dictionary<string, bool>
            {
                ["Optional Selected"] = true,
                ["Optional Unselected"] = false,
            },
            RecommendedLoaderVersionLookup = recommendedLookup,
        };
        task.SetName(name);
        var wrapped = list.WrapInstanceTask(task);
        try
        {
            await wrapped(TestContext.Current.CancellationToken);
            return (task, null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return (task, e.Message);
        }
    }

    // -------------------------------------------------------------------- index

    [Fact]
    public async Task PackIndex_ParsesPacksnew()
    {
        _server.PacksNewJson = new JsonArray(
            new JsonObject
            {
                ["id"] = 1,
                ["position"] = 10,
                ["name"] = "Public Pack",
                ["type"] = "public",
                ["system"] = false,
                ["description"] = "A public pack",
                ["versions"] = new JsonArray(
                    new JsonObject { ["version"] = "1.0.0", ["minecraft"] = "1.20.1" },
                    new JsonObject { ["version"] = "1.1.0", ["minecraft"] = "1.20.4" }),
            },
            new JsonObject
            {
                ["id"] = 2,
                ["position"] = 5,
                ["name"] = "Private Pack!",
                ["type"] = "private",
                ["system"] = true,
                ["description"] = "",
                ["versions"] = new JsonArray(),
            }).ToJsonString();

        var packs = await _api.Packs.Index().SendTo(TestNet.Http);

        Assert.Equal(2, packs.Count);
        Assert.Equal(1, packs[0].Id);
        Assert.Equal(10, packs[0].Position);
        Assert.Equal("Public Pack", packs[0].Name);
        Assert.Equal(PackType.Public, packs[0].Type);
        Assert.False(packs[0].System);
        Assert.Equal("A public pack", packs[0].Description);
        Assert.Equal(2, packs[0].Versions.Count);
        Assert.Equal("1.0.0", packs[0].Versions[0].Version);
        Assert.Equal("1.20.1", packs[0].Versions[0].Minecraft);
        Assert.Equal("publicpack.png", packs[0].SafeName);
        Assert.Equal(PackType.Private, packs[1].Type);
        Assert.True(packs[1].System);
        Assert.Equal("privatepack.png", packs[1].SafeName);
    }

    // ---------------------------------------------------------------- share codes

    [Fact]
    public async Task ShareCode_ParsesSelection()
    {
        _server.ShareCodes["goodcode"] = new JsonObject
        {
            ["error"] = false,
            ["code"] = 0,
            ["data"] = new JsonObject
            {
                ["pack"] = "Test Pack",
                ["version"] = "1.0.0",
                ["mods"] = new JsonObject
                {
                    ["optional"] = new JsonArray(
                        new JsonObject { ["selected"] = true, ["name"] = "Optifine" },
                        new JsonObject { ["selected"] = false, ["name"] = "JourneyMap" }),
                },
            },
        }.ToJsonString();

        var response = await _api.ShareCodes.ByCode("goodcode").SendTo(TestNet.Http);

        Assert.False(response.Error);
        var shareCode = response.Data!;
        Assert.Equal("Test Pack", shareCode.Pack);
        Assert.Equal("1.0.0", shareCode.Version);
        Assert.Equal(2, shareCode.Mods.Count);
        Assert.True(shareCode.Mods[0].Selected);
        Assert.Equal("Optifine", shareCode.Mods[0].Name);
        Assert.False(shareCode.Mods[1].Selected);
    }

    [Fact]
    public void ShareCode_ErrorResponse_ReportsMessage()
    {
        var parsed = AtLauncherJson.LoadShareCode(
            """{"error":true,"code":400,"message":"Unknown share code"}""", out var error);

        Assert.Null(parsed);
        Assert.Equal("Unknown share code", error);
    }

    [Fact]
    public void ShareCode_MessageMayBeNull()
    {
        // a null message must not throw (the C++ checks isNull before requireString)
        var parsed = AtLauncherJson.LoadShareCode(
            """{"error":true,"code":1,"message":null}""", out var error);

        Assert.Null(parsed);
        Assert.Null(error);
    }

    // ------------------------------------------------------------------ install

    [Fact]
    public async Task Install_EndToEnd_CreatesInstanceWithModLayout()
    {
        var fixture = AtlPackFixture.Create();
        var (_, failure) = await RunInstall(fixture);
        Assert.Null(failure);


        var staging = Path.Combine(_root, "instances", "ATL Imported");
        // the configs zip landed in minecraft/ (sha1-verified download)
        Assert.Equal(fixture.ConfigTxt, await File.ReadAllTextAsync(Path.Combine(staging, "minecraft", "config", "test.cfg")));
        // the required mod was downloaded from the stub (md5-verified) and copied into mods/
        Assert.Equal(fixture.RequiredModBytes,
            await File.ReadAllBytesAsync(Path.Combine(staging, "minecraft", "mods", "required.jar")));
        // the optional-mod selection was honored
        Assert.Equal(fixture.OptionalSelectedBytes,
            await File.ReadAllBytesAsync(Path.Combine(staging, "minecraft", "mods", "optsel.jar")));
        Assert.False(File.Exists(Path.Combine(staging, "minecraft", "mods", "optunsel.jar")));
        // the jar mod was copied into jarmods/ and installed as a component
        Assert.Equal(fixture.JarModBytes,
            await File.ReadAllBytesAsync(Path.Combine(staging, "minecraft", "jarmods", "legacy.jar")));
        var patches = Path.Combine(staging, "patches");
        Assert.True(Directory.Exists(patches));
        Assert.Contains(Directory.GetFiles(patches), f => Path.GetFileName(f).StartsWith("custom.jarmod."));

        // instance.json identity + the net.minecraft component from the pack
        var cfg = await File.ReadAllTextAsync(Path.Combine(staging, "instance.json"));
        Assert.Contains("ATL Imported", cfg);
        var components = Components(staging);
        Assert.Contains(components, c => c is { Uid: "net.minecraft", Version: "1.20.1" });
        // the jar-mod patch component is registered without a version
        Assert.Contains(components, c => c.Uid is not null && c.Uid.StartsWith("custom.jarmod."));

        // managed-pack registration
        Assert.Equal("atlauncher", GetManagedType(staging));
    }

    private static string? GetManagedType(string staging)
    {
        // managed-pack registration lives in instance.json ("managedPackType")
        var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(staging, "instance.json")));
        return json.RootElement.TryGetProperty("managed", out var managed) &&
               managed.TryGetProperty("type", out var type)
            ? type.GetString()
            : null;
    }

    [Fact]
    public async Task Install_FabricLoader_SetsFabricComponent()
    {
        var fixture = AtlPackFixture.Create("""{"type":"fabric","metadata":{"loader":"0.14.21","latest":false,"recommended":false}}""");
        var (_, failure) = await RunInstall(fixture, name: "ATL Fabric");
        Assert.Null(failure);

        var staging = Path.Combine(_root, "instances", "ATL Fabric");
        var components = Components(staging);
        Assert.Contains(components, c => c is { Uid: "net.minecraft", Version: "1.20.1" });
        Assert.Contains(components, c => c is { Uid: "net.fabricmc.fabric-loader", Version: "0.14.21" });
    }

    [Fact]
    public async Task Install_ForgeLoader_SetsForgeComponent()
    {
        var fixture = AtlPackFixture.Create("""{"type":"forge","metadata":{"version":"40.1.0","latest":false,"recommended":false}}""");
        var (_, failure) = await RunInstall(fixture, name: "ATL Forge");
        Assert.Null(failure);

        var staging = Path.Combine(_root, "instances", "ATL Forge");
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(staging, "mmc-pack.json")));
        Assert.Contains(doc.RootElement.GetProperty("components").EnumerateArray(),
            c => c.GetProperty("uid").GetString() == "net.minecraftforge"
                 && c.GetProperty("version").GetString() == "40.1.0");
    }

    [Fact]
    public async Task Install_NoConfigs_SkipsConfigDownload()
    {
        // a "noConfigs" pack goes straight to the mods (nothing requests the Configs.zip)
        var fixture = AtlPackFixture.Create();
        var configsObj = JsonNode.Parse(fixture.ConfigsJson)?.AsObject();
        Assert.NotNull(configsObj);
        configsObj["noConfigs"] = true;
        fixture.ConfigsJson = configsObj.ToJsonString();
        fixture.Register(_server);

        var list = CreateList();
        var task = new AtLauncherPackInstallTask(fixture.PackName, fixture.VersionName, api: _api, http: TestNet.Http);
        task.SetName("ATL NoConfigs");
        var wrapped = list.WrapInstanceTask(task);
        await wrapped(TestContext.Current.CancellationToken);

        var staging = Path.Combine(_root, "instances", "ATL NoConfigs");
        Assert.True(File.Exists(Path.Combine(staging, "minecraft", "mods", "required.jar")));
        Assert.False(File.Exists(Path.Combine(staging, "minecraft", "config", "test.cfg")));
    }

    [Fact]
    public async Task Install_ConfigsSha1Mismatch_Fails()
    {
        var fixture = AtlPackFixture.Create();
        var configsObj = ParseObject(fixture.ConfigsJson);
        ChildObject(configsObj, "configs")["sha1"] = new string('0', 40);
        fixture.ConfigsJson = configsObj.ToJsonString();
        fixture.Register(_server);

        var list = CreateList();
        var task = new AtLauncherPackInstallTask(fixture.PackName, fixture.VersionName, api: _api, http: TestNet.Http);
        task.SetName("ATL BadSha");
        var wrapped = list.WrapInstanceTask(task);
        await Assert.ThrowsAnyAsync<Exception>(() => wrapped(TestContext.Current.CancellationToken));

        // nothing was installed
        Assert.False(File.Exists(Path.Combine(_root, "instances", "ATL BadSha", "minecraft", "mods", "required.jar")));
    }

    [Fact]
    public async Task Install_ModMd5Mismatch_Fails()
    {
        var fixture = AtlPackFixture.Create();
        var configsObj = ParseObject(fixture.ConfigsJson);
        var mods = configsObj["mods"]?.AsArray();
        Assert.NotNull(mods);
        var firstMod = mods[0]?.AsObject();
        Assert.NotNull(firstMod);
        firstMod["md5"] = new string('0', 32);
        fixture.ConfigsJson = configsObj.ToJsonString();
        fixture.Register(_server);

        var list = CreateList();
        var task = new AtLauncherPackInstallTask(fixture.PackName, fixture.VersionName, api: _api, http: TestNet.Http);
        task.SetName("ATL BadMd5");
        var wrapped = list.WrapInstanceTask(task);
        await Assert.ThrowsAnyAsync<Exception>(() => wrapped(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Install_ThroughMetaCache_UsesCacheEntries()
    {
        var fixture = AtlPackFixture.Create();
        fixture.Register(_server);

        var cacheDir = Path.Combine(_root, "cache");
        var cache = new HttpMetaCache(new AbsolutePath(Path.Combine(_root, "metacache")));
        cache.AddBase("ATLauncherPacks", new AbsolutePath(cacheDir));

        var list = CreateList();
        var task = new AtLauncherPackInstallTask(fixture.PackName, fixture.VersionName, api: _api, http: TestNet.Http)
        {
            MetaCache = cache,
            OptionalModsSelection = new Dictionary<string, bool> { ["Optional Selected"] = true },
        };
        task.SetName("ATL Cached");
        var wrapped = list.WrapInstanceTask(task);
        await wrapped(TestContext.Current.CancellationToken);

        Assert.True(Directory.GetFiles(cacheDir, "*", SearchOption.AllDirectories).Any(),
            "the configs zip should have been cached");
        var staging = Path.Combine(_root, "instances", "ATL Cached");
        Assert.True(File.Exists(Path.Combine(staging, "minecraft", "mods", "required.jar")));
    }

    [Fact]
    public async Task Install_UnknownLoaderType_Fails()
    {
        var fixture = AtlPackFixture.Create("""{"type":"unknownloader","metadata":{"version":"1"}}""");
        var (_, failure) = await RunInstall(fixture, name: "ATL UnknownLoader");

        Assert.Contains("Unknown loader type", failure);
    }

    [Fact]
    public async Task Install_BadConfigsJson_FailsWithClearMessage()
    {
        var fixture = AtlPackFixture.Create();
        fixture.Register(_server);
        _server.ConfigsJson[$"{fixture.SafeName}/{fixture.VersionName}"] = """{"version":"1.0.0"}""";

        var list = CreateList();
        var task = new AtLauncherPackInstallTask(fixture.PackName, fixture.VersionName, api: _api, http: TestNet.Http);
        task.SetName("ATL BadJson");
        var wrapped = list.WrapInstanceTask(task);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => wrapped(TestContext.Current.CancellationToken));
        Assert.Contains("Could not understand pack manifest", ex.Message);
    }

    // ------------------------------------------------------- loader version hooks

    [Fact]
    public async Task Install_RecommendedLoader_WithoutLookupHook_Fails()
    {
        var fixture = AtlPackFixture.Create("""{"type":"forge","metadata":{"version":"","recommended":true,"latest":false}}""");
        var (_, failure) = await RunInstall(fixture, name: "ATL Recommended");

        Assert.Contains("Failed to get local metadata index for net.minecraftforge", failure);
    }

    [Fact]
    public async Task Install_RecommendedLoader_WithLookupHook_ResolvesVersion()
    {
        var fixture = AtlPackFixture.Create("""{"type":"forge","metadata":{"version":"","recommended":true,"latest":false}}""");
        // the lookup hook is per-task (DI): set through RunInstall's initializer below
        await RunInstall(fixture, name: "ATL Recommended2", recommendedLookup: (uid, loaderType, mcVersion) =>
        {
            Assert.Equal("net.minecraftforge", uid);
            Assert.Equal("forge", loaderType);
            Assert.Equal("1.20.1", mcVersion);
            return "40.2.0";
        });

        var staging = Path.Combine(_root, "instances", "ATL Recommended2");
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(staging, "mmc-pack.json")));
        Assert.Contains(doc.RootElement.GetProperty("components").EnumerateArray(),
            c => c.GetProperty("uid").GetString() == "net.minecraftforge"
                 && c.GetProperty("version").GetString() == "40.2.0");
    }

    [Fact]
    public async Task Install_ChooseLoader_UsesPresetChoice()
    {
        var fixture = AtlPackFixture.Create("""{"type":"fabric","choose":true,"metadata":{"loader":"","latest":false,"recommended":false}}""");
        var list = CreateList();
        var task = new AtLauncherPackInstallTask(fixture.PackName, fixture.VersionName, api: _api, http: TestNet.Http)
        {
            OptionalModsSelection = new Dictionary<string, bool>(),
            ChooseLoaderVersion = (uid, mcVersion) =>
            {
                Assert.Equal("net.fabricmc.fabric-loader", uid);
                Assert.Null(mcVersion); // fabric is not filtered by the minecraft version
                return "0.14.21";
            },
        };
        task.SetName("ATL Choose");
        fixture.Register(_server);
        var wrapped = list.WrapInstanceTask(task);
        await wrapped(TestContext.Current.CancellationToken);

        var staging = Path.Combine(_root, "instances", "ATL Choose");
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(staging, "mmc-pack.json")));
        Assert.Contains(doc.RootElement.GetProperty("components").EnumerateArray(),
            c => c.GetProperty("uid").GetString() == "net.fabricmc.fabric-loader"
                 && c.GetProperty("version").GetString() == "0.14.21");
    }

    // ------------------------------------------------------------- pack component

    [Fact]
    public async Task Install_MainClassDepends_WritesPackComponent()
    {
        // mainClass declared with a depends on a mod that IS in the pack -> the pack
        // component (patches/org.multimc.atlauncher.<uuid>.json) carries the main class
        var fixture = AtlPackFixture.Create();
        var configsObj = ParseObject(fixture.ConfigsJson);
        configsObj["mainClass"] = new JsonObject { ["mainClass"] = "net.minecraft.launchwrapper.Launch", ["depends"] = "Required Mod" };
        fixture.ConfigsJson = configsObj.ToJsonString();
        fixture.Register(_server);

        var list = CreateList();
        var task = new AtLauncherPackInstallTask(fixture.PackName, fixture.VersionName, api: _api, http: TestNet.Http);
        task.SetName("ATL MainClass");
        var wrapped = list.WrapInstanceTask(task);
        await wrapped(TestContext.Current.CancellationToken);

        var staging = Path.Combine(_root, "instances", "ATL MainClass");
        var patch = Directory.GetFiles(Path.Combine(staging, "patches"))
            .First(f => Path.GetFileName(f).StartsWith("org.multimc.atlauncher."));
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(patch));
        Assert.Equal("net.minecraft.launchwrapper.Launch", doc.RootElement.GetProperty("mainClass").GetString());
        Assert.Equal("Test Pack 1.0.0", doc.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Install_MainClassDependsMissingMod_DropsMainClass()
    {
        // depends names a mod that is not in the pack -> no pack component at all
        var fixture = AtlPackFixture.Create();
        var configsObj = ParseObject(fixture.ConfigsJson);
        configsObj["mainClass"] = new JsonObject { ["mainClass"] = "some.Main", ["depends"] = "Not In Pack" };
        fixture.ConfigsJson = configsObj.ToJsonString();
        fixture.Register(_server);

        var list = CreateList();
        var task = new AtLauncherPackInstallTask(fixture.PackName, fixture.VersionName, api: _api, http: TestNet.Http);
        task.SetName("ATL DepMissing");
        var wrapped = list.WrapInstanceTask(task);
        await wrapped(TestContext.Current.CancellationToken);

        var staging = Path.Combine(_root, "instances", "ATL DepMissing");
        Assert.DoesNotContain(Directory.GetFiles(Path.Combine(staging, "patches")),
            f => Path.GetFileName(f).StartsWith("org.multimc.atlauncher."));
    }

    // --------------------------------------------------------- display message

    [Fact]
    public async Task Install_InstallMessage_ReachesDisplayHook()
    {
        var fixture = AtlPackFixture.Create();
        var configsObj = ParseObject(fixture.ConfigsJson);
        configsObj["messages"] = new JsonObject { ["install"] = "Welcome!", ["update"] = "Updating!" };
        fixture.ConfigsJson = configsObj.ToJsonString();
        fixture.Register(_server);

        string? displayed = null;
        var list = CreateList();
        var task = new AtLauncherPackInstallTask(fixture.PackName, fixture.VersionName, api: _api, http: TestNet.Http)
        {
            DisplayMessage = message => displayed = message,
        };
        task.SetName("ATL Message");
        var wrapped = list.WrapInstanceTask(task);
        await wrapped(TestContext.Current.CancellationToken);

        Assert.Equal("Welcome!", displayed);
    }
}
