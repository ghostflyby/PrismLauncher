// SPDX-License-Identifier: GPL-3.0-only
// Read-side behavior pins for the Meta metadata parsers (launcher/meta/JsonFormat port) on the
// JsonNode model: format-version number semantics (no TryGetValue<T>), Require/Ensure message
// parity, require-set round trips and the requireIsArrayOf<QJsonObject> quirks.

using System.Text.Json.Nodes;
using FlintLauncher.Core.Json;
using FlintLauncher.Core.Meta;
using Xunit;
using FluentIndex = FlintLauncher.Core.Meta.Index;

namespace FlintLauncher.Tests;

public class MetaJsonFormatTests
{
    private static JsonNode Parse(string json) => JsonNode.Parse(json)!;

    private static void ParseAndDiscard(string json)
    {
        var metaVersion = new MetaVersion("uid", "1.0");
        MetaJsonFormat.ParseVersion((JsonObject)Parse(json), metaVersion);
    }

    // ------------------------------------------------------------- format version

    [Theory]
    [InlineData("""{"formatVersion":1}""", MetadataVersion.InitialRelease)]
    [InlineData("""{"formatVersion":0}""", MetadataVersion.InitialRelease)]
    [InlineData("""{"formatVersion":1.0}""", MetadataVersion.InitialRelease)] // integral double, like Qt isDouble
    [InlineData("""{"formatVersion":2}""", MetadataVersion.Invalid)]
    [InlineData("""{"formatVersion":"1"}""", MetadataVersion.Invalid)] // string numbers are NOT coerced
    [InlineData("""{"formatVersion":null}""", MetadataVersion.Invalid)]
    [InlineData("""{"formatVersion":true}""", MetadataVersion.Invalid)]
    [InlineData("""{"formatVersion":{}}""", MetadataVersion.Invalid)]
    [InlineData("""{}""", MetadataVersion.Invalid)]
    public void ParseFormatVersion_NumberSemantics(string json, MetadataVersion expected)
    {
        Assert.Equal(expected, MetaJsonFormat.ParseFormatVersion(Parse(json)));
    }

    [Fact]
    public void ParseFormatVersion_NotRequired_FallsBackToInitialRelease()
    {
        Assert.Equal(MetadataVersion.InitialRelease, MetaJsonFormat.ParseFormatVersion(Parse("{}"), required: false));
        // a present but non-numeric formatVersion is still invalid when not required
        Assert.Equal(MetadataVersion.Invalid, MetaJsonFormat.ParseFormatVersion(Parse("""{"formatVersion":"x"}"""), required: false));
    }

    [Fact]
    public void ParseFormatVersion_CreatedPrimitives_ReadLikeParsedNumbers()
    {
        // a member built in code (JsonValue.Create) must not be rejected like the old
        // TryGetValue<double> read did for created (non parsed-backed) values
        var obj = new JsonObject { ["formatVersion"] = JsonValue.Create(1) };
        Assert.Equal(MetadataVersion.InitialRelease, MetaJsonFormat.ParseFormatVersion(obj));
    }

    // ------------------------------------------------------------------ version

    [Fact]
    public void ParseVersion_ReadsCommonFields()
    {
        var obj = (JsonObject)Parse("""
                                    {
                                        "formatVersion": 1,
                                        "uid": "net.minecraft",
                                        "version": "1.12.2",
                                        "releaseTime": "2017-06-02T09:59:14+00:00",
                                        "type": "release",
                                        "recommended": true,
                                        "volatile": true,
                                        "requires": [{"uid": "org.lwjgl3", "equals": "3.2.2"}],
                                        "conflicts": [{"uid": "net.fabricmc", "suggests": "9.9"}]
                                    }
                                    """);
        var metaVersion = new MetaVersion("net.minecraft", "1.12.2");
        MetaJsonFormat.ParseVersion(obj, metaVersion);

        Assert.Equal("net.minecraft", metaVersion.Uid);
        Assert.Equal("1.12.2", metaVersion.Version);
        Assert.Equal("release", metaVersion.Type);
        Assert.Equal(new DateTimeOffset(2017, 6, 2, 9, 59, 14, TimeSpan.Zero).ToUnixTimeSeconds(), metaVersion.RawTime);
        // "recommended" is only applied for versions parsed through a version list
        // (MetaVersion.MergeFromList gates on providesRecommendations) — not via ParseVersion
        Assert.False(metaVersion.IsRecommended);

        var requires = metaVersion.RequiredSet;
        var lwjgl = requires.Single(r => r.Uid == "org.lwjgl3");
        Assert.Equal("3.2.2", lwjgl.EqualsVersion);
        // the version file body (OneSixVersionFormat, own batch) is parsed through as data
        Assert.NotNull(metaVersion.Data);
    }

