// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// The mod-metadata wire: typed probes
// over the four JSON schemas LocalModParse consumes — mcmod.info, fabric.mod.json,
// quilt.mod.json and litemod.json. These are fixed structured formats of files WE consume,
// so the former "heuristic" node probing becomes records + the Qt coercion converters
// (QJsonValue::toString("")/toInt() semantics per member) + union declarations for the
// documented polymorphic members (icon string|map, license string|object|array, authors
// string|{name}, quilt depends string|{id,optional}, the mcmod.info object|array root).
//
// Failure behavior: a token-stream-level broken document fails the deserialization and the
// reader yields empty details (the former ParseJson null path); a wrong-kind MEMBER
// degrades per member through the coercing converters, like the former `is JsonArray` /
// StringOr field probes. The records are read-only (nothing here is ever written).

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FlintLauncher.Core.Json;

namespace FlintLauncher.Core.Minecraft.Resources;

// --------------------------------------------------------------------------- mcmod.info

/// <summary>The mcmod.info root: the object {"modinfoversion"|"modListVersion",
/// "modlist"|"modList"} or the very old bare entry array. The version member is dropped —
/// the port only warned about it.</summary>
internal union McModInfoDocument(List<McModEntryJson?>, McModInfoObject);

internal sealed record McModInfoObject
{
    [JsonPropertyName("modlist")]
    public List<McModEntryJson?>? Modlist { get; set; }

    [JsonPropertyName("modList")]
    public List<McModEntryJson?>? ModList { get; set; }
}

internal sealed record McModEntryJson
{
    [JsonPropertyName("modid")]
    [JsonConverter(typeof(QtCoercingStringConverter))]
    public string ModId { get; set; } = "";

    [JsonConverter(typeof(QtCoercingStringConverter))]
    public string Name { get; set; } = "";

    [JsonConverter(typeof(QtCoercingStringConverter))]
    public string Version { get; set; } = "";

    [JsonConverter(typeof(QtCoercingStringConverter))]
    public string Url { get; set; } = "";

    [JsonConverter(typeof(QtCoercingStringConverter))]
    public string Description { get; set; } = "";

    /// <summary>Presence-checked upstream (a present-but-junk logoFile sets IconFile "");
    /// the nullable-coercing read makes a wrong-kind value null and the mapper leaves the
    /// default "" IconFile — the same observable outcome.</summary>
    [JsonConverter(typeof(QtCoercingNullableStringConverter))]
    public string? LogoFile { get; set; }

    [JsonPropertyName("authorList")]
    [JsonConverter(typeof(CoercingStringListConverter))]
    public List<string>? AuthorList { get; set; }

    [JsonConverter(typeof(CoercingStringListConverter))]
    public List<string>? Authors { get; set; }

    [JsonPropertyName("requiredMods")]
    [JsonConverter(typeof(CoercingStringListConverter))]
    public List<string>? RequiredMods { get; set; }

    [JsonConverter(typeof(CoercingStringListConverter))]
    public List<string>? Dependencies { get; set; }
}

/// <summary>An array of coerce-to-string elements (junk elements become ""); a non-array
/// member reads null — the former `is JsonArray` field probe.</summary>
internal sealed class CoercingStringListConverter : JsonConverter<List<string>?>
{
    public override List<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is not JsonTokenType.StartArray)
        {
            return null;
        }

        var result = new List<string>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return result;
            }

            result.Add(reader.TokenType == JsonTokenType.String ? reader.GetString() ?? "" : "");
        }

        throw new JsonException("unterminated array");
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

// --------------------------------------------------------------------- shared polymorphs

/// <summary>The license entry of fabric/quilt metadata: the string form just the name
/// (ModLicense parses the URL), the object form carries the details verbatim.</summary>
internal union FabricLicenseJson(string, FabricLicenseObject);

internal sealed record FabricLicenseObject
{
    public required string Name { get; set; }

    public string? Id { get; set; }
    public string? Url { get; set; }
    public string? Description { get; set; }
}

/// <summary>license: one entry or an array of entries.</summary>
internal union FabricLicenseValue(string, FabricLicenseObject, List<FabricLicenseJson>);

/// <summary>The icon member: a single path string or a size-keyed map ("32x32" → path).
/// The largest/first resolution stays in the mapper.</summary>
internal union FabricIconJson(string, Dictionary<string, string>);

