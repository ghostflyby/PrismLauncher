// SPDX-License-Identifier: GPL-3.0-only
// Port of tests/MetaComponentParse_test.cpp. Upstream this exercises
// DataPackUtils::processComponent (the JSON chat component renderer) on fixtures under
// tests/testdata/MetaComponentParse — the ported renderer lives in
// FlintLauncher.Core/Minecraft/Resources (moved there from Minecraft/ in P3-b).

using System.Text.Json.Nodes;
using FlintLauncher.Core.Java;
using FlintLauncher.Core.Minecraft.Resources;
using Xunit;

namespace FlintLauncher.Tests;

public class MetaComponentParseTests
{
    private static string TestDataDir() => Path.Combine(AppContext.BaseDirectory, "TestData", "MetaComponentParse");

    private static void DoTest(string name)
    {
        var compRp = Path.Combine(TestDataDir(), name);

        var data = File.ReadAllText(compRp);

        var obj = JsonNode.Parse(data)!.AsObject();

        Assert.True(obj.Contains("description"));
        Assert.True(obj.Contains("expected_output"));

        var expected = obj.StringOr("expected_output");

        var processed = DataPackUtils.ProcessComponent(obj["description"]);

        Assert.Equal(expected, processed);
    }

    [Fact]
    public void Test_ParseComponentBasic() => DoTest("component_basic.json");

    [Fact]
    public void Test_ParseComponentWithFormat() => DoTest("component_with_format.json");

    [Fact]
    public void Test_ParseComponentWithExtra() => DoTest("component_with_extra.json");

    [Fact]
    public void Test_ParseComponentWithLink() => DoTest("component_with_link.json");

    [Fact]
    public void Test_ParseComponentWithMixed() => DoTest("component_with_mixed.json");
}
