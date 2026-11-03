// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Tests for LauncherPartLaunch: the game runs as
// `java [javaArguments] -Djava.library.path=<natives> -cp <jars> org.prismlauncher.EntryPoint`
// with the launch script piped to stdin followed by "launch\n" (or "abort\n"). A fake
// shell-script "java" plays the EntryPoint: it echoes every stdin line back (so the script
// delivery is observable through the log) and honours the launch/abort command vocabulary,
// without any real Minecraft download.

using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Launch;
using FlintLauncher.Core.Launch.Steps;
using Xunit;

namespace FlintLauncher.Tests;

[Collection("LauncherJars")]
public class LauncherPartLaunchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"flint-launchpart-{Guid.NewGuid():N}");

    public LauncherPartLaunchTests()
    {
        Directory.CreateDirectory(_root);
        // the jar lookup (Application::getJarPath) searches <install root>/jars/NewLaunch.jar;
        // the factories below point the injected FlintPaths.InstallRoot at this fixture root
        // (the former JavaUtils.LauncherRootPath static test hook)
        Directory.CreateDirectory(Path.Combine(_root, "jars"));
        File.WriteAllText(Path.Combine(_root, "jars", LauncherJars.NewLaunchJarName), "dummy jar bytes");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException) { }
    }

    /// <summary>
    /// A stand-in for the NewLaunch EntryPoint: echo every stdin line (the launch script),
    /// exit 0 on the "launch" command, 42 on "abort".
    /// </summary>
    private string WriteFakeJava()
    {
        var path = Path.Combine(_root, "fake-java");
        File.WriteAllText(path, """
                                #!/bin/sh
                                while IFS= read -r line || [ -n "$line" ]; do
                                    printf '%s\n' "$line"
                                    if [ "$line" = "launch" ]; then
                                        exit 0
                                    fi
                                    if [ "$line" = "abort" ]; then
                                        exit 42
                                    fi
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

    private bool IsUnix => OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    private LaunchTask BuildLaunchTask(LaunchFixture fixture, MinecraftInstance instance, string javaPath,
        out LauncherPartLaunch step, string? installRoot = null)
    {
        // JavaPath is the (gate-controlled) global override; the stub applies in both cases
        fixture.Global.StubSet("JavaPath", javaPath);
        Directory.CreateDirectory(instance.GameRoot.Value); // the step's working directory
        var factory = fixture.CreateFactory(installRoot: installRoot ?? _root);
        var task = fixture.CreateTask(instance);
        step = factory.CreateLauncherPartLaunch(task);
        step.SetWorkingDirectory(instance.GameRoot);
        task.AppendStep(step);
        return task;
    }

    [Fact]
    public async Task DeliversLaunchScript_ThenProceedsWithTheLaunchCommand()
    {
        if (!IsUnix)
        {
            return;
        }

        using var fixture = new LaunchFixture();
        fixture.SeedJavaDetection("java", 0, languageVersion: 17, version: "17.0.2"); // probe cache, not settings
        var instance = await fixture.CreateInstance();
        var session = fixture.OfflineSession();
        var javaPath = WriteFakeJava();

        var task = BuildLaunchTask(fixture, instance, javaPath, out var step);
        step.Session = session;

        long pidAtLaunch = -1;
        task.ReadyForLaunch += () =>
        {
            // the process is running and the script was already piped to stdin
            pidAtLaunch = task.Pid;
            Assert.True(instance.IsRunning); // the launch counts as running until the game exits
            task.Proceed();                  // write "launch\n"
        };
        var outcome = await task.RunToCompletionAsync(TestContext.Current.CancellationToken);

        // the step only succeeds at game exit
        Assert.True(outcome.Succeeded, outcome.FailReason);
        Assert.Equal(-1, task.Pid); // reset after the game stopped
        Assert.True(pidAtLaunch > 0);
        Assert.False(instance.IsRunning);
        Assert.False(instance.HasCrashed);

        // the echoed script came back through the game-output pipeline
        var lines = task.GetLogModel().Entries;
        Assert.Contains(lines, e => e.Line.Contains("Minecraft process ID:"));
        Assert.Contains(lines, e => e.Line == "mainClass net.minecraft.client.main.Main");
        Assert.Contains(lines, e => e.Line == "param --username");
        Assert.Contains(lines, e => e.Line == "userName Steve");
        Assert.Contains(lines, e => e.Line == "sessionId token:0:" + session.Uuid);
        Assert.Contains(lines, e => e.Line == "launcher standard");
        Assert.Contains(lines, e => e.Line.Contains("Java arguments:"));
    }

    [Fact]
    public async Task Abort_WritesTheAbortCommand_AndReportsGameCrashed()
    {
        if (!IsUnix)
        {
            return;
        }

        using var fixture = new LaunchFixture();
        fixture.SeedJavaDetection("java", 0, languageVersion: 17, version: "17.0.2"); // probe cache, not settings
        var instance = await fixture.CreateInstance();
        var javaPath = WriteFakeJava();

        var task = BuildLaunchTask(fixture, instance, javaPath, out var step);
        step.Session = fixture.OfflineSession();

        task.ReadyForLaunch += () => task.Abort(); // abort instead of proceeding

        var outcome = await task.RunToCompletionAsync(TestContext.Current.CancellationToken);

        // the fake EntryPoint exits with code 42 on "abort": "if the exit code wasn't 0,
        // report this as a crash" (LauncherPartLaunch::on_state)
        Assert.False(outcome.Succeeded);
        Assert.False(outcome.Aborted);
        Assert.Equal("Game crashed.", outcome.FailReason);
        Assert.True(instance.HasCrashed);
        var lines = task.GetLogModel().Entries;
        Assert.Contains(lines, e => e.Line == "abort"); // the command echoed back
    }

    /// <summary>A java binary that cannot be executed at all: the process reports
    /// FailedToStart synchronously from Start(), before the step could reach its own await.</summary>
    [Fact]
    public async Task UnstartableJava_FailsInsteadOfHanging()
    {
        using var fixture = new LaunchFixture();
        fixture.SeedJavaDetection("java", 0, languageVersion: 17, version: "17.0.2");
        var instance = await fixture.CreateInstance();

        var task = BuildLaunchTask(fixture, instance, Path.Combine(_root, "no-such-java"), out var step);
        step.Session = fixture.OfflineSession();

        LaunchTask.LaunchOutcome outcome;
        var run = task.RunToCompletionAsync(TestContext.Current.CancellationToken);
        var completed = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30)));

        Assert.Same(run, completed);
        outcome = await run;
        Assert.False(outcome.Succeeded);
        Assert.False(outcome.Aborted);
        Assert.Contains("Could not launch Minecraft", outcome.FailReason);
    }

    [Fact]
    public async Task MissingLauncherRoot_FallsBackToEmbeddedJars()
    {
        // Since the jars are embedded in the assembly (extracted to the per-user cache on
        // demand), an empty/broken launcher root no longer fails the launch: the fallback
        // serves the embedded copies. Only a truly absent embedded resource fails.
        if (!IsUnix)
        {
            return;
        }

        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance();
        var javaPath = WriteFakeJava();
        // an install root without the jar layout: the lookup falls through to the embedded
        // copies (extracted by EmbeddedJars)
        var task = BuildLaunchTask(fixture, instance, javaPath, out var step,
            installRoot: Path.Combine(_root, "does-not-exist"));
        step.Session = fixture.OfflineSession();
        task.ReadyForLaunch += () => task.Proceed();

        var outcome = await task.RunToCompletionAsync(TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded, outcome.FailReason);
        var lines = task.GetLogModel().Entries;
        Assert.Contains(lines, e => e.Line == "launcher standard");
    }
}
