// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Converters for the three former "opaque JSON-text blob" members of the instance/global
// configs. Read compatibility: the legacy wire form was a STRING holding the embedded
// JSON (the C++ QJsonDocument payload verbatim), so every converter accepts the String
// token (parsing the embedded JSON with the former lenient semantics) AND the native
// shape; write emits the native shape (the launcher config write shape is not guaranteed).
//
// The Env map is a Dictionary subclass with a TYPE-LEVEL converter: type-level
// converters are honored even where the value arrives through a [JsonConstructor]
// parameter, which property-level attributes do not guarantee.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FlintLauncher.Core.Instances;

namespace FlintLauncher.Core.Config;

/// <summary>The custom launch-environment map (the C++ "Env" QVariantMap).</summary>
public sealed class EnvMap : Dictionary<string, string>
{
    public EnvMap() { }

    public EnvMap(IDictionary<string, string> source) : base(source) { }
}

public sealed class EnvMapJsonConverter : JsonConverter<EnvMap>
{
    /// <summary>The settings/registry entry point: the value arrives as the native map
    /// (runtime Set), a JSON element (settings PUT), or a legacy JSON-text string.</summary>
    public static EnvMap? FromObject(object? v) => v switch
    {
        null => null,
        EnvMap map => map,
        JsonElement el => ReadElement(el),
        string text => ReadText(text),
        _ => null,
    };

    private static EnvMap ReadText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new EnvMap();
        }

        try
        {
            return MapFromNode(JsonNode.Parse(text));
        }
        catch (JsonException)
        {
            return new EnvMap();
        }
    }

    private static EnvMap ReadElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => ReadText(element.GetString()),
            JsonValueKind.Object => MapFromNode(JsonNode.Parse(element.GetRawText())),
            _ => new EnvMap(),
        };
    }

    public override EnvMap Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // legacy: the member held the environment map as its embedded JSON text
        if (reader.TokenType == JsonTokenType.String)
        {
            return ReadText(reader.GetString());
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            var node = JsonNode.Parse(ref reader);
            return MapFromNode(node);
        }

        // null / wrong kind -> no custom environment (the former lenient read)
        return new EnvMap();
    }

    public override void Write(Utf8JsonWriter writer, EnvMap value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (key, envValue) in value)
        {
            writer.WriteString(key, envValue);
        }

        writer.WriteEndObject();
    }

    internal static EnvMap FromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new EnvMap();
        }

        try
        {
            return MapFromNode(JsonNode.Parse(text));
        }
        catch (JsonException)
        {
            // junk blob: the former reader returned an empty environment
            return new EnvMap();
        }
    }

    private static EnvMap MapFromNode(JsonNode? node)
    {
        var map = new EnvMap();
        if (node is JsonObject obj)
        {
            foreach (var (key, value) in obj)
            {
                map[key] = value is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
            }
        }

        return map;
    }
}

/// <summary>The registered-shortcuts list (ShortcutData: name/filePath/target). Read
/// compatibility: the legacy wire form was a string holding the compact JSON array, and
/// the former reader dropped entries whose path does not exist — both preserved here
/// (the existence filter runs at deserialize, which is when the former read ran).</summary>
public sealed class ShortcutListJsonConverter : JsonConverter<List<ShortcutData>?>
{
    /// <summary>The settings/registry entry point: the value arrives as the native list
    /// (runtime Set), a JSON element (settings PUT), or a legacy JSON-text string.</summary>
    public static List<ShortcutData>? FromObject(object? v) => v switch
    {
        null => null,
        List<ShortcutData> list => list,
        JsonElement el => ReadElement(el),
        string text => ReadText(text),
        _ => null,
    };