    [Fact]
    public void ParseVersion_RequiredFieldErrors_FailTheDocument()
    {
        // the typed header reader collapses a missing required member onto the require text
        var missingUid = (JsonObject)Parse("""{"formatVersion":1,"releaseTime":"2017-06-02T09:59:14+00:00"}""");
        Assert.Equal("'uid' is not a string",
            Assert.Throws<MetaParseException>(() => MetaJsonFormat.ParseVersion(missingUid, new MetaVersion("x", "y"))).Message);

        var missingVersion = (JsonObject)Parse("""{"formatVersion":1,"uid":"net.minecraft"}""");
        Assert.Equal("'version' is not a string",
            Assert.Throws<MetaParseException>(() => MetaJsonFormat.ParseVersion(missingVersion, new MetaVersion("x", "y"))).Message);

        var missingReleaseTime = (JsonObject)Parse("""{"formatVersion":1,"uid":"net.minecraft","version":"1.12.2"}""");
        Assert.Equal("'releaseTime' is not a string",
            Assert.Throws<MetaParseException>(() => MetaJsonFormat.ParseVersion(missingReleaseTime, new MetaVersion("x", "y"))).Message);
    }

    [Fact]
    public void ParseVersion_KindErrors_FailTheDocument()
    {
        var badUid = (JsonObject)Parse("""{"formatVersion":1,"uid":3}""");
        var e = Assert.Throws<MetaParseException>(() => MetaJsonFormat.ParseVersion(badUid, new MetaVersion("x", "y")));
        Assert.StartsWith("The metadata version document is malformed:", e.Message);

        var badVersion = (JsonObject)Parse("""{"formatVersion":1,"uid":"u","releaseTime":"2017-06-02T09:59:14+00:00"}""");
        badVersion["version"] = 3;
        Assert.StartsWith("The metadata version document is malformed:",
            Assert.Throws<MetaParseException>(() => MetaJsonFormat.ParseVersion(badVersion, new MetaVersion("x", "y"))).Message);
    }

    [Theory]
    [InlineData("""{"uid":"u"}""")]
    [InlineData("""{"formatVersion":2,"uid":"u"}""")]
    public void ParseVersion_RejectsUnknownFormatVersion(string json)
    {
        var e = Assert.Throws<MetaParseException>(() => ParseAndDiscard(json));
        Assert.Equal("Unknown format version!", e.Message);
    }

    // ------------------------------------------------------------------ requires

    [Fact]
    public void ParseRequires_SerializeParseRoundTrip()
    {
        var original = new SortedSet<Require>
        {
            new Require { Uid = "org.lwjgl3", EqualsVersion = "3.2.2" },
            new Require { Uid = "net.fabricmc.intermediary", Suggests = "1.18" },
        };
        var obj = new JsonObject();
        MetaJsonFormat.SerializeRequires(obj, original);
        var parsed = new SortedSet<Require>();
        MetaJsonFormat.ParseRequires(obj, parsed);

        Assert.Equal(2, parsed.Count);
        Assert.True(parsed.SetEquals(original));
        Assert.True(original.Single(r => r.Uid == "org.lwjgl3").DeepEquals(
            parsed.Single(r => r.Uid == "org.lwjgl3")));
    }

    [Fact]
    public void ParseRequires_CustomKeyName_MatchesSerialize()
    {
        // the PackProfile cachedRequires/cachedConflicts path
        var original = new SortedSet<Require> { new Require { Uid = "a.b.c" } };
        var obj = new JsonObject();
        MetaJsonFormat.SerializeRequires(obj, original, "cachedRequires");
        var parsed = new SortedSet<Require>();
        MetaJsonFormat.ParseRequires(obj, parsed, "cachedRequires");
        Assert.True(parsed.SetEquals(original));

        // a different key name finds nothing
        var other = new SortedSet<Require>();
        MetaJsonFormat.ParseRequires(obj, other);
        Assert.Empty(other);
    }

    [Fact]
    public void ParseRequires_ErrorMessages_MatchTheFamily()
    {
        var ptr = new SortedSet<Require>();

        var nonArray = (JsonObject)Parse("""{"requires":"nope"}""");
        Assert.Equal("'requires' is not an array",
            Assert.Throws<JsonParseException>(() => MetaJsonFormat.ParseRequires(nonArray, ptr)).Message);

        var nonObjectEntry = (JsonObject)Parse("""{"requires":[1]}""");
        Assert.Equal("Value is not an object",
            Assert.Throws<JsonParseException>(() => MetaJsonFormat.ParseRequires(nonObjectEntry, ptr)).Message);

        var missingUid = (JsonObject)Parse("""{"requires":[{}]}""");
        Assert.Equal("'uid's parent does not contain 'uid'",
            Assert.Throws<JsonParseException>(() => MetaJsonFormat.ParseRequires(missingUid, ptr)).Message);

        var nullEntry = (JsonObject)Parse("""{"requires":[null]}""");
        Assert.Equal("Value is not an object",
            Assert.Throws<JsonParseException>(() => MetaJsonFormat.ParseRequires(nullEntry, ptr)).Message);
    }

