// SPDX-License-Identifier: GPL-3.0-only
// Qt runtime shim used by the version formats (no direct C++ counterpart): reproduces the
// QJsonDocument/QJsonObject behaviors the C++ version-format code relies on for round-trip
// serialization — QJsonObject iterates its members sorted by key, and
// QJsonDocument::toJson(QJsonDocument::Indented) writes 4-space indentation.

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FlintLauncher.Core.Minecraft;

public static class QtJson
{
    private static readonly JsonWriterOptions IndentedOptions = new()
    {
        Indented = true,
        IndentCharacter = ' ',
        IndentSize = 4,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Port of QJsonDocument::toJson(QJsonDocument::Indented) for a QJsonObject-shaped tree:
    /// object members are first re-ordered like QJsonObject (sorted by key) and then written
    /// with Qt's 4-space indentation.
    /// </summary>
    public static string ToIndentedString(JsonNode node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, IndentedOptions))
        {
            SortObjectKeys(node).WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Returns a copy of the tree with all object members sorted by key (QString order = ordinal).</summary>
    public static JsonNode SortObjectKeys(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var sorted = new JsonObject();
                foreach (var property in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    sorted[property.Key] = property.Value is null ? null : SortObjectKeys(property.Value);
                }

                return sorted;
            }
            case JsonArray array:
            {
                var sorted = new JsonArray();
                foreach (var item in array)
                {
                    sorted.Add(item is null ? null : SortObjectKeys(item));
                }

                return sorted;
            }
            default:
                return JsonNode.Parse(node.ToJsonString())!;
        }
    }

    /// <summary>QJsonValue::toDouble() as written by QString::number(double) ('g', 6 digits).</summary>
    public static string NumberToString(double value)
    {
        var text = value.ToString("G6", CultureInfo.InvariantCulture);
        // Qt emits a lowercase 'e' in exponents ("1.23457e+08")
        return text.Replace('E', 'e');
    }
}