internal static class ModMetadataCoercion
{
    public static string String(JsonObject? obj, string key) =>
        obj is not null && obj.TryGetPropertyValue(key, out var node) &&
        node is JsonValue value && value.TryGetValue<string>(out var s)
            ? s
            : "";

    public static string? StringOrNull(JsonObject? obj, string key)
    {
        var value = String(obj, key);
        return value.Length != 0 ? value : null;
    }
}

/// <summary>String/map are the valid icon shapes; anything else reads absent and
/// non-string map values coerce to "" (the former QJsonValue::toString read).</summary>
internal sealed class TolerantFabricIconConverter : JsonConverter<FabricIconJson?>
{
    public override FabricIconJson? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return (FabricIconJson)(reader.GetString() ?? "");
            case JsonTokenType.StartObject:
            {
                var node = JsonNode.Parse(ref reader) as JsonObject;
                var sizes = new Dictionary<string, string>(StringComparer.Ordinal);
                if (node is not null)
                {
                    foreach (var (key, value) in node)
                    {
                        sizes[key] = value is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
                    }
                }

                return (FabricIconJson)sizes;
            }
            default:
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, FabricIconJson? value, JsonSerializerOptions options) =>
        throw new NotSupportedException("the mod metadata records are read-only");
}

/// <summary>String/object/array are the valid license shapes; junk array elements are
/// skipped and object fields coerce like the former node reads.</summary>
internal sealed class TolerantFabricLicenseConverter : JsonConverter<FabricLicenseValue?>
{
    public override FabricLicenseValue? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return (FabricLicenseValue)(reader.GetString() ?? "");
            case JsonTokenType.StartObject:
                return (FabricLicenseValue)ReadObject(ref reader);
            case JsonTokenType.StartArray:
            {
                var result = new List<FabricLicenseJson>();
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndArray)
                    {
                        return (FabricLicenseValue)result;
                    }

                    if (reader.TokenType == JsonTokenType.String)
                    {
                        result.Add((FabricLicenseJson)(reader.GetString() ?? ""));
                    }
                    else if (reader.TokenType == JsonTokenType.StartObject)
                    {
                        result.Add((FabricLicenseJson)ReadObject(ref reader));
                    }
                }

                throw new JsonException("unterminated array");
            }
            default:
                return null;
        }
    }

    private static FabricLicenseObject ReadObject(ref Utf8JsonReader reader)
    {
        var node = JsonNode.Parse(ref reader) as JsonObject;
        return new FabricLicenseObject
        {
            Name = ModMetadataCoercion.String(node, "name"),
            Id = ModMetadataCoercion.StringOrNull(node, "id"),
            Url = ModMetadataCoercion.StringOrNull(node, "url"),
            Description = ModMetadataCoercion.StringOrNull(node, "description"),
        };
    }

    public override void Write(Utf8JsonWriter writer, FabricLicenseValue? value, JsonSerializerOptions options) =>
        throw new NotSupportedException("the mod metadata records are read-only");
}

/// <summary>Author entries are strings or {name} objects; junk entries coerce to ""
/// (the former StringOr semantics). A non-array member reads null.</summary>
internal sealed class TolerantFabricAuthorsConverter : JsonConverter<List<FabricAuthorJson>?>
{
    public override List<FabricAuthorJson>? Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType is not JsonTokenType.StartArray)
        {
            return null;
        }

        var result = new List<FabricAuthorJson>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return result;
            }

            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    result.Add((FabricAuthorJson)(reader.GetString() ?? ""));
                    break;
                case JsonTokenType.StartObject:
                {
                    var node = JsonNode.Parse(ref reader) as JsonObject;
                    result.Add((FabricAuthorJson)new FabricAuthorObject
                    {
                        Name = ModMetadataCoercion.String(node, "name"),
                    });
                    break;
                }
                default:
                    result.Add((FabricAuthorJson)"");
                    break;
            }
        }

        throw new JsonException("unterminated array");
    }

    public override void Write(Utf8JsonWriter writer, List<FabricAuthorJson>? value, JsonSerializerOptions options) =>
        throw new NotSupportedException("the mod metadata records are read-only");
}

