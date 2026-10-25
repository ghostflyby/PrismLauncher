// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Regression tests for the subagent-review fixes on the typed JSON readers: every fix
// pins input compatibility that the first typed conversion had silently narrowed.
//
//  - ATL Configs.json required members (loader.type / configs.sha1 / decompFile) fail the
//    manifest again instead of degrading;
//  - QtCoercing* converters coerce JSON null members (HandleNull) — the FTB case lives in
//    FtbTests, the metacache/ATL cases ride on the same converters;
//  - collection/dictionary null elements and values surface as "Value is not an object"
//    or fall back to the former defaults instead of NullReferenceException;
//  - Modrinth hash lookups and version lists keep the former per-entry tolerance
//    and the sha512->sha1 fallback walks past a JSON null hash (in ModrinthApiTests);
//  - the OneSix removed-feature problems probe QJsonObject::contains semantics, so a
//    JSON null member still counts as present.

using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.Launch;
using FlintLauncher.Core.Meta;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.ModPlatform.AtLauncher;
using FlintLauncher.Core.ModPlatform.Technic;
using FlintLauncher.ExternalApi;
using TruePath;
using Xunit;
using MetaIndex = FlintLauncher.Core.Meta.Index;

namespace FlintLauncher.Tests;

public class JsonNullToleranceTests
{
    private static JsonObject Parse(string json) => (JsonObject)(JsonNode.Parse(json) ?? throw new InvalidOperationException());

    // ------------------------------------------------------------------ ATL

    [Fact]
    public void Atl_Configs_MissingSha1_FailsTheManifest()
    {
        var e = Assert.Throws<JsonException>(() => AtLauncherApi.LoadPackVersion(
            """{"version":"1","minecraft":"1.12.2","noConfigs":false,"configs":{"filesize":10}}"""));
        Assert.Equal("'sha1' is not a string", e.Message);
    }

    [Fact]
    public void Atl_Loader_MissingType_FailsTheManifest()
    {
        var e = Assert.Throws<JsonException>(() => AtLauncherApi.LoadPackVersion(
            """{"version":"1","minecraft":"1.12.2","noConfigs":true,"loader":{"choose":false,"metadata":{"latest":true}}}"""));
        Assert.Equal("'type' is not a string", e.Message);
    }

    [Fact]
    public void Atl_ModDecomp_WithoutDecompFile_FailsTheManifest()
    {
        var e = Assert.Throws<JsonException>(() => AtLauncherApi.LoadPackVersion(
            """
            {"version":"1","minecraft":"1.12.2","noConfigs":true,
             "mods":[{"name":"D","version":"1","url":"u","file":"f.jar","md5":"m",
                      "download":"direct","type":"decomp","decompType":"decomp"}]}
            """));
        Assert.Equal("'decompFile' is not a string", e.Message);
    }

    // ------------------------------------------------------------------ Solder

    [Fact]
    public void Solder_Build_NullMinecraft_FailsWithTheRequireText()
    {
        var e = Assert.Throws<JsonException>(() => TechnicApi.LoadSolderPackBuild(
            """{"minecraft":null,"mods":[]}"""));
        Assert.Equal("'minecraft' is not a string", e.Message);
    }

    [Fact]
    public void Solder_Build_NullModElement_FailsAsAnObjectCheck()
    {
        var e = Assert.Throws<JsonException>(() => TechnicApi.LoadSolderPackBuild(
            """{"minecraft":"1.0","mods":[null]}"""));
        Assert.Equal("Value is not an object", e.Message);
    }

    // ------------------------------------------------------------------ meta

    [Fact]
    public void Meta_Index_NullPackageElement_FailsTheDocument()
    {
        var e = Assert.Throws<MetaParseException>(() => MetaJsonFormat.ParseIndex(
            Parse("""{"formatVersion":1,"packages":[null]}"""), new MetaIndex()));
        Assert.Equal("Document is not an object", e.Message);
    }

    [Fact]
    public void Meta_VersionList_NullVersionElement_FailsTheDocument()
    {
        var e = Assert.Throws<MetaParseException>(() => MetaJsonFormat.ParseVersionList(
            Parse("""{"formatVersion":1,"uid":"u","versions":[null]}"""), new VersionList("u")));
        Assert.Equal("Document is not an object", e.Message);
    }

    // ---------------------------------------------------------- OneSix

