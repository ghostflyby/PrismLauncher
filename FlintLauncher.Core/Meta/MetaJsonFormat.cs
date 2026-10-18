// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/meta/JsonFormat.{h,cpp}: the shared metadata format version handling and
// the Require type plus the index/version/version-list parsers. Reads go through
// the JsonNode model (single-DOM); the Parse(JsonObject) entity entry points keep JsonObject.

using System.Collections;
using System.Text.Json.Nodes;
using FlintLauncher.Core.Java;
using FlintLauncher.Core.Json;
using FlintLauncher.Core.Minecraft;

namespace FlintLauncher.Core.Meta;

public enum MetadataVersion
{
    Invalid = -1,
    InitialRelease = 1,
}

/// <summary>Port of Meta::ParseException.</summary>
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

public static class MetaJsonFormat
{
    /// <summary>Port of Meta::currentFormatVersion().</summary>
    public static MetadataVersion CurrentFormatVersion() => MetadataVersion.InitialRelease;

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

    private static Index ParseIndexInternal(JsonNode obj)
    {
        var objects = RequireArrayOfObjects(obj, "packages");
        var lists = new List<VersionList>(objects.Count);
        foreach (var element in objects)
        {
            var list = new VersionList(RequireStringOf(element, "uid"))
            {
                Name = element.StringOr("name"),
            };
            list.Sha256 = element.StringOr("sha256");
            lists.Add(list);
        }

        return new Index(lists);
    }

    // ------------------------------------------------------------------ Version

    private static MetaVersion ParseCommonVersion(string uid, JsonNode obj)
    {
        var version = new MetaVersion(uid, RequireStringOf(obj, "version"));
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
            version.Sha256  = sha256;
        }

        return version;
    }

    private static MetaVersion ParseVersionInternal(JsonNode obj)
    {
        var version = ParseCommonVersion(RequireStringOf(obj, "uid"), obj);

        // OneSixVersionFormat (own migration batch) still exposes the JsonObject entry; the
        // meta version document is an object by construction (ParseVersion takes JsonObject).
        version.Data = OneSixVersionFormat.VersionFileFromJson(
            (JsonObject)obj,
            $"{version.Uid}/{version.Version}.json",
            Has(obj, "order"));
        return version;
    }

    // ------------------------------------------------------- Version list / package

    private static VersionList ParseVersionListInternal(JsonNode obj)
    {
        var uid = RequireStringOf(obj, "uid");

        var versionsRaw = RequireArrayOfObjects(obj, "versions");
        var versions = new List<MetaVersion>(versionsRaw.Count);
        foreach (var vObj in versionsRaw)
        {
            var version = ParseCommonVersion(uid, vObj);
            version.SetProvidesRecommendations();
            versions.Add(version);
        }

        var list = new VersionList(uid)
        {
            Name = Str(obj, "name"),
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
        var version = ParseFormatVersion(obj);
        switch (version)
        {
            case MetadataVersion.InitialRelease:
                ptr.Merge(ParseIndexInternal(obj));
                break;
            case MetadataVersion.Invalid:
                throw new MetaParseException("Unknown format version!");
        }
    }

    public static void ParseVersionList(JsonObject obj, VersionList ptr)
    {
        var version = ParseFormatVersion(obj);
        switch (version)
        {
            case MetadataVersion.InitialRelease:
                ptr.Merge(ParseVersionListInternal(obj));
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

    /// <summary>
    /// Port of requireIsArrayOf&lt;QJsonObject&gt;(parent, key): a missing key throws, a
    /// non-array value yields an empty list (parent[key].toArray() cast upstream), and
    /// non-object elements throw with the "Document" label.
    /// </summary>
    private static List<JsonObject> RequireArrayOfObjects(JsonNode? parent, string key)
    {
        if (parent is not JsonObject map || !map.Contains(key))
        {
            throw new JsonParseException("'" + key + "'s parent does not contain '" + key + "'");
        }

        var result = new List<JsonObject>();
        var node = map[key];
        if (node is not JsonArray array)
        {
            return result;
        }

        foreach (var val in array)
        {
            if (val is not JsonObject obj)
            {
                throw new JsonParseException("Document is not an object");
            }

            result.Add(obj);
        }

        return result;
    }
}