/// <summary>A JSON object read as its string-keyed raw elements (the "iterate the keys"
/// members: fabric depends, quilt contributors); a non-object reads null.</summary>
internal sealed class CoercingObjectElementDictionaryConverter : JsonConverter<Dictionary<string, JsonElement>?>
{
    public override Dictionary<string, JsonElement>? Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType is not JsonTokenType.StartObject)
        {
            return null;
        }

        var node = JsonNode.Parse(ref reader) as JsonObject;
        if (node is null)
        {
            return [];
        }

        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in node)
        {
            result[key] = JsonDocument.Parse(value?.ToJsonString() ?? "null").RootElement.Clone();
        }

        return result;
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, JsonElement>? value,
        JsonSerializerOptions options) => throw new NotSupportedException("the mod metadata records are read-only");
}

// ---------------------------------------------------------------------- fabric.mod.json

internal sealed record FabricModJson
{
    [JsonConverter(typeof(QtCoercingIntConverter))]
    public required int SchemaVersion { get; set; }

    [JsonPropertyName("id")]
    [JsonConverter(typeof(QtCoercingStringConverter))]
    public required string Id { get; set; }

    [JsonConverter(typeof(QtCoercingStringConverter))]
    public required string Version { get; set; }

    /// <summary>Absent-or-junk falls back to the mod id (the spec default).</summary>
    [JsonConverter(typeof(QtCoercingNullableStringConverter))]
    public string? Name { get; set; }

    [JsonConverter(typeof(QtCoercingStringConverter))]
    public string Description { get; set; } = "";

    [JsonConverter(typeof(TolerantFabricAuthorsConverter))]
    public List<FabricAuthorJson>? Authors { get; set; }

    /// <summary>Only an object counts (the former `is JsonObject` probe); a wrong-kind
    /// member reads null and the rest of the document survives.</summary>
    [JsonConverter(typeof(FabricContactConverter))]
    public FabricContactJson? Contact { get; set; }

    [JsonConverter(typeof(TolerantFabricLicenseConverter))]
    public FabricLicenseValue? License { get; set; }

    [JsonConverter(typeof(TolerantFabricIconConverter))]
    public FabricIconJson? Icon { get; set; }

    [JsonConverter(typeof(CoercingObjectElementDictionaryConverter))]
    public Dictionary<string, JsonElement>? Depends { get; set; }
}

/// <summary>An author entry: a plain string or a {name} object.</summary>
internal union FabricAuthorJson(string, FabricAuthorObject);

internal sealed record FabricAuthorObject
{
    public required string Name { get; set; }
}

internal sealed record FabricContactJson
{
    [JsonConverter(typeof(QtCoercingNullableStringConverter))]
    public string? Homepage { get; set; }

    [JsonConverter(typeof(QtCoercingNullableStringConverter))]
    public string? Issues { get; set; }
}

/// <summary>A non-object contact member reads null (the former `is JsonObject` probe) so
/// the rest of the document survives.</summary>
internal sealed class FabricContactConverter : JsonConverter<FabricContactJson?>
{
    public override FabricContactJson? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType == JsonTokenType.StartObject
            ? JsonSerializer.Deserialize(ref reader, ModMetadataJsonContext.Default.FabricContactJson)
            : null;
    }

    public override void Write(Utf8JsonWriter writer, FabricContactJson? value, JsonSerializerOptions options) =>
        throw new NotSupportedException("the mod metadata records are read-only");
}

// ---------------------------------------------------------------------- quilt.mod.json

internal sealed record QuiltModJson
{
    [JsonPropertyName("schema_version")]
    [JsonConverter(typeof(QtCoercingIntConverter))]
    public required int SchemaVersion { get; set; }

    [JsonPropertyName("quilt_loader")]
    public required QuiltLoaderJson QuiltLoader { get; set; }

    [JsonConverter(typeof(TolerantQuiltDependsConverter))]
    public List<QuiltDependJson>? Depends { get; set; }
}

internal sealed record QuiltLoaderJson
{
    [JsonConverter(typeof(QtCoercingStringConverter))]
    public required string Id { get; set; }

    [JsonConverter(typeof(QtCoercingStringConverter))]
    public required string Version { get; set; }

    /// <summary>A non-object metadata member reads null (the former `as JsonObject ?? []`
    /// probe) so a broken member cannot empty the whole mod.</summary>
    [JsonConverter(typeof(QuiltMetadataConverter))]
    public QuiltMetadataJson? Metadata { get; set; }
}

