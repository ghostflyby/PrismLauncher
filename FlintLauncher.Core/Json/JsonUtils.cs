// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/Json.{h,cpp} on top of System.Text.Json (JsonDocument/JsonElement),
// preserving the C++ error-message patterns of the requireIsType family.
//
// Qt JSON behaviors that cannot be reproduced exactly:
//  - QJsonDocument's legacy binary format detection (isBinaryJson) is dropped;
//    System.Text.Json never emits or accepts it, so only the parse-error path remains.
//  - QJsonParseError::errorString texts differ from .NET JsonException messages; the
//    surrounding "{what}: Error parsing JSON: ..." wrapper is kept.
//  - requireIsType<QByteArray>/requireIsType<QUrl> call QJsonValue::toString(what),
//    which silently substitutes the `what` label when the value is not a string (a
//    latent upstream bug: QByteArray would hex-decode the label). The port throws
//    "{what} is not a string" instead.
//  - QJsonValue stores every number as a double; JsonElement keeps integer literals.
//    RequireDouble therefore accepts any JSON number, like Qt.
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

    /// <summary>Port of Json::write(QJsonDocument, filename): writes indented JSON (QJsonDocument::toJson default).</summary>
    public static void Write(JsonDocument doc, string filename)
    {
        FileSystem.Write(filename, ToText(doc.RootElement, indented: true));
    }

    /// <summary>Port of Json::write(QJsonObject, filename).</summary>
    public static void Write(JsonElement element, string filename)
    {
        FileSystem.Write(filename, ToText(element, indented: true));
    }

    /// <summary>Port of Json::toText(QJsonObject)/toText(QJsonArray): compact JSON bytes.</summary>
    public static byte[] ToText(JsonElement element)
    {
        return ToText(element, indented: false);
    }

    private static byte[] ToText(JsonElement element, bool indented)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = indented }))
        {
            element.WriteTo(writer);
        }
        return stream.ToArray();
    }

    /// <summary>
    /// Port of Json::requireDocument(QByteArray, what). Throws JsonParseException on
    /// invalid JSON with the message "{what}: Error parsing JSON: ...".
    /// </summary>
    public static JsonDocument RequireDocument(string json, string what = "Document")
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            throw new JsonParseException(what + ": Error parsing JSON: " + e.Message);
        }
    }

    /// <summary>Port of Json::requireDocument(filename, what): reads the file via FS::read.</summary>
    public static JsonDocument RequireDocumentFromFile(string filename, string what = "Document")
    {
        return RequireDocument(Encoding.UTF8.GetString(FileSystem.Read(filename)), what);
    }

    /// <summary>Port of Json::requireObject(QJsonDocument, what).</summary>
    public static JsonElement RequireObject(JsonDocument doc, string what = "Document")
    {
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonParseException(what + " is not an object");
        }
        return doc.RootElement;
    }

    /// <summary>Port of Json::requireArray(QJsonDocument, what).</summary>
    public static JsonElement RequireArray(JsonDocument doc, string what = "Document")
    {
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonParseException(what + " is not an array");
        }
        return doc.RootElement;
    }

    /// <summary>
    /// Port of Json::parseUntilGarbage: parse the first JSON value and ignore any trailing
    /// garbage. Returns null when not even a valid prefix could be parsed (C++ would return
    /// a null QJsonDocument and fill in the parse error). The garbage is reported as the
    /// remaining string; note Qt reports QJsonParseError offsets in UTF-8 bytes while this
    /// port works on UTF-16 chars.
    /// </summary>
    public static JsonDocument? ParseUntilGarbage(string json, out string? garbage)
    {
        garbage = null;
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
        }

        var end = FindFirstValueEnd(json);
        if (end < 0)
        {
            return null;
        }
        try
        {
            var doc = JsonDocument.Parse(json[..end]);
            if (end < json.Length)
            {
                garbage = json[end..];
            }
            return doc;
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

    /// <summary>Port of Json::toJson&lt;QByteArray&gt;: lowercase hex string.</summary>
    public static JsonNode ToJsonByteArray(byte[] data)
    {
        return JsonValue.Create(Convert.ToHexString(data).ToLowerInvariant())!;
    }

    /// <summary>Port of Json::toJson&lt;QDateTime&gt;: Qt::ISODate formatted text.</summary>
    public static JsonNode ToJsonDateTime(DateTime datetime)
    {
        return JsonValue.Create(datetime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture))!;
    }

    /// <summary>Port of Json::toJson&lt;QUuid&gt;: lowercase hyphenated form.</summary>
    public static JsonNode ToJsonUuid(Guid uuid)
    {
        return JsonValue.Create(uuid.ToString())!;
    }

    // --------------------------------------------------------------- reading

    /// <summary>Port of requireIsType&lt;QString&gt;.</summary>
    public static string RequireString(JsonElement value, string what = "Value")
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new JsonParseException(what + " is not a string");
        }
        return value.GetString()!;
    }

    /// <summary>Port of requireIsType&lt;bool&gt;.</summary>
    public static bool RequireBoolean(JsonElement value, string what = "Value")
    {
        if (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False)
        {
            throw new JsonParseException(what + " is not a bool");
        }
        return value.GetBoolean();
    }

    /// <summary>Port of requireIsType&lt;double&gt;.</summary>
    public static double RequireDouble(JsonElement value, string what = "Value")
    {
        if (value.ValueKind != JsonValueKind.Number)
        {
            throw new JsonParseException(what + " is not a double");
        }
        return value.GetDouble();
    }

    /// <summary>Port of requireIsType&lt;int&gt; (integral check via fmod).</summary>
    public static int RequireInteger(JsonElement value, string what = "Value")
    {
        var doubl = RequireDouble(value, what);
        if (doubl % 1 != 0)
        {
            throw new JsonParseException(what + " is not an integer");
        }
        return (int)doubl;
    }

    /// <summary>Port of requireIsType&lt;QJsonObject&gt;.</summary>
    public static JsonElement RequireObject(JsonElement value, string what = "Value")
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new JsonParseException(what + " is not an object");
        }
        return value;
    }

    /// <summary>Port of requireIsType&lt;QJsonArray&gt;.</summary>
    public static JsonElement RequireArray(JsonElement value, string what = "Value")
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new JsonParseException(what + " is not an array");
        }
        return value;
    }

    /// <summary>Port of requireIsType&lt;QJsonValue&gt;: rejects null/undefined.</summary>
    public static JsonElement RequireJsonValue(JsonElement value, string what = "Value")
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new JsonParseException(what + " is null or undefined");
        }
        return value;
    }

    /// <summary>
    /// Port of requireIsType&lt;QByteArray&gt;: hex-encoded string. Deviation: Qt's
    /// QJsonValue::toString(what) fallback would hex-decode the `what` label for non-string
    /// input; here it throws like RequireString.
    /// </summary>
    public static byte[] RequireByteArray(JsonElement value, string what = "Value")
    {
        var str = RequireString(value, what);
        // ensure that the string can be safely cast to Latin1
        foreach (var c in str)
        {
            if (c > 0xFF)
            {
                throw new JsonParseException(what + " is not encodable as Latin1");
            }
        }
        try
        {
            return Convert.FromHexString(str);
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            // Qt's QByteArray::fromHex silently skips invalid characters and drops a
            // trailing odd nibble; mirror that lenient decoding.
            return FromHexLenient(str);
        }
    }

    private static byte[] FromHexLenient(string str)
    {
        var bytes = new List<byte>(str.Length / 2);
        int high = -1;
        foreach (var c in str)
        {
            var nibble = HexValue(c);
            if (nibble < 0)
            {
                high = -1; // Qt's fromHex skips invalid characters
                continue;
            }
            if (high < 0)
            {
                high = nibble;
            }
            else
            {
                bytes.Add((byte)((high << 4) | nibble));
                high = -1;
            }
        }
        return [.. bytes];
    }

    private static int HexValue(char c)
    {
        if (c is >= '0' and <= '9')
        {
            return c - '0';
        }
        if (c is >= 'a' and <= 'f')
        {
            return c - 'a' + 10;
        }
        if (c is >= 'A' and <= 'F')
        {
            return c - 'A' + 10;
        }
        return -1;
    }

    /// <summary>Port of requireIsType&lt;QDateTime&gt; (Qt::ISODate format).</summary>
    public static DateTime RequireDateTime(JsonElement value, string what = "Value")
    {
        var str = RequireString(value, what);
        if (!TryParseIsoDate(str, out var datetime))
        {
            throw new JsonParseException(what + " is not a ISO formatted date/time value");
        }
        return datetime;
    }

    private static readonly string[] IsoFormats =
    [
        "yyyy-MM-ddTHH:mm:ssK",
        "yyyy-MM-ddTHH:mm:ssZ",
        "yyyy-MM-ddTHH:mm:sszzz",
        "yyyy-MM-ddTHH:mm:ss.fffK",
        "yyyy-MM-ddTHH:mm:ss.fffffffK",
        "yyyy-MM-ddTHH:mmK",
        "yyyy-MM-dd",
    ];

    private static bool TryParseIsoDate(string str, out DateTime datetime)
    {
        // Qt::ISODate accepts date, date+time (seconds optional) and Z/offset zones.
        return DateTime.TryParseExact(str, IsoFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind, out datetime);
    }

    /// <summary>
    /// Port of requireIsType&lt;QUrl&gt; (strict mode). An empty string maps to the
    /// default-constructed QUrl, represented as null here.
    /// </summary>
    public static Uri? RequireUrl(JsonElement value, string what = "Value")
    {
        var str = RequireString(value, what);
        if (str.Length == 0)
        {
            return null;
        }
        if (!Uri.TryCreate(str, UriKind.Absolute, out var url))
        {
            throw new JsonParseException(what + " is not a correctly formatted URL");
        }
        return url;
    }

    /// <summary>
    /// Port of requireIsType&lt;QUuid&gt;: valid only when the string is exactly the
    /// canonical lowercase hyphenated form (QUuid round-trip check).
    /// </summary>
    public static Guid RequireUuid(JsonElement value, string what = "Value")
    {
        var str = RequireString(value, what);
        if (!Guid.TryParse(str, out var uuid) || uuid.ToString() != str) // converts back => valid
        {
            throw new JsonParseException(what + " is not a valid UUID");
        }
        return uuid;
    }

    // ---------------------------------------------- parent + key convenience
    //
    // The C++ overloads are distinguished by their first parameter type
    // (QJsonValue& value vs QJsonObject& parent). JsonElement cannot play that role
    // (a 2-argument call would always bind to the value overload), so the parent+key
    // helpers take JsonObject, mirroring QJsonObject.

    /// <summary>
    /// Port of the requireIsType(parent, key, what) template: the `what` label defaults to
    /// 'key' (the "__placeholder__" pattern upstream) and missing keys throw
    /// "'key's parent does not contain 'key'".
    /// </summary>
    private static string LocalWhat(string key, string what)
    {
        return what.Replace("__placeholder__", "'" + key + "'");
    }

    private static JsonElement GetProperty(JsonObject parent, string key, string localWhat)
    {
        if (!parent.TryGetPropertyValue(key, out var node))
        {
            throw new JsonParseException(localWhat + "s parent does not contain " + localWhat);
        }
        // a JSON null is surfaced as a null node reference
        return node is null
            ? JsonDocument.Parse("null").RootElement
            : JsonDocument.Parse(node.ToJsonString()).RootElement;
    }

    /// <summary>Port of require&lt;String&gt;(parent, key).</summary>
    public static string RequireString(JsonObject parent, string key, string what = "__placeholder__")
    {
        var localWhat = LocalWhat(key, what);
        return RequireString(GetProperty(parent, key, localWhat), localWhat);
    }

    /// <summary>Port of require&lt;Boolean&gt;(parent, key).</summary>
    public static bool RequireBoolean(JsonObject parent, string key, string what = "__placeholder__")
    {
        var localWhat = LocalWhat(key, what);
        return RequireBoolean(GetProperty(parent, key, localWhat), localWhat);
    }

    /// <summary>Port of require&lt;Double&gt;(parent, key).</summary>
    public static double RequireDouble(JsonObject parent, string key, string what = "__placeholder__")
    {
        var localWhat = LocalWhat(key, what);
        return RequireDouble(GetProperty(parent, key, localWhat), localWhat);
    }

    /// <summary>Port of require&lt;Integer&gt;(parent, key).</summary>
    public static int RequireInteger(JsonObject parent, string key, string what = "__placeholder__")
    {
        var localWhat = LocalWhat(key, what);
        return RequireInteger(GetProperty(parent, key, localWhat), localWhat);
    }

    /// <summary>Port of require&lt;Object&gt;(parent, key).</summary>
    public static JsonElement RequireObject(JsonObject parent, string key, string what = "__placeholder__")
    {
        var localWhat = LocalWhat(key, what);
        return RequireObject(GetProperty(parent, key, localWhat), localWhat);
    }

    /// <summary>Port of require&lt;Array&gt;(parent, key).</summary>
    public static JsonElement RequireArray(JsonObject parent, string key, string what = "__placeholder__")
    {
        var localWhat = LocalWhat(key, what);
        return RequireArray(GetProperty(parent, key, localWhat), localWhat);
    }

    /// <summary>Port of requireIsArrayOf&lt;QString&gt;(doc).</summary>
    public static List<string> RequireArrayOfStrings(JsonDocument doc)
    {
        var array = RequireArray(doc);
        return RequireArrayOfStrings(array, "Document");
    }

    /// <summary>Port of requireIsArrayOf&lt;QString&gt;(parent, key).</summary>
    public static List<string> RequireArrayOfStrings(JsonObject parent, string key, string what = "__placeholder__")
    {
        var localWhat = LocalWhat(key, what);
        var value = GetProperty(parent, key, localWhat);
        // upstream calls parent[key].toArray() (a cast, not a check) and then validates
        // the elements, so a non-array parent value yields an empty list here as well
        if (value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return RequireArrayOfStrings(value, "Document");
    }

    private static List<string> RequireArrayOfStrings(JsonElement array, string what)
    {
        var outList = new List<string>();
        foreach (var val in array.EnumerateArray())
        {
            outList.Add(RequireString(val, what));
        }
        return outList;
    }

    // --------------------------------------------------------- ensure* family
    //
    // The C++ module itself has no ensure* helpers; the QJsonValue accessors with a
    // default value (toString(x), toDouble(x), toBool(x), toInt(x)) fill that role and
    // are ported below as Ensure*.

    /// <summary>QJsonValue::toString(defaultValue) equivalent.</summary>
    public static string EnsureString(JsonElement value, string def = "")
    {
        return value.ValueKind == JsonValueKind.String ? value.GetString()! : def;
    }

    /// <summary>QJsonValue::toBool(defaultValue) equivalent.</summary>
    public static bool EnsureBoolean(JsonElement value, bool def = false)
    {
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : def;
    }

    /// <summary>QJsonValue::toDouble(defaultValue) equivalent.</summary>
    public static double EnsureDouble(JsonElement value, double def = 0)
    {
        return value.ValueKind == JsonValueKind.Number ? value.GetDouble() : def;
    }

    /// <summary>QJsonValue::toInt(defaultValue) equivalent (Qt truncates like a C cast).</summary>
    public static int EnsureInteger(JsonElement value, int def = 0)
    {
        return value.ValueKind == JsonValueKind.Number ? (int)value.GetDouble() : def;
    }

    // ------------------------------------------------------ settings helpers

    /// <summary>Port of Json::toStringList: empty list on any parse/element error.</summary>
    public static List<string> ToStringList(string jsonString)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(jsonString);
        }
        catch (JsonException)
        {
            return [];
        }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }
            try
            {
                return RequireArrayOfStrings(doc);
            }
            catch (JsonParseException)
            {
                return [];
            }
        }
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
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(jsonString);
        }
        catch (JsonException)
        {
            return map;
        }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return map;
            }
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                map[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString() ?? "",
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Number => property.Value.GetRawText(),
                    JsonValueKind.Null => "",
                    _ => JsonNodeToCompactString(JsonNode.Parse(property.Value.GetRawText())!),
                };
            }
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
