// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/java/JavaMetadata.{h,cpp}. The P2a parse-half (parseJavaMeta, DownloadType
// and the Metadata record) lived in Minecraft/JavaMetadata.cs and has been MOVED here so the
// whole java/ subsystem sits in the Java/ folder + FlintLauncher.Core.Java namespace; the
// comparison operators (<, >, <=, >= over CompareTo; == is the record's Equals) are added on top.
// Java::Metadata descends from BaseVersion upstream — the trivial BaseVersion accessors
// (descriptor/name/typeString) are inlined here.
//
// NOTE: the file holds two namespaces. JsonObjectExtensions (the Qt QJsonObject/QJsonValue
// shim used across the Minecraft format layer since P2a) keeps its original
// FlintLauncher.Core.Minecraft namespace so its many consumers keep compiling; that is also
// why this file uses classic block-scoped namespaces instead of the file-scoped form.

using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Text;
using JdkFind;

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

public sealed record JavaMetadata : IComparable<JavaMetadata>,
    IComparisonOperators<JavaMetadata, JavaMetadata, bool>
{
    /// <summary>Port of BaseVersion::descriptor().</summary>
    public string Descriptor() => Version.ToString();

    /// <summary>Port of BaseVersion::name().</summary>
    public string Name { get; internal init; } = "";

    /// <summary>Port of BaseVersion::typeString().</summary>
    public string TypeString() => Vendor;

    public string Vendor { get; init; } = "";

    public string Url { get; init; } = "";

    public DateTimeOffset? ReleaseTime { get; init; }

    public string ChecksumType { get; set; } = "";

    public string ChecksumHash { get; set; } = "";

    public JavaDownloadType DownloadType { get; init; } = JavaDownloadType.Unknown;

    public string PackageType { get; init; } = "";

    public JvmVersion Version { get; init; } = JvmVersion.Unknown();

    public string RuntimeOs { get; init; } = "";

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
    private static JavaMetadata MapJavaMeta(JavaRuntimeJson dto)
    {
        var meta = new JavaMetadata
        {
            Name = dto.Name ?? "",
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
            meta = meta with
            {
                Version = JvmVersion.Parse(ComposeVersion(
                    version.Major ?? 0,
                    version.Minor ?? 0,
                    version.Security ?? 0,
                    version.Build ?? 0))
            };
        }

        return meta;
    }

    private static JavaRuntimeJson MapJavaMetaNode(JsonObject obj)
    {
        JavaRuntimeJson? wire;
        try
        {
            wire = obj.Deserialize(JavaJsonContext.Default.JavaRuntimeJson);
        }
        catch (JsonException e)
        {
            throw new JsonException("java runtime: Error parsing JSON: " + e.Message);
        }

        return wire ?? throw new JsonException("java runtime: Error parsing JSON: document is null");
    }

    /// <summary>The version string the C++ JavaVersion(major, minor, security, build)
    /// constructor built from the meta "version" members: trailing zero components are
    /// dropped, a zero between non-zero components is kept, and a non-zero build becomes the
    /// fourth component. It is the descriptor the entry is displayed and matched by, so the
    /// shape is kept verbatim and JdkFind only parses the result.</summary>
    internal static string ComposeVersion(int major, int minor, int security, int build)
    {
        var versions = new List<string>();
        if (build != 0)
        {
            versions.Insert(0, build.ToString(CultureInfo.InvariantCulture));
        }

        if (security != 0)
        {
            versions.Insert(0, security.ToString(CultureInfo.InvariantCulture));
        }
        else if (versions.Count > 0)
        {
            versions.Insert(0, "0");
        }

        if (minor != 0)
        {
            versions.Insert(0, minor.ToString(CultureInfo.InvariantCulture));
        }
        else if (versions.Count > 0)
        {
            versions.Insert(0, "0");
        }

        versions.Insert(0, major.ToString(CultureInfo.InvariantCulture));
        return string.Join(".", versions);
    }

    // ------------------------------------------------------------- comparators

    /// <summary>The Metadata ordering as a trichotomy — the port of Metadata::operator&lt;:
    /// version, then release time, then name.</summary>
    public int CompareTo(JavaMetadata? other)
    {
        if (other is null)
        {
            return -1;
        }

        var versionCompare = Version.CompareTo(other.Version);
        if (versionCompare != 0)
        {
            return versionCompare;
        }

        var date = ReleaseTime;
        var rhsDate = other.ReleaseTime;
        // Qt compares an invalid QDateTime as smaller than any valid one
        if (date.HasValue != rhsDate.HasValue)
        {
            return date.HasValue ? 1 : -1;
        }

        if (date.HasValue && rhsDate.HasValue)
        {
            var dateCompare = date.Value.CompareTo(rhsDate.Value);
            if (dateCompare != 0)
            {
                return dateCompare;
            }
        }

        return Math.Sign(StringUtils.NaturalCompare(Name, other.Name, ignoreCase: true));
    }

    /// <summary>Port of Metadata::operator==.</summary>
    public bool Equals(JavaMetadata? other)
    {
        if (other is null)
        {
            return false;
        }

        return Version == other.Version && Name == other.Name;
    }

    public override int GetHashCode() => HashCode.Combine(Version, Name);

    public static bool operator <(JavaMetadata left, JavaMetadata right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return left.CompareTo(right) < 0;
    }

    public static bool operator >(JavaMetadata left, JavaMetadata right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return left.CompareTo(right) > 0;
    }

    public static bool operator <=(JavaMetadata left, JavaMetadata right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return left.CompareTo(right) <= 0;
    }

    public static bool operator >=(JavaMetadata left, JavaMetadata right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return left.CompareTo(right) >= 0;
    }
}
