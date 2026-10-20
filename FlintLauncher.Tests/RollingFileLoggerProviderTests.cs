// SPDX-License-Identifier: GPL-3.0-only
// Tests for the rolling log file sink (Application.cpp:513-528 port): generations shift down
// at startup, a fresh Flint-0.log receives the lines, and an unwritable location degrades to
// a no-op instead of taking the launcher down.

using FlintLauncher.Web.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FlintLauncher.Tests;

public class RollingFileLoggerProviderTests : IDisposable
{
    private readonly string _dir;

    public RollingFileLoggerProviderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"flint-logtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Windows file locks; best effort cleanup
        }
    }

    private string PathOf(int generation) => Path.Combine(_dir, $"Flint-{generation}.log");

    [Fact]
    public void RotatesGenerationsAndReceivesLines()
    {
        File.WriteAllText(PathOf(0), "gen0\n");
        File.WriteAllText(PathOf(1), "gen1\n");
        File.WriteAllText(PathOf(3), "gen3\n");
        File.WriteAllText(PathOf(4), "gen4-oldest\n");

        using var provider = new RollingFileLoggerProvider(_dir);
        using (var factory = LoggerFactory.Create(builder =>
               {
                   builder.SetMinimumLevel(LogLevel.Information);
                   builder.AddProvider(provider);
               }))
        {
            factory.CreateLogger("test.category").LogInformation("hello file");
        }

        // generations shifted down (0->1, 1->2, 3->4) and the oldest was dropped
        Assert.Equal("gen0\n", File.ReadAllText(PathOf(1)));
        Assert.Equal("gen1\n", File.ReadAllText(PathOf(2)));
        Assert.Equal("gen3\n", File.ReadAllText(PathOf(4)));
        Assert.False(File.Exists(Path.Combine(_dir, "Flint-5.log")));
        // a fresh Flint-0.log received the formatted line
        var fresh = File.ReadAllText(PathOf(0));
        Assert.Contains("hello file", fresh);
        Assert.Contains("[test.category]", fresh);
        Assert.Contains("[info]", fresh);
    }

    [Fact]
    public void UnwritableDirectoryDegradesToNoop()
    {
        // a FILE where the directory should be — the provider must not throw and the
        // loggers must swallow writes instead of faulting the app
        var filePath = Path.Combine(_dir, "not-a-directory");
        File.WriteAllText(filePath, "occupied");

        using var provider = new RollingFileLoggerProvider(filePath);
        var logger = provider.CreateLogger("any");
        logger.LogInformation("dropped");
    }
}
