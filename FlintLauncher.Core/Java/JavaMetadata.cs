// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/java/JavaMetadata.{h,cpp}. The P2a parse-half (parseJavaMeta, DownloadType
// and the Metadata record) lived in Minecraft/JavaMetadata.cs and has been MOVED here so the
// whole java/ subsystem sits in the Java/ folder + FlintLauncher.Core.Java namespace; the
// comparator operators (operator<, ==, > against another Metadata) are added on top.
// Java::Metadata descends from BaseVersion upstream — the trivial BaseVersion accessors
// (descriptor/name/typeString) are inlined here.
//
// NOTE: the file holds two namespaces. JsonObjectExtensions (the Qt QJsonObject/QJsonValue
// shim used across the Minecraft format layer since P2a) keeps its original
// FlintLauncher.Core.Minecraft namespace so its many consumers keep compiling; that is also
// why this file uses classic block-scoped namespaces instead of the file-scoped form.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FlintLauncher.Core.Json;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Text;

namespace FlintLauncher.Core.Java;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(JavaRuntimeJson))]
internal sealed partial class JavaJsonContext : JsonSerializerContext;

public enum JavaDownloadType
{
    Manifest,
    Archive,
    Unknown,
}

public sealed class JavaMetadata : IComparable<JavaMetadata>, IEquatable<JavaMetadata>
{
    /// <summary>Port of BaseVersion::descriptor().</summary>
    public string Descriptor() => Version.ToString();

    /// <summary>Port of BaseVersion::name().</summary>
    public string Name() => name;

    /// <summary>Port of BaseVersion::typeString().</summary>
    public string TypeString() => Vendor;

    internal string name = "";

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
        var meta = MapJavaMeta(MapJavaMetaNode(obj));
        return meta;
    }

    /// <summary>The typed read over the runtime entry: the members keep their
    /// QJsonValue fallbacks (toString(default "") / toInteger(0)).</summary>
    internal static JavaMetadata MapJavaMeta(JavaRuntimeJson dto)
    {
        var meta = new JavaMetadata
        {
            name = dto.Name ?? "",
            Vendor = dto.Vendor ?? "",
            Url = dto.Url ?? "",
            ReleaseTime = ParseUtils.TimeFromS3Time(dto.ReleaseTime ?? ""),
            DownloadType = ParseDownloadType(dto.DownloadType ?? ""),
            PackageType = dto.PackageType ?? "",
            RuntimeOs = dto.RuntimeOs ?? "unknown",
        };

        if (dto.Checksum is { } checksum)
        {
            meta.ChecksumHash = checksum.Hash ?? "";
            meta.ChecksumType = checksum.Type ?? "";
        }

        if (dto.Version is { } version)
        {
            meta.Version = new JavaVersion(
                version.Major ?? 0,
                version.Minor ?? 0,
                version.Security ?? 0,
                version.Build ?? 0,
                version.Name ?? "");
        }

        return meta;
    }

    private static JavaRuntimeJson MapJavaMetaNode(JsonObject obj)
    {
        try
        {
            return JsonSerializer.Deserialize(obj, JavaJsonContext.Default.JavaRuntimeJson)
                   ?? throw new JsonParseException("java runtime: Error parsing JSON: document is null");
        }
        catch (JsonException e)
        {
            throw new JsonParseException("java runtime: Error parsing JSON: " + e.Message);
        }
    }

    // ------------------------------------------------------------- comparators

    /// <summary>Port of Metadata::operator&lt;: version, then release time, then name.</summary>
    public bool LessThan(JavaMetadata rhs)
    {
        if (Version < rhs.Version)
        {
            return true;
        }

        if (rhs.Version < Version)
        {
            return false;
        }

        var date = ReleaseTime;
        var rhsDate = rhs.ReleaseTime;
        // Qt compares an invalid QDateTime as smaller than any valid one
        if (date.HasValue != rhsDate.HasValue)
        {
            return !date.HasValue;
        }

        if (date.HasValue && rhsDate.HasValue)
        {
            if (date.Value < rhsDate.Value)
            {
                return true;
            }

            if (rhsDate.Value < date.Value)
            {
                return false;
            }
        }

        return StringUtils.NaturalCompare(name, rhs.name, ignoreCase: true) < 0;
    }

    /// <summary>Port of Metadata::operator==.</summary>
    public bool Equals(JavaMetadata? other)
    {
        if (other is null)
        {
            return false;
        }

        return Version == other.Version && name == other.name;
    }

    /// <summary>Port of Metadata::operator&gt;.</summary>
    public bool GreaterThan(JavaMetadata rhs) => !LessThan(rhs) && !Equals(rhs);

    public int CompareTo(JavaMetadata? other)
    {
        if (other is null)
        {
            return -1;
        }

        if (LessThan(other))
        {
            return -1;
        }

        if (other.LessThan(this))
        {
            return 1;
        }

        return 0;
    }

    public override bool Equals(object? obj) => Equals(obj as JavaMetadata);

    public override int GetHashCode() => HashCode.Combine(Version, name);

    public static bool operator <(JavaMetadata left, JavaMetadata right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return left.LessThan(right);
    }

    public static bool operator >(JavaMetadata left, JavaMetadata right) => left.GreaterThan(right);

    public static bool operator ==(JavaMetadata? left, JavaMetadata? right) => left?.Equals(right) ?? right is null;

    public static bool operator !=(JavaMetadata? left, JavaMetadata? right) => !(left == right);
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
}
