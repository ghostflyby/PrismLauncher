// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// EnsureMetadataTask: the per-resource failures travel as RunAsync's return value.

using FlintLauncher.Core.Minecraft.Resources;
using FlintLauncher.Core.ModPlatform;
using TruePath;
using Xunit;

namespace FlintLauncher.Tests;

public class EnsureMetadataTaskTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"flint-ensure-meta-{Guid.NewGuid():N}");

    public EnsureMetadataTaskTests()
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

    [Fact]
    public async Task UnreachableApi_FailsThePass()
    {
        var indexDir = new AbsolutePath(Path.Combine(_root, ".index"));
        var resources = new Dictionary<string, Resource>
        {
            ["hash-a"] = new(Path.Combine(_root, "a.jar")),
            ["hash-b"] = new(Path.Combine(_root, "b.jar")),
        };

        var (modrinth, http) = TestModrinthApi.Unreachable();
        var task = new EnsureMetadataTask(resources, indexDir, ResourceProvider.Modrinth, modrinth, http);

        // a transport failure is not a per-resource outcome: the lookup itself is what failed,
        // so the pass throws instead of reporting every resource
        await Assert.ThrowsAnyAsync<Exception>(() =>
            task.RunAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResourcesTheApiDoesNotKnow_AreReturnedAsFailures()
    {
        // the API answers, but with nothing that matches: this is per-resource, so the pass
        // returns the entries rather than throwing
        using var server = new StubModrinthServer();
        var (modrinth, http) = TestModrinthApi.At(server.Url);

        var indexDir = new AbsolutePath(Path.Combine(_root, ".index"));
        var resources = new Dictionary<string, Resource>
        {
            ["only"] = new(Path.Combine(_root, "only.jar")),
        };

        var task = new EnsureMetadataTask(resources, indexDir, ResourceProvider.Modrinth, modrinth, http);
        var failures = await task.RunAsync(TestContext.Current.CancellationToken);

        Assert.Single(failures);
        Assert.Contains("only", failures[0].Reason);
    }

    [Fact]
    public async Task EveryUnresolvedResourceIsReportedExactlyOnce()
    {
        // the API answers, but knows none of these hashes. Each must be reported once: the
        // sweep removes entries as it reports them, so walking the live set would throw.
        using var server = new StubModrinthServer();
        var (modrinth, http) = TestModrinthApi.At(server.Url);

        var indexDir = new AbsolutePath(Path.Combine(_root, ".index"));
        var resources = new Dictionary<string, Resource>
        {
            ["leftover-a"] = new(Path.Combine(_root, "a.jar")),
            ["leftover-b"] = new(Path.Combine(_root, "b.jar")),
            ["leftover-c"] = new(Path.Combine(_root, "c.jar")),
        };

        var task = new EnsureMetadataTask(resources, indexDir, ResourceProvider.Modrinth, modrinth, http);
        var failures = await task.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, failures.Count);
        Assert.Equal(3, failures.Select(f => f.Resource).Distinct().Count());
    }
}
