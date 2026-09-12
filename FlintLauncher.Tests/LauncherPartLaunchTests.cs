// SPDX-License-Identifier: GPL-3.0-only
// Tests for LauncherPartLaunch: the game runs as
// `java [javaArguments] -Djava.library.path=<natives> -cp <jars> org.prismlauncher.EntryPoint`
// with the launch script piped to stdin followed by "launch\n" (or "abort\n"). A fake
// shell-script "java" plays the EntryPoint: it echoes every stdin line back (so the script
// delivery is observable through the log) and honours the launch/abort command vocabulary,
// without any real Minecraft download.
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Java;
using FlintLauncher.Core.Launch;
using FlintLauncher.Core.Launch.Steps;
using FlintLauncher.Core.Tasks;
using Xunit;

namespace FlintLauncher.Tests;

public class LauncherPartLaunchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"flint-launchpart-{Guid.NewGuid():N}");
    private readonly string? _savedLauncherRoot;

    public LauncherPartLaunchTests()
    {
        Directory.CreateDirectory(_root);
        // the jar lookup (Application::getJarPath) searches <launcher root>/jars/NewLaunch.jar
        _savedLauncherRoot = JavaUtils.LauncherRootPath;
        Directory.CreateDirectory(Path.Combine(_root, "jars"));
        File.WriteAllText(Path.Combine(_root, "jars", LauncherJars.NewLaunchJarName), "dummy jar bytes");
        JavaUtils.LauncherRootPath = _root;
    }

    public void Dispose()
    {
        JavaUtils.LauncherRootPath = _savedLauncherRoot ?? AppContext.BaseDirectory;
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
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
                                       out LauncherPartLaunch step)
    {
        // JavaPath is the (gate-controlled) global override; the stub applies in both cases
        fixture.Global.StubSet("JavaPath", javaPath);
        Directory.CreateDirectory(instance.GameRoot()); // the step's working directory
        var task = LaunchTask.Create(instance);
        step = new LauncherPartLaunch(task);
        step.SetWorkingDirectory(instance.GameRoot());
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
        fixture.Global.StubSet("JavaVersion", "17.0.2");
        var instance = await fixture.CreateInstance();
        var session = fixture.OfflineSession("Steve");
        var javaPath = WriteFakeJava();

        var task = BuildLaunchTask(fixture, instance, javaPath, out var step);
        step.SetAuthSession(session);

        long pidAtLaunch = -1;
        task.ReadyForLaunch += () =>
        {
            // the process is running and the script was already piped to stdin
            pidAtLaunch = task.Pid;
            Assert.True(instance.IsRunning); // the launch counts as running until the game exits
            task.Proceed();                  // write "launch\n"
        };
        await task.StartAsync();

        // the step only succeeds at game exit
        Assert.Equal(TaskState.Succeeded, task.State);
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
        fixture.Global.StubSet("JavaVersion", "17.0.2");
        var instance = await fixture.CreateInstance();
        var javaPath = WriteFakeJava();

        var task = BuildLaunchTask(fixture, instance, javaPath, out var step);
        step.SetAuthSession(fixture.OfflineSession("Steve"));

        task.ReadyForLaunch += () => task.Abort(); // abort instead of proceeding

        await task.StartAsync();

        // the fake EntryPoint exits with code 42 on "abort": "if the exit code wasn't 0,
        // report this as a crash" (LauncherPartLaunch::on_state)
        Assert.Equal(TaskState.Failed, task.State);
        Assert.Equal("Game crashed.", task.FailReason);
        Assert.True(instance.HasCrashed);
        var lines = task.GetLogModel().Entries;
        Assert.Contains(lines, e => e.Line == "abort"); // the command echoed back
    }

    [Fact]
    public async Task MissingLauncherJar_FailsTheStep()
    {
        if (!IsUnix)
        {
            return;
        }
        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance();
        JavaUtils.LauncherRootPath = Path.Combine(_root, "does-not-exist");
        try
        {
            var javaPath = WriteFakeJava();
            var task = BuildLaunchTask(fixture, instance, javaPath, out _);

            await task.StartAsync();

            Assert.Equal(TaskState.Failed, task.State);
            Assert.Contains("Launcher library could not be found", task.FailReason);
            var lines = task.GetLogModel().Entries;
            Assert.Contains(lines, e => e.Level == MessageLevel.Fatal &&
                                        e.Line.Contains("Launcher library could not be found"));
        }
        finally
        {
            JavaUtils.LauncherRootPath = _root;
        }
    }
}