    [Fact]
    public void OneSix_Libraries_NullElement_FailsAsAnObjectCheck()
    {
        var e = Assert.Throws<JsonException>(() => OneSixVersionFormat.VersionFileFromJson(
            Parse("""{"formatVersion":1,"uid":"u","version":"1","libraries":[null]}"""), "t.json", false));
        Assert.Equal("Value is not an object", e.Message);
    }

    [Fact]
    public void OneSix_RemovedFeature_NullMember_StillCountsAsPresent()
    {
        // QJsonObject::contains semantics: "tweakers": null is present
        var vfile = OneSixVersionFormat.VersionFileFromJson(
            Parse("""{"formatVersion":1,"uid":"u","version":"1","tweakers":null}"""), "t.json", false);
        Assert.Contains(vfile.GetProblems(), p => p.Severity == ProblemSeverity.Error &&
                                                  p.Description.Contains("'tweakers'"));
    }

    // ------------------------------------------------- Mojang download map

    [Fact]
    public void Mojang_Downloads_NullEntry_FailsAsAnObjectCheck()
    {
        var downloadsNode = JsonNode.Parse("""{"downloads": {"client": null}}""");
        Assert.NotNull(downloadsNode);
        var e = Assert.Throws<JsonException>(() =>
            MojangVersionFormat.ReadVersionProperties(downloadsNode, new VersionFile()));
        Assert.Equal("Value is not an object", e.Message);
    }

    // ------------------------------------------------------- assets index

    [Fact]
    public void AssetsIndex_NullObjectEntry_KeepsAnEmptyAssetObject()
    {
        var path = Path.Combine(Path.GetTempPath(), $"flint-nullasset-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{"objects": {"x": null}}""");
        try
        {
            var index = new AssetsIndex();
            Assert.True(AssetsUtils.LoadAssetsIndexJson("test", new AbsolutePath(path), index));
            Assert.True(index.Objects.ContainsKey("x"));
            Assert.Equal("", index.Objects["x"].Hash);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // --------------------------------------------------- Modrinth (direct decode)

    [Fact]
    public void Modrinth_Lookup_MalformedEntry_FailsTheDocument()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("""
                                                                     {
                                                                         "good": {"project_id": "p", "id": "v", "date_published": "2024-01-01",
                                                                                  "game_versions": ["1.20.1"], "loaders": ["fabric"], "name": "V",
                                                                                  "version_number": "1", "version_type": "release",
                                                                                  "files": [{"filename": "a.jar", "primary": true, "hashes": {"sha1": "h"},
                                                                                             "url": "https://x/a.jar", "size": 1}]},
                                                                         "bad": 3
                                                                     }
                                                                     """, ModrinthJsonContext.Default.DictionaryStringModrinthVersion));
    }

    [Fact]
    public void Modrinth_VersionList_MalformedEntry_FailsTheDocument()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("""
                                                                      [
                                                                          {"project_id": "p", "id": "v", "date_published": "2024-01-01",
                                                                           "game_versions": ["1.20.1"], "loaders": ["fabric"], "name": "V",
                                                                           "version_number": "1", "version_type": "release",
                                                                           "files": [{"filename": "a.jar", "primary": true, "hashes": {"sha1": "h"},
                                                                                      "url": "https://x/a.jar", "size": 1}]},
                                                                          5
                                                                      ]
                                                                      """, ModrinthJsonContext.Default.ListModrinthVersion));
    }

    [Fact]
    public void Modrinth_NullValueForRequiredMember_FailsTheDocument()
    {
        // the spec types these members as non-nullable strings: an explicit null fails the
        // decode (RespectNullableAnnotations) instead of landing as a null reference
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("""
            {
                "project_id": "p", "id": "v1", "date_published": "2024-01-01T00:00:00Z",
                "game_versions": ["1.20.1"], "loaders": ["fabric"],
                "name": "V", "version_number": null, "version_type": "release", "files": []
            }
            """, ModrinthJsonContext.Default.ModrinthVersion));
    }

    [Fact]
    public void Modrinth_NullMemberInsideListElement_FailsTheDocument()
    {
        // element-level nullability is not emitted by the source generator, but the
        // element's own required members still are
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("""
            {"hits": [{"project_id": "p", "slug": null, "title": "T", "author": "a", "downloads": 1}]}
            """, ModrinthJsonContext.Default.ModrinthSearchResults));
    }
}
