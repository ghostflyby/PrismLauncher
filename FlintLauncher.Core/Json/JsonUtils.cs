// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/Json.{h,cpp} on top of System.Text.Json (JsonNode — the single-DOM read
// model), preserving the C++ error-message patterns of the requireIsType family.
//
// The former JsonDocument/JsonElement track (the "dual-track" bridge family) is retired:
// every reader parses straight to JsonNode, so there is no element<->node conversion seam
// left (the former ToNode/ToOwnedElement round-trips and the cached element stand-ins went
// with it).
//
// Qt JSON behaviors that cannot be reproduced exactly:
//  - QJsonDocument's legacy binary format detection (isBinaryJson) is dropped;
//    System.Text.Json never emits or accepts it, so only the parse-error path remains.
//  - QJsonParseError::errorString texts differ from .NET JsonException messages; the
//    surrounding "{what}: Error parsing JSON: ..." wrapper is kept.
//  - QJsonValue stores every number as a double; the node reads parse the raw token text,
//    which agrees for every JSON number literal Qt could round-trip.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.IO;

namespace FlintLauncher.Core.Json;

/// <summary>Port of Json::JsonException (aliased JSONValidationError upstream).</summary>
public sealed class JsonParseException(string message) : Exception(message)
{
}

public static class JsonUtils
{
    // ------------------------------------------------------------- documents

    /// <summary>Node-side RequireDocumentFromFile: same parse-error wrapping, no owned
    /// document to dispose (the returned root is the single-DOM read model).</summary>
    public static JsonNode RequireNodeFromFile(string filename, string what = "Document")
    {
        try
        {
            return JsonNode.Parse(Encoding.UTF8.GetString(FileSystem.Read(filename)))
                ?? throw new JsonParseException(what + ": Error parsing JSON: document is null");
        }
        catch (JsonException e)
        {
            throw new JsonParseException(what + ": Error parsing JSON: " + e.Message);
        }
    }

    /// <summary>In-memory counterpart of <see cref="RequireNodeFromFile"/>: same parse-error
    /// wrapping for a string that is already read (the single-DOM read model).</summary>
    public static JsonNode RequireNode(string json, string what = "Document")
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

    /// <summary>Node-side parseUntilGarbage: parse the first JSON value and ignore any
    /// trailing garbage. Returns null when not even a valid prefix could be parsed; the
    /// garbage is reported as the remaining string (offsets counted in UTF-16 chars).</summary>
    public static JsonNode? ParseNodeUntilGarbage(string json, out string? garbage)
    {
        garbage = null;
        var end = FindFirstValueEnd(json);
        if (end < 0)
        {
            return null;
        }
        try
        {
            var node = JsonNode.Parse(json[..end]);
            if (node is null)
            {
                return null;
            }
            if (end < json.Length)
            {
                garbage = json[end..];
            }
            return node;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Index just past the first complete JSON value, or -1 if there is none.</summary>
    private static int FindFirstValueEnd(string json)
    {
        var i = 0;
        var n = json.Length;
        SkipWhitespace(json, ref i);
        if (i >= n)
        {
            return -1;
        }
        var depth = 0;
        var inString = false;
        var escaped = false;
        var started = false;
        for (; i < n; i++)
        {
            var c = json[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                    if (depth == 0)
                    {
                        return i + 1;
                    }
                }
                continue;
            }
            switch (c)
            {
                case '"':
                    inString = true;
                    started = true;
                    break;
                case '{':
                case '[':
                    depth++;
                    started = true;
                    break;
                case '}':
                case ']':
                    depth--;
                    if (depth == 0)
                    {
                        return i + 1;
                    }
                    break;
                default:
                    if (depth == 0 && started && (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == ',' ||
                                                  char.IsWhiteSpace(c)))
                    {
                        return i;
                    }
                    if (!char.IsWhiteSpace(c))
                    {
                        started = true;
                    }
                    break;
            }
        }
        return started && depth == 0 && !inString ? n : -1;
    }

    private static void SkipWhitespace(string json, ref int i)
    {
        while (i < json.Length && char.IsWhiteSpace(json[i]))
        {
            i++;
        }
    }

    // --------------------------------------------------------------- writing

    /// <summary>Port of Json::writeString: only inserts non-empty strings.</summary>
    public static void WriteString(JsonObject to, string key, string value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            to[key] = JsonValue.Create(value);
        }
    }

