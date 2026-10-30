// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Tests for the JVM discovery (macOS-first, matching the host machine), the process environment
// cleaner. Discovery runs on the JdkFind facilities (LocateAsync primary + FromExecutable
// fallback) and both layers are tested here with synthetic homes; real-machine assertions are
// guarded like RealDataSmokeTests.

using FlintLauncher.Core;
using FlintLauncher.Core.Java;
using JdkFind;
using JdkFind.Providers;
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

    private static bool MacHasJdk()
    {
        if (OperatingSystem.IsMacOS() && File.Exists("/usr/libexec/java_home"))
        {
            return true;
        }

        return Directory.Exists("/Library/Java/JavaVirtualMachines") &&
               Directory.GetDirectories("/Library/Java/JavaVirtualMachines").Length > 0;
    }

    // ------------------------------------------------------------------ discovery

    [Fact]
    public void FindJavaPaths_AlwaysContainsTheDefaultJava()
    {
        var candidates = JavaUtils.FindJavaPaths();

        Assert.NotEmpty(candidates);
        Assert.Contains(JavaUtils.GetDefaultJava().Path, candidates);
        // upstream deduplicates while preserving order
        Assert.Equal(candidates.Count, candidates.Distinct().Count());
    }

    [Fact]
    public void FindJavaPaths_OnMacOS_FindsLibraryJvmBundles()
    {
        if (!OperatingSystem.IsMacOS() || !MacHasJdk())
        {
            return; // skip-style early return: no JDK on this machine (CI)
        }

        var candidates = JavaUtils.FindJavaPaths();

        Assert.True(candidates.Count >= 1);
        // the /Library/Java/JavaVirtualMachines scan is the core of the macOS branch: at least
        // one installed JVM bundle must produce a candidate java binary
        var fromLibrary = candidates.Where(c => c.StartsWith("/Library/Java/JavaVirtualMachines/", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(fromLibrary);
        Assert.All(fromLibrary, path => Assert.EndsWith("/bin/java", path));
        Assert.Contains(fromLibrary, File.Exists);
    }

    [Fact]
    public void FindJavaPaths_OnMacOS_DropsAppleJava6Candidates()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var candidates = JavaUtils.FindJavaPaths();

        // the Apple Java 6 era candidates died with the Java <8 support floor
        Assert.DoesNotContain("/Applications/Xcode.app/Contents/Applications/Application Loader.app/Contents/MacOS/itms/java/bin/java", candidates);
        Assert.DoesNotContain("/Library/Internet Plug-Ins/JavaAppletPlugin.plugin/Contents/Home/bin/java", candidates);
        Assert.DoesNotContain("/System/Library/Frameworks/JavaVM.framework/Versions/Current/Commands/java", candidates);
        Assert.DoesNotContain(candidates, c => c.Contains("Contents/Commands/java", StringComparison.Ordinal));
    }

    [Fact]
    public void FindJavaPaths_PicksUpEnvProvidedPaths()
    {
        var extra = "/opt/flint-test-jdk/bin/java";
        SetEnv($"{FlintConfig.Default.LauncherAppBinaryEnvName}_JAVA_PATHS", extra);

        var candidates = JavaUtils.FindJavaPaths();

        Assert.Contains(extra, candidates);
    }

    [Fact]
    public void GetDefaultJava_MatchesUpstream()
    {
        var java = JavaUtils.GetDefaultJava();

        Assert.Equal("java", java.Id.ToString());
        Assert.Equal("unknown", java.Arch);
        Assert.Equal(OperatingSystem.IsWindows() ? "javaw" : "java", java.Path);
        Assert.Equal(java.Path, JavaUtils.MakeJavaPtr(java.Path, "java").Path);
    }

    [Fact]
    public async Task DetectAsync_MapsAndDeduplicatesTheLayers()
    {
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
        Assert.True(primary.Valid);
        Assert.Equal("21.0.5", primary.Version.Original);
        Assert.Equal(21, primary.LanguageVersion);
        // the probe cannot execute the empty stub java, so metadata degrades to the release
        // file; OS_ARCH is absent from the fixture
        Assert.Equal("unknown", primary.Architecture);

        // release-backed but unable to run: listed, marked invalid
        var dead = Assert.Single(candidates, c => c.Path == broken);
        Assert.False(dead.Valid);
        Assert.Equal("unknown", dead.Version.Original);
        Assert.Null(dead.LanguageVersion);

        // nothing behind it: dropped instead of listed
        Assert.DoesNotContain(candidates, c => c.Path == Path.Combine(root, "nonexistent", "java"));
    }

    [Fact]
    public async Task DetectAsync_ExposesReleaseFileArchitecture()
    {
        // OS_ARCH in the release file feeds the same architecture slot the checker's
        // os.arch used to — JavaInstall.Is64Bit derives from it downstream
        var root = TempRoot();
        var home = Path.Combine(root, "Contents", "Home");
        Directory.CreateDirectory(Path.Combine(home, "bin"));
        await File.WriteAllTextAsync(Path.Combine(home, "bin",
            OperatingSystem.IsWindows() ? "java.exe" : "java"), "");
        await File.WriteAllTextAsync(Path.Combine(home, "release"), "JAVA_VERSION=\"17.0.2\"\nOS_ARCH=\"aarch64\"\n");

        var candidates = await JavaDetection.DetectAsync(
            providers: [new MacOsJvmProvider([root]), new JavaHomeJvmProvider(root)]);

        // the legacy/launcher fallback contributes its own (real-machine) candidates, so
        // scope the assertion to the synthetic home
        var candidate = Assert.Single(candidates,
            c => c.Path == Path.Combine(home, "bin", JavaUtils.JavaExecutable));
        Assert.True(candidate.Valid);
        Assert.Equal("aarch64", candidate.Architecture);
        Assert.True(JavaUtils.Is64BitArchitecture(candidate.Architecture));
        Assert.Equal(17, candidate.LanguageVersion);
    }

    [Fact]
    public async Task JavaListLoadTask_FillsTheListFromDetection()
    {
        var root = TempRoot();
        var home = Path.Combine(root, "jdk-21.0.5");
        Directory.CreateDirectory(Path.Combine(home, "bin"));
        await File.WriteAllTextAsync(Path.Combine(home, "bin",
            OperatingSystem.IsWindows() ? "java.exe" : "java"), "");
        await File.WriteAllTextAsync(Path.Combine(home, "release"), "JAVA_VERSION=\"21.0.5\"\nOS_ARCH=\"x86_64\"\n");

        IJvmProvider provider = OperatingSystem.IsWindows()
            ? new WindowsProgramFilesJvmProvider([root])
            : OperatingSystem.IsMacOS()
                ? new MacOsJvmProvider([root])
                : new UnixJvmProvider(root);

        var list = new JavaInstallList();
        await JavaListLoadTask.RunAsync(list, TestContext.Current.CancellationToken, providers: [provider]);

        var install = Assert.Single(list.Versions(), j => j.Path == Path.Combine(home, "bin", JavaUtils.JavaExecutable));
        Assert.Equal("21.0.5", install.Id.ToString());
        Assert.Equal(21, install.LanguageVersion);
        Assert.Equal("x86_64", install.Arch);
        Assert.True(install.Is64Bit);
    }

    [Fact]
    public async Task DetectAsync_IncludesHomebrewKegHomes()
    {
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
            c.Path == Path.Combine(home, "bin", JavaUtils.JavaExecutable) && c.Valid);
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

        Assert.False(env.Contains("JAVA_HOME"));
        Assert.False(env.Contains("_JAVA_OPTIONS"));
        Assert.False(env.Contains("JAVA_TOOL_OPTIONS"));
        Assert.True(env.Contains("FLINT_TEST_KEEP_ME"));
        Assert.Equal("kept", env.Value("FLINT_TEST_KEEP_ME"));
    }

    [Fact]
    public void CleanEnvironment_DropsLauncherPrefixedMirrors()
    {
        SetEnv("LAUNCHER_WHATEVER", "dropped");

        var env = JavaUtils.CleanEnvironment();

        Assert.False(env.Contains("LAUNCHER_WHATEVER"));
    }

    [Fact]
    public void StripVariableEntries_RemovesAndKeepsOrder()
    {
        Assert.Equal("a:c", JavaUtils.StripVariableEntries("PATH", "a:b:c", "b"));
        Assert.Equal("a:b:c", JavaUtils.StripVariableEntries("PATH", "a:b:c", "missing"));
        Assert.Equal("b", JavaUtils.StripVariableEntries("PATH", "a:b:a", "a:a"));
    }
}
