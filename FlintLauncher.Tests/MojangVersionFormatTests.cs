// SPDX-License-Identifier: GPL-3.0-only
// Port of tests/MojangVersionFormat_test.cpp: byte-level JSON round trips. The C++ test
// compares QJsonDocument::toJson() of the parsed fixture against toJson() of the re-serialized
// model; here both sides go through QtJson.ToIndentedString (QJsonObject sorts keys, Qt writes
// 4-space indentation).
using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.Minecraft;
using Xunit;

namespace FlintLauncher.Tests;

public class MojangVersionFormatTests
{
    private static string TestData(string name) => Path.Combine(AppContext.BaseDirectory, "TestData", "Libraries", name);

    private static JsonDocument ReadJson(string path)
    {
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static string ToQtJson(JsonNode node)
    {
        return QtJson.ToIndentedString(node);
    }

    private static void RoundTrip(string fixture)
    {
        using var doc = ReadJson(TestData(fixture));
        var vfile = MojangVersionFormat.VersionFileFromJson(doc, fixture);
        var doc2 = MojangVersionFormat.VersionFileToJson(vfile);

        Assert.Equal(ToQtJson(doc.RootElement.ToJsonObject()), ToQtJson(doc2));
    }

    [Fact]
    public void Test_Through_Simple()
    {
        RoundTrip("1.9-simple.json");
    }

    [Fact]
    public void Test_Through()
    {
        RoundTrip("1.9.json");
    }
}