    /// <summary>Port of Json::writeStringList: only inserts non-empty lists.</summary>
    public static void WriteStringList(JsonObject to, string key, IReadOnlyList<string> values)
    {
        if (values.Count != 0)
        {
            var array = new JsonArray();
            foreach (var value in values)
            {
                array.Add((JsonNode?)JsonValue.Create(value)); // non-generic Add: AOT/trimming clean
            }
            to[key] = array;
        }
    }

    // --------------------------------------------------------------- reading
    //
    // Single-DOM read model: every reader works on JsonNode (parse once, no owned document,
    // no element<->node conversion seam). Same messages, same fallback and truncation
    // semantics as the C++ requireIsType family; number reads parse the raw token text, so
    // parsed-backed and code-created JsonValues behave identically.
    //
    // RECEIVER RULE: keep parent variables typed **JsonNode or JsonObject** — both bind to
    // the (parent, key, what) overloads below via the JsonNode upcast.

    /// <summary>Port of requireIsType&lt;QString&gt;.</summary>
    public static string RequireString(JsonNode? value, string what = "Value")
    {
        return AsString(value) ?? throw new JsonParseException(what + " is not a string");
    }

    /// <summary>Port of requireIsType&lt;bool&gt;.</summary>
    public static bool RequireBoolean(JsonNode? value, string what = "Value")
    {
        return AsBoolean(value) ?? throw new JsonParseException(what + " is not a bool");
    }

    /// <summary>Port of requireIsType&lt;double&gt;.</summary>
    public static double RequireDouble(JsonNode? value, string what = "Value")
    {
        return AsNumber(value) ?? throw new JsonParseException(what + " is not a double");
    }

    /// <summary>Port of requireIsType&lt;int&gt; (integral check via fmod).</summary>
    public static int RequireInteger(JsonNode? value, string what = "Value")
    {
        var doubl = RequireDouble(value, what);
        if (doubl % 1 != 0)
        {
            throw new JsonParseException(what + " is not an integer");
        }
        return (int)doubl;
    }

    /// <summary>Port of requireIsType&lt;QJsonObject&gt;.</summary>
    public static JsonObject RequireObject(JsonNode? value, string what = "Value")
    {
        return value as JsonObject ?? throw new JsonParseException(what + " is not an object");
    }

    /// <summary>Port of requireIsType&lt;QJsonArray&gt;.</summary>
    public static JsonArray RequireArray(JsonNode? value, string what = "Value")
    {
        return value as JsonArray ?? throw new JsonParseException(what + " is not an array");
    }

    /// <summary>QJsonValue::toString(defaultValue) equivalent.</summary>
    public static string EnsureString(JsonNode? value, string def = "")
    {
        return AsString(value) ?? def;
    }

    /// <summary>QJsonValue::toBool(defaultValue) equivalent.</summary>
    public static bool EnsureBoolean(JsonNode? value, bool def = false)
    {
        return AsBoolean(value) ?? def;
    }

    /// <summary>QJsonValue::toDouble(defaultValue) equivalent.</summary>
    public static double EnsureDouble(JsonNode? value, double def = 0)
    {
        return AsNumber(value) ?? def;
    }

    /// <summary>QJsonValue::toInt(defaultValue) equivalent (Qt truncates like a C cast).</summary>
    public static int EnsureInteger(JsonNode? value, int def = 0)
    {
        return AsNumber(value) is { } number ? (int)number : def;
    }

    /// <summary>64-bit variant of EnsureInteger (file sizes, timestamps in ms).</summary>
    public static long EnsureLong(JsonNode? value, long def = 0)
    {
        return AsNumber(value) is { } number ? (long)number : def;
    }

    // (parent, key, what) forms — the static invocation convention the parsers use
    // (JsonUtils.RequireX(parent, key, what)). The `what` default mirrors the C++
    // "__placeholder__" pattern upstream: the label becomes the quoted key unless overridden.

    public static string RequireString(JsonNode? parent, string key, string what = "__placeholder__")
        => RequireString(GetPropertyNode(parent, key, what), what);

    public static bool RequireBoolean(JsonNode? parent, string key, string what = "__placeholder__")
        => RequireBoolean(GetPropertyNode(parent, key, what), what);

    public static double RequireDouble(JsonNode? parent, string key, string what = "__placeholder__")
        => RequireDouble(GetPropertyNode(parent, key, what), what);

    public static int RequireInteger(JsonNode? parent, string key, string what = "__placeholder__")
        => RequireInteger(GetPropertyNode(parent, key, what), what);

    public static JsonObject RequireObject(JsonNode? parent, string key, string what = "__placeholder__")
        => RequireObject(GetPropertyNode(parent, key, what), what);

