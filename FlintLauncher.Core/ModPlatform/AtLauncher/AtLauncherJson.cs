// SPDX-License-Identifier: GPL-3.0-only
// Shared node-model accessors for the ATL parsers. Reads go through the JsonNode model
// (single-DOM, the JsonUtils node-side family — same migration as TechnicJson); the
// former element→node bridges are gone. What remains is the response-body entry point
// (JsonNode.Parse with the C++ Json::requireDocument "{what}: Error parsing JSON: ..."
// wrapping) and the QJsonValue object cast ("Value is not an object") the array-entry
// and root-document paths share.

using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.Json;

namespace FlintLauncher.Core.ModPlatform.AtLauncher;

internal static class AtLauncherJson
{
    /// <summary>Node-side Json::requireDocument(QByteArray, what): JsonNode.Parse with the
    /// parse-error wrapping (the null parse result of a "null" document throws like any
    /// other parse failure).</summary>
    public static JsonNode RequireDocument(string json, string what)
    {
        try
        {
            return JsonNode.Parse(json)
                ?? throw new JsonParseException(what + ": Error parsing JSON: document is null");
        }
        catch (JsonException e)
        {
            throw new JsonParseException(what + ": Error parsing JSON: " + e.Message);
        }
    }

    /// <summary>Json::requireObject(value) as a JsonObject cast: throws for both a missing
    /// value and a non-object (the QJsonValue cast's "Value is not an object" path).</summary>
    public static JsonObject Object(JsonNode? node)
    {
        return node is JsonObject obj ? obj : throw new JsonParseException("Value is not an object");
    }
}