    // ------------------------------------------------------------------ index

    [Fact]
    public void ParseIndex_MergesPackages()
    {
        var obj = (JsonObject)Parse("""
                                    {
                                        "formatVersion": 1,
                                        "packages": [
                                            {"uid": "net.minecraft", "name": "Minecraft"},
                                            {"uid": "org.lwjgl3", "name": "LWJGL 3", "sha256": "aa"}
                                        ]
                                    }
                                    """);
        var index = new FluentIndex();
        MetaJsonFormat.ParseIndex(obj, index);

        Assert.Equal(2, index.Lists().Count);
        Assert.True(index.HasUid("net.minecraft"));
        Assert.Equal("Minecraft", index.Get("net.minecraft").Name);
        Assert.Equal("LWJGL 3", index.Get("org.lwjgl3").Name);
    }

    [Fact]
    public void ParseIndex_DocumentFailures_FailTheDocument()
    {
        // the typed readers fail the DOCUMENT on malformed members (the empty-list and
        // per-key quirks of the node readers are gone — a malformed meta payload fails the
        // entity, which just refetches)
        var index = new FluentIndex();

        // missing member
        var noPackages = (JsonObject)Parse("""{"formatVersion":1}""");
        Assert.Equal("The metadata document has no 'packages' member.",
            Assert.Throws<MetaParseException>(() => MetaJsonFormat.ParseIndex(noPackages, index)).Message);

        // a non-array / wrong-kind member fails the binding
        var scalarPackages = (JsonObject)Parse("""{"formatVersion":1,"packages":3}""");
        var malformed = Assert.Throws<MetaParseException>(() => MetaJsonFormat.ParseIndex(scalarPackages, index));
        Assert.StartsWith("The metadata index document is malformed:", malformed.Message);

        // so does a non-object element
        var nonObjectEntry = (JsonObject)Parse("""{"formatVersion":1,"packages":[1]}""");
        Assert.Throws<MetaParseException>(() => MetaJsonFormat.ParseIndex(nonObjectEntry, index));

        // an entry without its required uid fails the entry
        var missingUid = (JsonObject)Parse("""{"formatVersion":1,"packages":[{}]}""");
        Assert.Equal("'uid' is not a string",
            Assert.Throws<MetaParseException>(() => MetaJsonFormat.ParseIndex(missingUid, index)).Message);

        // unknown format version is a MetaParseException
        var badFormat = (JsonObject)Parse("""{"formatVersion":2,"packages":[]}""");
        Assert.Equal("Unknown format version!",
            Assert.Throws<MetaParseException>(() => MetaJsonFormat.ParseIndex(badFormat, index)).Message);
    }

    // ------------------------------------------------------------- version list

    [Fact]
    public void ParseVersionList_ReadsVersionsWithRecommendations()
    {
        var obj = (JsonObject)Parse("""
                                    {
                                        "formatVersion": 1,
                                        "uid": "net.minecraft",
                                        "name": "Minecraft",
                                        "versions": [
                                            {"uid": "net.minecraft", "version": "1.12.2", "releaseTime": "2017-06-02T09:59:14+00:00", "recommended": true, "type": "release"},
                                            {"uid": "net.minecraft", "version": "1.13", "releaseTime": "2018-07-18T13:28:51+00:00"}
                                        ]
                                    }
                                    """);
        var list = new VersionList("net.minecraft");
        MetaJsonFormat.ParseVersionList(obj, list);

        Assert.Equal("Minecraft", list.Name);
        Assert.Equal(2, list.Versions().Count);
        var recommended = list.GetVersion("1.12.2");
        Assert.Equal("release", recommended.Type);
        // versions coming from a list provide recommendations (SetProvidesRecommendations)
        Assert.True(recommended.IsRecommended);
        Assert.False(list.GetVersion("1.13").IsRecommended);
    }

    [Fact]
    public void ParseVersionList_MissingVersions_FailsTheDocument()
    {
        var list = new VersionList("net.minecraft");
        var obj = (JsonObject)Parse("""{"formatVersion":1,"uid":"net.minecraft"}""");
        Assert.Equal("The metadata document has no 'versions' member.",
            Assert.Throws<MetaParseException>(() => MetaJsonFormat.ParseVersionList(obj, list)).Message);
    }
}
