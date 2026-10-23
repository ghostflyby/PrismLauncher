// SPDX-License-Identifier: GPL-3.0-only
// Tests for GetModDependenciesTask (the recursive dependency resolver of the install/update
// flow): a two-level Modrinth chain (A requires B requires C, with C already installed ->
// only B is resolved), the optional-dependency skip, the version-pinned (Modrinth
// version-only) dependency that resolves its project through a second lookup, and the
// Flame file-id based variant.

using System.Text.Json.Nodes;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Minecraft.Resources;
using FlintLauncher.Core.ModPlatform;
using FlintLauncher.Core.ModPlatform.Dependencies;
using FlintLauncher.Core.ModPlatform.Flame;
using FlintLauncher.Core.ModPlatform.Modrinth;
using TruePath;
using Xunit;
using PackwizModel = FlintLauncher.Core.ModPlatform.Packwiz.Packwiz;

namespace FlintLauncher.Tests;

[Collection("ModrinthApi")]
public class GetModDependenciesTaskTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"flint-deps-{Guid.NewGuid():N}");
    private readonly StubModrinthServer _server = new();
    private readonly ModInstallTestEnv _env;

    private readonly ModrinthApi _modrinth = new();

    public GetModDependenciesTaskTests()
    {
        Directory.CreateDirectory(_root);
        _modrinth.BaseUrl = _server.Url;
        _env = new ModInstallTestEnv(_root);
    }

    public void Dispose()
    {
        _server.Dispose();
        _env.Dispose();
    }

    private string ModsDir => Path.Combine(_root, "mods");

    private string IndexDir => Path.Combine(ModsDir, ".index");

    /// <summary>An instance with mc 1.20.1 + fabric, a mods folder with mod C installed.</summary>
    private (MinecraftInstance Instance, ModFolderModel Model) SetupInstance(bool installC)
    {
        if (installC)
        {
            Directory.CreateDirectory(IndexDir);
            File.WriteAllBytes(Path.Combine(ModsDir, "c.jar"), "c jar bytes"u8.ToArray());
            PackwizModel.UpdateModIndex(new AbsolutePath(IndexDir), new PackwizModel.Mod
            {
                Slug = "c-mod",
                Name = "C Mod",
                Filename = "c.jar",
                Provider = ResourceProvider.Modrinth,
                ProjectId = "projC",
                FileId = "verC1",
                VersionNumber = "3.0.0",
                Mode = "url",
            });
        }

        var model = new ModFolderModel(MaterializedDirectory.Ensure(new AbsolutePath(ModsDir))!, isIndexed: true);
        model.Update();

        var instance = _env.CreateInstance("1.20.1", ModLoaderType.Fabric, "0.14.21");
        return (instance, model);
    }

    private static string VersionJson(string projectId, string versionId, string fileName, string downloadName,
        string versionNumber, string baseUrl, string[][]? dependencies = null, string date = "2024-01-01T00:00:00Z")
    {
        var deps = new JsonArray();
        foreach (var d in dependencies ?? [])
        {
            deps.Add((JsonNode?)new JsonObject
            {
                ["project_id"] = d[0],
                ["version_id"] = d[1],
                ["dependency_type"] = d[2],
            });
        }

        return new JsonObject
        {
            ["id"] = versionId,
            ["project_id"] = projectId,
            ["date_published"] = date,
            ["game_versions"] = new JsonArray("1.20.1"),
            ["loaders"] = new JsonArray("fabric"),
            ["name"] = $"{versionNumber} release",
            ["version_number"] = versionNumber,
            ["version_type"] = "release",
            ["files"] = new JsonArray(
                new JsonObject
                {
                    ["url"] = $"{baseUrl}/{downloadName}",
                    ["filename"] = fileName,
                    ["primary"] = true,
                    ["size"] = 42,
                    ["hashes"] = new JsonObject
                    {
                        ["sha1"] = "deadbeef",
                        ["sha512"] = "cafebabecafebabecafebabecafebabecafebabecafebabecafebabecafebabe",
                    },
                }),
            ["dependencies"] = deps,
        }.ToJsonString();
    }

    private static string ProjectJson(string projectId, string slug, string title)
    {
        return new JsonObject
        {
            ["id"] = projectId,
            ["slug"] = slug,
            ["title"] = title,
            ["description"] = "A synthetic mod",
            ["project_type"] = "mod",
            ["client_side"] = "required",
            ["server_side"] = "required",
        }.ToJsonString();
    }

    [Fact]
    public async Task TwoLevelChain_InstalledDependencySkipped()
    {
        var (instance, model) = SetupInstance(installC: true);

        // A (selected) requires B (required) and D (optional); B requires C (required, installed)
        _server.ProjectVersions["projB"] = new JsonArray(
            JsonNode.Parse(VersionJson("projB", "verB1", "b.jar", "b.bin", "2.0.0", _server.Url,
                dependencies:
                [
                    ["projC", "verC1", "required"],
                ]))).ToJsonString();
        _server.Projects["projB"] = ProjectJson("projB", "b-mod", "B Mod");
        _server.Projects["projD"] = ProjectJson("projD", "d-mod", "D Mod");

        var packA = new IndexedPack { AddonId = "projA", Slug = "a-mod", Name = "A Mod", Provider = ResourceProvider.Modrinth };
        var verA = new IndexedVersion
        {
            AddonId = "projA",
            FileId = "verA1",
            VersionNumber = "1.0.0",
            FileName = "a.jar",
            Loaders = ModLoaderType.Fabric,
            Dependencies =
            [
                new Dependency { AddonId = "projB", Type = new DependencyType(DependencyTypeValue.Required) },
                new Dependency { AddonId = "projD", Type = new DependencyType(DependencyTypeValue.Optional) },
            ],
        };

        var task = new GetModDependenciesTask(instance, model,
            [new GetModDependenciesTask.PackDependency(packA, verA)], _modrinth);
        await task.StartAsync();

        Assert.True(task.WasSuccessful, task.FailReason);

        // only B was resolved: A was selected (not a dependency), C is installed locally,
        // D is optional
        var dep = Assert.Single(task.GetDependencies());
        Assert.Equal("projB", dep.Pack.AddonId);
        // the project info task filled the pack's display data
        Assert.Equal("B Mod", dep.Pack.Name);
        Assert.Equal("b-mod", dep.Pack.Slug);
        Assert.Equal("verB1", dep.Version.FileId);
        Assert.Equal("2.0.0", dep.Version.VersionNumber);
        Assert.True(dep.Version.IsCurrentlySelected);
        Assert.True(dep.Pack.VersionsLoaded);
        Assert.Equal(ResourceProvider.Modrinth, dep.Pack.Provider);
        Assert.NotNull(dep.Dependency);
        Assert.Equal(DependencyTypeValue.Required, dep.Dependency.Type.Value);

        // the walk consulted B's version list but never touched C's (installed) or D's (optional)
        Assert.Contains(_server.Requests, r => r.Contains("/project/projB/version"));
        Assert.DoesNotContain(_server.Requests, r => r.Contains("/project/projC"));
        Assert.DoesNotContain(_server.Requests, r => r.Contains("/project/projD"));
    }

    [Fact]
    public async Task VersionPinnedDependency_ResolvesProjectAfterwards()
    {
        var (instance, model) = SetupInstance(installC: false);

        // A requires a pinned Modrinth version (no project id)
        _server.VersionsById["verX1"] = VersionJson("projX", "verX1", "x.jar", "x.bin", "7.5.0", _server.Url);
        _server.Projects["projX"] = ProjectJson("projX", "x-mod", "X Mod");

        var packA = new IndexedPack { AddonId = "projA", Slug = "a-mod", Name = "A Mod", Provider = ResourceProvider.Modrinth };
        var verA = new IndexedVersion
        {
            AddonId = "projA",
            FileId = "verA1",
            FileName = "a.jar",
            Loaders = ModLoaderType.Fabric,
            Dependencies =
            [
                new Dependency { AddonId = "", Version = "verX1", Type = new DependencyType(DependencyTypeValue.Required) },
            ],
        };

        var task = new GetModDependenciesTask(instance, model,
            [new GetModDependenciesTask.PackDependency(packA, verA)], _modrinth);
        await task.StartAsync();

        Assert.True(task.WasSuccessful, task.FailReason);

        var dep = Assert.Single(task.GetDependencies());
        // the pack adopted the resolved version's project id and fetched its info
        Assert.Equal("projX", dep.Pack.AddonId);
        Assert.Equal("X Mod", dep.Pack.Name);
        Assert.Equal("verX1", dep.Version.FileId);
        Assert.Contains(_server.Requests, r => r.Contains("/version/verX1"));
        Assert.Contains(_server.Requests, r => r.Contains("/project/projX"));
    }

    [Fact]
    public async Task LoaderFilteredVersionLookup_UsesInstanceFilters()
    {
        var (instance, model) = SetupInstance(installC: false);

        _server.ProjectVersions["projB"] = new JsonArray(
            JsonNode.Parse(VersionJson("projB", "verB1", "b.jar", "b.bin", "2.0.0", _server.Url))).ToJsonString();
        _server.Projects["projB"] = ProjectJson("projB", "b-mod", "B Mod");

        var packA = new IndexedPack { AddonId = "projA", Slug = "a-mod", Name = "A Mod", Provider = ResourceProvider.Modrinth };
        var verA = new IndexedVersion
        {
            AddonId = "projA",
            FileId = "verA1",
            FileName = "a.jar",
            Loaders = ModLoaderType.Fabric,
            Dependencies =
            [
                new Dependency { AddonId = "projB", Type = new DependencyType(DependencyTypeValue.Required) },
            ],
        };

        var task = new GetModDependenciesTask(instance, model,
            [new GetModDependenciesTask.PackDependency(packA, verA)], _modrinth);
        await task.StartAsync();

        Assert.True(task.WasSuccessful, task.FailReason);
        Assert.Single(task.GetDependencies());

        // the dependency search carried the instance's game version + loader
        var versionReq = Assert.Single(_server.Requests, r => r.Contains("/project/projB/version"));
        Assert.Contains("game_versions=", versionReq);
        Assert.Contains("loaders=", versionReq);
    }
}

