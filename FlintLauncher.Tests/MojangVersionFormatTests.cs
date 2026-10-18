// SPDX-License-Identifier: GPL-3.0-only
// Port of tests/MojangVersionFormat_test.cpp: byte-level JSON round trips. The C++ test
// compares QJsonDocument::toJson() of the parsed fixture against toJson() of the re-serialized
// model; here both sides go through QtJson.ToIndentedString (QJsonObject sorts keys, Qt writes
// 4-space indentation). Fixtures parse straight onto the JsonNode read model.

using System.Text.Json.Nodes;
using FlintLauncher.Core.Json;
using FlintLauncher.Core.Minecraft;
using Xunit;

namespace FlintLauncher.Tests;

public class MojangVersionFormatTests
{
    private static string TestData(string name) => Path.Combine(AppContext.BaseDirectory, "TestData", "Libraries", name);

    private static string ToQtJson(JsonNode node)
    {
        return QtJson.ToIndentedString(node);
    }

    private static void RoundTrip(string fixture)
    {
        var root = JsonNode.Parse(File.ReadAllText(TestData(fixture)));
        var vfile = MojangVersionFormat.VersionFileFromJson(root, fixture);
        var doc2 = MojangVersionFormat.VersionFileToJson(vfile);

        Assert.Equal(ToQtJson(root!), ToQtJson(doc2));
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

    // ----------------------------------------------------- document entry guards

    [Theory]
    [InlineData("null", " is empty or null")]
    [InlineData("[1,2]", " is not an object")]
    [InlineData("\"text\"", " is not an object")]
    public void VersionFileFromJson_Rejects_NonObjectRoots(string json, string expectedSuffix)
    {
        var e = Assert.Throws<JsonParseException>(() => MojangVersionFormat.VersionFileFromJson(JsonNode.Parse(json), "test.json"));
        Assert.Equal("test.json" + expectedSuffix, e.Message);
    }

    // --------------------------------------------------- version file read behavior

    [Fact]
    public void ReadVersionProperties_ReadsAllSections()
    {
        var root = JsonNode.Parse("""
                                  {
                                      "id": "1.12.2",
                                      "mainClass": "net.minecraft.client.main.Main",
                                      "minecraftArguments": "--username x",
                                      "type": "release",
                                      "assets": "1.12",
                                      "assetIndex": {"id": "1.12", "sha1": "abc", "size": 100, "totalSize": 200, "url": "https://example.com/idx"},
                                      "releaseTime": "2017-06-02T09:59:14+00:00",
                                      "time": "2017-06-02T10:00:00+00:00",
                                      "minimumLauncherVersion": 18,
                                      "compatibleJavaMajors": [8, 9],
                                      "compatibleJavaName": "java-runtime",
                                      "downloads": {
                                          "client": {"path": "c.jar", "sha1": "d1", "size": 1000, "url": "https://example.com/client.jar"}
                                      }
                                  }
                                  """)!;
        var vfile = new VersionFile();
        MojangVersionFormat.ReadVersionProperties(root, vfile);

        Assert.Equal("1.12.2", vfile.MinecraftVersion);
        Assert.Equal("net.minecraft.client.main.Main", vfile.MainClass);
        Assert.Equal("--username x", vfile.MinecraftArguments);
        Assert.Equal("release", vfile.Type);
        Assert.Equal("1.12", vfile.Assets);

        Assert.NotNull(vfile.MojangAssetIndex);
        var assetIndex = vfile.MojangAssetIndex!;
        Assert.Equal("1.12", assetIndex.Id);
        Assert.Equal(200, assetIndex.TotalSize);
        Assert.Equal("abc", assetIndex.Sha1);

        Assert.Equal(new DateTimeOffset(2017, 6, 2, 9, 59, 14, TimeSpan.Zero), vfile.ReleaseTime);
        Assert.Equal(new DateTimeOffset(2017, 6, 2, 10, 0, 0, TimeSpan.Zero), vfile.UpdateTime);

        Assert.Equal(18, vfile.MinimumLauncherVersion);
        Assert.Equal([8, 9], vfile.CompatibleJavaMajors);
        Assert.Equal("java-runtime", vfile.CompatibleJavaName);

        var client = vfile.MojangDownloads["client"];
        Assert.Equal("c.jar", client.Path);
        Assert.Equal("d1", client.Sha1);
        Assert.Equal(1000, client.Size);
        Assert.Equal("https://example.com/client.jar", client.Url);
    }

    [Fact]
    public void ReadVersionProperties_MinimumLauncherVersionAboveSupported_Warns()
    {
        var root = JsonNode.Parse("""{"minimumLauncherVersion": 99}""")!;
        var vfile = new VersionFile();
        MojangVersionFormat.ReadVersionProperties(root, vfile);

        Assert.Equal(99, vfile.MinimumLauncherVersion);
        Assert.Contains(vfile.GetProblems(), p => p.Severity == ProblemSeverity.Warning &&
                                                  p.Description.Contains("minimumLauncherVersion"));
    }

    [Fact]
    public void ReadVersionProperties_ErrorMessages_FailTheDocument()
    {
        // the typed readers fail the whole document on malformed members, wrapped in the
        // "{what}: Error parsing JSON: ..." family like the former RequireDocument reads;
        // a MISSING required member keeps the require-check collapse ("'key' is not a ...")
        Assert.StartsWith("version file: Error parsing JSON: ", ReadError("""{"assetIndex": 3}"""));
        Assert.Equal("'sha1' is not a string", ReadError("""{"downloads": {"client": {"url": "u"}}}"""));
        Assert.StartsWith("version file: Error parsing JSON: ", ReadError("""{"downloads": {"client": {"sha1": "s", "size": 1.5, "url": "u"}}}"""));
        Assert.StartsWith("version file: Error parsing JSON: ", ReadError("""{"downloads": {"client": 3}}"""));
        Assert.StartsWith("version file: Error parsing JSON: ", ReadError("""{"minimumLauncherVersion": 1.5}"""));
        Assert.StartsWith("version file: Error parsing JSON: ", ReadError("""{"id": 3}"""));
        Assert.StartsWith("version file: Error parsing JSON: ", ReadError("""{"compatibleJavaMajors": 8}"""));
        Assert.StartsWith("version file: Error parsing JSON: ", ReadError("""{"compatibleJavaMajors": ["8"]}"""));
    }

    private static string ReadError(string json)
    {
        return Assert.Throws<JsonParseException>(() => MojangVersionFormat.ReadVersionProperties(JsonNode.Parse(json)!, new VersionFile())).Message;
    }

    // ------------------------------------------------------- library read behavior

    [Fact]
    public void LibraryFromJson_ReadsAllSections()
    {
        var libObj = JsonNode.Parse("""
                                    {
                                        "name": "org.lwjgl.lwjgl:lwjgl:3.3.1",
                                        "url": "https://libraries.minecraft.net/",
                                        "extract": {"exclude": ["META-INF/", "windows/x86"]},
                                        "natives": {"linux": "natives-linux", "osx": 3},
                                        "rules": [
                                            {"action": "allow"},
                                            {"action": "disallow", "os": {"name": "windows"}}
                                        ],
                                        "downloads": {
                                            "artifact": {"sha1": "art", "size": 10, "url": "https://example.com/a.jar"},
                                            "classifiers": {"natives-linux": {"sha1": "cls", "size": 20, "url": "https://example.com/n.jar"}}
                                        }
                                    }
                                    """)!;
        var problems = new ProblemContainer();
        var lib = MojangVersionFormat.LibraryFromJson(problems, libObj, "lib.json");

        Assert.Empty(problems.GetProblems());
        Assert.Equal("org.lwjgl.lwjgl:lwjgl:3.3.1", lib.Name.Serialize());
        Assert.Equal("https://libraries.minecraft.net/", lib.RepositoryUrl);
        Assert.True(lib.HasExcludes);
        Assert.Equal(["META-INF/", "windows/x86"], lib.ExtractExcludes);
        Assert.Equal("natives-linux", lib.NativeClassifiers["linux"]);
        // non-string native classifiers fall back to "" (QJsonValue::toString default)
        Assert.Equal("", lib.NativeClassifiers["osx"]);
        Assert.True(lib.ApplyRules);
        Assert.Equal(2, lib.Rules.Count);

        Assert.NotNull(lib.MojangDownloads);
        var downloads = lib.MojangDownloads!;
        Assert.Equal("art", downloads.Artifact!.Sha1);
        Assert.Equal("cls", downloads.Classifiers["natives-linux"].Sha1);
    }

    [Fact]
    public void LibraryFromJson_MissingName_KeepsTheUpstreamTypo()
    {
        var e = Assert.Throws<JsonParseException>(() => MojangVersionFormat.LibraryFromJson(new ProblemContainer(), JsonNode.Parse("{}")!, "lib.json"));
        // upstream typo preserved: no space between the filename and "contains"
        Assert.Equal("lib.jsoncontains a library that doesn't have a 'name' field", e.Message);
    }

    [Fact]
    public void LibraryFromJson_BrokenName_AddsProblem()
    {
        var problems = new ProblemContainer();
        MojangVersionFormat.LibraryFromJson(problems, JsonNode.Parse("""{"name": "not-a-spec"}""")!, "lib.json");
        Assert.Contains(problems.GetProblems(), p => p.Severity == ProblemSeverity.Error &&
                                                     p.Description.Contains("name is broken"));
    }

    [Fact]
    public void LibraryFromJson_RulesErrors_FailTheDocument()
    {
        Assert.StartsWith("lib.json: Error parsing JSON: ", LibraryError("""{"name": "a:b:c", "rules": 3}"""));
        Assert.StartsWith("lib.json: Error parsing JSON: ", LibraryError("""{"name": "a:b:c", "rules": [1]}"""));
        Assert.Equal("Value is not an object", LibraryError("""{"name": "a:b:c", "rules": [null]}"""));
    }

    [Fact]
    public void LibraryFromJson_ExtractExcludeErrors_FailTheDocument()
    {
        Assert.StartsWith("lib.json: Error parsing JSON: ", LibraryError("""{"name": "a:b:c", "extract": {"exclude": 3}}"""));
        Assert.StartsWith("lib.json: Error parsing JSON: ", LibraryError("""{"name": "a:b:c", "extract": {"exclude": [1]}}"""));
    }

    private static string LibraryError(string json)
    {
        return Assert.Throws<JsonParseException>(() => MojangVersionFormat.LibraryFromJson(new ProblemContainer(), JsonNode.Parse(json)!, "lib.json")).Message;
    }
}
