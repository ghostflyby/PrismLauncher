// SPDX-License-Identifier: GPL-3.0-only
// Focused tests for the port of launcher/Commandline.{h,cpp}.
using Flint.Core.Text;
using Xunit;

namespace Flint.Tests;

public class CommandlineTests
{
    [Fact]
    public void SplitArgs_Splits_Like_A_Shell()
    {
        Assert.Equal(["a", "b", "c"], Commandline.SplitArgs("a b c"));
        Assert.Equal(["one", "two three", "four five"],
            Commandline.SplitArgs("  one  \"two three\" 'four five'  "));
        // multiple spaces produce no empty args
        Assert.Empty(Commandline.SplitArgs("   "));
    }

    [Fact]
    public void SplitArgs_Handles_Escapes()
    {
        // backslash escapes the next char inside quotes
        Assert.Equal(["a\"b"], Commandline.SplitArgs("\"a\\\"b\""));
        Assert.Equal(["a\\b"], Commandline.SplitArgs("'a\\\\b'"));
        // unterminated quote still yields the collected text
        Assert.Equal(["abc"], Commandline.SplitArgs("\"abc"));
    }

    [Fact]
    public void ExpandVariables_Brace_And_Bare()
    {
        var dict = new Dictionary<string, string> { ["HOME"] = "/home/u", ["USER"] = "bob" };

        Assert.Equal("/home/u/bin", Commandline.ExpandVariables("${HOME}/bin", dict));
        Assert.Equal("hi bob!", Commandline.ExpandVariables("hi $USER!", dict));
        Assert.Equal("/home/u-bob-c", Commandline.ExpandVariables("$HOME-$USER-c", dict));
        // missing or empty variables stay untouched
        Assert.Equal("$NOPE/${ALSO_NOPE}", Commandline.ExpandVariables("$NOPE/${ALSO_NOPE}", dict));
        Assert.Equal("$USER", Commandline.ExpandVariables("$USER", new Dictionary<string, string> { ["USER"] = "" }));
        // a lone $ is preserved
        Assert.Equal("$ x", Commandline.ExpandVariables("$ x", dict));
    }

    [Fact]
    public void Process_Splits_Then_Expands()
    {
        var dict = new Dictionary<string, string> { ["JAVA_HOME"] = "/j", ["MEM"] = "2G" };
        Assert.Equal(["run", "/j/bin/java", "-Xmx2G"],
            Commandline.Process("run $JAVA_HOME/bin/java -Xmx${MEM}", dict));
        Assert.Equal(["a", "b c"], Commandline.Process("a \"b c\""));
    }
}
