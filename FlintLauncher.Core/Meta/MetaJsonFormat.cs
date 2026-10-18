// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/meta/JsonFormat.{h,cpp}: the shared metadata format version handling and
// the Require type plus the index/version/version-list parsers.
//
// Serialization decision (revised): the index and version-list readers deserialize typed
// probes through the MetaJsonContext and fail the DOCUMENT on malformed members — the
// former requireIsArrayOf quirks (a non-array packages/versions member parsing to an EMPTY
// list, per-key require texts) were internal failure-path behavior with no external
// consumer: meta documents come from the metadata server, fail the sha256 gate on any
// deviation, and a failed parse just refetches. Input compatibility is kept where it is
// external: formatVersion accepts any JSON number token with value 0/1 (raw element gate),
// and the optional members keep their QJsonValue fallbacks. The version DOCUMENT reader
// stays node-based (shares the Require reads with OneSixVersionFormat, own batch); the
// requires (de)serialization helpers are shared with PackProfile and stay node-based too.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using FlintLauncher.Core.Java;
using FlintLauncher.Core.Json;
using FlintLauncher.Core.Minecraft;

namespace FlintLauncher.Core.Meta;

public enum MetadataVersion
{
    Invalid = -1,
    InitialRelease = 1,
}

/// <summary>Port of Meta::ParseException. Routed to the same refetch path as
/// JsonParseException by the metadata-document consumers (see MetaEntity.ParsingValidator,
/// whose filter used to miss this type); derives directly from Exception like the upstream
/// ::Exception root.</summary>
public sealed class MetaParseException(string message) : Exception(message) { }

/// <summary>Port of Meta::Require. Set identity is by uid only, like the C++ operator==/operator&lt;.
/// DeepEquals compares all fields (used by Component's deep-inspect change detection).</summary>
public sealed class Require: IEquatable<Require>, IComparable<Require>
{
    public required string Uid { get; init; }

    public string EqualsVersion { get; init; } = "";

    public string Suggests { get; init; } = "";

    /// <summary>Uid-only equality — the set identity semantics (C++ operator==).</summary>
    public bool Equals(Require? other) => other is not null && Uid == other.Uid;

    public override bool Equals(object? obj) => Equals(obj as Require);

    public override int GetHashCode() => Uid.GetHashCode();

    /// <summary>Uid-only ordering — matches the C++ operator&lt; (port of std::set ordering).</summary>
    public int CompareTo(Require? other) =>
        other is null ? 1 : string.CompareOrdinal(Uid, other.Uid);

    /// <summary>Full-field comparison — detects content changes within the same uid.</summary>
    public bool DeepEquals(Require rhs)
    {
        return Uid == rhs.Uid && EqualsVersion == rhs.EqualsVersion && Suggests == rhs.Suggests;
    }

    public override string ToString() => Uid;
}

/// <summary>The index/version-list shapes as parsed DTOs. formatVersion stays a raw element
/// so the "any JSON number token" input compatibility of the former EnsureDouble read is
/// preserved (a quoted "1" is NOT a format version); the required strings are validated by
/// the mappers with document-level failures.</summary>
internal sealed record MetaIndexJson(
    JsonElement? FormatVersion,    List<MetaPackageJson>? Packages);
internal sealed record MetaPackageJson(
    string? Uid,    string? Name,    string? Sha256);
internal sealed record MetaVersionListJson(
    JsonElement? FormatVersion,    string? Uid,    string? Name,    List<MetaVersionEntryJson>? Versions);
internal sealed record MetaVersionEntryJson(
    string? Version,    string? ReleaseTime,    string? Type,    bool? Recommended,    bool? Volatile,    List<MetaRequireJson>? Requires,    List<MetaRequireJson>? Conflicts,    string? Sha256);
internal sealed record MetaRequireJson(
    string? Uid,    [property: JsonPropertyName("equals")] string? EqualsVersion,
    string? Suggests);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MetaIndexJson))]
[JsonSerializable(typeof(MetaVersionListJson))]
internal sealed partial class MetaJsonContext : JsonSerializerContext;

public static class MetaJsonFormat
{
    /// <summary>Port of Meta::currentFormatVersion().</summary>
    public static MetadataVersion CurrentFormatVersion() => MetadataVersion.InitialRelease;

    /// <summary>The formatVersion gate over the raw member: a JSON number of 0 or 1 is the
    /// InitialRelease, everything else (missing, null, non-number, any other value) fails
    /// the document — the EnsureDouble(raw token) input compatibility of the former read.</summary>
    private static MetadataVersion FormatVersionOf(JsonElement? formatVersion)
    {
        if (formatVersion is { ValueKind: JsonValueKind.Number } raw)
        {
            var value = raw.GetDouble();
            if (value == 0 || value == 1)
            {
                return MetadataVersion.InitialRelease;
            }
        }

        return MetadataVersion.Invalid;
    }

    private static string RequireStringOf(string? value, string key) =>
        value ?? throw new MetaParseException("'" + key + "' is not a string");

    /// <summary>A member the document schema requires (absent or explicit null fails the
    /// document — the reasonable failure behavior of the typed readers).</summary>
    private static MetaParseException MissingMember(string key) =>
        new($"The metadata document has no '{key}' member.");

