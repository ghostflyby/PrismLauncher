// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// The settings-layer dynamic-JSON text helpers (the tail of the former JsonUtils, relocated
// next to their only consumers): the INI-era settings values that carry arbitrary JSON
// strings — the custom environment QVariantMap (MinecraftInstance's "Env") and the
// linked-instances string list. These are dynamic shapes BY DESIGN (arbitrary user JSON in
// a settings string, not a fixed schema), which is why they are the documented typed-
// migration keep in docs/JSON-MIGRATION.md §4 rather than records.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace FlintLauncher.Core.Settings;

public static class SettingsJson
{
    /// <summary>Port of Json::toMap: a JSON object mapped to a string-to-string dictionary;
    /// parse failures and non-object roots yield an empty map. Scalar values convert to
    /// their textual form, nested values stay as compact JSON.</summary>
    public static Dictionary<string, string> ToMap(string jsonString)
    {
        var map = new Dictionary<string, string>();
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(jsonString);
        }
        catch (JsonException)
        {
            return map;
        }

        if (root is not JsonObject obj)
        {
            return map;
        }

        foreach (var (key, node) in obj)
        {
            map[key] = node switch
            {
                null => "", // JSON null
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
                // numbers keep their raw token text; nested objects/arrays serialize compact
                _ => node.ToJsonString(),
            };
        }

        return map;
    }

    /// <summary>Port of Json::toStringList: empty list on any parse/element error (a
    /// non-array root or any non-string element yields []).</summary>
    public static List<string> ToStringList(string jsonString)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(jsonString);
        }
        catch (JsonException)
        {
            return [];
        }

        if (root is not JsonArray array)
        {
            return [];
        }

        var outList = new List<string>();
        foreach (var val in array)
        {
            if (val is not JsonValue value || !value.TryGetValue<string>(out var str))
            {
                return [];
            }

            outList.Add(str);
        }

        return outList;
    }
}
