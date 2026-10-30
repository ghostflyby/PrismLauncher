// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
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
using System.Text.Json.Nodes;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Net;
using TruePath;
using Xunit;
using Index = FlintLauncher.Core.Meta.Index;

namespace FlintLauncher.Tests;

public class ComponentUpdateTaskTests : IDisposable
{
    private readonly string _root;
    private readonly HttpMetaCache _metaCache;

    public ComponentUpdateTaskTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "flint-resolve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        // the stamped fixture cache (fixture injection): "meta" base inside the test root,
        // replacing the former CWD-anchored static fallback; every profile gets it via
        // SetMetadataCache so metadata entities resolve files under <_root>/meta
        _metaCache = new HttpMetaCache(
            new AbsolutePath(Path.Combine(_root, "metacache.json")));
        _metaCache.AddBase("meta", new AbsolutePath(Path.Combine(_root, "meta")));
    }

    public void Dispose()
    {
        _metaCache.Dispose();
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException) { }
    }

    /// <summary>A PackProfile over the fixture root, wired to the fixture's stamped cache
    /// (the SetMetadataCache call the composition root performs through the instance).</summary>
    private PackProfile NewProfile()
    {
        var profile = new PackProfile(new AbsolutePath(_root));
        profile.SetMetadataCache(_metaCache);
        return profile;
    }

    private void WriteInstanceFile(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        var dir = Path.GetDirectoryName(path);
        Assert.NotNull(dir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, content);
    }

    private static string VersionFileJson(string uid, string version, string name, string? mainClass = null,
        (string uid, string equals)? requires = null, bool volatileValue = false)
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

        if (requires is { } req)
        {
            obj["requires"] = new JsonArray
            {
                new JsonObject { ["uid"] = req.uid, ["equals"] = req.equals },
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
        var pack = new JsonObject { ["formatVersion"] = 1 };
        var array = new JsonArray();
        foreach (var (uid, version, important) in components)
        {
            var entry = new JsonObject { ["uid"] = uid };
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
    /// Seeds a metadata version into the fixture's local meta cache (the stamped "meta" base
    /// under the test root) exactly like a previous online download would have, and marks the
    /// entity loaded in <paramref name="index"/> the way the online sha256 verification would.
    /// The test's Dispose owns the created folder.
    /// </summary>
    private async Task SeedLoadedMetaVersion(Index index, string uid, string version,
        string json)
    {
        var metaDir = MetaDirFor(uid);
        var metaFile = Path.Combine(metaDir, version + ".json");
        Directory.CreateDirectory(metaDir);
        await File.WriteAllTextAsync(metaFile, json);
        var metaVersion = index.Get(uid, version);
        metaVersion.Sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(metaFile))).ToLowerInvariant();
        await metaVersion.LoadAsync(NetMode.Offline);
        Assert.True(metaVersion.IsLoaded, "seeded meta version should be loaded");
    }

    /// <summary>The fixture meta cache folder for one uid (the stamped "meta" base).</summary>
    private string MetaDirFor(string uid) => Path.Combine(_root, "meta", uid);

    /// <summary>Meta-format version record (uid/version/releaseTime required by the meta parser).</summary>
    private static string MetaVersionJson(string uid, string version, string name, string? mainClass = null)
    {
        var obj = new JsonObject
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

        using var profile = NewProfile();
        var result = await profile.ReloadAsync(NetMode.Offline);
        Assert.True(result.Ok, result.ErrorMessage);

        var launchProfile = profile.GetProfile();
        Assert.NotNull(launchProfile);
        Assert.Equal("1.12.2", launchProfile.GetMinecraftVersion());
        Assert.Equal("net.minecraft.client.main.Main", launchProfile.GetMainClass());

        // components are loaded from their local patches and cached data updated
        var lwjgl = profile.GetComponent("org.lwjgl3");
        Assert.NotNull(lwjgl);
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

        using var profile = NewProfile();
        Assert.True(profile.Load().Ok);

        var task = new ComponentUpdateTask(ComponentUpdateTask.Mode.Resolution, NetMode.Offline, profile);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            task.RunAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Some component metadata load tasks failed.", failure.Message);

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

        using var profile = NewProfile();
        // the component file itself loads; the checkOnly dependency pass fails afterwards, and
        // the failure surfaces because ReloadAsync completes when the resolution does
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            profile.ReloadAsync(NetMode.Offline));
        Assert.Contains("unresolved dependencies", failure.Message);

        var component = profile.GetComponent("test.modloader");
        Assert.NotNull(component);
        var problems = component.GetProblems();
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

        using var profile = NewProfile();
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
            Assert.Equal("test.deplib", profile.Components[0].GetId());
            Assert.True(profile.Components[0].DependencyOnly);
            Assert.Equal("1.0", profile.Components[0].GetVersion());
            Assert.Equal("test.modloader", profile.Components[1].GetId());

            // both components ended up fully loaded, no problems reported
            Assert.Empty(profile.Components[0].GetProblems());
            Assert.Empty(profile.Components[1].GetProblems());

            // the injected component is persisted as dependencyOnly on the next save
            profile.SaveNow();
            var pack = JsonNode.Parse(
                await File.ReadAllTextAsync(Path.Combine(_root, "mmc-pack.json")));
            Assert.NotNull(pack);
            var components = pack["components"]?.AsArray();
            Assert.NotNull(components);
            var depEntry = components.OfType<JsonObject>()
                .First(o => o["uid"]?.ToString() == "test.deplib");
            Assert.True(depEntry["dependencyOnly"]?.GetValue<bool>());
            Assert.Equal("1.0", depEntry["version"]?.ToString());
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

        using var profile = NewProfile();
        var result = await profile.ReloadAsync(NetMode.Offline);
        Assert.True(result.Ok, result.ErrorMessage);

        Assert.DoesNotContain(profile.Components, c => c.GetId() == "test.dep");
        Assert.Null(profile.GetComponent("test.dep"));
        Assert.True(profile.GetProfile() is not null);

        // removal also deletes the patch file and persists the new list
        Assert.False(File.Exists(depPatchPath));
        profile.SaveNow();
        var pack = JsonNode.Parse(
            await File.ReadAllTextAsync(Path.Combine(_root, "mmc-pack.json")));
        Assert.NotNull(pack);
        var components = pack["components"]?.AsArray();
        Assert.NotNull(components);
        Assert.Single(components);
    }

    // ---------------------------------------------------------------- update actions

    [Fact]
    public async Task UpdateActionRemoveIsApplied()
    {
        using var profile = NewProfile();
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

        using var profile = NewProfile();
        var result = await profile.ReloadAsync(NetMode.Offline);
        Assert.True(result.Ok, result.ErrorMessage);
        var component = profile.GetComponent("test.a");
        Assert.NotNull(component);
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
