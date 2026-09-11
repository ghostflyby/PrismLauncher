// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/meta/JsonFormat.{h,cpp}: the shared metadata format version handling and
// the Require/RequireSet types, plus the index/version/version-list parsers.
using System.Text.Json;
using System.Text.Json.Nodes;
using Flint.Core.Json;
using Flint.Core.Minecraft;

namespace Flint.Core.Meta;

public enum MetadataVersion
{
    Invalid = -1,
    InitialRelease = 1,
}

/// <summary>Port of Meta::ParseException.</summary>
public sealed class MetaParseException(string message) : Exception(message)
{
}

/// <summary>Port of Meta::Require. Set identity is by uid only, like the C++ operator==/operator&lt;.</summary>
public sealed class Require
{
    public required string Uid { get; init; }

    public string EqualsVersion { get; init; } = "";

    public string Suggests { get; init; } = "";

    public bool DeepEquals(Require rhs)
    {
        return Uid == rhs.Uid && EqualsVersion == rhs.EqualsVersion && Suggests == rhs.Suggests;
    }

    public override string ToString() => Uid;
}

/// <summary>
/// Port of the std::set&lt;Meta::Require&gt;: ordered by uid, elements identified by uid only
/// (inserting an existing uid is a no-op, mirroring std::set::insert).
/// </summary>
public sealed class RequireSet : IEnumerable<Require>
{
    private readonly SortedSet<Require> _set = new(Comparer<Require>.Create((a, b) => string.CompareOrdinal(a.Uid, b.Uid)));

    public int Count => _set.Count;

    public bool IsEmpty => _set.Count == 0;

    public bool Add(Require require) => _set.Add(require);

    public bool Contains(Require require) => _set.Contains(require);

    public bool Remove(Require require) => _set.Remove(require);

    public void Clear() => _set.Clear();

    /// <summary>Port of std::set operator== (element equality is uid-only).</summary>
    public bool SetEquals(RequireSet other)
    {
        return _set.SetEquals(other._set);
    }

    public RequireSet Clone()
    {
        var result = new RequireSet();
        foreach (var require in _set)
        {
            result.Add(require);
        }
        return result;
    }

    public IEnumerator<Require> GetEnumerator() => _set.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

public static class MetaJsonFormat
{
    /// <summary>Port of Meta::currentFormatVersion().</summary>
    public static MetadataVersion CurrentFormatVersion() => MetadataVersion.InitialRelease;

    // ------------------------------------------------------------------ Index

    private static Index ParseIndexInternal(JsonObject obj)
    {
        var objects = RequireArrayOfObjects(obj, "packages");
        var lists = new List<VersionList>(objects.Count);
        foreach (var element in objects)
        {
            var list = new VersionList(JsonUtils.RequireString(element, "uid"))
            {
                Name = element.StringOr("name"),
            };
            list.SetSha256(element.StringOr("sha256"));
            lists.Add(list);
        }
        return new Index(lists);
    }

    // ------------------------------------------------------------------ Version

    private static MetaVersion ParseCommonVersion(string uid, JsonObject obj)
    {
        var version = new MetaVersion(uid, JsonUtils.RequireString(obj, "version"));
        // QDateTime::fromString(..., Qt::ISODate).toMSecsSinceEpoch() / 1000; an invalid
        // QDateTime contributes 0
        var releaseTime = ParseUtils.TimeFromS3Time(JsonUtils.RequireString(obj, "releaseTime"));
        version.SetTime(releaseTime?.ToUnixTimeMilliseconds() / 1000 ?? 0);
        version.SetType(obj.StringOr("type"));
        version.SetRecommended(obj.BoolOr("recommended"));
        version.SetVolatile(obj.BoolOr("volatile"));
        var reqs = new RequireSet();
        var conflicts = new RequireSet();
        ParseRequires(obj, reqs, "requires");
        ParseRequires(obj, conflicts, "conflicts");
        version.SetRequires(reqs, conflicts);
        var sha256 = obj.StringOr("sha256");
        if (sha256.Length != 0)
        {
            version.SetSha256(sha256);
        }
        return version;
    }

    private static MetaVersion ParseVersionInternal(JsonObject obj)
    {
        var version = ParseCommonVersion(JsonUtils.RequireString(obj, "uid"), obj);

        version.SetData(OneSixVersionFormat.VersionFileFromJson(
            obj,
            $"{version.Uid}/{version.Version}.json",
            obj.Contains("order")));
        return version;
    }

    // ------------------------------------------------------- Version list / package

    private static VersionList ParseVersionListInternal(JsonObject obj)
    {
        var uid = JsonUtils.RequireString(obj, "uid");

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
            Name = obj.StringOr("name"),
        };
        list.SetVersions(versions);
        return list;
    }

    // ------------------------------------------------------------ format version

    public static MetadataVersion ParseFormatVersion(JsonObject obj, bool required = true)
    {
        if (!obj.Contains("formatVersion"))
        {
            if (required)
            {
                return MetadataVersion.Invalid;
            }
            return MetadataVersion.InitialRelease;
        }
        // isDouble(): any JSON number
        var node = obj["formatVersion"];
        if (node is not JsonValue || !((JsonValue)node).TryGetValue<double>(out var raw))
        {
            return MetadataVersion.Invalid;
        }
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
    public static void ParseRequires(JsonObject obj, RequireSet ptr, string keyName = "requires")
    {
        if (obj.Contains(keyName))
        {
            var reqArray = JsonUtils.RequireArray(obj, keyName);
            foreach (var iter in reqArray.EnumerateArray())
            {
                // NOTE(p2 fix): the request object must be a JsonObject so requireString binds
                // the (parent, key) overload like the C++ requireString(reqObject, "uid");
                // the previous JsonElement form passed "uid" as the error label and rejected
                // every entry with "uid is not a string"
                var reqObject = JsonUtils.RequireObject(iter).ToJsonObject();
                var uid = JsonUtils.RequireString(reqObject, "uid");
                var equals = reqObject.StringOr("equals");
                var suggests = reqObject.StringOr("suggests");
                ptr.Add(new Require { Uid = uid, EqualsVersion = equals, Suggests = suggests });
            }
        }
    }

    public static void SerializeRequires(JsonObject obj, RequireSet? ptr, string keyName = "requires")
    {
        if (ptr is null || ptr.IsEmpty)
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
    private static List<JsonObject> RequireArrayOfObjects(JsonObject parent, string key)
    {
        if (!parent.Contains(key))
        {
            throw new JsonParseException("'" + key + "'s parent does not contain '" + key + "'");
        }
        var result = new List<JsonObject>();
        var node = parent[key];
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
