// SPDX-License-Identifier: GPL-3.0-only
// Number/kind semantics of the JsonNode-side Require*/Ensure* family: the node mirrors must
// agree with the JsonElement family for parsed documents — especially around numbers (int
// truncation, double rejection in RequireInteger, string numbers NOT coerced) because the
// node read model is what parsers migrate onto.
using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.Json;
using Xunit;

namespace FlintLauncher.Tests;

public class JsonUtilsNodeTests
{
    private static JsonNode? Parse(string json) => JsonNode.Parse(json);

    private static JsonNode Object(string json) =>
        Parse(json) ?? throw new Xunit.Sdk.XunitException("not an object");

    [Theory]
    [InlineData("""{"n":42}""", 42)]
    [InlineData("""{"n":-7}""", -7)]
    [InlineData("""{"n":1.0}""", 1)] // integral double parses and truncates to the same value
    public void RequireInteger_AcceptsIntegralNumbers(string json, int expected)
    {
        JsonNode obj = Object(json);
        Assert.Equal(expected, JsonUtils.RequireInteger(obj["n"], "n"));
    }

    [Fact]
    public void RequireInteger_RejectsFractionalAndNonNumbers()
    {
        JsonNode fractional = Object("""{"n":1.5}""");
        Assert.Throws<JsonParseException>(() => JsonUtils.RequireInteger(fractional["n"], "n"));

        JsonNode stringValue = Object("""{"n":"42"}""");
        Assert.Throws<JsonParseException>(() => JsonUtils.RequireInteger(stringValue["n"], "n"));

        JsonNode nullValue = Object("""{"n":null}""");
        Assert.Throws<JsonParseException>(() => JsonUtils.RequireInteger(nullValue["n"], "n"));

        JsonNode missing = Object("{}");
        Assert.Throws<JsonParseException>(() => JsonUtils.RequireInteger(missing["n"], "n"));
    }

    [Fact]
    public void EnsureInteger_TruncatesLikeTheElementFamily()
    {
        JsonNode obj = Object("""{"a":1.9, "b":"7", "c":null}""");
        Assert.Equal(1, JsonUtils.EnsureInteger(obj["a"]));   // truncation, like (int)GetDouble
        Assert.Equal(0, JsonUtils.EnsureInteger(obj["b"]));   // string numbers are NOT coerced
        Assert.Equal(0, JsonUtils.EnsureInteger(obj["c"]));   // JSON null -> default
        Assert.Equal(0, JsonUtils.EnsureInteger(null));       // missing -> default
        Assert.Equal(5, JsonUtils.EnsureInteger(obj["b"], 5));
    }

    [Fact]
    public void RequireString_RejectsNumbersAndNull()
    {
        JsonNode obj = Object("""{"s":"x", "n":42, "z":null}""");
        Assert.Equal("x", JsonUtils.RequireString(obj["s"], "s"));
        Assert.Throws<JsonParseException>(() => JsonUtils.RequireString(obj["n"], "n"));
        Assert.Throws<JsonParseException>(() => JsonUtils.RequireString(obj["z"], "z"));
    }

    [Fact]
    public void EnsureString_MissingOrNullFallBack()
    {
        JsonNode obj = Object("""{"s":"x", "z":null}""");
        Assert.Equal("x", JsonUtils.EnsureString(obj["s"]));
        Assert.Equal("", JsonUtils.EnsureString(obj["z"]));
        Assert.Equal("def", JsonUtils.EnsureString(obj["z"], "def"));
        Assert.Equal("def", JsonUtils.EnsureString(null, "def"));
    }

    [Fact]
    public void RequireObjectAndArray_KindChecked()
    {
        JsonNode obj = Object("""{"o":{}, "a":[], "s":"x"}""");
        Assert.IsType<JsonObject>(JsonUtils.RequireObject(obj["o"], "o"));
        Assert.IsType<JsonArray>(JsonUtils.RequireArray(obj["a"], "a"));
        Assert.Throws<JsonParseException>(() => JsonUtils.RequireObject(obj["a"], "a"));
        Assert.Throws<JsonParseException>(() => JsonUtils.RequireArray(obj["o"], "o"));
        Assert.Throws<JsonParseException>(() => JsonUtils.RequireObject(obj["s"], "s"));

        // element<->node round trips through the AOT-safe helpers
        var element = JsonUtils.ToOwnedElement(obj);
        var back = JsonUtils.ToNode(element);
        Assert.Equal(obj.ToJsonString(), back.ToJsonString());
    }

    [Fact]
    public void CachedStandIns_BehaveLikeMissingValues()
    {
        Assert.Equal(JsonValueKind.Null, JsonUtils.NullElement.ValueKind);
        Assert.Equal(JsonValueKind.Object, JsonUtils.EmptyObjectElement.ValueKind);
        Assert.Equal(JsonValueKind.Array, JsonUtils.EmptyArrayElement.ValueKind);
        // cloned stand-ins stay usable across many reads (they outlive their documents)
        Assert.Equal("", JsonUtils.EnsureString(JsonUtils.NullElement));
    }
}
