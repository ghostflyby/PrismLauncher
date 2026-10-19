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
    string? Version,    string? ReleaseTime,    string? Type,    bool? Recommended,    bool? Volatile,    List<MetaRequireJson?>? Requires,    List<MetaRequireJson?>? Conflicts,    string? Sha256);

/// <summary>The ONE requires wire — shared by the meta documents, the OneSix version
/// format and the mmc-pack components. equals/suggests coerce (a junk member reads as
/// absent, like the former Str read); uid is enforced by the parsers.</summary>
internal sealed record MetaRequireJson(
    string? Uid,
    [property: JsonPropertyName("equals"), JsonConverter(typeof(QtCoercingNullableStringConverter))] string? EqualsVersion,
    [property: JsonConverter(typeof(QtCoercingNullableStringConverter))] string? Suggests);

/// <summary>The version DOCUMENT header (uid + the common version members); the OneSix
/// body keys are read by OneSixVersionFormat over the same node.</summary>
internal sealed record MetaVersionDocumentJson(
    JsonElement? FormatVersion,
    string? Uid,
    string? Version,
    string? ReleaseTime,
    string? Type,
    bool? Recommended,
    bool? Volatile,
    List<MetaRequireJson?>? Requires,
    List<MetaRequireJson?>? Conflicts,
    string? Sha256);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MetaIndexJson))]
[JsonSerializable(typeof(MetaVersionListJson))]
[JsonSerializable(typeof(MetaVersionDocumentJson))]
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

    /// <summary>QJsonObject::contains(key) equivalent: true even when the member holds JSON null.</summary>
    private static bool Has(JsonNode? node, string key) => node is JsonObject obj && obj.Contains(key);

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

    private static MetaVersion ParseVersionInternal(JsonObject obj, MetaVersionDocumentJson dto)
    {
        var version = MapCommonVersion(RequireStringOf(dto.Uid, "uid"), new MetaVersionEntryJson(
            dto.Version, dto.ReleaseTime, dto.Type, dto.Recommended, dto.Volatile,
            dto.Requires, dto.Conflicts, dto.Sha256));

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
        version.Volatile = dto.Volatile ?? false;
        var reqs = new SortedSet<Require>();
        var conflicts = new SortedSet<Require>();
        MapRequires(dto.Requires, reqs);
        MapRequires(dto.Conflicts, conflicts);
        version.RequiredSet = reqs;
        version.Conflicts = conflicts;
        var sha256 = dto.Sha256 ?? "";
        if (sha256.Length != 0)
        {
            version.Sha256 = sha256;
        }

        return version;
    }

    /// <summary>The requires/conflicts arrays of a parsed member (absent member -> empty).
    /// The meta documents flow through here (not the JsonParseException ParseRequires);
    /// a null entry fails as a MetaParseException like the former node read.</summary>
    private static void MapRequires(List<MetaRequireJson?>? raw, SortedSet<Require> ptr)
    {
        if (raw is null)
        {
            return;
        }

        foreach (var req in raw)
        {
            ptr.Add(new Require
            {
                Uid = RequireStringOf(req?.Uid, "uid"),
                EqualsVersion = req?.EqualsVersion ?? "",
                Suggests = req?.Suggests ?? "",
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
            version.ProvidesRecommendations = true;
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

    /// <summary>The formatVersion gate over the RAW member element (the probes carry it as
    /// an unbound JsonElement?, so any token binds): a JSON number of 0 or 1 is the
    /// InitialRelease, any other PRESENT value fails the document in both modes, and only
    /// the ABSENT member falls back (Invalid when required, InitialRelease otherwise) — the
    /// EnsureDouble(raw token) input compatibility of the former read.</summary>
    public static MetadataVersion ParseFormatVersion(JsonElement? formatVersion, bool required = true)
    {
        if (formatVersion is null || formatVersion.Value.ValueKind == JsonValueKind.Null)
        {
            // the ABSENT member falls back; an explicit JSON null counts as absent too,
            // uniformly on both the probe path (the nullable binds it as no value) and the
            // direct Null-kind element path (accepted deviation, ledger 4.1)
            return required ? MetadataVersion.Invalid : MetadataVersion.InitialRelease;
        }

        var value = formatVersion.Value.ValueKind == JsonValueKind.Number
            ? formatVersion.Value.GetDouble()
            : double.NaN;
        return value is 0 or 1
            ? MetadataVersion.InitialRelease
            : MetadataVersion.Invalid;
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
        var dto = DeserializeDocument(obj, "version", MetaJsonContext.Default.MetaVersionDocumentJson);
        switch (ParseFormatVersion(dto.FormatVersion))
        {
            case MetadataVersion.InitialRelease:
                ptr.Merge(ParseVersionInternal(obj, dto));
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

    /// <summary>The typed requires/conflicts arrays over the shared MetaRequireJson wire
    /// (absent member reads as empty; a null or uid-less entry fails with the collapsed
    /// kind text). Serves the version format and the mmc-pack components — the meta
    /// documents route through MapRequires (MetaParseException semantics).</summary>
    internal static void ParseRequires(List<MetaRequireJson?>? raw, SortedSet<Require> ptr, string keyName = "requires")
    {
        foreach (var req in raw ?? [])
        {
            ptr.Add(new Require
            {
                Uid = (req is not null ? req.Uid : null)
                    ?? throw new JsonParseException("'" + keyName + "' entry has no 'uid' string"),
                EqualsVersion = req?.EqualsVersion ?? "",
                Suggests = req?.Suggests ?? "",
            });
        }
    }

}
