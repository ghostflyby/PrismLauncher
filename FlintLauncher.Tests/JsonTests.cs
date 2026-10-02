// SPDX-License-Identifier: GPL-3.0-only
// Focused tests for the port of launcher/Json.{h,cpp} (require/ensure helpers,
// string-list/map settings helpers, parseUntilGarbage). All readers run on the JsonNode
// single-DOM read model — the former JsonDocument/JsonElement dual track is retired.
using System.Text.Json.Nodes;
using FlintLauncher.Core.Json;
using Xunit;

namespace FlintLauncher.Tests;

public class JsonTests
{
    private static JsonNode? Parse(string json)
    {
        return JsonNode.Parse(json);
    }

    private static JsonObject ParseObject(string json)
    {
        return JsonNode.Parse(json)!.AsObject();
    }

    // -------------------------------------------------------------- require*

    [Fact]
    public void RequireNode_Throws_On_Invalid_Json()
    {
        var e = Assert.Throws<JsonParseException>(() => JsonUtils.RequireNode("{invalid", "Doc"));
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
    public void StringListOfMembers_MirrorsRequireIsArrayOf()
    {
        // the requireIsArrayOf semantics now live in ToStringList (the parent+key overload
        // went with the element track): a non-string element yields [] instead of throwing
        Assert.Equal(["a", "b"], JsonUtils.ToStringList("""["a","b"]"""));
        Assert.Empty(JsonUtils.ToStringList("""["a",1]"""));
    }

    // --------------------------------------------------------------- ensure*

    [Fact]
    public void Ensure_Returns_Defaults()
    {
        var value = ParseObject("""{"s":"x","b":true,"n":1.9}""");
        Assert.Equal("x", JsonUtils.EnsureString(value["s"]));
        Assert.Equal("", JsonUtils.EnsureString(value["n"]));
        Assert.Equal("def", JsonUtils.EnsureString(value["n"], "def"));
        Assert.True(JsonUtils.EnsureBoolean(value["b"]));
        Assert.False(JsonUtils.EnsureBoolean(value["s"]));
        Assert.Equal(1.9, JsonUtils.EnsureDouble(value["n"]));
        Assert.Equal(0.0, JsonUtils.EnsureDouble(value["s"]));
        Assert.Equal(1, JsonUtils.EnsureInteger(value["n"])); // truncates like Qt
        Assert.Equal(7, JsonUtils.EnsureInteger(value["s"], 7));
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
    public void ParseNodeUntilGarbage()
    {
        var doc = JsonUtils.ParseNodeUntilGarbage("""{"a":1} trailing garbage""", out var garbage);
        Assert.NotNull(doc);
        Assert.Equal(" trailing garbage", garbage);
        Assert.Equal(1, JsonUtils.RequireInteger(doc!.AsObject()["a"]));

        var strict = JsonUtils.ParseNodeUntilGarbage("""{"a":1}""", out var noGarbage);
        Assert.NotNull(strict);
        Assert.Null(noGarbage);

        var broken = JsonUtils.ParseNodeUntilGarbage("{invalid", out _);
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
