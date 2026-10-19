// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/meta/VersionList.{h,cpp} (the non-Qt subset). The QAbstractItemModel
// machinery (data()/roles/roleNames/waitToLoad/QEventLoop), the BaseVersionList interface and
// the change signals are UI-layer plumbing and are not ported; everything metadata-related is.

using System.Text.Json.Nodes;

namespace FlintLauncher.Core.Meta;

public sealed class VersionList(string uid) : MetaEntity
{
    private readonly List<MetaVersion> _versions = [];
    private readonly Dictionary<string, MetaVersion> _lookup = new(StringComparer.Ordinal);

    private MetaVersion? recommended;

    public string Uid { get; } = uid;

    /// <summary>The display name; a settable property for the parsers (setName upstream).</summary>
    public string Name { get; set; } = "";

    public override string LocalFilename => Uid + "/index.json";

    public string HumanReadable() => Name.Length == 0 ? Uid : Name;

    public IReadOnlyList<MetaVersion> Versions() => _versions;

    public MetaVersion GetVersion(string version)
    {
        if (_lookup.TryGetValue(version, out var outVersion)) return outVersion;
        outVersion = new MetaVersion(Uid, version);
        if (Cache is { } cache)
        {
            outVersion.Cache = cache;
        }

        _lookup[version] = outVersion;
        _versions.Add(outVersion);

        return outVersion;
    }

    public bool HasVersion(string version)
    {
        return _versions.Exists(a => a.Version == version);
    }

    /// <summary>
    /// Port of sortVersions(): `*a &lt; *b` resolves to BaseVersion::operator&lt;, which
    /// compares name() (QString ordering = ordinal).
    /// </summary>
    public void SortVersions()
    {
        _versions.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
    }

    public override void Parse(JsonObject obj)
    {
        MetaJsonFormat.ParseVersionList(obj, this);
    }

    public void SetVersions(IReadOnlyList<MetaVersion> versions)
    {
        _versions.Clear();
        foreach (var version in versions)
        {
            if (Cache is { } cache)
            {
                version.Cache = cache;
            }
        }

        _versions.AddRange(versions);
        // newest first by raw time (std::sort upstream, so ties may reorder either way)
        _versions.Sort((a, b) => b.RawTime.CompareTo(a.RawTime));
        _lookup.Clear();
        foreach (var version in _versions)
        {
            _lookup[version.Version] = version;
        }

        // FIXME upstream: this is dumb, we have 'recommended' as part of the metadata already...
        recommended = _versions.Find(ptr => ptr.Type == "release");
    }

    public void MergeFromIndex(VersionList other)
    {
        if (Name != other.Name)
        {
            Name = other.Name;
        }

        if (other.Sha256.Length != 0)
        {
            Sha256 = other.Sha256;
        }
    }

    public void Merge(VersionList other)
    {
        if (Name != other.Name)
        {
            Name = other.Name;
        }

        if (other.Sha256.Length != 0)
        {
            Sha256 = other.Sha256;
        }

        if (other._versions.Count == 0)
        {
            // qWarning() << "Empty list loaded ...";
        }

        foreach (var otherVersion in other._versions)
        {
            MetaVersion version;
            // we already have the version. merge the contents
            if (_lookup.TryGetValue(otherVersion.Version, out var existing))
            {
                existing.MergeFromList(otherVersion);
                version = existing;
            }
            else
            {
                if (Cache is { } cache)
                {
                    otherVersion.Cache = cache;
                }

                _lookup[otherVersion.Version] = otherVersion;
                _versions.Add(otherVersion);
                version = otherVersion;
            }

            recommended = GetBetterVersion(recommended, version);
        }
    }

    // FIXME upstream: this is dumb, we have 'recommended' as part of the metadata already...
    private static MetaVersion? GetBetterVersion(MetaVersion? a, MetaVersion? b)
    {
        if (a is null)
        {
            return b;
        }

        if (b is null)
        {
            return a;
        }

        if (a.Type == b.Type)
        {
            // newer of same type wins
            return a.RawTime > b.RawTime ? a : b;
        }

        // 'release' type wins
        return a.Type == "release" ? a : b;
    }

    public MetaVersion? GetRecommended() => recommended;

    public MetaVersion? GetRecommendedForParent(string uid, string version)
    {
        var foundExplicit = _versions.Find(ver =>
        {
            foreach (var req in ver.RequiredSet)
            {
                if (req.Uid == uid && req.EqualsVersion == version)
                {
                    return ver.IsRecommended;
                }
            }

            return false;
        });
        return foundExplicit;
    }

    public MetaVersion? GetLatestForParent(string uid, string version)
    {
        MetaVersion? latestCompat = null;
        foreach (var ver in _versions)
        {
            foreach (var req in ver.RequiredSet)
            {
                if (req.Uid == uid && req.EqualsVersion == version)
                {
                    latestCompat = GetBetterVersion(latestCompat, ver);
                    break;
                }
            }
        }

        return latestCompat;
    }

    public MetaVersion? GetLatest(bool onlyRelease = true)
    {
        MetaVersion? latestCompat = null;
        foreach (var ver in _versions)
        {
            if (!onlyRelease || ver.Type == "release")
            {
                latestCompat = GetLatestVersion(latestCompat, ver);
            }
        }

        return latestCompat;
    }

    private static MetaVersion? GetLatestVersion(MetaVersion? a, MetaVersion? b)
    {
        if (a is null)
        {
            return b;
        }

        if (b is null)
        {
            return a;
        }

        return a.RawTime > b.RawTime ? a : b;
    }

}
