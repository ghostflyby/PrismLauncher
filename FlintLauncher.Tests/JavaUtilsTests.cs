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
    public void FindJavaPaths_OnMacOS_ProbesBundleLocations()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var candidates = JavaUtils.FindJavaPaths();

        // fixed candidates from the C++ macOS branch
        Assert.Contains("/Library/Internet Plug-Ins/JavaAppletPlugin.plugin/Contents/Home/bin/java", candidates);
        Assert.Contains("/System/Library/Frameworks/JavaVM.framework/Versions/Current/Commands/java", candidates);
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
    public void FindJavaPaths_IncludesJdkFindProviderHomes()
    {
        // a synthetic plain-layout JDK home (bin/java + release): the JdkFind provider
        // contract (JavaHomeLayout.Probe) validates it, and the executable mapping turns
        // the home into a candidate path
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
                : new LinuxJvmProvider(root);

        var candidates = JavaUtils.FindJavaPathsFromProviders([provider]);

        // home/bin/<executable>, deduplicated across providers like the composition does
        Assert.Equal([Path.Combine(home, "bin", JavaUtils.JavaExecutable)],
            candidates);
    }

    [Fact]
    public void FindJavaPaths_MapsEveryProviderHomeToTheExecutable()
    {
        // two providers reporting the same home must not double-report the candidate
        var root = TempRoot();
        var home = Path.Combine(root, "Contents", "Home");
        Directory.CreateDirectory(Path.Combine(home, "bin"));
        File.WriteAllText(Path.Combine(home, "bin",
            OperatingSystem.IsWindows() ? "java.exe" : "java"), "");
        File.WriteAllText(Path.Combine(home, "release"), "JAVA_VERSION=\"17.0.2\"\n");

        var candidates = JavaUtils.FindJavaPathsFromProviders(
            [new MacOsJvmProvider([root]), new JavaHomeJvmProvider(root)]);

        Assert.Equal([Path.Combine(home, "bin", JavaUtils.JavaExecutable)], candidates);
    }

    [Fact]
    public void FindJavaPaths_OnMacOS_IncludesHomebrewKegHomes()
    {
        // the Homebrew keg layout (libexec/openjdk.jdk/Contents/Home) is a JdkFind-only
        // source: the legacy filesystem scan never looked under the brew prefix
        var keg = "/opt/homebrew/opt/openjdk";
        if (!OperatingSystem.IsMacOS() || !Directory.Exists(keg))
        {
            return;
        }

        var candidates = JavaUtils.FindJavaPaths();

        Assert.Contains(candidates, c =>
            c.StartsWith("/opt/homebrew/opt/openjdk", StringComparison.Ordinal) &&
            c.EndsWith("/bin/java", StringComparison.Ordinal) &&
            File.Exists(c));
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
