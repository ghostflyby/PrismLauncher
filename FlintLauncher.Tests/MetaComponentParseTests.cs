// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022 Sefa Eyeoglu <contact@scrumplex.net>
//
// This file incorporates work covered by the following copyright and
// permission notice:
//
//     Copyright 2013-2021 MultiMC Contributors
//
//     Licensed under the Apache License, Version 2.0 (the "License");
//     you may not use this file except in compliance with the License.
//     You may obtain a copy of the License at
//
//         http://www.apache.org/licenses/LICENSE-2.0
//
//     Unless required by applicable law or agreed to in writing, software
//     distributed under the License is distributed on an "AS IS" BASIS,
//     WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//     See the License for the specific language governing permissions and
//     limitations under the License.
//
// Port of tests/MetaComponentParse_test.cpp. Upstream this exercises
// DataPackUtils::processComponent (the JSON chat component renderer) on fixtures under
// tests/testdata/MetaComponentParse — the ported renderer lives in
// FlintLauncher.Core/Minecraft/Resources (moved there from Minecraft/).

using System.Text.Json;
using System.Text.Json.Nodes;
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

        var obj = JsonNode.Parse(data)?.AsObject();
        Assert.NotNull(obj);

        Assert.True(obj.ContainsKey("description"));
        Assert.True(obj.ContainsKey("expected_output"));

        var expected = obj["expected_output"]?.GetValue<string>() ?? "";

        // the renderer consumes the typed ChatComponentJson wire now
        var description = obj["description"];
        Assert.NotNull(description);
        var component = JsonSerializer.Deserialize(
            description.ToJsonString(), DataPackJsonContext.Default.ChatComponentJson);
        var processed = DataPackUtils.ProcessComponent(component);

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