    // ------------------------------------------------- node-side member readers
    //
    // FlameJson convention: the helper parameters are JsonNode? so JsonObject arguments upcast
    // at the call site instead of re-binding the (JsonObject, key, what) element-bridge
    // overloads — see the JsonUtils node-family migration rule.

    /// <summary>obj["key"] equivalent: null node when missing or not an object.</summary>
    private static JsonNode? Ptr(JsonNode? node, string key) => node is JsonObject obj ? obj[key] : null;

    /// <summary>QJsonObject::contains(key) equivalent: true even when the member holds JSON null.</summary>
    private static bool Has(JsonNode? node, string key) => node is JsonObject obj && obj.Contains(key);

    /// <summary>Port of require&lt;String&gt;(parent, key).</summary>
    private static string RequireStringOf(JsonNode? parent, string key) =>
        JsonUtils.RequireString(parent, key, "'" + key + "'");

    /// <summary>parent.value(key).toString(default) equivalent.</summary>
    private static string Str(JsonNode? node, string key, string def = "") =>
        JsonObjectExtensions.StringOr(Ptr(node, key), def);

    /// <summary>parent.value(key).toBool(default) equivalent.</summary>
    private static bool Bool(JsonNode? node, string key, bool def = false) =>
        JsonObjectExtensions.BoolOr(Ptr(node, key), def);

    // ------------------------------------------------------------------ Index

    private static Index ParseIndexInternal(MetaIndexJson dto)
    {
        var packages = dto.Packages ?? throw MissingMember("packages");
        var lists = new List<VersionList>(packages.Count);
        foreach (var element in packages)
        {
            if (element is null)
            {
                throw new MetaParseException("Document is not an object");
            }

            var list = new VersionList(RequireStringOf(element.Uid, "uid"))
            {
                Name = element.Name ?? "",
                Sha256 = element.Sha256 ?? "",
            };
            lists.Add(list);
        }

        return new Index(lists);
    }

    // ------------------------------------------------------------------ Version document
    //
    // The version DOCUMENT parse stays node-based: it shares the Require reads with the
    // OneSix version format and hands the whole object to OneSixVersionFormat (own batch).

    private static MetaVersion ParseVersionInternal(JsonNode obj)
    {
        var version = new MetaVersion(RequireStringOf(obj, "uid"), RequireStringOf(obj, "version"));
        // QDateTime::fromString(..., Qt::ISODate).toMSecsSinceEpoch() / 1000; an invalid
        // QDateTime contributes 0
        var releaseTime = ParseUtils.TimeFromS3Time(RequireStringOf(obj, "releaseTime"));
        version.RawTime = releaseTime?.ToUnixTimeMilliseconds() / 1000 ?? 0;
        version.Type = Str(obj, "type");
        version.IsRecommended = Bool(obj, "recommended");
        version.SetVolatile(Bool(obj, "volatile"));
        var reqs = new SortedSet<Require>();
        var conflicts = new SortedSet<Require>();
        ParseRequires(obj, reqs);
        ParseRequires(obj, conflicts, "conflicts");
        version.SetRequires(reqs, conflicts);
        var sha256 = Str(obj, "sha256");
        if (sha256.Length != 0)
        {
            version.Sha256 = sha256;
        }

        version.Data = OneSixVersionFormat.VersionFileFromJson(
            (JsonObject)obj,
            $"{version.Uid}/{version.Version}.json",
            Has(obj, "order"));
        return version;
    }

    // ------------------------------------------------------- Version list / package

    private static MetaVersion MapCommonVersion(string uid, MetaVersionEntryJson dto)
    {
        var version = new MetaVersion(uid, RequireStringOf(dto.Version, "version"));
        // QDateTime::fromString(..., Qt::ISODate).toMSecsSinceEpoch() / 1000; an invalid
        // QDateTime contributes 0
        var releaseTime = ParseUtils.TimeFromS3Time(RequireStringOf(dto.ReleaseTime, "releaseTime"));
        version.RawTime = releaseTime?.ToUnixTimeMilliseconds() / 1000 ?? 0;
        version.Type = dto.Type ?? "";
        version.IsRecommended = dto.Recommended ?? false;
        version.SetVolatile(dto.Volatile ?? false);
        var reqs = new SortedSet<Require>();
        var conflicts = new SortedSet<Require>();
        MapRequires(dto.Requires, reqs);
        MapRequires(dto.Conflicts, conflicts);
        version.SetRequires(reqs, conflicts);
        var sha256 = dto.Sha256 ?? "";
        if (sha256.Length != 0)
        {
            version.Sha256 = sha256;
        }

        return version;
    }

    /// <summary>The requires/conflicts arrays of a parsed member (absent member -> empty).</summary>
    private static void MapRequires(List<MetaRequireJson>? raw, SortedSet<Require> ptr)
    {
        if (raw is null)
        {
            return;
        }

        foreach (var req in raw)
        {
            ptr.Add(new Require
            {
                Uid = RequireStringOf(req.Uid, "uid"),
                EqualsVersion = req.EqualsVersion ?? "",
                Suggests = req.Suggests ?? "",
            });
        }
    }

