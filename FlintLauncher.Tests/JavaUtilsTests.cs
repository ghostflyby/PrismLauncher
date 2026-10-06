// SPDX-License-Identifier: GPL-3.0-only
// Tests for the JVM discovery (macOS-first, matching the host machine), the process environment
// cleaner and the JavaCheck.jar lookup. The macOS discovery is a filesystem scan (upstream does
// not call java_home), so the "real JDK" test is guarded like RealDataSmokeTests: it only
// asserts when this machine actually has a JDK installed. Discovery is layered — the JdkFind
// provider suite on top of the C++-parity filesystem scan — and both layers are tested here.
using JdkFind;
using JdkFind.Providers;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.Java;
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
            catch (DirectoryNotFoundException)
            {
            }
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
        SetEnv($"{FlintLauncher.Core.FlintConfig.Default.LauncherAppBinaryEnvName}_JAVA_PATHS", extra);

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
        Assert.Equal(java.Path, JavaUtils.MakeJavaPtr(java.Path, "java", "unknown").Path);
    }

    [Fact]
    public async Task DetectAsync_MapsAndDeduplicatesTheLayers()
    {
        // a synthetic plain-layout JDK home reported by an injected provider is the primary
        // candidate; an env-provided copy of the same path collapses into it, and an env
        // path that leads nowhere survives as an invalid fallback candidate
        var root = TempRoot();
        var home = Path.Combine(root, "jdk-21.0.5");
        Directory.CreateDirectory(Path.Combine(home, "bin"));
        File.WriteAllText(Path.Combine(home, "bin",
            OperatingSystem.IsWindows() ? "java.exe" : "java"), "");
        File.WriteAllText(Path.Combine(home, "release"), "JAVA_VERSION=\"21.0.5\"\n");

        IJvmProvider provider = OperatingSystem.IsWindows()
            ? new WindowsProgramFilesJvmProvider([root])
            : OperatingSystem.IsMacOS()
                ? new MacOsJvmProvider([root])
                : new UnixJvmProvider(root);
        var executable = Path.Combine(home, "bin", JavaUtils.JavaExecutable);
        SetEnv($"{FlintLauncher.Core.FlintConfig.Default.LauncherAppBinaryEnvName}_JAVA_PATHS",
            string.Join(Path.PathSeparator, executable, Path.Combine(root, "nonexistent", "java")));

        var candidates = await JavaDetection.DetectAsync(providers: [provider]);

        var primary = Assert.Single(candidates, c => c.Path == executable);
        Assert.True(primary.Valid);
        Assert.Equal("21.0.5", primary.Version);
        // the probe cannot execute the empty stub java, so metadata degrades to the release
        // file; OS_ARCH is absent from the fixture
        Assert.Equal("unknown", primary.Architecture);

        var dead = Assert.Single(candidates, c => c.Path == Path.Combine(root, "nonexistent", "java"));
        Assert.False(dead.Valid);
        Assert.Equal("unknown", dead.Version);
    }

    [Fact]
    public async Task DetectAsync_ExposesReleaseFileArchitecture()
    {
        // OS_ARCH in the release file feeds the same architecture slot the checker's
        // os.arch used to — JavaInstall.Is64Bit derives from it downstream
        var root = TempRoot();
        var home = Path.Combine(root, "Contents", "Home");
        Directory.CreateDirectory(Path.Combine(home, "bin"));
        File.WriteAllText(Path.Combine(home, "bin",
            OperatingSystem.IsWindows() ? "java.exe" : "java"), "");
        File.WriteAllText(Path.Combine(home, "release"), "JAVA_VERSION=\"17.0.2\"\nOS_ARCH=\"aarch64\"\n");

        var candidates = await JavaDetection.DetectAsync(
            providers: [new MacOsJvmProvider([root]), new JavaHomeJvmProvider(root)]);

        // the legacy/launcher fallback contributes its own (real-machine) candidates, so
        // scope the assertion to the synthetic home
        var candidate = Assert.Single(candidates,
            c => c.Path == Path.Combine(home, "bin", JavaUtils.JavaExecutable));
        Assert.True(candidate.Valid);
        Assert.Equal("aarch64", candidate.Architecture);
        Assert.True(JavaChecker.Is64BitArchitecture(candidate.Architecture));
    }

    [Fact]
    public async Task JavaListLoadTask_FillsTheListFromDetection()
    {
        var root = TempRoot();
        var home = Path.Combine(root, "jdk-21.0.5");
        Directory.CreateDirectory(Path.Combine(home, "bin"));
        File.WriteAllText(Path.Combine(home, "bin",
            OperatingSystem.IsWindows() ? "java.exe" : "java"), "");
        File.WriteAllText(Path.Combine(home, "release"), "JAVA_VERSION=\"21.0.5\"\nOS_ARCH=\"x86_64\"\n");

        IJvmProvider provider = OperatingSystem.IsWindows()
            ? new WindowsProgramFilesJvmProvider([root])
            : OperatingSystem.IsMacOS()
                ? new MacOsJvmProvider([root])
                : new UnixJvmProvider(root);

        var list = new JavaInstallList();
        var task = new JavaListLoadTask(list, providers: [provider], maxConcurrent: 16);
        await task.StartAsync();

        var install = Assert.Single(list.Versions(), j => j.Path == Path.Combine(home, "bin", JavaUtils.JavaExecutable));
        Assert.Equal(21, install.Id.Major);
        Assert.Equal("x86_64", install.Arch);
        Assert.True(install.Is64Bit);
    }

    [Fact]
    public void JavaVersion_ParsesRuntimeVersionStrings()
    {
        // the detection layer feeds RuntimeVersion (java.runtime.version, which carries the
        // "+build" suffix) into JavaVersion — the same string shape the checker used to
        Assert.Equal(21, new JavaVersion("21.0.5+9-LTS").Major);
        Assert.Equal(8, new JavaVersion("1.8.0_402").Major);
    }

    [Fact]
    public async Task DetectAsync_IncludesHomebrewKegHomes()
    {
        // the Homebrew keg layout (libexec/openjdk.jdk/Contents/Home) is a JdkFind-only
        // source: the legacy filesystem scan never looked under the brew prefix
        var prefix = TempRoot();
        var home = Path.Combine(prefix, "opt", "openjdk21", "libexec", "openjdk.jdk", "Contents", "Home");
        Directory.CreateDirectory(Path.Combine(home, "bin"));
        File.WriteAllText(Path.Combine(home, "bin",
            OperatingSystem.IsWindows() ? "java.exe" : "java"), "");
        File.WriteAllText(Path.Combine(home, "release"), "JAVA_VERSION=\"21.0.5\"\n");

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

    // ---------------------------------------------------------- JavaCheck lookup

    [Fact]
    public void GetJavaCheckPath_IsEmptyOrAnExistingFile()
    {
        var path = JavaUtils.GetJavaCheckPath();

        Assert.True(path.Length == 0 || File.Exists(path),
            "GetJavaCheckPath must return either the P5-a stub empty string or a real jar path");
    }
}
