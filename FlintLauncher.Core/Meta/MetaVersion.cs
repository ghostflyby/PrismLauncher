// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/meta/Version.{h,cpp}: a metadata version record wrapping a VersionFile.
// The QObject signals (typeChanged/timeChanged/requiresChanged) are model plumbing for the Qt
// views and are not ported; MergeFromList assigns directly like upstream.
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Versioning;

namespace FlintLauncher.Core.Meta;

public sealed class MetaVersion : MetaEntity
{
    private bool _providesRecommendations;

    private bool _recommended;

    // NOTE: the C++ m_name member is dead there as well (never written); dropped to keep
    // the port warning-clean
    private readonly string _uid;

    private readonly string _version;

    private string _type = "";

    private long _time;

    private RequireSet _requires = new();

    private RequireSet _conflicts = new();

    private bool _volatile;

    private VersionFile? _data;

    public MetaVersion(string uid, string version)
    {
        _uid = uid;
        _version = version;
    }

    /// <summary>Port of BaseVersion::descriptor().</summary>
    public string Descriptor() => _version;

    /// <summary>Port of BaseVersion::name().</summary>
    public string Name()
    {
        if (_data is not null)
        {
            return _data.Name;
        }
        return _uid;
    }

    /// <summary>Port of BaseVersion::typeString().</summary>
    public string TypeString() => _type;

    public string Uid() => _uid;

    public string Version() => _version;

    public string Type() => _type;

    /// <summary>QDateTime::fromMSecsSinceEpoch(m_time * 1000, Qt::UTC).</summary>
    public DateTimeOffset Time() => DateTimeOffset.FromUnixTimeMilliseconds(_time * 1000);

    public long RawTime() => _time;

    public RequireSet RequiredSet() => _requires;

    public VersionFile? Data() => _data;

    public bool IsRecommended() => _recommended;

    /// <summary>Hides BaseEntity::isLoaded() like the C++ shadowing declaration.</summary>
    public new bool IsLoaded() => _data is not null && base.IsLoaded();

    public void Merge(MetaVersion other)
    {
        MergeFromList(other);
        if (other._data is not null)
        {
            SetData(other._data);
        }
    }

    public void MergeFromList(MetaVersion other)
    {
        if (other._providesRecommendations)
        {
            if (_recommended != other._recommended)
            {
                SetRecommended(other._recommended);
            }
        }
        if (_type != other._type)
        {
            SetType(other._type);
        }
        if (_time != other._time)
        {
            SetTime(other._time);
        }
        if (!_requires.SetEquals(other._requires))
        {
            _requires = other._requires.Clone();
        }
        if (!_conflicts.SetEquals(other._conflicts))
        {
            _conflicts = other._conflicts.Clone();
        }
        if (_volatile != other._volatile)
        {
            SetVolatile(other._volatile);
        }
        if (other.Sha256.Length != 0)
        {
            Sha256 = other.Sha256;
        }
    }

    public override void Parse(System.Text.Json.Nodes.JsonObject obj)
    {
        MetaJsonFormat.ParseVersion(obj, this);
    }

    public override string LocalFilename() => _uid + '/' + _version + ".json";

    /// <summary>Port of toComparableVersion(): a FlexVer comparable version of the descriptor.</summary>
    public McVersion ToComparableVersion()
    {
        return new McVersion(Descriptor());
    }

    // ------------------------------------------------- for usage by format parsers only

    public void SetType(string type) => _type = type;

    public void SetTime(long time) => _time = time;

    public void SetRequires(RequireSet reqs, RequireSet conflicts)
    {
        _requires = reqs;
        _conflicts = conflicts;
    }

    public void SetVolatile(bool volatileValue) => _volatile = volatileValue;

    public void SetRecommended(bool recommended) => _recommended = recommended;

    public void SetProvidesRecommendations() => _providesRecommendations = true;

    public void SetData(VersionFile data) => _data = data;
}
