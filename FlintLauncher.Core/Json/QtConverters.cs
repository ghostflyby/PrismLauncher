// SPDX-License-Identifier: GPL-3.0-only
// QJsonValue coercion as JSON converters — the typed-serialization counterparts of the
// JsonUtils Ensure*/Require* families, for third-party formats whose semantics the port
// must reproduce (the FTB App API pins them in tests). Apply per member with
// [JsonConverter(typeof(...))]; required-member presence is enforced by the C# `required`
// modifier on the model property, not by these converters.

using System.Text.Json;
using System.Text.Json.Serialization;
using TruePath;

namespace FlintLauncher.Core.Json;

/// <summary>QJsonValue::toInt() for OPTIONAL members: a number token truncates toward zero
/// (43.9 -> 43), every other token (string/bool/null) yields 0. A missing key never reaches
/// the converter — the property default applies.</summary>
public sealed class QtCoercingIntConverter : JsonConverter<int>
{
    /// <summary>null participates in the coercion (the QJsonValue fallback),
    /// instead of the STJ default of bypassing the converter.</summary>
    public override bool HandleNull => true;

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
    /// <summary>null participates in the coercion (the QJsonValue fallback),
    /// instead of the STJ default of bypassing the converter.</summary>
    public override bool HandleNull => true;

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
    /// <summary>null participates in the coercion (the QJsonValue fallback),
    /// instead of the STJ default of bypassing the converter.</summary>
    public override bool HandleNull => true;

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String ? reader.GetString() ?? "" : "";

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}

/// <summary>The optional-member variant: a string token yields its value, every other
/// token (number/bool/null) yields null — for "use the fallback when absent-or-junk"
/// members (the mod-metadata contact/license strings).</summary>
public sealed class QtCoercingNullableStringConverter : JsonConverter<string?>
{
    public override bool HandleNull => true;

    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String ? reader.GetString() : null;

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}

/// <summary>QJsonValue::toBool(default false) for OPTIONAL members: a bool token yields its
/// value, every other token (string/number/null) yields false.</summary>
public sealed class QtCoercingBoolConverter : JsonConverter<bool>
{
    /// <summary>null participates in the coercion (the QJsonValue fallback),
    /// instead of the STJ default of bypassing the converter.</summary>
    public override bool HandleNull => true;

    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType is JsonTokenType.True;

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) =>
        writer.WriteBooleanValue(value);
}

/// <summary>The tri-state variant: a bool token yields its value, every other token
/// (string/number/null) yields null — for presence-checked flags where junk must not
/// masquerade as false.</summary>
public sealed class QtCoercingNullableBoolConverter : JsonConverter<bool?>
{
    public override bool HandleNull => true;

    public override bool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            _ => null,
        };

    public override void Write(Utf8JsonWriter writer, bool? value, JsonSerializerOptions options) =>
        writer.WriteBooleanValue(value ?? false);
}

/// <summary>The presence-sensitive style-flag variant: an absent member stays null (the
/// caller emits nothing), but a PRESENT member of any kind reads its bool (junk and JSON
/// null read false) — the old Contains(key) + BoolOr() pair of the chat-component style
/// builder, where a present "bold": "yes" still emitted the font-weight span.</summary>
public sealed class QtPresenceBoolConverter : JsonConverter<bool?>
{
    public override bool HandleNull => true;

    public override bool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.True => true,
            _ => false,
        };

    public override void Write(Utf8JsonWriter writer, bool? value, JsonSerializerOptions options) =>
        writer.WriteBooleanValue(value ?? false);
}

/// <summary>The {"packs":[id,...]} index arrays: QJsonValue::toInt() per element (doubles
/// truncate, junk zeroes); a missing member keeps the property default, a JSON null yields
/// an empty list.</summary>
public sealed class QtCoercingIntListConverter : JsonConverter<List<int>>
{
    public override bool HandleNull => true;

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

/// <summary>TruePath <see cref="AbsolutePath"/> as its plain string form (the wire shape of
/// the migration surface's source paths stays a JSON string, unchanged from the pre-typed
/// era). Read direction: a JSON null reads null; anything not a string token — or a string
/// that is not an absolute path — is a JsonException, the ordinary
/// deserialization-failure semantics (no silent coercion).</summary>
public sealed class AbsolutePathJsonConverter : JsonConverter<AbsolutePath>
{
    public override AbsolutePath Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is not JsonTokenType.String)
        {
            throw new JsonException($"expected an absolute path string, found {reader.TokenType}");
        }

        var value = reader.GetString();
        if (value is null)
        {
            throw new JsonException("expected an absolute path string, found null");
        }

        try
        {
            return new AbsolutePath(value);
        }
        catch (ArgumentException e)
        {
            throw new JsonException($"'{value}' is not an absolute path", e);
        }
    }

    public override void Write(Utf8JsonWriter writer, AbsolutePath value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}
