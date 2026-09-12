// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/meta/VersionList.{h,cpp} (the non-Qt subset). The QAbstractItemModel
// machinery (data()/roles/roleNames/waitToLoad/QEventLoop), the BaseVersionList interface and
// the change signals are UI-layer plumbing and are not ported; everything metadata-related is.
using FlintLauncher.Core.Versioning;

namespace FlintLauncher.Core.Meta;

public sealed class VersionList : MetaEntity
{
    private readonly List<MetaVersion> _versions = [];
    private readonly List<string> _externalRecommendsVersions = [];
    private readonly Dictionary<string, MetaVersion> _lookup = new(StringComparer.Ordinal);
    private readonly string _uid;
    private string _name = "";

    private MetaVersion? _recommended;

    public VersionList(string uid)
    {
        _uid = uid;
    }

    public string Uid() => _uid;

    /// <summary>The display name; a settable property for the parsers (setName upstream).</summary>
    public string Name
    {
        get => _name;
        set => _name = value;
    }

    public override string LocalFilename() => _uid + "/index.json";

    public string HumanReadable() => _name.Length == 0 ? _uid : _name;

    public IReadOnlyList<MetaVersion> Versions() => _versions;

    public MetaVersion GetVersion(string version)
    {
        if (!_lookup.TryGetValue(version, out var outVersion) || outVersion is null)
        {
            outVersion = new MetaVersion(_uid, version);
            _lookup[version] = outVersion;
            _versions.Add(outVersion);
        }
        return outVersion;
    }

    public bool HasVersion(string version)
    {
        return _versions.Exists(a => a.Version() == version);
    }

    /// <summary>
    /// Port of sortVersions(): `*a &lt; *b` resolves to BaseVersion::operator&lt;, which
    /// compares name() (QString ordering = ordinal).
    /// </summary>
    public void SortVersions()
    {
        _versions.Sort((a, b) => string.CompareOrdinal(a.Name(), b.Name()));
    }

    public override void Parse(System.Text.Json.Nodes.JsonObject obj)
    {
        MetaJsonFormat.ParseVersionList(obj, this);
    }

    public void AddExternalRecommends(IEnumerable<string> recommends) => _externalRecommendsVersions.AddRange(recommends);

    public void ClearExternalRecommends() => _externalRecommendsVersions.Clear();

    public void SetVersions(IReadOnlyList<MetaVersion> versions)
    {
        _versions.Clear();
        _versions.AddRange(versions);
        // newest first by raw time (std::sort upstream, so ties may reorder either way)
        _versions.Sort((a, b) => b.RawTime().CompareTo(a.RawTime()));
        _lookup.Clear();
        foreach (var version in _versions)
        {
            _lookup[version.Version()] = version;
        }

        // FIXME upstream: this is dumb, we have 'recommended' as part of the metadata already...
        _recommended = _versions.Find(ptr => ptr.Type() == "release");
    }

    public void MergeFromIndex(VersionList other)
    {
        if (_name != other._name)
        {
            Name = other._name;
        }
        if (other.Sha256.Length != 0)
        {
            Sha256 = other.Sha256;
        }
    }

    public void Merge(VersionList other)
    {
        if (_name != other._name)
        {
            Name = other._name;
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
            if (_lookup.TryGetValue(otherVersion.Version(), out var existing) && existing is not null)
            {
                existing.MergeFromList(otherVersion);
                version = existing;
            }
            else
            {
                _lookup[otherVersion.Version()] = otherVersion;
                _versions.Add(otherVersion);
                version = otherVersion;
            }
            _recommended = GetBetterVersion(_recommended, version);
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
        if (a.Type() == b.Type())
        {
            // newer of same type wins
            return a.RawTime() > b.RawTime() ? a : b;
        }
        // 'release' type wins
        return a.Type() == "release" ? a : b;
    }

    public MetaVersion? GetRecommended() => _recommended;

    public MetaVersion? GetRecommendedForParent(string uid, string version)
    {
        var foundExplicit = _versions.Find(ver =>
        {
            foreach (var req in ver.RequiredSet())
            {
                if (req.Uid == uid && req.EqualsVersion == version)
                {
                    return ver.IsRecommended();
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
            foreach (var req in ver.RequiredSet())
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
            if (!onlyRelease || ver.Type() == "release")
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
        return a.RawTime() > b.RawTime() ? a : b;
    }

    /// <summary>
    /// Port of VersionList::isLoaded() (a plain override returning BaseEntity::isLoaded()).
    /// The VersionList-specific data is loaded when the metadata file is.
    /// </summary>
    public new bool IsLoaded() => base.IsLoaded();
}
