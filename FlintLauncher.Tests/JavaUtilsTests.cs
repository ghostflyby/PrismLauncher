// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

using FlintLauncher.Core;
using FlintLauncher.Core.Java;
using JdkFind;
using JdkFind.Providers;
using TruePath;
using Xunit;

namespace FlintLauncher.Tests;

public class JavaUtilsTests : IDisposable
{
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

    /// <summary>A java binary that answers JdkFind's property probe with a complete trio,
    /// so the executable is valid without a release file.</summary>
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

    // ------------------------------------------------------------------ discovery

    [Fact]
    public async Task DetectAsync_MapsAndDeduplicatesTheLayers()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // the fixture names the binary with the unix "java" spelling; the
                    // Windows provider path is exercised by the system suite instead
        }
        // a synthetic plain-layout JDK home reported by an injected provider is the primary
        // candidate; an env-provided copy of the same path collapses into it, a second env
        // home (outside the scanned root, so purely a fallback candidate) with a release
        // file but a dead binary survives as an invalid fallback candidate, and an env path
        // with no establishable JVM drops entirely
        var root = TempRoot();
        var brokenRoot = TempRoot();
        var home = Path.Combine(root, "jdk-21.0.5");
        Directory.CreateDirectory(Path.Combine(home, "bin"));
        await File.WriteAllTextAsync(Path.Combine(home, "bin",
            OperatingSystem.IsWindows() ? "java.exe" : "java"), "");
        await File.WriteAllTextAsync(Path.Combine(home, "release"), "JAVA_VERSION=\"21.0.5\"\n");
        var brokenHome = Path.Combine(brokenRoot, "jdk-broken");
        Directory.CreateDirectory(Path.Combine(brokenHome, "bin"));
        await File.WriteAllTextAsync(Path.Combine(brokenHome, "bin",
            OperatingSystem.IsWindows() ? "java.exe" : "java"), "");
        await File.WriteAllTextAsync(Path.Combine(brokenHome, "release"), "JAVA_VERSION=\"11.0.1\"\n");

        IJvmProvider provider = OperatingSystem.IsWindows()
            ? new WindowsProgramFilesJvmProvider([root])
            : OperatingSystem.IsMacOS()
                ? new MacOsJvmProvider([root])
                : new UnixJvmProvider(root);
        var executable = Path.Combine(home, "bin", JavaUtils.JavaExecutable);
        var broken = Path.Combine(brokenHome, "bin", JavaUtils.JavaExecutable);
        SetEnv($"{FlintConfig.Default.LauncherAppBinaryEnvName}_JAVA_PATHS",
            string.Join(Path.PathSeparator, executable, broken, Path.Combine(root, "nonexistent", "java")));

        var candidates = await JavaDetection.DetectAsync(providers: [provider]);

        var primary = Assert.Single(candidates, c => c.Path == executable);
        Assert.Equal("21.0.5", primary.Version.Original);
        Assert.Equal(21, primary.Installation?.LanguageVersion);
        // the probe cannot execute the empty stub java, so metadata degrades to the release
        // file; OS_ARCH is absent from the fixture
        Assert.Equal("", primary.Architecture);
        Assert.NotNull(primary.StartFailure);

        // release-backed but unable to run: listed with the StartFailure verdict
        var dead = Assert.Single(candidates, c => c.Path == broken);
        Assert.NotNull(dead.StartFailure);
        Assert.Equal("11.0.1", dead.Version.Original);

        // nothing behind it: dropped instead of listed
        Assert.DoesNotContain(candidates, c => c.Path == Path.Combine(root, "nonexistent", "java"));
    }

    [Fact]
    public async Task DetectAsync_ExposesReleaseFileArchitecture()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // the Contents/Home bundle shape is a unix fixture: the mac provider is
                    // os-gated and JavaHomeJvmProvider does not honor the root override on
                    // windows, so the synthetic candidate never reaches the pipeline there
        }

        // OS_ARCH in the release file feeds the same architecture slot the checker's
        // os.arch used to — the 64-bit classifier derives from it downstream
        var root = TempRoot();
        var home = Path.Combine(root, "Contents", "Home");
        Directory.CreateDirectory(Path.Combine(home, "bin"));
        await File.WriteAllTextAsync(Path.Combine(home, "bin",
            OperatingSystem.IsWindows() ? "java.exe" : "java"), "");
        await File.WriteAllTextAsync(Path.Combine(home, "release"), "JAVA_VERSION=\"17.0.2\"\nOS_ARCH=\"aarch64\"\n");

        var candidates = await JavaDetection.DetectAsync(
            providers: [new MacOsJvmProvider([root]), new JavaHomeJvmProvider(root)]);

        // the system suite contributes its own (real-machine) candidates, so scope the
        // assertion to the synthetic home
        var candidate = Assert.Single(candidates,
            c => c.Path == Path.Combine(home, "bin", JavaUtils.JavaExecutable));
        Assert.Equal("aarch64", candidate.Architecture);
        Assert.True(JavaUtils.Is64BitArchitecture(candidate.Architecture));
        Assert.Equal(17, candidate.Installation?.LanguageVersion);
    }

    [Fact]
    public async Task JavaListLoadTask_FillsTheListFromDetection()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // the fixture names the binary with the unix "java" spelling
        }

        var root = TempRoot();
        var home = Path.Combine(root, "jdk-21.0.5");
        Directory.CreateDirectory(Path.Combine(home, "bin"));
        await WriteRunnableJavaAsync(Path.Combine(home, "bin", "java"), "21.0.5", "x86_64");
        await File.WriteAllTextAsync(Path.Combine(home, "release"), "JAVA_VERSION=\"21.0.5\"\nOS_ARCH=\"x86_64\"\n");

        IJvmProvider provider = OperatingSystem.IsWindows()
            ? new WindowsProgramFilesJvmProvider([root])
            : OperatingSystem.IsMacOS()
                ? new MacOsJvmProvider([root])
                : new UnixJvmProvider(root);

        var list = new JavaInstallList();
        await JavaListLoadTask.RunAsync(list, TestContext.Current.CancellationToken, providers: [provider]);

        var install = Assert.Single(list.Versions, j => j.Path == Path.Combine(home, "bin", JavaUtils.JavaExecutable));
        Assert.Equal("21.0.5", install.Version.Original);
        Assert.Equal(21, install.Installation?.LanguageVersion);
        Assert.Equal("x86_64", install.Architecture);
        Assert.True(JavaUtils.Is64BitArchitecture(install.Architecture));
        Assert.Null(install.StartFailure);
    }

    [Fact]
    public async Task JavaListLoadTask_ExcludesFailedProbe()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // the fixture names the binary with the unix "java" spelling
        }

        var root = TempRoot();
        var home = Path.Combine(root, "jdk-broken");
        Directory.CreateDirectory(Path.Combine(home, "bin"));
        await File.WriteAllTextAsync(Path.Combine(home, "bin", "java"), "");
        await File.WriteAllTextAsync(Path.Combine(home, "release"), "JAVA_VERSION=\"11.0.1\"\n");

        IJvmProvider provider = OperatingSystem.IsMacOS()
            ? new MacOsJvmProvider([root])
            : new UnixJvmProvider(root);
        var list = new JavaInstallList();
        await JavaListLoadTask.RunAsync(list, TestContext.Current.CancellationToken, providers: [provider]);

        Assert.Empty(list.Versions);
    }

    [Fact]
    public async Task JavaListLoadTask_ManagedOnly_FindsReleaseLessExecutable()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // the fixture names the binary with the unix "java" spelling
        }

        // no release file: the home provider rejects it, and the executable-directory
        // layer must identify it through the probe alone
        var root = TempRoot();
        var home = Path.Combine(root, "jdk-no-release");
        Directory.CreateDirectory(Path.Combine(home, "bin"));
        await WriteRunnableJavaAsync(Path.Combine(home, "bin", "java"), "21.0.5", "aarch64");

        var javaRoot = new AbsolutePath(root);
        var list = new JavaInstallList(onlyManagedVersions: true, javaRoot: javaRoot);
        await JavaListLoadTask.RunAsync(list, TestContext.Current.CancellationToken,
            onlyManagedVersions: true, javaRoot: javaRoot);

        var install = Assert.Single(list.Versions);
        Assert.Equal("21.0.5", install.Version.Original);
        Assert.Equal("aarch64", install.Architecture);
        Assert.Null(install.Home);
        Assert.Null(install.StartFailure);
    }

    [Fact]
    public async Task DetectAsync_IncludesHomebrewKegHomes()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // the keg layout and the fixture's binary name are unix shapes
        }

        // the Homebrew keg layout (libexec/openjdk.jdk/Contents/Home) is a JdkFind-only
        // source: the legacy filesystem scan never looked under the brew prefix
        var prefix = TempRoot();
        var home = Path.Combine(prefix, "opt", "openjdk21", "libexec", "openjdk.jdk", "Contents", "Home");
        Directory.CreateDirectory(Path.Combine(home, "bin"));
        await File.WriteAllTextAsync(Path.Combine(home, "bin",
            OperatingSystem.IsWindows() ? "java.exe" : "java"), "");
        await File.WriteAllTextAsync(Path.Combine(home, "release"), "JAVA_VERSION=\"21.0.5\"\n");

        var candidates = await JavaDetection.DetectAsync(providers: [new HomebrewJvmProvider([prefix])]);

        Assert.Contains(candidates, c =>
            c.Path == Path.Combine(home, "bin", JavaUtils.JavaExecutable) &&
            c.Version.Original == "21.0.5");
    }

    // ------------------------------------------------------------- environment

    [Fact]
    public void CleanEnvironment_DropsDangerousJavaVariables()
    {
        SetEnv("JAVA_HOME", "/opt/jdk");
        SetEnv("_JAVA_OPTIONS", "-Xmx1g");
        SetEnv("JAVA_TOOL_OPTIONS", "-agentlib:foo");
        SetEnv("FLINT_TEST_KEEP_ME", "kept");

        var env = JavaUtils.CleanEnvironment();

        Assert.False(env.ContainsKey("JAVA_HOME"));
        Assert.False(env.ContainsKey("_JAVA_OPTIONS"));
        Assert.False(env.ContainsKey("JAVA_TOOL_OPTIONS"));
        Assert.True(env.ContainsKey("FLINT_TEST_KEEP_ME"));
        Assert.Equal("kept", env["FLINT_TEST_KEEP_ME"]);
    }

    [Fact]
    public void CleanEnvironment_DropsLauncherPrefixedMirrors()
    {
        SetEnv("LAUNCHER_WHATEVER", "dropped");

        var env = JavaUtils.CleanEnvironment();

        Assert.False(env.ContainsKey("LAUNCHER_WHATEVER"));
    }

    [Fact]
    public void StripVariableEntries_RemovesAndKeepsOrder()
    {
        // the list separator is the platform's own (':' on unix, ';' on windows)
        var sep = Path.PathSeparator;
        Assert.Equal(string.Join(sep, "a", "c"), JavaUtils.StripVariableEntries("PATH", string.Join(sep, "a", "b", "c"), "b"));
        Assert.Equal(string.Join(sep, "a", "b", "c"), JavaUtils.StripVariableEntries("PATH", string.Join(sep, "a", "b", "c"), "missing"));
        Assert.Equal("b", JavaUtils.StripVariableEntries("PATH", string.Join(sep, "a", "b", "a"), string.Join(sep, "a", "a")));
    }
}
