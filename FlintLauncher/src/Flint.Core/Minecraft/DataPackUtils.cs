// SPDX-License-Identifier: GPL-3.0-only
// Port of the DataPackUtils::processComponent family from
// launcher/minecraft/mod/tasks/LocalDataPackParseTask.{h,cpp} (the JSON chat component to
// HTML renderer used by the MetaComponentParse tests). The rest of the data-pack parsing
// (pack.png handling, format validation, the parse Task itself) is left for the resource
// parsing phase.
//
// Deviation: QJsonValue::isDouble()/QString::number(double) formats numbers like Qt's
// "%1"/'g' with 6 significant digits (lowercase exponent); .NET's "G6" plus a lowercase 'e'
// mirrors that for common values.
using System.Globalization;
using System.Text.Json.Nodes;

namespace Flint.Core.Minecraft;

public static class DataPackUtils
{
    private static string BuildStyle(JsonObject obj)
    {
        var styles = new List<string>();
        var color = obj.StringOr("color");
        if (color.Length != 0)
        {
            styles.Add($"color: {color};");
        }
        if (obj.Contains("bold"))
        {
            var weight = obj.BoolOr("bold") ? "bold" : "normal";
            styles.Add($"font-weight: {weight};");
        }
        if (obj.Contains("italic"))
        {
            var style = obj.BoolOr("italic") ? "italic" : "normal";
            styles.Add($"font-style: {style};");
        }

        return styles.Count == 0 ? "" : $"style=\"{string.Join(" ", styles)}\"";
    }

    private static string ProcessComponent(JsonArray value, bool strikethrough, bool underline)
    {
        var result = "";
        foreach (var current in value)
        {
            result += ProcessComponent(current, strikethrough, underline);
        }
        return result;
    }

    private static string ProcessComponent(JsonObject obj, bool strikethrough, bool underline)
    {
        underline = obj.BoolOr("underlined", underline);
        strikethrough = obj.BoolOr("strikethrough", strikethrough);

        var result = obj.StringOr("text");
        if (underline)
        {
            result = $"<u>{result}</u>";
        }
        if (strikethrough)
        {
            result = $"<s>{result}</s>";
        }
        // the extra needs to be an array
        if (obj.TryGetPropertyValue("extra", out var extra) && extra is JsonArray extraArray)
        {
            result += ProcessComponent(extraArray, strikethrough, underline);
        }
        var style = BuildStyle(obj);
        if (style.Length != 0)
        {
            result = $"<span {style}>{result}</span>";
        }
        if (obj.TryGetPropertyValue("clickEvent", out var clickEvent) && clickEvent is JsonObject clickEventObj)
        {
            var action = clickEventObj.StringOr("action");
            var value = clickEventObj.StringOr("value");
            if (action == "open_url" && value.Length != 0)
            {
                result = $"<a href=\"{value}\">{result}</a>";
            }
        }
        return result;
    }

    public static string ProcessComponent(JsonNode? value, bool strikethrough = false, bool underline = false)
    {
        switch (value)
        {
            case JsonValue stringValue when stringValue.TryGetValue<string>(out _):
                return JsonObjectExtensions.StringOr(stringValue);
            case JsonValue boolValue when boolValue.TryGetValue<bool>(out var b):
                return b ? "true" : "false";
            case JsonValue numberValue when numberValue.TryGetValue<double>(out var d):
                return QtJson.NumberToString(d);
            case JsonArray array:
                return ProcessComponent(array, strikethrough, underline);
            case JsonObject obj:
                return ProcessComponent(obj, strikethrough, underline);
            default:
                // qWarning() << "Invalid component type!";
                return "";
        }
    }
}
