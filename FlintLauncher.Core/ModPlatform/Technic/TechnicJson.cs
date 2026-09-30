// SPDX-License-Identifier: GPL-3.0-only
// Shared node-model accessors for the Technic parsers. Reads go through the JsonNode model
// (single-DOM, the JsonUtils node-side family); the former element→node bridges are gone.
// What remains is the response-body entry point: JsonNode.Parse throws the raw JsonException,
// so the "{what}: Error parsing JSON: ..." wrapping of the C++ Json::requireDocument is kept
// here for the surfaced "Could not understand ..." failure messages.
using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.Json;

namespace FlintLauncher.Core.ModPlatform.Technic;

internal static class TechnicJson
{
    /// <summary>Node-side Json::requireDocument(QByteArray, what): JsonNode.Parse with the
    /// parse-error wrapping (a JSON null document is impossible — the null parse result
    /// throws like any other parse failure).</summary>
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
}
