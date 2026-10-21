// SPDX-License-Identifier: GPL-3.0-only
// Response-body entry point shared by the ATL parsers (the wire models deserialize through
// the AtLauncherJsonContext — see AtLauncherApi.cs): JsonNode.Parse with the C++
// Json::requireDocument "{what}: Error parsing JSON: ..." wrapping, kept for the surfaced
// "Could not understand ..." failure messages.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace FlintLauncher.Core.ModPlatform.AtLauncher;

internal static class AtLauncherJson
{
    /// <summary>Node-side Json::requireDocument(QByteArray, what): JsonNode.Parse with the
    /// parse-error wrapping (the null parse result of a "null" document throws like any
    /// other parse failure).</summary>
    public static JsonNode RequireDocument(string json, string what)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException e)
        {
            throw new JsonException(what + ": Error parsing JSON: " + e.Message);
        }

        return node ?? throw new JsonException(what + ": Error parsing JSON: document is null");
    }
}