internal sealed record QuiltMetadataJson
{
    [JsonConverter(typeof(QtCoercingNullableStringConverter))]
    public string? Name { get; set; }

    [JsonConverter(typeof(QtCoercingStringConverter))]
    public string Description { get; set; } = "";

    /// <summary>The contributor map: the KEYS are the names (roles ignored).</summary>
    [JsonConverter(typeof(CoercingObjectElementDictionaryConverter))]
    public Dictionary<string, JsonElement>? Contributors { get; set; }

    [JsonConverter(typeof(FabricContactConverter))]
    public FabricContactJson? Contact { get; set; }

    [JsonConverter(typeof(TolerantFabricLicenseConverter))]
    public FabricLicenseValue? License { get; set; }

    [JsonConverter(typeof(TolerantFabricIconConverter))]
    public FabricIconJson? Icon { get; set; }
}

internal sealed class QuiltMetadataConverter : JsonConverter<QuiltMetadataJson?>
{
    public override QuiltMetadataJson? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType == JsonTokenType.StartObject
            ? JsonSerializer.Deserialize(ref reader, ModMetadataJsonContext.Default.QuiltMetadataJson)
            : null;
    }

    public override void Write(Utf8JsonWriter writer, QuiltMetadataJson? value, JsonSerializerOptions options) =>
        throw new NotSupportedException("the mod metadata records are read-only");
}

/// <summary>A dependency entry: a mod id string or an {id, optional} object.</summary>
internal union QuiltDependJson(string, QuiltDependObject);

internal sealed record QuiltDependObject
{
    public required string Id { get; set; }

    public bool? Optional { get; set; }
}

/// <summary>Dependency entries are id strings or {id, optional} objects; junk elements
/// are dropped and junk optional flags read null (the former node reads).</summary>
internal sealed class TolerantQuiltDependsConverter : JsonConverter<List<QuiltDependJson>?>
{
    public override List<QuiltDependJson>? Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType is not JsonTokenType.StartArray)
        {
            return null;
        }

        var result = new List<QuiltDependJson>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return result;
            }

            if (reader.TokenType == JsonTokenType.String)
            {
                result.Add((QuiltDependJson)(reader.GetString() ?? ""));
            }
            else if (reader.TokenType == JsonTokenType.StartObject)
            {
                var node = JsonNode.Parse(ref reader) as JsonObject;
                result.Add((QuiltDependJson)new QuiltDependObject
                {
                    Id = ModMetadataCoercion.String(node, "id"),
                    Optional = node is not null && node.TryGetPropertyValue("optional", out var optional) &&
                               optional is JsonValue ov && ov.TryGetValue<bool>(out var flag)
                        ? flag
                        : null,
                });
            }
        }

        throw new JsonException("unterminated array");
    }

    public override void Write(Utf8JsonWriter writer, List<QuiltDependJson>? value, JsonSerializerOptions options) =>
        throw new NotSupportedException("the mod metadata records are read-only");
}

// ------------------------------------------------------------------------ litemod.json

internal sealed record LiteModJson
{
    [JsonConverter(typeof(QtCoercingStringConverter))]
    public string Name { get; set; } = "";

    [JsonConverter(typeof(QtCoercingStringConverter))]
    public string Version { get; set; } = "";

    [JsonConverter(typeof(QtCoercingStringConverter))]
    public string Revision { get; set; } = "";

    [JsonPropertyName("mcversion")]
    [JsonConverter(typeof(QtCoercingStringConverter))]
    public string McVersion { get; set; } = "";

    [JsonConverter(typeof(QtCoercingStringConverter))]
    public string Author { get; set; } = "";

    [JsonConverter(typeof(QtCoercingStringConverter))]
    public string Description { get; set; } = "";

    [JsonConverter(typeof(QtCoercingStringConverter))]
    public string Url { get; set; } = "";
}

[JsonSourceGenerationOptions(RespectNullableAnnotations = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(McModInfoDocument))]
[JsonSerializable(typeof(List<McModEntryJson>))]
[JsonSerializable(typeof(FabricModJson))]
[JsonSerializable(typeof(QuiltModJson))]
[JsonSerializable(typeof(LiteModJson))]
internal sealed partial class ModMetadataJsonContext : JsonSerializerContext;
