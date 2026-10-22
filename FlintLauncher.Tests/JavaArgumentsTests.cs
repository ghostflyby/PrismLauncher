// SPDX-License-Identifier: GPL-3.0-only
// Tests for MinecraftInstance::javaArguments(): Xms/Xmx orderings, FirstThreadOnMacOS on
// macOS, the PermGen hack for old Java and the --add-opens online-fixes rule.

using Xunit;

namespace FlintLauncher.Tests;

public class JavaArgumentsTests
{
    [Fact]
    public async Task AlwaysStartsWithUserLanguage_AndContainsMemoryFlags()
    {
        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance();
        var args = instance.JavaArguments();

        Assert.Equal("-Duser.language=en", args[0]);
        Assert.Contains("-Xms512m", args);
        Assert.Contains("-Xmx4096m", args);
        // min (512) < max (4096): Xms first with min, then Xmx with max
        Assert.True(args.IndexOf("-Xms512m") < args.IndexOf("-Xmx4096m"));
    }

    [Fact]
    public async Task MemoryFlags_Reversed_WhenMinAboveMax()
    {
        using var fixture = new LaunchFixture();
        fixture.Global.StubSet("MinMemAlloc", 4096);
        fixture.Global.StubSet("MaxMemAlloc", 512);
        var instance = await fixture.CreateInstance();

        var args = instance.JavaArguments();

        // min >= max: upstream emits -Xms<max> then -Xmx<min>
        Assert.True(args.IndexOf("-Xms512m") < args.IndexOf("-Xmx4096m"));
    }

    [Fact]
    public async Task FirstThreadOnMacOS_OnlyOnMac_TraitPresent()
    {
        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance(); // default patch has the trait
        Assert.Contains("FirstThreadOnMacOS", instance.Traits());

        var args = instance.JavaArguments();

        if (OperatingSystem.IsMacOS())
        {
            Assert.Contains("-XstartOnFirstThread", args);
            Assert.Contains("-Xdock:icon=icon.png", args);
            Assert.Contains("-Xdock:name=\"Flint Launcher: Unnamed Instance\"", args);
        }
        else
        {
            Assert.DoesNotContain("-XstartOnFirstThread", args);
            Assert.DoesNotContain("-Xdock:icon=icon.png", args);
        }
    }

    [Fact]
    public async Task FirstThreadOnMacOS_Skipped_WithoutTrait()
    {
        using var fixture = new LaunchFixture();
        var patch = LaunchFixture.DefaultPatchJson.Replace("\"+traits\": [\"FirstThreadOnMacOS\"]", "\"+traits\": []");
        var instance = await fixture.CreateInstance(patch);

        var args = instance.JavaArguments();

        Assert.DoesNotContain("-XstartOnFirstThread", args);
    }

    [Fact]
    public async Task PermGen_NeverAdded_AfterTheJava8Floor()
    {
        // the Java <8 support floor removed the PermGen accommodation: no version string
        // produces -XX:PermSize anymore, whatever the PermGen setting says
        using var fixture = new LaunchFixture();
        fixture.SeedJavaDetection("java", 0, languageVersion: 7, version: "1.7.0_80");
        var instance = await fixture.CreateInstance();

        Assert.DoesNotContain(instance.JavaArguments(), a => a.StartsWith("-XX:PermSize"));

        fixture.SeedJavaDetection("java", 0, languageVersion: 17, version: "17.0.2");
        var modern = await fixture.CreateInstance();
        Assert.DoesNotContain(modern.JavaArguments(), a => a.StartsWith("-XX:PermSize"));
    }

    [Fact]
    public async Task AddOpensRule_OnlyWithModularJava_AndOnlineFixes()
    {
        using var fixture = new LaunchFixture();
        var patch = LaunchFixture.DefaultPatchJson.Replace("\"+traits\": [\"FirstThreadOnMacOS\"]",
            "\"+traits\": [\"FirstThreadOnMacOS\", \"legacyServices\"]");
        fixture.Global.StubSet("OnlineFixes", true);
        fixture.SeedJavaDetection("java", 0, languageVersion: 17, version: "17.0.2");
        var instance = await fixture.CreateInstance(patch);
        Assert.True(instance.ShouldApplyOnlineFixes());

        var args = instance.JavaArguments();
        var index = args.IndexOf("--add-opens");
        Assert.True(index >= 0);
        Assert.Equal("java.base/java.net=ALL-UNNAMED", args[index + 1]);

        // without the OnlineFixes setting the rule is absent
        fixture.Global.StubSet("OnlineFixes", false);
        var without = await fixture.CreateInstance(patch);
        Assert.DoesNotContain(without.JavaArguments(), a => a == "--add-opens");
    }

    [Fact]
    public async Task InstJavaArgsVariable_JoinsArguments()
    {
        using var fixture = new LaunchFixture();
        var instance = await fixture.CreateInstance();

        var variables = instance.GetVariables();

        Assert.Equal(string.Join(" ", instance.JavaArguments()), variables["INST_JAVA_ARGS"]);
        Assert.Equal(instance.Name(), variables["INST_NAME"]);
        Assert.Equal("1", variables["NO_COLOR"]);
        Assert.Equal(instance.InstanceRoot.Value, variables["INST_DIR"]);
        Assert.Equal(instance.GameRoot.Value, variables["INST_MC_DIR"]);
    }
}
