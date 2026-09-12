// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/java/JavaMetadata.{h,cpp} (parseJavaMeta, DownloadType and the Metadata
// record). Only what the version-file "runtimes" member needs is ported; the comparator
// operators and the download flow belong to the Java-install layer and are left for a later
// phase. Java::Metadata descends from BaseVersion upstream — the trivial BaseVersion accessors
// (descriptor/name/typeString) are inlined here.
using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.Java;
using FlintLauncher.Core.Json;

namespace FlintLauncher.Core.Minecraft;

public enum JavaDownloadType
{
    Manifest,
    Archive,
    Unknown,
}

public sealed class JavaMetadata
{
    /// <summary>Port of BaseVersion::descriptor().</summary>
    public string Descriptor() => Version.ToString();

    /// <summary>Port of BaseVersion::name().</summary>
    public string Name() => _name;

    /// <summary>Port of BaseVersion::typeString().</summary>
    public string TypeString() => Vendor;

    private string _name = "";

    public string Vendor { get; set; } = "";

    public string Url { get; set; } = "";

    public DateTimeOffset? ReleaseTime { get; set; }

    public string ChecksumType { get; set; } = "";

    public string ChecksumHash { get; set; } = "";

    public JavaDownloadType DownloadType { get; set; } = JavaDownloadType.Unknown;

    public string PackageType { get; set; } = "";

    public JavaVersion Version { get; set; } = new();

    public string RuntimeOs { get; set; } = "";

    public static JavaDownloadType ParseDownloadType(string javaDownload)
    {
        return javaDownload switch
        {
            "manifest" => JavaDownloadType.Manifest,
            "archive" => JavaDownloadType.Archive,
            _ => JavaDownloadType.Unknown,
        };
    }

    public static string DownloadTypeToString(JavaDownloadType javaDownload)
    {
        return javaDownload switch
        {
            JavaDownloadType.Manifest => "manifest",
            JavaDownloadType.Archive => "archive",
            _ => "unknown",
        };
    }

    public static JavaMetadata ParseJavaMeta(JsonObject obj)
    {
        var meta = new JavaMetadata();

        meta._name = obj.StringOr("name");
        meta.Vendor = obj.StringOr("vendor");
        meta.Url = obj.StringOr("url");
        meta.ReleaseTime = ParseUtils.TimeFromS3Time(obj.StringOr("releaseTime"));
        meta.DownloadType = ParseDownloadType(obj.StringOr("downloadType"));
        meta.PackageType = obj.StringOr("packageType");
        meta.RuntimeOs = obj.StringOr("runtimeOS", "unknown");

        if (obj.Contains("checksum"))
        {
            var checksumObj = JsonUtils.RequireObject(obj, "checksum");
            checksumObj.TryGetProperty("hash", out var hash);
            meta.ChecksumHash = JsonUtils.EnsureString(hash);
            checksumObj.TryGetProperty("type", out var type);
            meta.ChecksumType = JsonUtils.EnsureString(type);
        }

        if (obj.Contains("version"))
        {
            var versionObj = JsonUtils.RequireObject(obj, "version");
            versionObj.TryGetProperty("name", out var nameNode);
            var name = JsonUtils.EnsureString(nameNode);
            // QJsonValue::toInteger() yields 0 for missing/non-numeric members
            var major = IntegerOr(versionObj, "major");
            var minor = IntegerOr(versionObj, "minor");
            var security = IntegerOr(versionObj, "security");
            var build = IntegerOr(versionObj, "build");
            meta.Version = new JavaVersion(major, minor, security, build, name);
        }
        return meta;
    }

    /// <summary>QJsonValue::toInteger(default) on an object member (0 for missing/non-numbers).</summary>
    private static int IntegerOr(JsonElement obj, string key)
    {
        return obj.TryGetProperty(key, out var value) ? JsonUtils.EnsureInteger(value) : 0;
    }
}

/// <summary>QJsonObject/QJsonValue member accessors with Qt defaults, used across the formats.</summary>
public static class JsonObjectExtensions
{
    /// <summary>QJsonObject::contains(key) — true even when the member holds JSON null.</summary>
    public static bool Contains(this JsonObject obj, string key) => obj.ContainsKey(key);

    /// <summary>QJsonValue::toString(defaultValue) on a member.</summary>
    public static string StringOr(this JsonObject obj, string key, string defaultValue = "")
    {
        return obj.TryGetPropertyValue(key, out var node) ? StringOr(node, defaultValue) : defaultValue;
    }

    /// <summary>QJsonValue::toString(defaultValue).</summary>
    public static string StringOr(JsonNode? node, string defaultValue = "")
    {
        return node is JsonValue value && value.TryGetValue<string>(out var str) ? str : defaultValue;
    }

    /// <summary>QJsonValue::toBool(defaultValue) on a member.</summary>
    public static bool BoolOr(this JsonObject obj, string key, bool defaultValue = false)
    {
        return obj.TryGetPropertyValue(key, out var node) ? BoolOr(node, defaultValue) : defaultValue;
    }

    /// <summary>QJsonValue::toBool(defaultValue).</summary>
    public static bool BoolOr(JsonNode? node, bool defaultValue = false)
    {
        return node is JsonValue value && value.TryGetValue<bool>(out var b) ? b : defaultValue;
    }

    /// <summary>QJsonValue::toInteger(defaultValue = 0) on a member.</summary>
    public static int IntegerOr(this JsonObject obj, string key, int defaultValue = 0)
    {
        return obj.TryGetPropertyValue(key, out var node) ? IntegerOr(node, defaultValue) : defaultValue;
    }

    /// <summary>QJsonValue::toInteger(defaultValue = 0).</summary>
    public static int IntegerOr(JsonNode? node, int defaultValue = 0)
    {
        return node is JsonValue value && value.TryGetValue<double>(out var d) && d % 1 == 0 ? (int)d : defaultValue;
    }

    /// <summary>
    /// Converts a JsonNode into a JsonElement so the JsonUtils require*/ensure* family applies
    /// to it (the same bridge JsonUtils uses internally for JsonObject members).
    /// </summary>
    public static JsonElement ToElement(this JsonNode? node)
    {
        using var doc = JsonDocument.Parse(node?.ToJsonString() ?? "null");
        // clone so the element outlives the document
        return doc.RootElement.Clone();
    }

    /// <summary>Bridges a JsonElement back to a JsonObject (throws when it is not an object).</summary>
    public static JsonObject ToJsonObject(this JsonElement element)
    {
        return JsonNode.Parse(element.GetRawText()) as JsonObject
            ?? throw new JsonParseException("Value is not an object");
    }
}
