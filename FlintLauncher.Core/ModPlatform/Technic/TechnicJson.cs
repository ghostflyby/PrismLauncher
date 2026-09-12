// SPDX-License-Identifier: GPL-3.0-only
// Shared QJsonObject-style accessors for the Technic parsers (JsonUtils' require* family
// returns JsonElement; the Technic models work on the node model directly).
using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.Json;

namespace FlintLauncher.Core.ModPlatform.Technic;

internal static class TechnicJson
{
    /// <summary>Json::requireObject(doc, what) as a JsonObject.</summary>
    public static JsonObject RequireObject(JsonDocument doc)
    {
        return JsonNode.Parse(JsonUtils.RequireObject(doc).GetRawText())!.AsObject();
    }

    /// <summary>Json::requireObject(parent, key) as a JsonObject.</summary>
    public static JsonObject RequireObject(JsonElement parent, string what)
    {
        return JsonNode.Parse(JsonUtils.RequireObject(parent, what).GetRawText())!.AsObject();
    }

    /// <summary>Json::requireArray(parent, key) as a JsonArray.</summary>
    public static JsonArray RequireArray(JsonObject parent, string key)
    {
        return (JsonArray)JsonNode.Parse(JsonUtils.RequireArray(parent, key).GetRawText())!;
    }
}
