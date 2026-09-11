// SPDX-License-Identifier: GPL-3.0-only
// Focused tests for the port of launcher/Json.{h,cpp} (require/ensure helpers,
// string-list/map settings helpers, parseUntilGarbage).
using System.Text.Json;
using System.Text.Json.Nodes;
using Flint.Core.Json;
using Xunit;

namespace Flint.Tests;

public class JsonTests
{
    private static JsonElement Parse(string json)
    {
        return JsonDocument.Parse(json).RootElement;
    }

    private static JsonObject ParseObject(string json)
    {
        return JsonNode.Parse(json)!.AsObject();
    }

    // -------------------------------------------------------------- require*

    [Fact]
    public void RequireDocument_Throws_On_Invalid_Json()
    {
        var e = Assert.Throws<JsonParseException>(() => JsonUtils.RequireDocument("{invalid", "Doc"));
        Assert.StartsWith("Doc: Error parsing JSON:", e.Message);
    }

    [Fact]
    public void RequireObject_Array_TypeChecks()
    {
        Assert.Throws<JsonParseException>(() => JsonUtils.RequireObject(Parse("[1]"), "Document"));
        Assert.Throws<JsonParseException>(() => JsonUtils.RequireArray(Parse("{}"), "Document"));
    }

    [Theory]
    [InlineData("42", "val is not a string")]
    [InlineData("\"x\"", "val is not a bool")]
    [InlineData("\"x\"", "val is not a double")]
    public void Require_Scalar_Mismatch_Throws(string json, string expectedMessage)
    {
        var value = Parse(json);
        if (expectedMessage.EndsWith("is not a string"))
        {
            Assert.Equal(expectedMessage, Assert.Throws<JsonParseException>(() => JsonUtils.RequireString(value, "val")).Message);
        }
        else if (expectedMessage.EndsWith("is not a bool"))
        {
            Assert.Equal(expectedMessage, Assert.Throws<JsonParseException>(() => JsonUtils.RequireBoolean(value, "val")).Message);
        }
        else
        {
            Assert.Equal(expectedMessage, Assert.Throws<JsonParseException>(() => JsonUtils.RequireDouble(value, "val")).Message);
        }
    }

    [Fact]
    public void Require_Booleans_And_Strings()
    {
        Assert.True(JsonUtils.RequireBoolean(Parse("true"), "val"));
        Assert.Equal("hello", JsonUtils.RequireString(Parse("\"hello\""), "val"));
    }

    [Fact]
    public void RequireDouble_And_Integer()
    {
        Assert.Equal(1.5, JsonUtils.RequireDouble(Parse("1.5")));
        // Qt stores every number as double, so integer literals pass through RequireDouble
        Assert.Equal(1.0, JsonUtils.RequireDouble(Parse("1")));
        Assert.Equal(3, JsonUtils.RequireInteger(Parse("3.0")));
        Assert.Equal(1, JsonUtils.RequireInteger(Parse("1")));
        // the `what` label is used verbatim in the message
        var e = Assert.Throws<JsonParseException>(() => JsonUtils.RequireInteger(Parse("1.5"), "val"));
        Assert.Equal("val is not an integer", e.Message);
        Assert.Throws<JsonParseException>(() => JsonUtils.RequireDouble(Parse("\"x\"")));
    }

    [Fact]
    public void Require_ParentKey_Placeholder()
    {
        var parent = ParseObject("""{"a":"b"}""");
        Assert.Equal("b", JsonUtils.RequireString(parent, "a"));
        var e = Assert.Throws<JsonParseException>(() => JsonUtils.RequireString(parent, "c"));
        Assert.Equal("'c's parent does not contain 'c'", e.Message);
        // a custom `what` without the placeholder is used verbatim, like upstream
        Assert.Equal("Xs parent does not contain X",
            Assert.Throws<JsonParseException>(() => JsonUtils.RequireString(parent, "c", "X")).Message);
    }

    [Fact]
    public void RequireArrayOfStrings()
    {
        Assert.Equal(["a", "b"],
            JsonUtils.RequireArrayOfStrings(ParseObject("""{"list":["a","b"]}"""), "list"));
        Assert.Throws<JsonParseException>(() =>
            JsonUtils.RequireArrayOfStrings(ParseObject("""{"list":["a",1]}"""), "list"));
    }

    [Fact]
    public void RequireByteArray_HexRoundTrip()
    {
        var data = new byte[] { 0x00, 0xDE, 0xAD, 0xBE, 0xEF, 0xFF };
        var hex = Convert.ToHexString(data).ToLowerInvariant();
        Assert.Equal(data, JsonUtils.RequireByteArray(Parse($"\"{hex}\"")));

        // Qt's fromHex silently stops at invalid characters (yields an empty buffer here)
        Assert.Empty(JsonUtils.RequireByteArray(Parse("\"zz\"")));
        Assert.Throws<JsonParseException>(() => JsonUtils.RequireByteArray(Parse("\"\u0100\""))); // not Latin1
        Assert.Throws<JsonParseException>(() => JsonUtils.RequireByteArray(Parse("42")));
    }