    private static VersionList ParseVersionListInternal(MetaVersionListJson dto)
    {
        var uid = RequireStringOf(dto.Uid, "uid");

        var versionsRaw = dto.Versions ?? throw MissingMember("versions");
        var versions = new List<MetaVersion>(versionsRaw.Count);
        foreach (var entry in versionsRaw)
        {
            if (entry is null)
            {
                throw new MetaParseException("Document is not an object");
            }

            var version = MapCommonVersion(uid, entry);
            version.SetProvidesRecommendations();
            versions.Add(version);
        }

        var list = new VersionList(uid)
        {
            Name = dto.Name ?? "",
        };
        list.SetVersions(versions);
        return list;
    }

    // ------------------------------------------------------------ format version

    public static MetadataVersion ParseFormatVersion(JsonNode obj, bool required = true)
    {
        if (obj is not JsonObject map || !map.Contains("formatVersion"))
        {
            if (required)
            {
                return MetadataVersion.Invalid;
            }

            return MetadataVersion.InitialRelease;
        }

        // isDouble(): any JSON number. Node-side read (raw token text) — not TryGetValue<T>,
        // whose result depends on whether the JsonValue is parsed-backed or created in code.
        var raw = JsonUtils.EnsureDouble(map["formatVersion"], double.NaN);
        return raw switch
        {
            0 or 1 => MetadataVersion.InitialRelease,
            _ => MetadataVersion.Invalid,
        };
    }

    public static void SerializeFormatVersion(JsonObject obj, MetadataVersion version)
    {
        if (version == MetadataVersion.Invalid)
        {
            return;
        }

        obj["formatVersion"] = (int)version;
    }

    // ----------------------------------------------------------------- entry points

    public static void ParseIndex(JsonObject obj, Index ptr)
    {
        var dto = DeserializeDocument(obj, "index", MetaJsonContext.Default.MetaIndexJson);
        switch (FormatVersionOf(dto.FormatVersion))
        {
            case MetadataVersion.InitialRelease:
                ptr.Merge(ParseIndexInternal(dto));
                break;
            case MetadataVersion.Invalid:
                throw new MetaParseException("Unknown format version!");
        }
    }

    public static void ParseVersionList(JsonObject obj, VersionList ptr)
    {
        var dto = DeserializeDocument(obj, "version list", MetaJsonContext.Default.MetaVersionListJson);
        switch (FormatVersionOf(dto.FormatVersion))
        {
            case MetadataVersion.InitialRelease:
                ptr.Merge(ParseVersionListInternal(dto));
                break;
            case MetadataVersion.Invalid:
                throw new MetaParseException("Unknown format version!");
        }
    }

    public static void ParseVersion(JsonObject obj, MetaVersion ptr)
    {
        var version = ParseFormatVersion(obj);
        switch (version)
        {
            case MetadataVersion.InitialRelease:
                ptr.Merge(ParseVersionInternal(obj));
                break;
            case MetadataVersion.Invalid:
                throw new MetaParseException("Unknown format version!");
        }
    }

    /// <summary>The typed document read over the entity JsonObject contract: a malformed
    /// document (bad shape, wrong member kinds) fails as a MetaParseException, which every
    /// metadata-document consumer already routes to a refetch.</summary>
    private static T DeserializeDocument<T>(JsonObject obj, string what, JsonTypeInfo<T> typeInfo) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(obj, typeInfo)
                   ?? throw new MetaParseException($"The metadata {what} document is null.");
        }
        catch (JsonException e)
        {
            throw new MetaParseException($"The metadata {what} document is malformed: {e.Message}");
        }
    }

    /*
    [
    {"uid":"foo", "equals":"version"}
    ]
    */
    public static void ParseRequires(JsonNode obj, SortedSet<Require> ptr, string keyName = "requires")
    {
        if (Has(obj, keyName))
        {
            var reqArray = JsonUtils.RequireArray(obj, keyName, "'" + keyName + "'");
            foreach (var iter in reqArray)
            {
                var reqObject = JsonUtils.RequireObject(iter);
                var uid = RequireStringOf(reqObject, "uid");
                var equals = Str(reqObject, "equals");
                var suggests = Str(reqObject, "suggests");
                ptr.Add(new Require { Uid = uid, EqualsVersion = equals, Suggests = suggests });
            }
        }
    }

    public static void SerializeRequires(JsonObject obj, SortedSet<Require>? ptr, string keyName = "requires")
    {
        if (ptr is null || ptr.Count == 0)
        {
            return;
        }

        var arrOut = new JsonArray();
        foreach (var iter in ptr)
        {
            var reqOut = new JsonObject
            {
                ["uid"] = iter.Uid,
            };
            if (iter.EqualsVersion.Length != 0)
            {
                reqOut["equals"] = iter.EqualsVersion;
            }

            if (iter.Suggests.Length != 0)
            {
                reqOut["suggests"] = iter.Suggests;
            }

            arrOut.Add((JsonNode)reqOut);
        }

        obj[keyName] = arrOut;
    }

}
