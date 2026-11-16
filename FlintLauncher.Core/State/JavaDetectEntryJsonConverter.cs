// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using JdkFind;

namespace FlintLauncher.Core.State;

public sealed class JavaDetectEntryJsonConverter : JsonConverter<JavaDetectEntry>
{
    public override JavaDetectEntry Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("A java detection entry must be a JSON object.");
        }

        var signature = GetString(root, "signature") ?? "";
        int? languageVersion = null;
        if (root.TryGetProperty("languageVersion", out var language) &&
            language.ValueKind != JsonValueKind.Null)
        {
            if (!language.TryGetInt32(out var languageValue))
            {
                throw new JsonException("The java detection languageVersion must be an integer.");
            }

            languageVersion = languageValue;
        }

        if (root.TryGetProperty("executable", out var executableElement))
        {
            var executable = JsonSerializer.Deserialize(executableElement,
                (JsonTypeInfo<JavaExecutable>)options.GetTypeInfo(typeof(JavaExecutable))) ?? UnknownExecutable();
            return new JavaDetectEntry(signature, executable, languageVersion);
        }

        var version = GetString(root, "version") ?? "";
        var parsedVersion = JvmVersion.TryParse(version, out var parsed) ? parsed : JvmVersion.Unknown(version);
        var legacy = new JavaExecutable
        {
            Path = "",
            Version = parsedVersion,
            Architecture = GetString(root, "realArchitecture") ?? "",
            VendorRaw = GetString(root, "vendor") ?? "",
        };
        return new JavaDetectEntry(signature, legacy, languageVersion);
    }

    public override void Write(Utf8JsonWriter writer, JavaDetectEntry value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("signature", value.Signature);
        writer.WritePropertyName("executable");
        JsonSerializer.Serialize(writer, value.Executable,
            (JsonTypeInfo<JavaExecutable>)options.GetTypeInfo(typeof(JavaExecutable)));
        if (value.LanguageVersion is { } languageVersion)
        {
            writer.WriteNumber("languageVersion", languageVersion);
        }
        else
        {
            writer.WriteNull("languageVersion");
        }

        writer.WriteEndObject();
    }

    private static string? GetString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static JavaExecutable UnknownExecutable() => new()
    {
        Path = "",
        Version = JvmVersion.Unknown(),
    };
}