    public static JsonArray RequireArray(JsonNode? parent, string key, string what = "__placeholder__")
        => RequireArray(GetPropertyNode(parent, key, what), what);

    // JsonObject-keyed forms: the identity conversion must beat the JsonNode upcast, or a
    // 2-argument (parent, key) call would silently rebind to the (value, what) overload above
    // and read the WHOLE parent as the value ("key is not a string"). They delegate to the
    // same node family — no element bridge involved.
    public static string RequireString(JsonObject parent, string key, string what = "__placeholder__")
        => RequireString((JsonNode?)parent, key, what);

    public static bool RequireBoolean(JsonObject parent, string key, string what = "__placeholder__")
        => RequireBoolean((JsonNode?)parent, key, what);

    public static double RequireDouble(JsonObject parent, string key, string what = "__placeholder__")
        => RequireDouble((JsonNode?)parent, key, what);

    public static int RequireInteger(JsonObject parent, string key, string what = "__placeholder__")
        => RequireInteger((JsonNode?)parent, key, what);

    public static JsonObject RequireObject(JsonObject parent, string key, string what = "__placeholder__")
        => RequireObject((JsonNode?)parent, key, what);

    public static JsonArray RequireArray(JsonObject parent, string key, string what = "__placeholder__")
        => RequireArray((JsonNode?)parent, key, what);

    /// <summary>
    /// Port of the requireIsType(parent, key, what) template: the `what` label defaults to
    /// 'key' (the "__placeholder__" pattern upstream) and missing keys throw
    /// "'key's parent does not contain 'key'".
    /// </summary>
    private static string LocalWhat(string key, string what)
    {
        return what.Replace("__placeholder__", "'" + key + "'");
    }

    /// <summary>A non-object parent or a missing key throws "does not contain" (a JSON null
    /// value falls through as a null node and fails the kind check downstream).</summary>
    private static JsonNode? GetPropertyNode(JsonNode? parent, string key, string what)
    {
        var localWhat = LocalWhat(key, what);
        if (parent is not JsonObject obj || !obj.TryGetPropertyValue(key, out var node))
        {
            throw new JsonParseException(localWhat + "s parent does not contain " + localWhat);
        }
        return node;
    }

    /// <summary>Materialize a JsonArray as a list of non-null nodes (JSON null entries are
    /// junk for every reader here and are dropped, matching the kind checks that reject
    /// them downstream).</summary>
    public static List<JsonNode> ToNodeList(JsonArray array)
    {
        return array.Where(n => n is not null).Select(n => n!).ToList();
    }

    private static string? AsString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

    private static bool? AsBoolean(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }
        if (value.TryGetValue<bool>(out var b))
        {
            return b;
        }
        if (value.TryGetValue<JsonElement>(out var el) &&
            el.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return el.GetBoolean();
        }
        return null;
    }

    private static double? AsNumber(JsonNode? node)
    {
        // Deliberately NOT TryGetValue<T>: its behavior differs between parsed-backed and
        // created-primitive JsonValues (a parsed "42" refused TryGetValue<double> at
        // runtime). The raw token text via ToJsonString is deterministic for every backing;
        // non-number tokens simply fail the parse.
        if (node is not JsonValue value)
        {
            return null;
        }
        return double.TryParse(
            value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
    }

    // ------------------------------------------------------ settings helpers

    /// <summary>Port of Json::toStringList: empty list on any parse/element error (a
    /// non-array root or any non-string element yields [], like the requireIsArrayOf port).</summary>
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

    /// <summary>Port of Json::fromStringList: compact JSON array text.</summary>
    public static string FromStringList(IReadOnlyList<string> list)
    {
        var array = new JsonArray();
        foreach (var str in list)
        {
            array.Add((JsonNode?)JsonValue.Create(str)); // non-generic Add: AOT/trimming clean
        }
        return JsonNodeToCompactString(array);
    }

    /// <summary>
    /// Port of Json::toMap: a JSON object mapped to a string-to-string dictionary.
    /// Deviation: QVariantMap can hold nested variants; scalar values are converted to
    /// their textual form and nested values are kept as compact JSON, which covers the
    /// launcher's settings use cases (string maps).
    /// </summary>
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

    /// <summary>Port of Json::fromMap: compact JSON object text.</summary>
    public static string FromMap(IReadOnlyDictionary<string, string> map)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in map)
        {
            obj[key] = JsonValue.Create(value);
        }
        return JsonNodeToCompactString(obj);
    }

    private static string JsonNodeToCompactString(JsonNode node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            node.WriteTo(writer);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
