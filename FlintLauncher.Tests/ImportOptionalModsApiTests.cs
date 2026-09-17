// SPDX-License-Identifier: GPL-3.0-only
// API-level tests for the import optional-mods input (fix-2d): POST /instances/import gains
// `optionalMods` ({manifest path: selected}) and the selection is honored by the Modrinth
// creation task — an explicitly unselected optional file installs under its ".disabled"
// name, a selected one under its real path (the OptionalModDialog answers, headless).
//
// [Collection("ModrinthApi")] matches InstanceImportTaskTests: ModrinthApi.BaseUrl /
// DownloadHost are process-global statics and the API host runs the very same
// InstanceImportTask the core tests drive directly.
using System.Net;
using System.Text.Json;
using FlintLauncher.Core.ModPlatform.Modrinth;
using FlintLauncher.Tests;
using Xunit;

namespace Flint.Tests;

[Collection("ModrinthApi")]
public class ImportOptionalModsApiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"flint-import-api-{Guid.NewGuid():N}");
    private readonly StubModrinthServer _server = new();

    public ImportOptionalModsApiTests()
    {
        Directory.CreateDirectory(_root);
        ModrinthApi.BaseUrl = _server.Url;
        ModrinthApi.DownloadHost = "127.0.0.1";
    }

    public void Dispose()
    {
        ModrinthApi.BaseUrl = "https://api.modrinth.com/v2";
        ModrinthApi.DownloadHost = "cdn.modrinth.com";
        _server.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    /// <summary>A synthetic .mrpack with a required + an optional mod, with the API stub
    /// answering the metadata lookups; returns the mrpack's file URL.</summary>
    private string WriteMrpack()
    {
        var pack = MrPackBuilder.Create(_server.Url, withOptionalMod: true);
        _server.Downloads["mod.bin"] = pack.ModBytes;
        _server.Downloads["optional.bin"] = pack.OptionalBytes!;
        _server.VersionFiles[pack.ModSha512] = MrPackBuilder.VersionJson(_server.Url, pack);
        _server.Projects["projId1"] = MrPackBuilder.ProjectJson();

        var path = Path.Combine(_root, $"pack-{Guid.NewGuid():N}.mrpack");
        Assert.True(pack.WriteZip(path));
        return new Uri(path).ToString();
    }

    private async Task<string> ImportAndAwait(FlintLauncher.Tests.FlintApiHost host, string url,
        string name, Dictionary<string, bool>? optionalMods)
    {
        var post = await host.PostAsync("/api/v1/instances/import",
            new { url, name, optionalMods });
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);
        var taskId = JsonDocument.Parse(await post.Content.ReadAsStringAsync())
            .RootElement.GetProperty("taskId").GetString()!;
        await host.WaitForTaskStateAsync(taskId, "succeeded", TimeSpan.FromSeconds(30));
        return Path.Combine(host.DataDir, "instances", name);
    }

    [Fact]
    public async Task Unselected_Optional_Installs_Disabled()
    {
        var url = WriteMrpack();
        await using var host = await FlintApiHost.StartAsync();

        var instanceDir = await ImportAndAwait(host, url, "Opt Disabled",
            new Dictionary<string, bool> { ["mods/optional.jar"] = false });

        Assert.True(File.Exists(Path.Combine(instanceDir, "minecraft", "mods", "test.jar")));
        // the optional file kept the non-dialog behavior
        Assert.True(File.Exists(Path.Combine(instanceDir, "minecraft", "mods", "optional.jar.disabled")));
        Assert.False(File.Exists(Path.Combine(instanceDir, "minecraft", "mods", "optional.jar")));
    }

    [Fact]
    public async Task Selected_Optional_Installs_Enabled()
    {
        var url = WriteMrpack();
        await using var host = await FlintApiHost.StartAsync();

        var instanceDir = await ImportAndAwait(host, url, "Opt Selected",
            new Dictionary<string, bool> { ["mods/optional.jar"] = true });

        Assert.True(File.Exists(Path.Combine(instanceDir, "minecraft", "mods", "test.jar")));
        // the dialog's selection: the optional file installed under its real path
        Assert.True(File.Exists(Path.Combine(instanceDir, "minecraft", "mods", "optional.jar")));
        Assert.False(File.Exists(Path.Combine(instanceDir, "minecraft", "mods", "optional.jar.disabled")));
    }

    [Fact]
    public async Task Absent_Selection_Keeps_The_Default_Disabled_Install()
    {
        var url = WriteMrpack();
        await using var host = await FlintApiHost.StartAsync();

        // no optionalMods in the body: unchanged behavior for old callers
        var instanceDir = await ImportAndAwait(host, url, "Opt Absent", null);

        Assert.True(File.Exists(Path.Combine(instanceDir, "minecraft", "mods", "optional.jar.disabled")));
        Assert.False(File.Exists(Path.Combine(instanceDir, "minecraft", "mods", "optional.jar")));
    }
}
