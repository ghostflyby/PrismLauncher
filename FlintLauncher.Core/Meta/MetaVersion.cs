// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/meta/Version.{h,cpp}: a metadata version record wrapping a VersionFile.
// The QObject signals (typeChanged/timeChanged/requiresChanged) are model plumbing for the Qt
// views and are not ported; MergeFromList assigns directly like upstream.

using System.Text.Json.Nodes;
using FlintLauncher.Core.Minecraft;
using FlintLauncher.Core.Versioning;

namespace FlintLauncher.Core.Meta;

public sealed class MetaVersion : MetaEntity
{
    private bool providesRecommendations;

    private bool recommended;

    // NOTE: the C++ m_name member is dead there as well (never written); dropped to keep
    // the port warning-clean
    private readonly string _uid;

    private readonly string _version;

    private string type = "";

    private long time;

    private RequireSet requires = new();

    private RequireSet conflicts = new();

    private bool @volatile;

    private VersionFile? data;

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
        if (data is not null)
        {
            return data.Name;
        }

        return _uid;
    }

    /// <summary>Port of BaseVersion::typeString().</summary>
    public string TypeString() => type;

    public string Uid() => _uid;

    public string Version() => _version;

    public string Type() => type;

    /// <summary>QDateTime::fromMSecsSinceEpoch(m_time * 1000, Qt::UTC).</summary>
    public DateTimeOffset Time() => DateTimeOffset.FromUnixTimeMilliseconds(time * 1000);

    public long RawTime() => time;

    public RequireSet RequiredSet() => requires;

    public VersionFile? Data() => data;

    public bool IsRecommended() => recommended;

    /// <summary>Hides BaseEntity::isLoaded() like the C++ shadowing declaration.</summary>
    public new bool IsLoaded() => data is not null && base.IsLoaded();

    public void Merge(MetaVersion other)
    {
        MergeFromList(other);
        if (other.data is not null)
        {
            SetData(other.data);
        }
    }

    public void MergeFromList(MetaVersion other)
    {
        if (other.providesRecommendations)
        {
            if (recommended != other.recommended)
            {
                SetRecommended(other.recommended);
            }
        }

        if (type != other.type)
        {
            SetType(other.type);
        }

        if (time != other.time)
        {
            SetTime(other.time);
        }

        if (!requires.SetEquals(other.requires))
        {
            requires = other.requires.Clone();
        }

        if (!conflicts.SetEquals(other.conflicts))
        {
            conflicts = other.conflicts.Clone();
        }

        if (@volatile != other.@volatile)
        {
            SetVolatile(other.@volatile);
        }

        if (other.Sha256.Length != 0)
        {
            Sha256 = other.Sha256;
        }
    }

    public override void Parse(JsonObject obj)
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

    public void SetType(string type) => this.type = type;

    public void SetTime(long time) => this.time = time;

    public void SetRequires(RequireSet reqs, RequireSet conflicts)
    {
        requires = reqs;
        this.conflicts = conflicts;
    }

    public void SetVolatile(bool volatileValue) => @volatile = volatileValue;

    public void SetRecommended(bool recommended) => this.recommended = recommended;

    public void SetProvidesRecommendations() => providesRecommendations = true;

    public void SetData(VersionFile data) => this.data = data;
}
