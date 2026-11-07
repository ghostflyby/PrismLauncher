// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

using System.Net;
using System.Text.Json;
using FlintLauncher.Core.Config;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Migration;
using FlintLauncher.Web;
using TruePath;
using Xunit;

namespace FlintLauncher.Tests;

[Collection("LauncherJars")]
public class FlintMigrationApiTests
{
    [Fact]
    public async Task Nothing_Auto_Runs_And_The_Manual_Run_Clears_Everything()
    {
        await using var host = await FlintApiHost.StartAsync();

        // the legacy material lands AFTER the host booted: nothing migrated — the settings
        // stay default and the cfg-only instance is invisible to discovery
        var legacy = Path.Combine(host.DataDir, "legacy-anchor");
        Directory.CreateDirectory(legacy);
        await File.WriteAllTextAsync(Path.Combine(host.DataDir, "flint.cfg"),
            "[General]\nProxyAddr=proxy.corp\nProxyPort=3128\n");
        var instanceDir = Path.Combine(host.DataDir, "instances", "legacy-inst");
        Directory.CreateDirectory(instanceDir);
        await File.WriteAllTextAsync(Path.Combine(instanceDir, "instance.cfg"),
            "InstanceType=OneSix\nname=Legacy Inst\nuuid=0123456789abcdef0123456789abcdef\ntotalTimePlayed=3600\n");

        var before = await GetJsonAsync(host, "/api/v1/settings");
        Assert.Equal(8080, before.GetProperty("groups").GetProperty("proxy")
            .GetProperty("ProxyPort").GetInt32());
        var instances = await GetJsonAsync(host, "/api/v1/instances");
        Assert.False(HasInstance(instances, "legacy-inst"));

        // candidates report all three halves
        var candidates = await GetJsonAsync(host, "/api/v1/migration/flint/candidates");
        Assert.True(candidates.GetProperty("any").GetBoolean());
        Assert.NotNull(candidates.GetProperty("legacyRoot").GetString());
        Assert.NotNull(candidates.GetProperty("iniChainFile").GetString());
        Assert.Equal(1, candidates.GetProperty("pendingInstances").GetInt32());

        // one manual run clears everything in the same session
        var run = await host.PostAsync("/api/v1/migration/flint", new { });
        Assert.Equal(HttpStatusCode.Accepted, run.StatusCode);
        var taskId = JsonDocument.Parse(await run.Content.ReadAsStringAsync()).RootElement
            .GetProperty("taskId").GetString();
        Assert.NotNull(taskId);
        await host.WaitForTaskStateAsync(taskId, "succeeded", TimeSpan.FromSeconds(60));

        // the remount moved the INI to the config root; the projection made the values live
        Assert.False(File.Exists(Path.Combine(host.DataDir, "flint.cfg")));
        Assert.True(File.Exists(Path.Combine(host.HomeDir, "config", "flint.cfg")));
        var settings = await GetJsonAsync(host, "/api/v1/settings");
        Assert.Equal("proxy.corp", settings.GetProperty("groups").GetProperty("proxy")
            .GetProperty("ProxyAddr").GetString());
        Assert.Equal(3128, settings.GetProperty("groups").GetProperty("proxy")
            .GetProperty("ProxyPort").GetInt32());

        // the conversion materialized the instance (config half + the trio's state seed) and
        // the live list sees it
        Assert.True(File.Exists(Path.Combine(instanceDir, "instance.json")));
        Assert.True(File.Exists(Path.Combine(instanceDir, "instance-state.json")));
        instances = await GetJsonAsync(host, "/api/v1/instances");
        Assert.True(HasInstance(instances, "legacy-inst"));

        // the run is idempotent: candidates are spent (the INI's decision record seals the
        // projection, the remount record the layout, and no cfg-only directory remains)
        var after = await GetJsonAsync(host, "/api/v1/migration/flint/candidates");
        Assert.False(after.GetProperty("any").GetBoolean());
        Assert.Equal(0, after.GetProperty("pendingInstances").GetInt32());
    }

    private static async Task<JsonElement> GetJsonAsync(FlintApiHost host, string path)
    {
        var response = await host.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static bool HasInstance(JsonElement instances, string id) =>
        instances.ValueKind == JsonValueKind.Array &&
        instances.EnumerateArray().Any(instance =>
            instance.TryGetProperty("id", out var instanceId) && instanceId.GetString() == id);
}