    private static List<ShortcutData>? ReadText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return FromNode(JsonNode.Parse(text));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<ShortcutData>? ReadElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => ReadText(element.GetString()),
            JsonValueKind.Array => FromNode(JsonNode.Parse(element.GetRawText())),
            _ => null,
        };
    }

    public override List<ShortcutData>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // legacy: the member held the shortcuts array as its embedded JSON text
        if (reader.TokenType == JsonTokenType.String)
        {
            return ReadText(reader.GetString());
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            return FromNode(JsonNode.Parse(ref reader));
        }

        return null;
    }

    public override void Write(Utf8JsonWriter writer, List<ShortcutData>? value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var shortcut in value ?? [])
        {
            writer.WriteStartObject();
            writer.WriteString("name", shortcut.Name);
            writer.WriteString("filePath", shortcut.FilePath);
            writer.WriteNumber("target", (int)shortcut.Target);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static List<ShortcutData> FromNode(JsonNode? node)
    {
        var results = new List<ShortcutData>();
        if (node is not JsonArray array)
        {
            return results;
        }

        foreach (var elem in array)
        {
            if (elem is not JsonObject obj ||
                obj["name"] is not JsonValue name || !name.TryGetValue<string>(out var shortcutName) ||
                obj["filePath"] is not JsonValue filePath || !filePath.TryGetValue<string>(out var path) ||
                obj["target"] is not JsonValue target || !target.TryGetValue<int>(out var targetValue) ||
                targetValue < 0 || targetValue >= 3)
            {
                continue;
            }

            if (!File.Exists(path) && !Directory.Exists(path))
            {
                // (upstream logs a qWarning here)
                continue;
            }

            results.Add(new ShortcutData(shortcutName, path, (ShortcutTarget)targetValue));
        }

        return results;
    }
}

/// <summary>The mod-download loader list. Read compatibility: the legacy wire form was a
/// string holding the JSON array of loader names.</summary>
public sealed class StringListJsonConverter : JsonConverter<List<string>?>
{
    /// <summary>The settings/registry entry point: the value arrives as the native list
    /// (runtime Set), a JSON element (settings PUT), or a legacy JSON-text string.</summary>
    public static List<string>? FromObject(object? v) => v switch
    {
        null => null,
        List<string> list => list,
        JsonElement el => ReadElement(el),
        string text => ReadText(text),
        _ => null,
    };

    private static List<string>? ReadText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            var node = JsonNode.Parse(text);
            var parsed = new List<string>();
            if (node is JsonArray parsedArray)
            {
                foreach (var entry in parsedArray)
                {
                    if (entry is JsonValue value && value.TryGetValue<string>(out var s))
                    {
                        parsed.Add(s);
                    }
                }
            }

            return parsed;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<string>? ReadElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => ReadText(element.GetString()),
            JsonValueKind.Array => FromNode(JsonNode.Parse(element.GetRawText())),
            _ => null,
        };
    }

    private static List<string> FromNode(JsonNode? node)
    {
        var parsed = new List<string>();
        if (node is JsonArray array)
        {
            foreach (var entry in array)
            {
                if (entry is JsonValue value && value.TryGetValue<string>(out var s))
                {
                    parsed.Add(s);
                }
            }
        }

        return parsed;
    }

    public override List<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // legacy: the member held the loader list as its embedded JSON text
        if (reader.TokenType == JsonTokenType.String)
        {
            var text = reader.GetString();
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            try
            {
                var node = JsonNode.Parse(text);
                var parsed = new List<string>();
                if (node is JsonArray parsedArray)
                {
                    foreach (var entry in parsedArray)
                    {
                        if (entry is JsonValue value && value.TryGetValue<string>(out var s))
                        {
                            parsed.Add(s);
                        }
                    }
                }

                return parsed;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var result = new List<string>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                {
                    return result;
                }

                if (reader.TokenType == JsonTokenType.String)
                {
                    result.Add(reader.GetString() ?? "");
                }
            }

            throw new JsonException("unterminated array");
        }

        return null;
    }

    public override void Write(Utf8JsonWriter writer, List<string>? value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var entry in value ?? [])
        {
            writer.WriteStringValue(entry);
        }

        writer.WriteEndArray();
    }
}
