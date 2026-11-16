// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

using FlintLauncher.Core;
using FlintLauncher.Core.Java;
using JdkFind;
using JdkFind.Providers;
using TruePath;
using Xunit;

namespace FlintLauncher.Tests;

public class JavaDiscoveryTests : IDisposable
{
    private const string JavaBinary = "java";
    private readonly List<(string name, string? value)> _savedEnv = [];
    private readonly List<string> _tempRoots = [];

    public void Dispose()
    {
        foreach (var (name, value) in _savedEnv)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        foreach (var root in _tempRoots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (DirectoryNotFoundException) { }
        }
    }

    private string TempRoot()
    {
        var root = Directory.CreateTempSubdirectory("flint-jdkfind-test").FullName;
        _tempRoots.Add(root);
        return root;
    }

    private void SetEnv(string name, string? value)
    {
        _savedEnv.Add((name, Environment.GetEnvironmentVariable(name)));
        Environment.SetEnvironmentVariable(name, value);
    }

    private static async Task WriteRunnableJavaAsync(string path, string version, string architecture)
    {
        await File.WriteAllTextAsync(path, $"""
                                           #!/bin/sh
                                           printf '%s\n' "    java.version = {version}" "    java.vendor = Test" "    os.arch = {architecture}" >&2
                                           exit 0
                                           """);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute |
                                       UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
    }

    [Fact]
    public async Task DetectAsync_MapsAndDeduplicatesTheLayers()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = TempRoot();
        var brokenRoot = TempRoot();
        var home = Path.Combine(root, "jdk-21.0.5");
        Directory.CreateDirectory(Path.Combine(home, "bin"));
        await File.WriteAllTextAsync(Path.Combine(home, "bin", "java"), "");
        await File.WriteAllTextAsync(Path.Combine(home, "release"), "JAVA_VERSION=\"21.0.5\"\n");
        var brokenHome = Path.Combine(brokenRoot, "jdk-broken");
        Directory.CreateDirectory(Path.Combine(brokenHome, "bin"));
        await File.WriteAllTextAsync(Path.Combine(brokenHome, "bin", "java"), "");
        await File.WriteAllTextAsync(Path.Combine(brokenHome, "release"), "JAVA_VERSION=\"11.0.1\"\n");

        IJvmProvider provider = OperatingSystem.IsMacOS()
            ? new MacOsJvmProvider([root])
            : new UnixJvmProvider(root);
        var executable = Path.Combine(home, "bin", JavaBinary);
        var broken = Path.Combine(brokenHome, "bin", JavaBinary);
        SetEnv($"{FlintConfig.Default.LauncherAppBinaryEnvName}_JAVA_PATHS",
            string.Join(Path.PathSeparator, executable, broken, Path.Combine(root, "nonexistent", "java")));

        var candidates = await JavaDetection.DetectAsync(providers: [provider]);

        var primary = Assert.Single(candidates, c => c.Path == executable);
        Assert.Equal("21.0.5", primary.Version.Original);
        Assert.Equal(21, primary.Installation?.LanguageVersion);
        Assert.Equal("", primary.Architecture);
        Assert.NotNull(primary.StartFailure);

        var dead = Assert.Single(candidates, c => c.Path == broken);
        Assert.NotNull(dead.StartFailure);
        Assert.Equal("11.0.1", dead.Version.Original);

        Assert.DoesNotContain(candidates, c => c.Path == Path.Combine(root, "nonexistent", "java"));
    }

    [Fact]
    public async Task DetectAsync_ExposesReleaseFileArchitecture()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = TempRoot();
        var home = Path.Combine(root, "Contents", "Home");
        Directory.CreateDirectory(Path.Combine(home, "bin"));
        await File.WriteAllTextAsync(Path.Combine(home, "bin", "java"), "");
        await File.WriteAllTextAsync(Path.Combine(home, "release"), "JAVA_VERSION=\"17.0.2\"\nOS_ARCH=\"aarch64\"\n");

        var candidates = await JavaDetection.DetectAsync(
            providers: [new MacOsJvmProvider([root]), new JavaHomeJvmProvider(root)]);

        var candidate = Assert.Single(candidates,
            c => c.Path == Path.Combine(home, "bin", JavaBinary));
        Assert.Equal("aarch64", candidate.Architecture);
        Assert.Equal("64", candidate.MojangArchitecture);
        Assert.Equal(17, candidate.Installation?.LanguageVersion);
    }

    [Fact]
    public async Task DetectAsync_IncludesManagedRootHomes()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = TempRoot();
        var home = Path.Combine(root, "jdk-21.0.5");
        Directory.CreateDirectory(Path.Combine(home, "bin"));
        await WriteRunnableJavaAsync(Path.Combine(home, "bin", JavaBinary), "21.0.5", "aarch64");
        await File.WriteAllTextAsync(Path.Combine(home, "release"), "JAVA_VERSION=\"21.0.5\"\n");

        var candidates = await JavaDetection.DetectAsync(providers: [], javaRoot: new AbsolutePath(root));

        var candidate = Assert.Single(candidates);
        Assert.Equal("21.0.5", candidate.Version.Original);
        Assert.NotNull(candidate.Home);
    }

    [Fact]
    public async Task DetectAsync_IncludesHomebrewKegHomes()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var prefix = TempRoot();
        var home = Path.Combine(prefix, "opt", "openjdk21", "libexec", "openjdk.jdk", "Contents", "Home");
        Directory.CreateDirectory(Path.Combine(home, "bin"));
        await File.WriteAllTextAsync(Path.Combine(home, "bin", "java"), "");
        await File.WriteAllTextAsync(Path.Combine(home, "release"), "JAVA_VERSION=\"21.0.5\"\n");

        var candidates = await JavaDetection.DetectAsync(providers: [new HomebrewJvmProvider([prefix])]);

        Assert.Contains(candidates, c =>
            c.Path == Path.Combine(home, "bin", JavaBinary) &&
            c.Version.Original == "21.0.5");
    }
}
