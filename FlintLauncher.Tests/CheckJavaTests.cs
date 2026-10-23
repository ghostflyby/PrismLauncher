// SPDX-License-Identifier: GPL-3.0-only
// CheckJava's probe-cache gate: a cached entry whose signature matches and whose members
// are complete skips the JdkFind probe; an entry without a language version — written before
// the member existed, or by a probe that found no readable release file — is stale and gets
// probed again. This is what migrates launcher-state.json written by older builds.

using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Launch;
using FlintLauncher.Core.Launch.Steps;
using FlintLauncher.Core.Tasks;
using Xunit;

namespace FlintLauncher.Tests;

public class CheckJavaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"flint-checkjava-{Guid.NewGuid():N}");

    public CheckJavaTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException) { }
    }

    private static bool IsUnix => OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    [Fact]
    public async Task CachedEntryWithoutLanguageVersion_IsStale_AndProbesAgain()
    {
        if (!IsUnix)
        {
            return;
        }

        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance();
        SeedCachedJava(fixture, instance, languageVersion: null);

        var task = await RunCheckJavaAsync(fixture, instance);

        // the stale entry sent the step into a fresh JdkFind probe (the fake java cannot
        // answer it), instead of trusting the cache
        Assert.Equal(TaskState.Failed, task.State);
        Assert.Equal("Could not start java!", task.FailReason);
        Assert.Contains(task.GetLogModel().Entries, e => e.Line == "Checking Java version...");
    }

    [Fact]
    public async Task CompleteCachedEntry_SkipsTheProbe()
    {
        if (!IsUnix)
        {
            return;
        }

        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance();
        SeedCachedJava(fixture, instance, languageVersion: 17);

        var task = await RunCheckJavaAsync(fixture, instance);

        Assert.Equal(TaskState.Succeeded, task.State);
        Assert.DoesNotContain(task.GetLogModel().Entries, e => e.Line == "Checking Java version...");
        Assert.Contains(task.GetLogModel().Entries,
            e => e.Line == "Java is version 17.0.2, using 64 (arm64) architecture, from fixture");
    }

    private void SeedCachedJava(LaunchFixture fixture, MinecraftInstance instance, int? languageVersion)
    {
        var javaPath = WriteFakeJava();
        var javaUnixTime = new DateTimeOffset(File.GetLastWriteTimeUtc(javaPath)).ToUnixTimeMilliseconds();
        instance.Settings.Set("JavaPath", javaPath);
        // signature, version, architectures and vendor all present: the language version is
        // the only member that differs between the two arms
        fixture.SeedJavaDetection(javaPath, javaUnixTime, languageVersion);
    }

    private async Task<LaunchTask> RunCheckJavaAsync(LaunchFixture fixture, MinecraftInstance instance)
    {
        var task = LaunchTask.Create(instance, fixture.CreateServices(installRoot: _root));
        task.AppendStep(new CheckJava(task));
        await task.StartAsync();
        return task;
    }

    /// <summary>
    /// A java binary that answers the launch stdin protocol but not JdkFind's probe: with no
    /// stdin to read, the script falls through to a non-zero exit.
    /// </summary>
    private string WriteFakeJava()
    {
        var path = Path.Combine(_root, "fake-java");
        File.WriteAllText(path, """
                                #!/bin/sh
                                while IFS= read -r line || [ -n "$line" ]; do
                                    printf '%s\n' "$line"
                                    exit 0
                                done
                                exit 1
                                """);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute |
                                       UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }

        return path;
    }
}