    [Fact]
    public void RequireDateTime_IsoFormat()
    {
        Assert.Equal(new DateTime(2023, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            JsonUtils.RequireDateTime(Parse("\"2023-01-02T03:04:05Z\"")));
        Assert.Equal(new DateTime(2023, 1, 2), JsonUtils.RequireDateTime(Parse("\"2023-01-02\"")));
        Assert.Throws<JsonParseException>(() => JsonUtils.RequireDateTime(Parse("\"not a date\"")));
    }

    [Fact]
    public void RequireUrl()
    {
        Assert.Null(JsonUtils.RequireUrl(Parse("\"\""))); // default-constructed QUrl
        Assert.Equal(new Uri("https://docs.prismlauncher.org/x"),
            JsonUtils.RequireUrl(Parse("\"https://docs.prismlauncher.org/x\"")));
        Assert.Throws<JsonParseException>(() => JsonUtils.RequireUrl(Parse("\"not a url\"")));
    }

    [Fact]
    public void RequireUuid_RoundTripCheck()
    {
        var canonical = "de1813ee-7f63-4b63-bb8e-9fa0b8b5c1a8";
        Assert.Equal(Guid.Parse(canonical), JsonUtils.RequireUuid(Parse($"\"{canonical}\"")));
        // anything that does not round-trip to the exact same text is rejected, like QUuid
        Assert.Throws<JsonParseException>(() =>
            JsonUtils.RequireUuid(Parse("\"DE1813EE-7F63-4B63-BB8E-9FA0B8B5C1A8\"")));
        Assert.Throws<JsonParseException>(() =>
            JsonUtils.RequireUuid(Parse("\"{de1813ee-7f63-4b63-bb8e-9fa0b8b5c1a8}\"")));
        Assert.Throws<JsonParseException>(() => JsonUtils.RequireUuid(Parse("\"nope\"")));
    }

    // --------------------------------------------------------------- ensure*

    [Fact]
    public void Ensure_Returns_Defaults()
    {
        var value = Parse("""{"s":"x","b":true,"n":1.9}""");
        Assert.Equal("x", JsonUtils.EnsureString(value.GetProperty("s")));
        Assert.Equal("", JsonUtils.EnsureString(value.GetProperty("n")));
        Assert.Equal("def", JsonUtils.EnsureString(value.GetProperty("n"), "def"));
        Assert.True(JsonUtils.EnsureBoolean(value.GetProperty("b")));
        Assert.False(JsonUtils.EnsureBoolean(value.GetProperty("s")));
        Assert.Equal(1.9, JsonUtils.EnsureDouble(value.GetProperty("n")));
        Assert.Equal(0.0, JsonUtils.EnsureDouble(value.GetProperty("s")));
        Assert.Equal(1, JsonUtils.EnsureInteger(value.GetProperty("n"))); // truncates like Qt
        Assert.Equal(7, JsonUtils.EnsureInteger(value.GetProperty("s"), 7));
    }

    // -------------------------------------------------------- settings helpers

    [Fact]
    public void StringList_RoundTrip()
    {
        Assert.Equal("""["a","b"]""", JsonUtils.FromStringList(["a", "b"]));
        Assert.Equal(["a", "b"], JsonUtils.ToStringList("""["a","b"]"""));
        Assert.Equal([""], JsonUtils.ToStringList("""[""]"""));
        // any parse or element error yields an empty list
        Assert.Empty(JsonUtils.ToStringList("not json"));
        Assert.Empty(JsonUtils.ToStringList("""{"a":1}"""));
        Assert.Empty(JsonUtils.ToStringList("""["a",1]"""));
    }

    [Fact]
    public void Map_RoundTrip()
    {
        Assert.Equal("""{"a":"b"}""", JsonUtils.FromMap(new Dictionary<string, string> { ["a"] = "b" }));
        var map = JsonUtils.ToMap("""{"a":"b","n":42,"t":true}""");
        Assert.Equal(3, map.Count);
        Assert.Equal("b", map["a"]);
        Assert.Equal("42", map["n"]);
        Assert.Equal("true", map["t"]);
        Assert.Empty(JsonUtils.ToMap("not json"));
        Assert.Empty(JsonUtils.ToMap("[1]"));
    }

    // ----------------------------------------------------- documents & garbage

    [Fact]
    public void Write_ToText_RoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"flint-json-{Guid.NewGuid():N}.json");
        try
        {
            using (var doc = JsonDocument.Parse("""{"key":"value","n":3}"""))
            {
                JsonUtils.Write(doc.RootElement, path);
                // toText is compact
                Assert.Equal("""{"key":"value","n":3}""", System.Text.Encoding.UTF8.GetString(JsonUtils.ToText(doc.RootElement)));
            }
            using var read = JsonUtils.RequireDocumentFromFile(path);
            // requireString(document.object(), "key") analog
            Assert.Equal("value", JsonUtils.RequireString(ParseObject(File.ReadAllText(path)), "key"));
            Assert.Equal("value", JsonUtils.RequireString(read.RootElement.GetProperty("key")));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ParseUntilGarbage()
    {
        using var doc = JsonUtils.ParseUntilGarbage("""{"a":1} trailing garbage""", out var garbage);
        Assert.NotNull(doc);
        Assert.Equal(" trailing garbage", garbage);
        Assert.Equal(1, JsonUtils.RequireInteger(doc!.RootElement.GetProperty("a")));

        using var strict = JsonUtils.ParseUntilGarbage("""{"a":1}""", out var noGarbage);
        Assert.NotNull(strict);
        Assert.Null(noGarbage);

        using var broken = JsonUtils.ParseUntilGarbage("{invalid", out _);
        Assert.Null(broken);
    }

    [Fact]
    public void WriteString_SkipsEmpty()
    {
        var obj = new JsonObject();
        JsonUtils.WriteString(obj, "keep", "v");
        JsonUtils.WriteString(obj, "skip", "");
        Assert.Equal("v", obj["keep"]!.GetValue<string>());
        Assert.Null(obj["skip"]);

        JsonUtils.WriteStringList(obj, "list", ["a", "b"]);
        JsonUtils.WriteStringList(obj, "empty", []);
        Assert.Equal(2, obj["list"]!.AsArray().Count);
        Assert.Null(obj["empty"]);
    }
}
