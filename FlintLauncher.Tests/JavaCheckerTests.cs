// SPDX-License-Identifier: GPL-3.0-only
// Tests for the JavaCheck output parsing, the missing-jar failure path, the jars/ lookup of
// JavaCheckJar (docs/tools/newlaunch-build.sh produces the jar) and the JavaCommon JVM-args
// sanity checks. Running a real java binary requires the JavaCheck.jar; when none is
// installed the tests assert the graceful failure paths.
using FlintLauncher.Core.Java;
using FlintLauncher.Core.Tasks;
using Xunit;

namespace FlintLauncher.Tests;

public class JavaCheckerTests
{
    // ------------------------------------------------------------------ output parsing

    [Fact]
    public void ParseCheckOutput_ReadsTheCheckedProperties()
    {
        var stdout = "os.arch=x86_64\njava.version=17.0.9\njava.vendor=Temurin\n";

        var results = JavaChecker.ParseCheckOutput(stdout);

        Assert.Equal("x86_64", results["os.arch"]);
        Assert.Equal("17.0.9", results["java.version"]);
        Assert.Equal("Temurin", results["java.vendor"]);
    }

    [Fact]
    public void ParseCheckOutput_SkipsGarbageAndMalformedLines()
    {
        var stdout = string.Join('\n',
            "some launcher banner",              // no '=' at all
            "prefix/bedrock/strata/mount os.arch=aarch64",  // GH-4125 bedrock garbage
            "=novalue",                          // empty key
            "nokey=",                            // empty value
            "a==b",                              // Qt SkipEmptyParts: parses as a=b
            "key=with=parts",                    // three parts -> dropped
            "java.version=21");                  // valid

        var results = JavaChecker.ParseCheckOutput(stdout);

        // "a==b" survives the Qt SkipEmptyParts split as ["a", "b"] -> a=b
        Assert.Equal(2, results.Count);
        Assert.Equal("b", results["a"]);
        Assert.Equal("21", results["java.version"]);
    }

    [Fact]
    public void ParseCheckOutput_TrimsWhitespace()
    {
        var results = JavaChecker.ParseCheckOutput("  os.arch = amd64  \n");

        // the line is trimmed, but the '=' split keeps interior spaces like the C++ original
        Assert.Equal(" amd64", results["os.arch "]);
    }

    // --------------------------------------------------- missing-jar / dead-binary paths

    [Fact]
    public async Task JavaChecker_WithoutJar_ReportsErroredResultAndSucceeds()
    {
        if (JavaUtils.GetJavaCheckPath().Length != 0)
        {
            // a JavaCheck.jar is installed on this machine — the stub path is not reachable
            return;
        }

        JavaChecker.Result? result = null;
        var checker = new JavaChecker("/nonexistent/java", "", 0, 0, 0, 7);
        checker.CheckFinished += r => result = r;

        await checker.StartAsync();

        Assert.Equal(TaskState.Succeeded, checker.State);
        Assert.NotNull(result);
        Assert.Equal(7, result!.Id);
        Assert.Equal(JavaChecker.Validity.Errored, result.Validity);
        Assert.Contains("Java checker library could not be found", result.ErrorLog);
        Assert.Equal("/nonexistent/java", result.Path);
    }

    [Fact]
    public void JavaCheckJar_ReportsClearMarker_WhenNoJarIsInstalled()
    {
        if (JavaUtils.GetJavaCheckPath().Length != 0)
        {
            // a JavaCheck.jar is installed on this machine — the missing-jar path is not reachable
            return;
        }

        var extracted = JavaCheckJar.TryExtractTo(Path.Combine(Path.GetTempPath(), "flint-tests-javacheck.jar"), out var error);

        Assert.False(extracted);
        Assert.Equal(JavaCheckJar.StubReason, error);
        Assert.Contains("JavaCheck.jar could not be found", JavaCheckJar.StubReason);
    }

    [Fact]
    public void JavaCheckJar_IsFoundInTheLauncherJarsLayout_AndCopiedToTheTarget()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            return; // the launcher-root lookup layout mirrors the unix install tree
        }
        var root = Path.Combine(Path.GetTempPath(), $"flint-javacheck-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "jars"));
        var savedRoot = JavaUtils.LauncherRootPath;
        try
        {
            var jar = Path.Combine(root, "jars", JavaCheckJar.JarFileName);
            File.WriteAllText(jar, "dummy jar bytes");
            JavaUtils.LauncherRootPath = root;

            Assert.Equal(jar, JavaCheckJar.GetInstalledJarPath());

            var target = Path.Combine(root, "extracted", JavaCheckJar.JarFileName);
            Assert.True(JavaCheckJar.TryExtractTo(target, out var error));
            Assert.Equal("", error);
            Assert.Equal("dummy jar bytes", File.ReadAllText(target));
        }
        finally
        {
            JavaUtils.LauncherRootPath = savedRoot ?? AppContext.BaseDirectory;
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task JavaChecker_WithNonexistentBinary_FailsGracefully_WhenJarExists()
    {
        if (JavaUtils.GetJavaCheckPath().Length == 0)
        {
            // no checker jar on this machine; the test above covers the stub path
            return;
        }

        JavaChecker.Result? result = null;
        var checker = new JavaChecker("/nonexistent/java", "", 0, 0, 0, 0);
        checker.CheckFinished += r => result = r;

        await checker.StartAsync();

        Assert.Equal(TaskState.Succeeded, checker.State);
        Assert.NotNull(result);
        Assert.Equal(JavaChecker.Validity.Errored, result!.Validity);
    }

    // ------------------------------------------------------------------ JavaCommon

    [Theory]
    [InlineData("-Xmx1024m", false)]
    [InlineData("-Xms256m", false)]
    [InlineData("-XX:PermSize=256m", false)]
    [InlineData("-XX-MaxHeapSize=1024m", false)]
    [InlineData("-XX:InitialHeapSize=1024m", false)]
    [InlineData("-version:17+", false)]
    [InlineData("-Dsome.property=value -XX:+UseG1GC", true)]
    [InlineData("", true)]
    public void CheckJvmArgs_RejectsMemoryAndVersionFlags(string args, bool acceptable)
    {
        Assert.Equal(acceptable, JavaCommon.CheckJvmArgs(args));
        if (!acceptable)
        {
            Assert.NotNull(JavaCommon.JvmArgsWarning(args));
        }
    }

    [Fact]
    public async Task TestCheck_WithoutJar_ReportsCheckerNotFound()
    {
        var testCheck = new JavaCommon.TestCheck();
        var outcome = await testCheck.RunAsync("/nonexistent/java", "-Dtest=1", 512, 1024, 64);

        if (JavaUtils.GetJavaCheckPath().Length == 0)
        {
            Assert.Equal(JavaCommon.TestOutcome.CheckerNotFound, outcome);
        }
        else
        {
            // with a jar installed the bare run fails for the fake binary instead
            Assert.Equal(JavaCommon.TestOutcome.BinaryBad, outcome);
        }
    }

    [Fact]
    public async Task TestCheck_RejectsBadArgumentsBeforeAnyCheck()
    {
        var testCheck = new JavaCommon.TestCheck();
        var outcome = await testCheck.RunAsync("/nonexistent/java", "-Xmx99999m", 512, 1024, 64);

        Assert.Equal(JavaCommon.TestOutcome.ArgsRejected, outcome);
    }
}
