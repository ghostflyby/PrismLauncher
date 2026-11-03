// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// smoke: launch a real assembled step chain through the LaunchController (the headless
// entry point) with a fake java that speaks the EntryPoint stdin protocol — no network, no
// real Minecraft. Also assembles the launch task of a real Prism instance (read-only).

using FlintLauncher.Core;
using FlintLauncher.Core.Config;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Launch;
using FlintLauncher.Core.Net;
using Microsoft.Extensions.Logging.Abstractions;
using TruePath;
using Xunit;

namespace FlintLauncher.Tests;

[Collection("LauncherJars")]
public class RealLaunchSmokeTests
{
    private static bool IsUnix => OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    [Fact]
    public async Task FullLaunchChain_EndToEnd_WithFakeJava()
    {
        if (!IsUnix)
        {
            return;
        }

        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance();
        var root = Path.Combine(Path.GetTempPath(), $"flint-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        // Pre-seed the java probe results so CheckJava skips the JavaCheck.jar run (exactly the
        // state of an instance whose java was already checked by a previous launch): the stored
        // signature must equal ComputeJavaSignature(mtime, path). The probe
        // cache lives in launcher-state.json, seeded through the fixture's state store.
        var javaPath = WriteFakeJava(root);
        var javaUnixTime = new DateTimeOffset(File.GetLastWriteTimeUtc(javaPath)).ToUnixTimeMilliseconds();
        // JavaPath is the instance-layer override: writing the member IS the override (the
        // derived OverrideJavaLocation gate reads true; real Prism instance.cfg files
        // carry the same override as a gated value).
        instance.Settings.Set("JavaPath", javaPath);
        fixture.SeedJavaDetection(javaPath, javaUnixTime, languageVersion: 17, vendor: "smoke");

        // Resolve the REAL NewLaunch.jar built by the BuildJavaJars MSBuild target (the fake
        // java ignores the classpath; the launch pipeline extracts the launcher jars).
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var launcherRoot = Path.Combine(repoRoot, "build");
        if (!File.Exists(Path.Combine(launcherRoot, "jars", "NewLaunch.jar")))
        {
            return; // jars not built on this machine — run docs/tools/newlaunch-build.sh first
        }

        // post-exit command leaves a marker: proves the steps AFTER the game exit ran
        var postExitMarker = Path.Combine(root, "post-exit-ran");
        fixture.Global.StubSet("PostExitCommand", $"/usr/bin/touch '{postExitMarker}'");

        // the jar/jar-check lookups resolve against this install root (the former
        // JavaUtils.LauncherRootPath static test hook); the factory is the composition point
        var controller = fixture.CreateFactory(installRoot: launcherRoot).CreateController(
            instance, LaunchMode.Offline, offlineName: "SmokeTester", accountToUse: null,
            accountsProvider: null, autoProceed: true, targetToJoin: null);

        // the controller's run covers the whole session, the game included
        var outcome = await controller.RunToCompletionAsync(TestContext.Current.CancellationToken);
        var launchTask = controller.Launcher;
        Assert.NotNull(launchTask);

        if (!outcome.Succeeded)
        {
            throw new Exception(
                "launch failed: " + outcome.FailReason + " | log: "
                + string.Join(" / ", launchTask.GetLogModel().Entries.Select(e => e.Line).TakeLast(12)));
        }

        // the launch script reached the fake EntryPoint and came back through the log
        var lines = launchTask.GetLogModel().Entries.Select(e => e.Line).ToList();
        Assert.Contains("mainClass net.minecraft.client.main.Main", lines);
        Assert.Contains(lines, l => l == "param --username");
        Assert.Contains(lines, l => l == "param SmokeTester");
        Assert.Contains(lines, l => l == "userName SmokeTester");
        Assert.Contains(lines, l => l == "traits FirstThreadOnMacOS");
        Assert.Contains(lines, l => l == "launcher standard");

        // game-time bookkeeping happened (setMinecraftRunning(true/false))
        Assert.True(instance.LastLaunch() > 0);
        Assert.False(instance.IsRunning);

        // the post-exit command ran after the game exited
        Assert.True(File.Exists(postExitMarker), "post-exit command did not run");
    }

    [Fact]
    public async Task RealInstance_LaunchTask_Assembles()
    {
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Application Support", "PrismLauncher");
        var instancesDir = Path.Combine(dataDir, "instances");
        if (!Directory.Exists(instancesDir))
        {
            return; // no real data on this machine (CI)
        }

        var instanceDir = Directory.EnumerateDirectories(instancesDir)
            .FirstOrDefault(d => File.Exists(Path.Combine(d, "instance.cfg")));
        if (instanceDir is null)
        {
            return;
        }

        // assembly needs a resolved component profile, which offline needs the meta cache
        // (in the deployed host the CWD is the data dir, so CWD/meta IS the Prism cache).
        var global = new GlobalLaunchSettingsStub();
        // the real Prism instance loads read-only through the typed layer stack (the
        // instance.cfg imports in memory; nothing materializes)
        var instanceRootPath = new AbsolutePath(instanceDir);
        var instance = new MinecraftInstance(
            new InstanceJsonSettingsObject(instanceRootPath, global.Environment),
            instanceRootPath);
        _ = instance.Settings;
        var reload = await instance.GetPackProfile().ReloadAsync(NetMode.Offline);
        if (!reload.Ok)
        {
            return; // meta cache unavailable in this test layout; covered by fixture-based tests
        }

        // assembly is explicit (no headless fallback): a CWD-anchored bare factory over a
        // null logger and a throwaway client stands in for the host's composition point
        var cwd = AbsolutePath.CurrentWorkingDirectory;
        var launchTask = new LaunchFactory(
            new FlintPaths
            {
                ConfigRoot = cwd / "config",
                DataRoot = cwd,
                StoreRoot = cwd / "store",
                CacheRoot = cwd / "cache",
                StateRoot = cwd / "state",
                HomeRoot = cwd,
            },
            NullLoggerFactory.Instance, NetPrimaryHandler.CreateClient())
            .CreateTask(instance, null, null);
        Assert.NotNull(launchTask);
        Assert.True(launchTask.IsMultiStep, "real instance launch task should contain the full step chain");
    }

    private static string WriteFakeJava(string root)
    {
        var path = Path.Combine(root, "fake-java");
        File.WriteAllText(path, """
                                #!/bin/sh
                                exec 2>>/tmp/fj-stderr.log
                                while IFS= read -r line || [ -n "$line" ]; do
                                    printf '%s\n' "$line"
                                    echo "got: $line" >> /tmp/fj-stdin.log
                                    if [ "$line" = "launch" ]; then
                                        exit 0
                                    fi
                                    if [ "$line" = "abort" ]; then
                                        exit 42
                                    fi
                                done
                                echo "eof-exit" >> /tmp/fj-stdin.log
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