[Collection("FlameApi")]
public class GetModDependenciesFlameTests : IDisposable
{
    private readonly FlameApi _flameApi = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"flint-deps-flame-{Guid.NewGuid():N}");
    private readonly StubFlameServer _server = new();
    private readonly ModInstallTestEnv _env;

    public GetModDependenciesFlameTests()
    {
        Directory.CreateDirectory(_root);
        _flameApi.BaseUrl = _server.Url;
        _env = new ModInstallTestEnv(_root);
    }

    public void Dispose()
    {
        _server.Dispose();
        _env.Dispose();
    }

    [Fact]
    public async Task FlameDependency_ResolvedThroughFileIds()
    {
        var modsDir = Path.Combine(_root, "mods");
        var model = new ModFolderModel(MaterializedDirectory.Ensure(new AbsolutePath(modsDir))!, isIndexed: true);
        model.Update();
        var instance = _env.CreateInstance("1.18.2", ModLoaderType.Forge, "40.1.0");

        // the dependency's project info + version list (file-id based)
        _server.ProjectJson = new JsonObject
        {
            ["data"] = JsonNode.Parse(FlamePayloads.ProjectJson(id: 789, name: "B Mod", slug: "b-mod")),
        }.ToJsonString();
        _server.FilesJson = new JsonObject
        {
            ["data"] = new JsonArray(JsonNode.Parse(FlamePayloads.FileJson(_server.Url, modId: 789, fileId: 999,
                displayName: "B Mod 1.0", fileName: "b.jar", fileDate: "2024-02-01T00:00:00Z"))),
        }.ToJsonString();

        var packA = new IndexedPack { AddonId = "123", Slug = "a-mod", Name = "A Mod", Provider = ResourceProvider.Flame };
        var verA = new IndexedVersion
        {
            AddonId = "123",
            FileId = "456",
            FileName = "a.jar",
            Loaders = ModLoaderType.Forge,
            Dependencies =
            [
                new Dependency { AddonId = "789", Type = new DependencyType(DependencyTypeValue.Required) },
            ],
        };

        var task = new GetModDependenciesTask(instance, model,
            [new GetModDependenciesTask.PackDependency(packA, verA)], flame: _flameApi);
        await task.StartAsync();

        Assert.True(task.WasSuccessful, task.FailReason);

        var dep = Assert.Single(task.GetDependencies());
        Assert.Equal("789", dep.Pack.AddonId);
        Assert.Equal("B Mod", dep.Pack.Name);
        Assert.Equal("999", dep.Version.FileId);
        Assert.Equal("b.jar", dep.Version.FileName);
        Assert.Equal(ResourceProvider.Flame, dep.Pack.Provider);

        // the Flame dependency lookup went through the file-list endpoint
        Assert.Contains(_server.Requests, r => r.Contains("/mods/789/files"));
        Assert.Contains(_server.Requests, r => r.EndsWith("/mods/789"));
    }
}
