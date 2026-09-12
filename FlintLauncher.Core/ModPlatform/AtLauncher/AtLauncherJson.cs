// SPDX-License-Identifier: GPL-3.0-only
// Shared QJsonObject-style accessors for the ATL parsers: JsonUtils covers the
// require*(parent, key) family over JsonObject (returning JsonElement); the ATL models
// work on the node model directly, so the object/array casts land here along with the
// QJsonValue "cast with default" accessors (obj["key"].toString("")/toBool()/toInt(0)).
using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.Json;

namespace FlintLauncher.Core.ModPlatform.AtLauncher;

internal static class AtLauncherJson
{
    /// <summary>Json::requireObject(obj, key) as a JsonObject.</summary>
    public static JsonObject RequireObject(JsonObject obj, string key)
    {
        if (obj.TryGetPropertyValue(key, out var node) && node is JsonObject child)
        {
            return child;
        }
        throw new JsonParseException("'" + key + "s parent does not contain '" + key + "'");
    }

    /// <summary>Json::requireArray(obj, key) as a JsonArray.</summary>
    public static JsonArray RequireArray(JsonObject obj, string key)
    {
        if (obj.TryGetPropertyValue(key, out var node) && node is JsonArray array)
        {
            return array;
        }
        throw new JsonParseException("'" + key + "s parent does not contain '" + key + "'");
    }

    public static JsonObject Object(JsonNode? node)
    {
        return node is JsonObject obj ? obj : throw new JsonParseException("Value is not an object");
    }

    /// <summary>Json::requireObject(doc, what) over the node model.</summary>
    public static JsonObject Object(JsonDocument doc)
    {
        return Object(JsonNode.Parse(doc.RootElement.GetRawText()));
    }

    public static JsonObject Object(JsonElement element)
    {
        return Object(JsonNode.Parse(element.GetRawText()));
    }

    /// <summary>Json::requireArray(doc) over the node model.</summary>
    public static JsonArray Array(JsonDocument doc)
    {
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonParseException("Document is not an array");
        }
        return (JsonArray)JsonNode.Parse(doc.RootElement.GetRawText())!;
    }

    public static string RequireString(JsonObject obj, string key) => JsonUtils.RequireString(obj, key);

    public static bool RequireBoolean(JsonObject obj, string key) => JsonUtils.RequireBoolean(obj, key);

    public static int RequireInteger(JsonObject obj, string key) => JsonUtils.RequireInteger(obj, key);

    /// <summary>Json::requireString over an array element.</summary>
    public static string RequireString(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var str)
            ? str
            : throw new JsonParseException("Value is not a string");
    }

    /// <summary>QJsonValue::toString(defaultValue) equivalent.</summary>
    public static string OptString(JsonObject obj, string key, string def = "")
    {
        return obj.TryGetPropertyValue(key, out var node) && node is JsonValue value
            && value.TryGetValue<string>(out var str) ? str : def;
    }

    /// <summary>QJsonValue::toBool() equivalent.</summary>
    public static bool OptBool(JsonObject obj, string key)
    {
        return obj.TryGetPropertyValue(key, out var node) && node is JsonValue value
            && value.TryGetValue<bool>(out var b) && b;
    }

    /// <summary>QJsonValue::toInt(defaultValue) equivalent.</summary>
    public static int OptInteger(JsonObject obj, string key, int def = 0)
    {
        return obj.TryGetPropertyValue(key, out var node) && node is JsonValue value
            && value.TryGetValue<int>(out var i) ? i : def;
    }

    /// <summary>QJsonValue::toString(defaultValue) over an array element.</summary>
    public static string OptString(JsonNode? node, string def = "")
    {
        return node is JsonValue value && value.TryGetValue<string>(out var str) ? str : def;
    }
}
