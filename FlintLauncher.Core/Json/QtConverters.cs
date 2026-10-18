// SPDX-License-Identifier: GPL-3.0-only
// QJsonValue coercion as JSON converters — the typed-serialization counterparts of the
// JsonUtils Ensure*/Require* families, for third-party formats whose semantics the port
// must reproduce (the FTB App API pins them in tests). Apply per member with
// [JsonConverter(typeof(...))]; required-member presence is enforced by the C# `required`
// modifier on the model property, not by these converters.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlintLauncher.Core.Json;

/// <summary>QJsonValue::toInt() for OPTIONAL members: a number token truncates toward zero
/// (43.9 -> 43), every other token (string/bool/null) yields 0. A missing key never reaches
/// the converter — the property default applies.</summary>
public sealed class QtCoercingIntConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var number)
            ? (int)number
            : 0;

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value);
}

/// <summary>EnsureLong(): the 64-bit variant of the toInt() coercion.</summary>
public sealed class QtCoercingLongConverter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var number)
            ? (long)number
            : 0;

    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value);
}

/// <summary>QJsonValue::toString(default ""): any non-string token yields "".</summary>
public sealed class QtCoercingStringConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String ? reader.GetString() ?? "" : "";

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}

/// <summary>The {"packs":[id,...]} index arrays: QJsonValue::toInt() per element (doubles
/// truncate, junk zeroes); a missing member keeps the property default, a JSON null yields
/// an empty list.</summary>
public sealed class QtCoercingIntListConverter : JsonConverter<List<int>>
{
    public override List<int> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is not JsonTokenType.StartArray)
        {
            return [];
        }

        var result = new List<int>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return result;
            }

            result.Add(reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var number)
                ? (int)number
                : 0);
        }

        throw new JsonException("unterminated array");
    }

    public override void Write(Utf8JsonWriter writer, List<int> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var item in value)
        {
            writer.WriteNumberValue(item);
        }

        writer.WriteEndArray();
    }
}
