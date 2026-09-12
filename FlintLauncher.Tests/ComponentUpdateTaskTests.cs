// SPDX-License-Identifier: GPL-3.0-only
// New tests locking the resolution algorithm ported from
// launcher/minecraft/ComponentUpdateTask.{h,cpp}:
//   - offline Launch/Resolution with locally loaded components builds the LaunchProfile in
//     mmc-pack.json order;
//   - Launch/offline checkOnly passes fail with unresolved dependencies (and record problems);
//   - Resolution mode (online) inserts a missing dependency before its first dependee, using
//     the local meta cache (no network: the metadata is seeded into Flint's meta cache folder,
//     which is what an online download would have produced);
//   - obsolete volatile dependency-only components are removed;
//   - unavailable metadata fails the task.
//
// Not covered: UpdateActionLatestRecommendedCompatible / UpdateActionImportantChanged (they
// need a populated remote version list) and the hardcoded org.lwjgl/org.lwjgl3 version
// decision hacks (upstream placeholders exercised only through real metadata).
using System.Security.Cryptography;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Net;
using Xunit;

namespace FlintLauncher.Tests;

public class ComponentUpdateTaskTests : IDisposable
{
    private readonly string _root;

    public ComponentUpdateTaskTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "flint-resolve-" + Guid.NewGuid().ToString("N"));
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

    private string WriteInstanceFile(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static string VersionFileJson(string uid, string version, string name, string? mainClass = null,
                                          (string uid, string equals)? requires = null, bool volatileValue = false)
    {
        var obj = new System.Text.Json.Nodes.JsonObject
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
        if (requires is { } req)
        {
            obj["requires"] = new System.Text.Json.Nodes.JsonArray
            {
                new System.Text.Json.Nodes.JsonObject { ["uid"] = req.uid, ["equals"] = req.equals },
            };
        }
        if (volatileValue)
        {
            obj["volatile"] = true;
        }
        return obj.ToJsonString();
    }

    private void WritePack(params (string uid, string version, bool important)[] components)
    {
        var pack = new System.Text.Json.Nodes.JsonObject { ["formatVersion"] = 1 };
        var array = new System.Text.Json.Nodes.JsonArray();
        foreach (var (uid, version, important) in components)
        {
            var entry = new System.Text.Json.Nodes.JsonObject { ["uid"] = uid };
            if (version.Length != 0)
            {
                entry["version"] = version;
            }
            if (important)
            {
                entry["important"] = true;
            }
            array.Add(entry);
        }
        pack["components"] = array;
        WriteInstanceFile("mmc-pack.json", pack.ToJsonString());
    }

    private void WritePatch(string uid, string json) => WriteInstanceFile($"patches/{uid}.json", json);

    /// <summary>
    /// Seeds a metadata version into the shared local meta cache (the CWD-relative "meta"
    /// folder) exactly like a previous online download would have, and marks the entity
    /// loaded in <paramref name="index"/> the way the online sha256 verification would. The
    /// caller owns the created folder and must delete it once the resolution finished.
    /// </summary>
    private static async Task SeedLoadedMetaVersion(FlintLauncher.Core.Meta.Index index, string uid, string version,
                                                    string json)
    {
        var metaDir = Path.Combine(Directory.GetCurrentDirectory(), "meta", uid);
        var metaFile = Path.Combine(metaDir, version + ".json");
        Directory.CreateDirectory(metaDir);
        await File.WriteAllTextAsync(metaFile, json);
        var metaVersion = index.Get(uid, version);
        metaVersion.SetSha256(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(metaFile))).ToLowerInvariant());
        await metaVersion.LoadTask(NetMode.Offline).StartAsync();
        Assert.True(metaVersion.IsLoaded(), "seeded meta version should be loaded");
    }

    /// <summary>The CWD-relative meta cache folder for one uid (shared static base of the Meta layer).</summary>
    private static string MetaDirFor(string uid) => Path.Combine(Directory.GetCurrentDirectory(), "meta", uid);

    /// <summary>Meta-format version record (uid/version/releaseTime required by the meta parser).</summary>
    private static string MetaVersionJson(string uid, string version, string name, string? mainClass = null)
    {
        var obj = new System.Text.Json.Nodes.JsonObject
        {
            ["formatVersion"] = 1,
            ["name"] = name,
            ["uid"] = uid,
            ["version"] = version,
            ["releaseTime"] = "2022-01-01T00:00:00",
            ["type"] = "release",
        };
        if (mainClass is not null)
        {
            obj["mainClass"] = mainClass;
        }
        return obj.ToJsonString();
    }

    // ---------------------------------------------------------------- offline launch

    [Fact]
    public async Task OfflineLaunchBuildsProfileFromLocalPatches()
    {
        WritePack(("net.minecraft", "1.12.2", true), ("org.lwjgl3", "", false));
        WritePatch("net.minecraft", VersionFileJson("net.minecraft", "1.12.2", "Minecraft",
            mainClass: "net.minecraft.client.main.Main"));
        WritePatch("org.lwjgl3", VersionFileJson("org.lwjgl3", "3.1.2", "LWJGL 3"));

        using var profile = new PackProfile(_root);
        var result = await profile.ReloadAsync(NetMode.Offline);
        Assert.True(result.Ok, result.ErrorMessage);
        Assert.Null(profile.GetCurrentTask()); // updateSucceeded() released it

        var launchProfile = profile.GetProfile();
        Assert.NotNull(launchProfile);
        Assert.Equal("1.12.2", launchProfile.GetMinecraftVersion());
        Assert.Equal("net.minecraft.client.main.Main", launchProfile.GetMainClass());

        // components are loaded from their local patches and cached data updated
        var lwjgl = profile.GetComponent("org.lwjgl3")!;
        Assert.True(lwjgl.Loaded);
        Assert.Equal("3.1.2", lwjgl.GetVersion());
        Assert.Equal("LWJGL 3", lwjgl.GetName());
        Assert.Empty(lwjgl.GetProblems());
    }

    [Fact]
    public async Task OfflineFailsWhenMetadataUnavailable()
    {
        // A meta file that was never verified against a sha256 (the version lists hand those
        // out on online refreshes) counts as unloaded, so the offline component load task
        // fails. No network is involved: the local meta file parses, but the entity does not
        // reach the loaded state.
        WritePack(("net.minecraft", "1.12.2", true));
        Directory.CreateDirectory(MetaDirFor("net.minecraft"));
        await File.WriteAllTextAsync(Path.Combine(MetaDirFor("net.minecraft"), "1.12.2.json"),
            MetaVersionJson("net.minecraft", "1.12.2", "Minecraft"));

        using var profile = new PackProfile(_root);
        Assert.True(profile.Load().Ok);

        var task = new ComponentUpdateTask(ComponentUpdateTask.Mode.Resolution, NetMode.Offline, profile);
        await task.StartAsync();

        Assert.Equal(FlintLauncher.Core.Tasks.TaskState.Failed, task.State);
        Assert.Equal("Some component metadata load tasks failed.", task.FailReason);

        // cleanup the shared meta cache folder (the profile does not own it)
        Directory.Delete(MetaDirFor("net.minecraft"), true);
    }

    [Fact]
    public async Task LaunchModeFailsOnUnresolvedDependencyAndRecordsProblem()
    {
        // the patch requires a net.minecraft version that is not in the profile
        WritePack(("test.modloader", "1.0.0", false));
        WritePatch("test.modloader", VersionFileJson("test.modloader", "1.0.0", "Test Mod Loader",
            requires: ("net.minecraft", "1.12.2")));

        using var profile = new PackProfile(_root);
        var result = await profile.ReloadAsync(NetMode.Offline);
        // the reload itself succeeded (the component file was read); the checkOnly dependency
        // pass failed afterwards (like upstream, reload() does not report the task failure)
        Assert.True(result.Ok, result.ErrorMessage);
        Assert.Null(profile.GetCurrentTask()); // updateFailed() released it

        var problems = profile.GetComponent("test.modloader")!.GetProblems();
        Assert.Contains(problems, p =>
            p.Severity == ProblemSeverity.Error &&
            p.Description.Contains("is missing requirement net.minecraft 1.12.2"));
    }

    // ---------------------------------------------------------------- dependency resolution

    [Fact]
    public async Task ResolutionModeAddsMissingDependencyFromMeta()
    {
        // "test.modloader" (local patch) requires "test.deplib" == "1.0"; the dependency is not
        // installed, so resolution mode must inject it before the dependee using metadata.
        WritePack(("test.modloader", "1.0.0", false));
        WritePatch("test.modloader", VersionFileJson("test.modloader", "1.0.0", "Test Mod Loader",
            requires: ("test.deplib", "1.0")));

        using var profile = new PackProfile(_root);
        try
        {
            // metadata cache as a previous online download would have left it, marked loaded
            // in this profile's metadata index
            await SeedLoadedMetaVersion(profile.MetadataIndex, "test.deplib", "1.0",
                MetaVersionJson("test.deplib", "1.0", "Test Dependency Library", mainClass: "test.dep.Main"));

            var result = await profile.ReloadAsync(NetMode.Online);
            Assert.True(result.Ok, result.ErrorMessage);
            Assert.NotNull(profile.GetProfile());

            // the injected dependency sits before its first dependee and is dependency-only
            Assert.Equal("test.deplib", profile.Components[0].GetID());
            Assert.True(profile.Components[0].DependencyOnly);
            Assert.Equal("1.0", profile.Components[0].GetVersion());
            Assert.Equal("test.modloader", profile.Components[1].GetID());

            // both components ended up fully loaded, no problems reported
            Assert.Empty(profile.Components[0].GetProblems());
            Assert.Empty(profile.Components[1].GetProblems());

            // the injected component is persisted as dependencyOnly on the next save
            profile.SaveNow();
            var pack = System.Text.Json.Nodes.JsonNode.Parse(
                File.ReadAllText(Path.Combine(_root, "mmc-pack.json")))!;
            var depEntry = pack["components"]!.AsArray()
                .OfType<System.Text.Json.Nodes.JsonObject>()
                .First(o => o["uid"]!.ToString() == "test.deplib");
            Assert.True(depEntry["dependencyOnly"]!.GetValue<bool>());
            Assert.Equal("1.0", depEntry["version"]!.ToString());
        }
        finally
        {
            Directory.Delete(MetaDirFor("test.deplib"), true);
        }
    }

    [Fact]
    public async Task ObsoleteVolatileDependencyIsRemoved()
    {
        // "test.dep" is a dependency-only volatile component that nothing requires anymore
        WriteInstanceFile("mmc-pack.json",
            """{"formatVersion":1,"components":[{"uid":"test.main","version":"1.0","important":true},{"uid":"test.dep","dependencyOnly":true}]}""");
        WritePatch("test.main", VersionFileJson("test.main", "1.0", "Test Main",
            mainClass: "test.main.Main"));
        WritePatch("test.dep", VersionFileJson("test.dep", "1.0", "Test Dependency",
            volatileValue: true));

        var depPatchPath = Path.Combine(_root, "patches", "test.dep.json");
        Assert.True(File.Exists(depPatchPath));

        using var profile = new PackProfile(_root);
        var result = await profile.ReloadAsync(NetMode.Offline);
        Assert.True(result.Ok, result.ErrorMessage);

        Assert.DoesNotContain(profile.Components, c => c.GetID() == "test.dep");
        Assert.Null(profile.GetComponent("test.dep"));
        Assert.True(profile.GetProfile() is not null);

        // removal also deletes the patch file and persists the new list
        Assert.False(File.Exists(depPatchPath));
        profile.SaveNow();
        var pack = System.Text.Json.Nodes.JsonNode.Parse(
            File.ReadAllText(Path.Combine(_root, "mmc-pack.json")))!;
        Assert.Single(pack["components"]!.AsArray());
    }

    // ---------------------------------------------------------------- update actions

    [Fact]
    public async Task UpdateActionRemoveIsApplied()
    {
        using var profile = new PackProfile(_root);
        profile.BuildingFromScratch();
        var file = new VersionFile { Name = "Test A", Uid = "test.a", Version = "1" };
        var component = new Component(profile, "test.a", file);
        profile.AppendComponent(component);
        component.SetUpdateAction(new UpdateAction.Remove());

        await profile.Resolve(NetMode.Offline);

        Assert.Null(profile.GetComponent("test.a"));
        Assert.Empty(profile.Components);
    }

    [Fact]
    public async Task UpdateActionChangeVersionIsApplied()
    {
        // the component comes as a local patch; the update action moves it onto the new
        // explicit version through setVersion + waitLoadMeta
        WriteInstanceFile("patches/test.a.json", VersionFileJson("test.a", "1.0", "Test A"));
        WritePack(("test.a", "1.0", false));

        using var profile = new PackProfile(_root);
        var result = await profile.ReloadAsync(NetMode.Offline);
        Assert.True(result.Ok, result.ErrorMessage);
        var component = profile.GetComponent("test.a")!;
        Assert.Equal("1.0", component.GetVersion());

        component.SetUpdateAction(new UpdateAction.ChangeVersion("2.0"));
        await profile.Resolve(NetMode.Online);

        // NOTE upstream: for a file-backed (custom) component setVersion() records the new
        // explicit version but leaves the cached display version alone
        Assert.Equal("2.0", component.Version);
        Assert.Equal("1.0", component.GetVersion());
        Assert.True(component.Loaded);
        Assert.NotNull(profile.GetProfile());
    }
}
